using thuvu.Desktop.ViewModels;
using thuvu.Models;
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
    public (ChatViewModel chat, DesktopAgentService agent) CreateAgent(string? name = null)
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

    /// <summary>Restore an agent from SQLite session + message records</summary>
    public (ChatViewModel chat, DesktopAgentService agent) RestoreAgentFromDb(
        SqSessionData session, List<MessageRecord> messages)
    {
        var id = session.SessionId;
        var name = session.Title ?? id;

        // Parse numeric suffix to keep counter ahead of restored IDs
        if (id.StartsWith("Chat_") && int.TryParse(id[5..], out var num))
            _counter = Math.Max(_counter, num);

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

    /// <summary>Get the agent service for a given chat ID</summary>
    public DesktopAgentService? GetAgent(string chatId)
    {
        return _agents.TryGetValue(chatId, out var entry) ? entry.Agent : null;
    }

    /// <summary>Remove an agent when its chat tab is closed</summary>
    public void RemoveAgent(string chatId)
    {
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
                var session = new SqSessionData
                {
                    SessionId = entry.Id,
                    AgentId = "desktop",
                    Title = entry.Name,
                    ModelId = entry.Agent.ModelOverride,
                    WorkDirectory = WorkDirectory,
                    LastActivityAt = DateTime.Now,
                    MetadataJson = isActive ? "{\"active\":true}" : null
                };
                _ = Task.Run(async () => { try { await SqliteService.Instance.SaveSessionAsync(session); } catch { } });
            }
            catch { }
        }
    }

    private static void SaveSessionToDb(string id, string name, DesktopAgentService agent)
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
                    // Tool calls are embedded in assistant messages by AgentLoop;
                    // they don't need separate reconstruction for the messages list
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
    DesktopAgentService Agent
);
