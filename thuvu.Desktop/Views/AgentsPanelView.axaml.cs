using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using thuvu.Desktop.ViewModels;

namespace thuvu.Desktop.Views;

public partial class AgentsPanelView : UserControl
{
    public AgentsPanelView()
    {
        InitializeComponent();
    }

    private AgentsPanelViewModel? Vm => DataContext as AgentsPanelViewModel;

    private void OnAgentDoubleTapped(object? sender, TappedEventArgs e)
    {
        Vm?.ShowAgentCommand.Execute(null);
    }

    private void OnShowChat(object? sender, RoutedEventArgs e) => Vm?.ShowAgentCommand.Execute(null);
    private void OnRename(object? sender, RoutedEventArgs e) => Vm?.StartRenameCommand.Execute(null);
    private void OnCommitRename(object? sender, RoutedEventArgs e) => Vm?.CommitRenameCommand.Execute(null);
    private void OnCancelRename(object? sender, RoutedEventArgs e) => Vm?.CancelRenameCommand.Execute(null);
    private void OnStop(object? sender, RoutedEventArgs e) => Vm?.StopAgentCommand.Execute(null);
    private void OnTerminate(object? sender, RoutedEventArgs e) => Vm?.TerminateAgentCommand.Execute(null);
}
