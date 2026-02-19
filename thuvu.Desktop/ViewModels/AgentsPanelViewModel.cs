using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using thuvu.Desktop.Services;

namespace thuvu.Desktop.ViewModels;

/// <summary>
/// Represents an agent entry in the Agents panel list
/// </summary>
public partial class AgentListItem : ObservableObject
{
    [ObservableProperty] private string _agentId = "";
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _status = "Idle";
    [ObservableProperty] private bool _isRenaming;
    [ObservableProperty] private string _renamingText = "";
    [ObservableProperty] private string _contextInfo = "";

    public string StatusIcon => Status switch
    {
        "Processing" => "⏳",
        "Error" => "⚠️",
        _ => "💬"
    };

    partial void OnStatusChanged(string value) => OnPropertyChanged(nameof(StatusIcon));
}

/// <summary>
/// ViewModel for the Agents panel — shows all active agents with status.
/// Supports rename, stop, terminate, and re-show closed chat tabs.
/// </summary>
public partial class AgentsPanelViewModel : ToolViewModel
{
    public ObservableCollection<AgentListItem> Agents { get; } = new();

    [ObservableProperty] private AgentListItem? _selectedAgent;

    /// <summary>Raised when user wants to show/focus an agent's chat tab</summary>
    public event Action<string>? ShowAgentRequested;

    /// <summary>Raised when user terminates an agent (chat tab should be closed)</summary>
    public event Action<string>? TerminateAgentRequested;

    public AgentsPanelViewModel()
    {
        Id = "AgentsPanel";
        Title = "🤖 Agents";
    }

    /// <summary>Add an agent to the panel</summary>
    public void AddAgent(string id, string name)
    {
        var item = new AgentListItem { AgentId = id, Name = name, Status = "Idle" };
        Agents.Add(item);
    }

    /// <summary>Remove an agent from the panel</summary>
    public void RemoveAgent(string id)
    {
        var item = Agents.FirstOrDefault(a => a.AgentId == id);
        if (item != null) Agents.Remove(item);
    }

    /// <summary>Update agent status</summary>
    public void UpdateStatus(string id, string status)
    {
        var item = Agents.FirstOrDefault(a => a.AgentId == id);
        if (item != null) item.Status = status;
    }

    /// <summary>Update context usage info for an agent</summary>
    public void UpdateContextInfo(string id, string info)
    {
        var item = Agents.FirstOrDefault(a => a.AgentId == id);
        if (item != null) item.ContextInfo = info;
    }

    [RelayCommand]
    private void ShowAgent()
    {
        if (SelectedAgent != null)
            ShowAgentRequested?.Invoke(SelectedAgent.AgentId);
    }

    [RelayCommand]
    private void StartRename()
    {
        if (SelectedAgent != null)
        {
            SelectedAgent.RenamingText = SelectedAgent.Name;
            SelectedAgent.IsRenaming = true;
        }
    }

    [RelayCommand]
    private void CommitRename()
    {
        if (SelectedAgent is { IsRenaming: true })
        {
            var newName = SelectedAgent.RenamingText.Trim();
            if (!string.IsNullOrEmpty(newName))
            {
                SelectedAgent.Name = newName;
                // Update registry and chat tab title
                var entry = AgentRegistry.Instance.Agents
                    .FirstOrDefault(kv => kv.Key == SelectedAgent.AgentId);
                if (entry.Value != null)
                {
                    entry.Value.Chat.Title = $"💬 {newName}";
                }
            }
            SelectedAgent.IsRenaming = false;
        }
    }

    [RelayCommand]
    private void CancelRename()
    {
        if (SelectedAgent != null)
            SelectedAgent.IsRenaming = false;
    }

    [RelayCommand]
    private void StopAgent()
    {
        if (SelectedAgent == null) return;
        var agent = AgentRegistry.Instance.GetAgent(SelectedAgent.AgentId);
        agent?.CancelRequest();
        SelectedAgent.Status = "Idle";
    }

    [RelayCommand]
    private void TerminateAgent()
    {
        if (SelectedAgent == null) return;
        var id = SelectedAgent.AgentId;
        var agent = AgentRegistry.Instance.GetAgent(id);
        agent?.CancelRequest();

        // Remove from panel and registry, close chat tab
        Agents.Remove(SelectedAgent);
        SelectedAgent = null;
        AgentRegistry.Instance.RemoveAgent(id);
        TerminateAgentRequested?.Invoke(id);
    }
}
