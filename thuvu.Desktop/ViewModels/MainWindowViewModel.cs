using Avalonia;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Mvvm.Controls;
using System.Text.Json;
using thuvu.Desktop.Models;
using thuvu.Desktop.Services;
using thuvu.Models;
using thuvu.Tools;

namespace thuvu.Desktop.ViewModels;

/// <summary>
/// Main window ViewModel managing the dock layout and global commands
/// </summary>
public partial class MainWindowViewModel : ObservableObject
{
    [ObservableProperty] private IDock? _dockLayout;
    [ObservableProperty] private string _statusText = "Ready";
    [ObservableProperty] private string _modelName = "No model configured";
    [ObservableProperty] private string _tokenUsageText = "";
    [ObservableProperty] private bool _isDarkTheme = true;
    [ObservableProperty] private string _windowTitle = "T.H.U.V.U.";

    private readonly DockFactory _factory;
    private readonly AgentRegistry _registry;
    private ProjectConfig _project;
    private AgentsPanelViewModel? _agentsPanel;

    /// <summary>Current project config (used by settings dialog)</summary>
    public ProjectConfig Project => _project;

    /// <summary>Set by MainWindow to show file picker dialog</summary>
    public Func<Task<string?>>? ShowOpenFileDialog { get; set; }

    /// <summary>Set by MainWindow to show settings dialog</summary>
    public Func<Task>? ShowSettingsDialog { get; set; }

    public MainWindowViewModel() : this(new ProjectConfig
    {
        Name = "Default",
        WorkDirectory = ".",
        FilePath = Path.Combine(Directory.GetCurrentDirectory(), ".thuvu")
    })
    { }

    public MainWindowViewModel(ProjectConfig project)
    {
        _project = project;
        _registry = AgentRegistry.Instance;
        _registry.WorkDirectory = _project.ResolvedWorkDirectory;

        // Initialize SQLite for session persistence and code indexing
        var dbDir = Path.Combine(_project.ProjectDirectory, ".db");
        SqliteConfig.Instance.DatabasePath = Path.Combine(dbDir, "thuvu.db");
        SqliteConfig.Instance.Enabled = true;
        try { SqliteService.Instance.InitializeAsync().GetAwaiter().GetResult(); }
        catch (Exception ex) { AgentLogger.LogError("SQLite init failed: {Error}", ex.Message); }

        _factory = new DockFactory();
        var layout = _factory.CreateLayout();
        _factory.InitLayout(layout);
        ApplySavedProportions(layout);
        DockLayout = layout;

        // Remove the placeholder chat from DockFactory
        var docDock = FindDocumentDock(layout);
        if (docDock != null)
        {
            var placeholder = docDock.VisibleDockables?.OfType<ChatViewModel>().FirstOrDefault();
            if (placeholder != null)
                _factory.RemoveDockable(placeholder, false);
        }

        // Try to restore saved sessions from SQLite
        bool restored = false;
        DesktopAgentService? firstAgent = null;
        ChatViewModel? activeChat = null;

        try
        {
            if (SqliteService.Instance != null)
            {
                // Use a dedicated agent_id prefix to find Desktop sessions
                var sessions = SqliteService.Instance.GetSessionsByAgentIdAsync("desktop")
                    .GetAwaiter().GetResult();

                foreach (var session in sessions)
                {
                    if (session.MessageCount == 0) continue;
                    var messages = SqliteService.Instance.GetSessionMessagesAsync(session.SessionId)
                        .GetAwaiter().GetResult();
                    if (messages.Count == 0) continue;

                    var (chatVm, agent) = _registry.RestoreAgentFromDb(session, messages);
                    WireAgentToStatusBar(agent);
                    WireChatOrchestration(chatVm);
                    if (docDock != null) _factory.AddDockable(docDock, chatVm);

                    firstAgent ??= agent;
                    // Use metadata to track which tab was active
                    if (session.MetadataJson?.Contains("\"active\":true") == true)
                        activeChat = chatVm;

                    restored = true;
                }
            }
        }
        catch (Exception ex)
        {
            AgentLogger.LogError("Failed to restore sessions: {Error}", ex.Message);
        }

        // If nothing was restored, create a fresh first chat
        if (!restored)
        {
            var (chatVm, agent) = _registry.CreateAgent("Chat 1");
            firstAgent = agent;
            activeChat = chatVm;
            WireChatOrchestration(chatVm);
            if (docDock != null) _factory.AddDockable(docDock, chatVm);
        }

        // Activate the correct tab
        if (docDock != null && activeChat != null)
        {
            _factory.SetActiveDockable(activeChat);
        }
        else if (docDock != null)
        {
            var first = docDock.VisibleDockables?.OfType<ChatViewModel>().FirstOrDefault();
            if (first != null) _factory.SetActiveDockable(first);
        }

        ModelName = firstAgent?.GetModelName() ?? "No model configured";
        StatusText = $"{project.Name} — {firstAgent?.GetHostUrl() ?? "Ready"}";
        WindowTitle = $"T.H.U.V.U. — {project.Name}";

        // Status bar tracks whichever agent is active
        _registry.OnAgentStateChanged += (id, processing) =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (!processing)
                    StatusText = $"{project.Name} — Ready";
                _agentsPanel?.UpdateStatus(id, processing ? "Processing" : "Idle");
            });
        };

        if (firstAgent != null)
            WireAgentToStatusBar(firstAgent);

        InitializeFileTree(layout);
        InitializeAgentsPanel(layout);

        // Register all agents in the panel
        foreach (var entry in _registry.Agents.Values)
            _agentsPanel?.AddAgent(entry.Id, entry.Name);

        // Register create_agent tool handler so agents can spawn new tabs
        CreateAgentToolImpl.Handler = async (request) =>
        {
            var tcs = new TaskCompletionSource<CreateAgentResult>();
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    var (chatVm, agent) = _registry.CreateAgent(request.Name);
                    WireAgentToStatusBar(agent);
                    WireChatOrchestration(chatVm);

                    var dd = FindDocumentDock(DockLayout!);
                    if (dd != null)
                    {
                        _factory.AddDockable(dd, chatVm);
                        _factory.SetActiveDockable(chatVm);
                    }
                    _agentsPanel?.AddAgent(chatVm.Id!, request.Name ?? chatVm.Title ?? "Agent");

                    // Configure model if specified
                    if (!string.IsNullOrEmpty(request.Model))
                    {
                        agent.SetModel(request.Model);
                        chatVm.SelectModelById(request.Model);
                    }

                    // Configure prompt template if specified
                    if (!string.IsNullOrEmpty(request.PromptTemplate))
                    {
                        chatVm.SelectPromptById(request.PromptTemplate);
                    }

                    // Fire-and-forget: send the prompt to the new agent
                    _ = agent.SendMessageAsync(request.Prompt);

                    tcs.SetResult(new CreateAgentResult
                    {
                        AgentId = chatVm.Id!,
                        WorkDirectory = _registry.WorkDirectory
                    });
                }
                catch (Exception ex) { tcs.SetException(ex); }
            });
            return await tcs.Task.ConfigureAwait(false);
        };

        // Set terminal working directory to project root
        var terminal = FindDockable<TerminalViewModel>(layout);
        if (terminal != null)
            terminal.WorkingDirectory = _project.ResolvedWorkDirectory;
    }

    private void InitializeFileTree(IDock layout)
    {
        var fileTree = FindDockable<FileTreeViewModel>(layout);
        if (fileTree == null) return;

        var rootPath = _project.ResolvedWorkDirectory;
        if (!Directory.Exists(rootPath))
            Directory.CreateDirectory(rootPath);

        fileTree.RootPath = rootPath;
        fileTree.ExcludePatterns = _project.ExcludePatterns;
        fileTree.RefreshCommand.Execute(null);
        fileTree.FileOpenRequested += path => OpenFileInEditor(path);
    }

    private void InitializeAgentsPanel(IDock layout)
    {
        _agentsPanel = FindDockable<AgentsPanelViewModel>(layout);
        System.Diagnostics.Debug.WriteLine($"[AGENTS] InitializeAgentsPanel: found={_agentsPanel != null}");
        if (_agentsPanel == null) return;

        _agentsPanel.ShowAgentRequested += ShowOrRestoreAgent;
        _agentsPanel.TerminateAgentRequested += TerminateAgentChat;
    }

    /// <summary>Show an agent's chat tab, re-adding it if it was closed</summary>
    private void ShowOrRestoreAgent(string agentId)
    {
        if (DockLayout == null) return;
        var docDock = FindDocumentDock(DockLayout);
        if (docDock == null) return;

        // Check if chat tab is already visible
        var existing = docDock.VisibleDockables?.OfType<ChatViewModel>()
            .FirstOrDefault(c => c.Id == agentId);
        if (existing != null)
        {
            _factory.SetActiveDockable(existing);
            _factory.SetFocusedDockable(docDock, existing);
            return;
        }

        // Re-add the chat tab from registry
        var entry = _registry.Agents.FirstOrDefault(kv => kv.Key == agentId);
        if (entry.Value != null)
        {
            _factory.AddDockable(docDock, entry.Value.Chat);
            _factory.SetActiveDockable(entry.Value.Chat);
            _factory.SetFocusedDockable(docDock, entry.Value.Chat);
            StatusText = $"Restored {entry.Value.Chat.Title}";
        }
    }

    /// <summary>Close the chat tab for a terminated agent</summary>
    private void TerminateAgentChat(string agentId)
    {
        if (DockLayout == null) return;
        var docDock = FindDocumentDock(DockLayout);
        if (docDock == null) return;

        var chatTab = docDock.VisibleDockables?.OfType<ChatViewModel>()
            .FirstOrDefault(c => c.Id == agentId);
        if (chatTab != null)
            _factory.CloseDockable(chatTab);

        StatusText = $"Agent {agentId} terminated";
    }

    private void WireAgentToStatusBar(DesktopAgentService agent)
    {
        agent.OnToolComplete += (name, args, result, elapsed) =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                StatusText = $"🔧 {name} completed ({elapsed.TotalSeconds:F1}s)");
        };
        agent.OnUsage += usage =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                TokenUsageText = $"Tokens: {usage.PromptTokens}↑ {usage.CompletionTokens}↓";
                if (!string.IsNullOrEmpty(agent.SessionId))
                {
                    var max = ResolveMaxContext(agent, usage);
                    var pct = Math.Min(100, (int)(usage.PromptTokens * 100.0 / max));
                    _agentsPanel?.UpdateContextInfo(agent.SessionId,
                        $"ctx: {usage.PromptTokens:N0}/{max:N0} ({pct}%)");
                }
            });
        };
    }

    private static int ResolveMaxContext(DesktopAgentService agent, thuvu.Models.Usage usage)
    {
        var max = usage.MaxContextLength ?? 0;
        if (max <= 0)
        {
            var entry = thuvu.Models.ModelRegistry.Instance.GetModel(agent.EffectiveModel);
            if (entry != null && entry.MaxContextLength > 0) max = entry.MaxContextLength;
        }
        if (max <= 0 && thuvu.Models.AgentConfig.Config.MaxContextLength > 0)
            max = thuvu.Models.AgentConfig.Config.MaxContextLength;
        if (max <= 0) max = 32768;
        return max;
    }

    /// <summary>Wire orchestration events so sub-agents appear in the Agents panel</summary>
    private void WireChatOrchestration(ChatViewModel chat)
    {
        chat.OrchestrationAgentChanged += (agentId, status) =>
        {
            if (status == "Remove")
            {
                _agentsPanel?.RemoveAgent(agentId);
            }
            else
            {
                // Add if not already present
                var existing = _agentsPanel?.Agents.FirstOrDefault(a => a.AgentId == agentId);
                if (existing == null)
                    _agentsPanel?.AddAgent(agentId, agentId);

                _agentsPanel?.UpdateStatus(agentId, status);
            }
        };
    }

    /// <summary>Get the active chat's agent service</summary>
    private DesktopAgentService? GetActiveAgent()
    {
        if (DockLayout == null) return null;
        var docDock = FindDocumentDock(DockLayout);
        var activeChat = docDock?.ActiveDockable as ChatViewModel;
        return activeChat?.AgentService;
    }

    private T? FindDockable<T>(IDock dock) where T : class
    {
        if (dock is T found) return found;
        if (dock is IDock container && container.VisibleDockables != null)
        {
            foreach (var child in container.VisibleDockables)
            {
                if (child is T match) return match;
                if (child is IDock childDock)
                {
                    var result = FindDockable<T>(childDock);
                    if (result != null) return result;
                }
            }
        }
        return null;
    }

    private IDocumentDock? FindDocumentDock(IDock dock)
    {
        if (dock is IDocumentDock dd) return dd;
        if (dock.VisibleDockables != null)
        {
            foreach (var child in dock.VisibleDockables)
            {
                if (child is IDock childDock)
                {
                    var result = FindDocumentDock(childDock);
                    if (result != null) return result;
                }
            }
        }
        return null;
    }

    public void OpenFileInEditor(string filePath)
    {
        if (DockLayout == null) return;
        var docDock = FindDocumentDock(DockLayout);
        if (docDock == null) return;

        // Check if file is already open
        if (docDock.VisibleDockables != null)
        {
            var existing = docDock.VisibleDockables.OfType<EditorViewModel>()
                .FirstOrDefault(e => e.FilePath.Equals(filePath, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                _factory.SetActiveDockable(existing);
                _factory.SetFocusedDockable(docDock, existing);
                return;
            }
        }

        var editor = new EditorViewModel(filePath);
        _factory.AddDockable(docDock, editor);
        _factory.SetActiveDockable(editor);
        _factory.SetFocusedDockable(docDock, editor);
        StatusText = $"Opened {Path.GetFileName(filePath)}";
    }

    [RelayCommand]
    private void NewChat()
    {
        if (DockLayout == null) return;
        var docDock = FindDocumentDock(DockLayout);
        if (docDock == null) return;

        var (chatVm, agent) = _registry.CreateAgent();
        WireAgentToStatusBar(agent);
        WireChatOrchestration(chatVm);
        _factory.AddDockable(docDock, chatVm);
        _factory.SetActiveDockable(chatVm);
        _factory.SetFocusedDockable(docDock, chatVm);

        // Register in agents panel
        var entry = _registry.Agents.FirstOrDefault(kv => kv.Value.Chat == chatVm);
        _agentsPanel?.AddAgent(chatVm.Id!, entry.Value?.Name ?? chatVm.Title ?? "Chat");

        StatusText = $"New chat: {chatVm.Title}";
    }

    [RelayCommand]
    private async Task OpenFile()
    {
        if (ShowOpenFileDialog != null)
        {
            var path = await ShowOpenFileDialog();
            if (!string.IsNullOrEmpty(path))
                OpenFileInEditor(path);
        }
    }

    [RelayCommand]
    private async Task ShowSettings()
    {
        if (ShowSettingsDialog != null)
            await ShowSettingsDialog();

        // Reload config on all agents
        _registry.ReloadAll();
        var activeAgent = GetActiveAgent();
        if (activeAgent != null)
        {
            ModelName = activeAgent.GetModelName();
            StatusText = $"{_project.Name} — {activeAgent.GetHostUrl()}";
        }
    }

    [RelayCommand]
    private void Exit()
    {
        SaveAllSessions();
        Environment.Exit(0);
    }

    /// <summary>Save all agent sessions and the session index to disk</summary>
    public void SaveAllSessions()
    {
        var activeId = (DockLayout != null ? FindDocumentDock(DockLayout)?.ActiveDockable as ChatViewModel : null)?.Id;
        _registry.SaveAllSessions(activeId);
        SaveDockLayout();
    }

    #region Dock Layout Persistence

    private string GetLayoutPath() =>
        Path.Combine(_project.ProjectDirectory, ".db", "layout.json");

    /// <summary>Save dock proportions to JSON</summary>
    private void SaveDockLayout()
    {
        if (DockLayout == null) return;
        try
        {
            var layoutPath = GetLayoutPath();
            Directory.CreateDirectory(Path.GetDirectoryName(layoutPath)!);
            var state = new Dictionary<string, double>();
            CollectProportions(DockLayout, state);
            var json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(layoutPath, json);
        }
        catch (Exception ex)
        {
            AgentLogger.LogError("Failed to save dock layout: {Error}", ex.Message);
        }
    }

    /// <summary>Apply saved proportions to the dock model before visual tree creation</summary>
    private void ApplySavedProportions(IDockable layout)
    {
        var path = GetLayoutPath();
        if (!File.Exists(path)) return;
        try
        {
            var json = File.ReadAllText(path);
            var state = JsonSerializer.Deserialize<Dictionary<string, double>>(json);
            if (state != null)
                ApplyProportions(layout, state);
        }
        catch (Exception ex)
        {
            AgentLogger.LogError("Failed to restore dock layout: {Error}", ex.Message);
        }
    }

    private static void CollectProportions(IDockable dockable, Dictionary<string, double> state)
    {
        if (dockable.Id != null && double.IsFinite(dockable.Proportion))
            state[dockable.Id] = dockable.Proportion;

        if (dockable is IDock dock && dock.VisibleDockables != null)
            foreach (var child in dock.VisibleDockables)
                CollectProportions(child, state);
    }

    private static void ApplyProportions(IDockable dockable, Dictionary<string, double> state)
    {
        if (dockable.Id != null && state.TryGetValue(dockable.Id, out var val))
            dockable.Proportion = val;

        if (dockable is IDock dock && dock.VisibleDockables != null)
            foreach (var child in dock.VisibleDockables)
                ApplyProportions(child, state);
    }

    #endregion

    [RelayCommand]
    private void ToggleFileTree()
    {
        if (DockLayout == null) return;
        var fileTree = FindDockable<FileTreeViewModel>(DockLayout);
        if (fileTree != null)
        {
            _factory.SetActiveDockable(fileTree);
            StatusText = "File Explorer focused";
        }
    }

    [RelayCommand]
    private void ToggleTerminal()
    {
        if (DockLayout == null) return;
        var terminal = FindDockable<TerminalViewModel>(DockLayout);
        if (terminal != null)
        {
            _factory.SetActiveDockable(terminal);
            StatusText = "Terminal focused";
        }
    }

    [RelayCommand]
    private void ResetLayout()
    {
        var layout = _factory.CreateLayout();
        _factory.InitLayout(layout);
        DockLayout = layout;

        // Create a fresh chat in the new layout
        var docDock = FindDocumentDock(layout);
        if (docDock != null)
        {
            var placeholder = docDock.VisibleDockables?.OfType<ChatViewModel>().FirstOrDefault();
            if (placeholder != null)
                _factory.RemoveDockable(placeholder, false);

            var (chatVm, agent) = _registry.CreateAgent();
            WireAgentToStatusBar(agent);
            WireChatOrchestration(chatVm);
            _factory.AddDockable(docDock, chatVm);
            _factory.SetActiveDockable(chatVm);
        }

        InitializeFileTree(layout);
        InitializeAgentsPanel(layout);

        // Register the new chat in agents panel
        if (docDock != null)
        {
            var chatVm2 = docDock.VisibleDockables?.OfType<ChatViewModel>().FirstOrDefault();
            if (chatVm2 != null)
            {
                var entry = _registry.Agents.FirstOrDefault(kv => kv.Value.Chat == chatVm2);
                _agentsPanel?.AddAgent(chatVm2.Id!, entry.Value?.Name ?? "Chat");
            }
        }

        var terminal = FindDockable<TerminalViewModel>(layout);
        if (terminal != null)
            terminal.WorkingDirectory = _project.ResolvedWorkDirectory;

        StatusText = "Layout reset";
    }

    [RelayCommand]
    private void ClearChat()
    {
        // Clear the active chat's messages and agent history
        if (DockLayout == null) return;
        var docDock = FindDocumentDock(DockLayout);
        var activeChat = docDock?.ActiveDockable as ChatViewModel;
        if (activeChat != null)
        {
            activeChat.AgentService?.ClearMessages();
            activeChat.Messages.Clear();
            StatusText = "Chat cleared";
        }
    }

    [RelayCommand]
    private void CancelRequest()
    {
        GetActiveAgent()?.CancelRequest();
        StatusText = "Request cancelled";
    }

    private int _terminalCounter = 1;

    [RelayCommand]
    private void NewTerminal()
    {
        if (DockLayout == null) return;

        // Find the terminal ToolDock
        var terminalDock = FindToolDock(DockLayout, "TerminalDock");
        if (terminalDock == null) return;

        _terminalCounter++;
        var terminal = TerminalViewModel.CreateUserTerminal(_terminalCounter);
        terminal.WorkingDirectory = _project.ResolvedWorkDirectory;
        _factory.AddDockable(terminalDock, terminal);
        _factory.SetActiveDockable(terminal);
        StatusText = $"New terminal created";
    }

    private IToolDock? FindToolDock(IDock dock, string id)
    {
        if (dock is IToolDock td && td.Id == id) return td;
        if (dock.VisibleDockables != null)
        {
            foreach (var child in dock.VisibleDockables)
            {
                if (child is IDock childDock)
                {
                    var result = FindToolDock(childDock, id);
                    if (result != null) return result;
                }
            }
        }
        return null;
    }

    [RelayCommand]
    private void ToggleTheme()
    {
        IsDarkTheme = !IsDarkTheme;
        if (Application.Current != null)
        {
            Application.Current.RequestedThemeVariant = IsDarkTheme
                ? ThemeVariant.Dark
                : ThemeVariant.Light;
        }
        StatusText = IsDarkTheme ? "Dark theme" : "Light theme";
    }

    [RelayCommand]
    private void ShowAbout()
    {
        StatusText = "T.H.U.V.U. — Tool for Heuristic Universal Versatile Usage v1.0";
    }
}
