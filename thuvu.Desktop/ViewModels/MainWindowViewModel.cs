using Avalonia;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Mvvm.Controls;
using thuvu.Desktop.Services;
using thuvu.Models;

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

    private readonly DockFactory _factory;
    private readonly DesktopAgentService _agentService;

    /// <summary>Set by MainWindow to show file picker dialog</summary>
    public Func<Task<string?>>? ShowOpenFileDialog { get; set; }

    /// <summary>Set by MainWindow to show settings dialog</summary>
    public Func<Task>? ShowSettingsDialog { get; set; }

    public MainWindowViewModel()
    {
        _factory = new DockFactory();
        var layout = _factory.CreateLayout();
        _factory.InitLayout(layout);
        DockLayout = layout;

        _agentService = new DesktopAgentService();
        ModelName = _agentService.GetModelName();
        StatusText = $"Connected to {_agentService.GetHostUrl()}";

        // Wire agent service to chat
        var chat = FindDockable<ChatViewModel>(layout);
        chat?.SetAgentService(_agentService);

        // Wire agent service to terminal
        _agentService.OnToolComplete += (name, args, result, elapsed) =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                var terminal = FindDockable<TerminalViewModel>(DockLayout!);
                terminal?.AddLine($"🔧 {name} ({elapsed.TotalSeconds:F1}s)");
            });
        };

        // Initialize file tree with project root
        var fileTree = FindDockable<FileTreeViewModel>(layout);
        if (fileTree != null)
        {
            var rootPath = Directory.GetCurrentDirectory();
            fileTree.RootPath = rootPath;
            fileTree.RefreshCommand.Execute(null);
            fileTree.FileOpenRequested += path => OpenFileInEditor(path);
        }

        _agentService.OnUsage += usage =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                TokenUsageText = $"Tokens: {usage.PromptTokens}↑ {usage.CompletionTokens}↓");
        };
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
        _ = editor.LoadFileCommand.ExecuteAsync(null);
        _factory.AddDockable(docDock, editor);
        _factory.SetActiveDockable(editor);
        _factory.SetFocusedDockable(docDock, editor);
        StatusText = $"Opened {Path.GetFileName(filePath)}";
    }

    [RelayCommand]
    private void NewChat()
    {
        _agentService.ClearMessages();
        var chat = DockLayout != null ? FindDockable<ChatViewModel>(DockLayout) : null;
        if (chat != null) chat.Messages.Clear();
        StatusText = "New chat started";
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

        // Reload config in case the user saved changes
        _agentService.ReloadConfig();
        ModelName = _agentService.GetModelName();
        StatusText = $"Connected to {_agentService.GetHostUrl()}";
    }

    [RelayCommand]
    private void Exit()
    {
        Environment.Exit(0);
    }

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

        var chat = FindDockable<ChatViewModel>(layout);
        chat?.SetAgentService(_agentService);

        var fileTree = FindDockable<FileTreeViewModel>(layout);
        if (fileTree != null)
        {
            fileTree.RootPath = Directory.GetCurrentDirectory();
            fileTree.RefreshCommand.Execute(null);
            fileTree.FileOpenRequested += path => OpenFileInEditor(path);
        }

        StatusText = "Layout reset";
    }

    [RelayCommand]
    private void ClearChat()
    {
        NewChat();
    }

    [RelayCommand]
    private void CancelRequest()
    {
        _agentService.CancelRequest();
        StatusText = "Request cancelled";
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
