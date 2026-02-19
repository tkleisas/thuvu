using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;

namespace thuvu.Desktop.ViewModels;

/// <summary>
/// ViewModel for an interactive terminal panel using Iciclecreek.Avalonia.Terminal
/// </summary>
public partial class TerminalViewModel : ToolViewModel
{
    [ObservableProperty] private string _shellPath = "";
    [ObservableProperty] private string _workingDirectory = "";
    [ObservableProperty] private bool _isReadOnly;

    public TerminalViewModel()
    {
        Id = "Terminal";
        Title = "⚡ Terminal";
        CanClose = true;

        // Detect available shell
        ShellPath = OperatingSystem.IsWindows() ? "powershell.exe" : "/bin/bash";
    }

    /// <summary>Create a user-interactive terminal</summary>
    public static TerminalViewModel CreateUserTerminal(int index = 1)
    {
        var shell = OperatingSystem.IsWindows() ? "powershell.exe" : "/bin/bash";
        return new TerminalViewModel
        {
            Id = $"Terminal_{index}",
            Title = $"⚡ Terminal {index}",
            ShellPath = shell,
            IsReadOnly = false
        };
    }

    /// <summary>Create a read-only terminal for agent command output</summary>
    public static TerminalViewModel CreateAgentTerminal(string agentName = "Agent")
    {
        var shell = OperatingSystem.IsWindows() ? "powershell.exe" : "/bin/bash";
        return new TerminalViewModel
        {
            Id = $"AgentTerminal_{agentName}",
            Title = $"🤖 {agentName}",
            ShellPath = shell,
            IsReadOnly = true
        };
    }
}

// Keep legacy types for backward compatibility with tool output display
public class TerminalLine
{
    public string Text { get; set; } = string.Empty;
    public string Timestamp { get; set; } = string.Empty;
    public TerminalLineType Type { get; set; }
}

public enum TerminalLineType
{
    Output,
    Command,
    Error,
    Info
}
