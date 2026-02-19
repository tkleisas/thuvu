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
            CanClose = true
        };
        chat.SetAgentService(agent);

        // Track processing state changes
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
    }

    /// <summary>Reload config on all agents (after settings change)</summary>
    public void ReloadAll()
    {
        foreach (var entry in _agents.Values)
            entry.Agent.ReloadConfig();
    }
}

public record AgentEntry(
    string Id,
    string Name,
    ChatViewModel Chat,
    DesktopAgentService Agent
);
