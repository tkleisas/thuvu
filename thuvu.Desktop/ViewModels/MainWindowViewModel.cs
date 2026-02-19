using Avalonia;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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
            var terminal = FindDockable<TerminalViewModel>(layout);
            terminal?.AddLine($"🔧 {name} ({elapsed.TotalSeconds:F1}s)");
        };

        // Initialize file tree with work directory
        var fileTree = FindDockable<FileTreeViewModel>(layout);
        if (fileTree != null)
        {
            fileTree.RootPath = AgentConfig.GetWorkDirectory();
            fileTree.RefreshCommand.Execute(null);
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
        // TODO: Integrate with Avalonia file dialog to open files in editor
        await Task.CompletedTask;
    }

    [RelayCommand]
    private void ShowSettings()
    {
        StatusText = "Settings (open via menu)";
    }

    [RelayCommand]
    private void Exit()
    {
        Environment.Exit(0);
    }

    [RelayCommand]
    private void ToggleFileTree()
    {
        // TODO: Toggle file tree visibility in dock
    }

    [RelayCommand]
    private void ToggleTerminal()
    {
        // TODO: Toggle terminal visibility in dock
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
            fileTree.RootPath = AgentConfig.GetWorkDirectory();
            fileTree.RefreshCommand.Execute(null);
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
        StatusText = "T.H.U.V.U. — Tool for Heuristic Universal Versatile Usage";
    }
}
