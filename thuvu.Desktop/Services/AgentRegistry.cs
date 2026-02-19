using System.Collections.ObjectModel;
using thuvu.Desktop.ViewModels;

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

    /// <summary>Session store for persistence (set once on startup)</summary>
    public SessionStore? SessionStore { get; set; }

    /// <summary>Create a new agent+chat pair with a unique ID</summary>
    public (ChatViewModel chat, DesktopAgentService agent) CreateAgent(string? name = null)
    {
        _counter++;
        var id = $"Chat_{_counter}";
        name ??= $"Chat {_counter}";

        var agent = new DesktopAgentService { WorkDirectory = WorkDirectory };
        var chat = new ChatViewModel
        {
            Id = id,
            Title = $"💬 {name}",
            CanClose = true,
            SessionName = name
        };
        chat.SetAgentService(agent);
        if (SessionStore != null) chat.SetSessionStore(SessionStore);

        // Track processing state changes
        chat.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ChatViewModel.IsProcessing))
                OnAgentStateChanged?.Invoke(id, chat.IsProcessing);
        };

        _agents[id] = new AgentEntry(id, name, chat, agent);
        return (chat, agent);
    }

    /// <summary>Restore an agent from saved session data</summary>
    public (ChatViewModel chat, DesktopAgentService agent) RestoreAgent(SessionData data)
    {
        // Parse numeric suffix to keep counter ahead of restored IDs
        if (data.Id.StartsWith("Chat_") && int.TryParse(data.Id[5..], out var num))
            _counter = Math.Max(_counter, num);

        var agent = new DesktopAgentService { WorkDirectory = WorkDirectory };
        if (!string.IsNullOrEmpty(data.ModelId))
            agent.SetModel(data.ModelId);
        agent.RestoreMessages(data.Messages);

        var chat = new ChatViewModel
        {
            Id = data.Id,
            Title = $"💬 {data.Name}",
            CanClose = true,
            SessionName = data.Name
        };
        chat.SetAgentService(agent);
        if (SessionStore != null) chat.SetSessionStore(SessionStore);
        chat.RestoreFromSession(data);

        chat.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ChatViewModel.IsProcessing))
                OnAgentStateChanged?.Invoke(data.Id, chat.IsProcessing);
        };

        _agents[data.Id] = new AgentEntry(data.Id, data.Name, chat, agent);
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
        SessionStore?.DeleteSession(chatId);
    }

    /// <summary>Reload config on all agents (after settings change)</summary>
    public void ReloadAll()
    {
        foreach (var entry in _agents.Values)
            entry.Agent.ReloadConfig();
    }

    /// <summary>Build a session index from all active agents</summary>
    public SessionIndex BuildSessionIndex(string? activeId = null)
    {
        var index = new SessionIndex { ActiveSessionId = activeId };
        foreach (var entry in _agents.Values)
        {
            index.Sessions.Add(new SessionSummary
            {
                Id = entry.Id,
                Name = entry.Name,
                ModelId = entry.Agent.ModelOverride,
                UpdatedAt = DateTime.UtcNow,
                MessageCount = entry.Chat.Messages.Count
            });
        }
        return index;
    }

    /// <summary>Save all sessions and index to disk</summary>
    public void SaveAllSessions(string? activeId = null)
    {
        if (SessionStore == null) return;
        var index = BuildSessionIndex(activeId);
        var sessions = _agents.Values.Select(e => e.Chat.CreateSessionData());
        SessionStore.SaveAll(index, sessions);
    }
}

public record AgentEntry(
    string Id,
    string Name,
    ChatViewModel Chat,
    DesktopAgentService Agent
);
