using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;

namespace thuvu.Desktop.ViewModels;

/// <summary>
/// ViewModel for the terminal output dockable panel
/// </summary>
public partial class TerminalViewModel : ToolViewModel
{
    [ObservableProperty] private bool _isRunning;

    public ObservableCollection<TerminalLine> Lines { get; } = new();

    public TerminalViewModel()
    {
        Id = "Terminal";
        Title = "⚡ Terminal";
        CanClose = true;
    }

    public void AddLine(string text, TerminalLineType type = TerminalLineType.Output)
    {
        Lines.Add(new TerminalLine
        {
            Text = text,
            Type = type,
            Timestamp = DateTime.Now.ToString("HH:mm:ss")
        });
    }

    public void AddCommand(string command) => AddLine($"$ {command}", TerminalLineType.Command);
    public void AddError(string error) => AddLine(error, TerminalLineType.Error);
    public void AddInfo(string info) => AddLine(info, TerminalLineType.Info);

    [RelayCommand]
    private void Clear() => Lines.Clear();
}

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
