using System.Text.Json;
using thuvu;
using thuvu.Desktop.ViewModels;
using thuvu.Models;
using thuvu.Services;
using thuvu.Tools;
using SqSessionData = thuvu.Tools.SessionData;

namespace thuvu.Desktop.Services;

/// <summary>
/// Tracks all active agent/chat pairs. Each chat tab has its own agent with
/// independent conversation history.
/// </summary>
public class AgentRegistry
{
    private static AgentRegistry? _instance;
    public static AgentRegistry Instance => _instance ??= new AgentRegistry();

    private int _counter;
    private readonly Dictionary<string, AgentEntry> _agents = new();

    public IReadOnlyDictionary<string, AgentEntry> Agents => _agents;

    /// <summary>Fired when any agent's processing state changes</summary>
    public event Action<string, bool>? OnAgentStateChanged;

    /// <summary>Working directory for agents created by this registry</summary>
    public string? WorkDirectory { get; set; }

    /// <summary>Create a new agent+chat pair with a unique ID</summary>
    public (ChatViewModel chat, IAgentService agent) CreateAgent(string? name = null)
    {
        _counter++;
        var id = $"Chat_{_counter}";
        name ??= $"Chat {_counter}";

        var agent = new DesktopAgentService { WorkDirectory = WorkDirectory, SessionId = id };
        var chat = new ChatViewModel
        {
            Id = id,
            Title = $"💬 {name}",
            CanClose = true,
            SessionName = name
        };
        chat.SetAgentService(agent);

        // Save initial session to DB
        SaveSessionToDb(id, name, agent);

        // Track processing state changes
        chat.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ChatViewModel.IsProcessing))
                OnAgentStateChanged?.Invoke(id, chat.IsProcessing);
        };

        _agents[id] = new AgentEntry(id, name, chat, agent);
        return (chat, agent);
    }

    /// <summary>Create a detached agent running in a separate process</summary>
    public async Task<(ChatViewModel chat, IAgentService? agent)> CreateDetachedAgentAsync(string? name = null)
    {
        _counter++;
        var id = $"Detached_{_counter}";
        name ??= $"Detached {_counter}";

        var chat = new ChatViewModel
        {
            Id = id,
            Title = $"🔗 {name}",
            CanClose = true,
            SessionName = name
        };

        // Spawn agent process
        var processInfo = await AgentProcessManager.Instance.SpawnAgentAsync(id, name);
        if (processInfo == null)
        {
            return (chat, null);
        }

        var agent = new AgentClient(processInfo.Url, processInfo.Token)
        {
            WorkDirectory = WorkDirectory,
            SessionId = id
        };

        // Verify connection and create conversation
        var connected = await agent.ConnectAsync();
        if (!connected)
        {
            AgentProcessManager.Instance.StopAgent(id);
            agent.Dispose();
            return (chat, null);
        }
        await agent.CreateConversationAsync();

        chat.SetAgentService(agent);
        SaveSessionToDb(id, name, agent);

        chat.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ChatViewModel.IsProcessing))
                OnAgentStateChanged?.Invoke(id, chat.IsProcessing);
        };

        _agents[id] = new AgentEntry(id, name, chat, agent);
        return (chat, agent);
    }

    /// <summary>Restore an agent from SQLite session + message records</summary>
    public (ChatViewModel chat, IAgentService agent) RestoreAgentFromDb(
        SqSessionData session, List<MessageRecord> messages)
    {
        var id = session.SessionId;
        var name = session.Title ?? id;

        // Parse numeric suffix to keep counter ahead of restored IDs
        if (id.StartsWith("Chat_") && int.TryParse(id[5..], out var num))
            _counter = Math.Max(_counter, num);
        else if (id.StartsWith("Detached_") && int.TryParse(id[9..], out var dnum))
            _counter = Math.Max(_counter, dnum);

        var agent = new DesktopAgentService { WorkDirectory = WorkDirectory, SessionId = id };
        if (!string.IsNullOrEmpty(session.ModelId))
            agent.SetModel(session.ModelId);

        // Reconstruct ChatMessage list from DB records
        var chatMessages = ReconstructMessages(messages, session.SystemPrompt);
        agent.RestoreMessages(chatMessages);

        var chat = new ChatViewModel
        {
            Id = id,
            Title = $"💬 {name}",
            CanClose = true,
            SessionName = name
        };
        chat.SetAgentService(agent);

        // Restore prompt template selection from metadata
        if (!string.IsNullOrEmpty(session.MetadataJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(session.MetadataJson);
                if (doc.RootElement.TryGetProperty("promptTemplateId", out var ptElem))
                    chat.SelectPromptById(ptElem.GetString() ?? "");
            }
            catch { }
        }

        // Rebuild UI messages from DB records
        RestoreUiMessages(chat, messages);

        chat.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ChatViewModel.IsProcessing))
                OnAgentStateChanged?.Invoke(id, chat.IsProcessing);
        };

        _agents[id] = new AgentEntry(id, name, chat, agent);
        return (chat, agent);
    }

    /// <summary>Reconnect to a running detached agent process discovered on startup</summary>
    public async Task<(ChatViewModel chat, IAgentService? agent)> ReconnectDetachedAgentAsync(
        AgentProcessInfo processInfo, SqSessionData? session = null, List<MessageRecord>? messages = null)
    {
        var id = processInfo.AgentId;
        var name = processInfo.Name;

        if (id.StartsWith("Detached_") && int.TryParse(id[9..], out var num))
            _counter = Math.Max(_counter, num);

        var remote = new AgentClient(processInfo.Url, processInfo.Token)
        {
            WorkDirectory = WorkDirectory,
            SessionId = id
        };

        var connected = await remote.ConnectAsync();
        if (!connected)
        {
            remote.Dispose();
            return (CreateDisconnectedChat(id, name), null);
        }
        await remote.CreateConversationAsync();

        var chat = new ChatViewModel
        {
            Id = id,
            Title = $"🔗 {name}",
            CanClose = true,
            SessionName = name
        };
        chat.SetAgentService(remote);

        // Restore UI messages from DB if available
        if (messages != null && messages.Count > 0)
            RestoreUiMessages(chat, messages);

        chat.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ChatViewModel.IsProcessing))
                OnAgentStateChanged?.Invoke(id, chat.IsProcessing);
        };

        _agents[id] = new AgentEntry(id, name, chat, remote);
        return (chat, remote);
    }

    private ChatViewModel CreateDisconnectedChat(string id, string name)
    {
        var chat = new ChatViewModel
        {
            Id = id,
            Title = $"❌ {name}",
            CanClose = true,
            SessionName = name
        };
        return chat;
    }

    /// <summary>Get the agent service for a given chat ID</summary>
    public IAgentService? GetAgent(string chatId)
    {
        return _agents.TryGetValue(chatId, out var entry) ? entry.Agent : null;
    }

    /// <summary>Remove an agent when its chat tab is closed</summary>
    public void RemoveAgent(string chatId)
    {
        if (_agents.TryGetValue(chatId, out var entry))
        {
            // Stop detached agent process if applicable
            if (entry.Agent is AgentClient client)
            {
                client.Dispose();
                AgentProcessManager.Instance.StopAgent(chatId);
            }
        }
        _agents.Remove(chatId);
        _ = Task.Run(async () => { try { await SqliteService.Instance.DeleteSessionAsync(chatId); } catch { } });
    }

    /// <summary>Reload config on all agents (after settings change)</summary>
    public void ReloadAll()
    {
        foreach (var entry in _agents.Values)
            entry.Agent.ReloadConfig();
    }

    /// <summary>Save all session metadata to SQLite (messages are already saved incrementally)</summary>
    public void SaveAllSessions(string? activeId = null)
    {
        foreach (var entry in _agents.Values)
        {
            try
            {
                var isActive = entry.Id == activeId;
                var metadata = new Dictionary<string, object>();
                if (isActive) metadata["active"] = true;
                if (entry.Chat.CurrentPromptTemplateId != null)
                    metadata["promptTemplateId"] = entry.Chat.CurrentPromptTemplateId;

                var session = new SqSessionData
                {
                    SessionId = entry.Id,
                    AgentId = "desktop",
                    Title = entry.Name,
                    ModelId = entry.Agent.ModelOverride,
                    WorkDirectory = WorkDirectory,
                    LastActivityAt = DateTime.Now,
                    MetadataJson = metadata.Count > 0 ? JsonSerializer.Serialize(metadata) : null
                };
                _ = Task.Run(async () => { try { await SqliteService.Instance.SaveSessionAsync(session); } catch { } });
            }
            catch { }
        }
    }

    private static void SaveSessionToDb(string id, string name, IAgentService agent)
    {
        var session = new SqSessionData
        {
            SessionId = id,
            AgentId = "desktop",
            Title = name,
            ModelId = agent.ModelOverride,
            SystemPrompt = SystemPromptManager.Instance.GetCurrentSystemPrompt(),
            CreatedAt = DateTime.Now,
            LastActivityAt = DateTime.Now
        };
        _ = Task.Run(async () => { try { await SqliteService.Instance.SaveSessionAsync(session); } catch { } });
    }

    /// <summary>Reconstruct Core ChatMessage list from DB message records</summary>
    private static List<ChatMessage> ReconstructMessages(List<MessageRecord> records, string? systemPrompt)
    {
        var messages = new List<ChatMessage>();
        messages.Add(new ChatMessage("system",
            systemPrompt ?? SystemPromptManager.Instance.GetCurrentSystemPrompt()));

        foreach (var record in records.Where(r => r.AgentDepth == 0))
        {
            switch (record.MessageType)
            {
                case "user":
                    if (!string.IsNullOrEmpty(record.RequestContent))
                        messages.Add(new ChatMessage("user", record.RequestContent));
                    break;

                case "assistant":
                    if (!string.IsNullOrEmpty(record.ResponseContent))
                    {
                        var msg = new ChatMessage("assistant", record.ResponseContent);
                        messages.Add(msg);
                    }
                    break;

                case "tool_call":
                    // Reconstruct the assistant+tool message pair so the LLM receives
                    // the full tool-use context, preserving the original token count.
                    if (!string.IsNullOrEmpty(record.ToolName))
                    {
                        var callId = $"restored_{record.ToolName}_{messages.Count}";
                        // Assistant message that requested this tool call
                        messages.Add(new ChatMessage
                        {
                            Role = "assistant",
                            Content = null,
                            ToolCalls = new List<ToolCall>
                            {
                                new ToolCall
                                {
                                    Id = callId,
                                    Type = "function",
                                    Function = new FunctionCall
                                    {
                                        Name = record.ToolName,
                                        Arguments = record.ToolArgsJson ?? "{}"
                                    }
                                }
                            }
                        });
                        // Tool result message — apply same compression as the live agent loop
                        // to prevent context overflow on session restore (DB stores full results).
                        var compressedResult = AgentLoop.CompressToolResult(record.ToolName, record.ToolResultJson ?? "");
                        messages.Add(new ChatMessage(
                            role: "tool",
                            content: compressedResult,
                            name: record.ToolName,
                            toolCallId: callId
                        ));
                    }
                    break;
            }
        }

        return messages;
    }

    /// <summary>Rebuild UI ChatMessageViewModels from DB records</summary>
    private static void RestoreUiMessages(ChatViewModel chat, List<MessageRecord> records)
    {
        foreach (var record in records.Where(r => r.AgentDepth == 0))
        {
            var timestamp = record.StartedAt.ToString("HH:mm:ss");

            switch (record.MessageType)
            {
                case "user":
                    if (!string.IsNullOrEmpty(record.RequestContent))
                    {
                        chat.Messages.Add(new ChatMessageViewModel
                        {
                            Role = "user",
                            Content = record.RequestContent,
                            Timestamp = timestamp
                        });
                    }
                    break;

                case "assistant":
                    if (!string.IsNullOrEmpty(record.ResponseContent))
                    {
                        chat.Messages.Add(new ChatMessageViewModel
                        {
                            Role = "assistant",
                            Content = record.ResponseContent,
                            Timestamp = timestamp
                        });
                    }
                    break;

                case "tool_call":
                    chat.Messages.Add(new ChatMessageViewModel
                    {
                        Role = "tool",
                        Content = "",
                        Timestamp = timestamp,
                        ToolName = record.ToolName,
                        ToolArgs = record.ToolArgsJson,
                        ToolResult = record.ToolResultJson
                    });
                    break;
            }
        }
    }
}

public record AgentEntry(
    string Id,
    string Name,
    ChatViewModel Chat,
    IAgentService Agent
);
