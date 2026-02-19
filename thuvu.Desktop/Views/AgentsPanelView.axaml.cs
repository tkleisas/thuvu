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

    private void OnShowChat(object? sender, RoutedEventArgs e) { SelectFromContext(sender); Vm?.ShowAgentCommand.Execute(null); }
    private void OnRename(object? sender, RoutedEventArgs e) { SelectFromContext(sender); Vm?.StartRenameCommand.Execute(null); }
    private void OnCommitRename(object? sender, RoutedEventArgs e) => Vm?.CommitRenameCommand.Execute(null);
    private void OnCancelRename(object? sender, RoutedEventArgs e) => Vm?.CancelRenameCommand.Execute(null);
    private void OnStop(object? sender, RoutedEventArgs e) { SelectFromContext(sender); Vm?.StopAgentCommand.Execute(null); }
    private void OnTerminate(object? sender, RoutedEventArgs e) { SelectFromContext(sender); Vm?.TerminateAgentCommand.Execute(null); }

    /// <summary>Set SelectedAgent from the right-clicked item's DataContext</summary>
    private void SelectFromContext(object? sender)
    {
        if (Vm == null) return;
        if (sender is MenuItem mi && mi.DataContext is AgentListItem item)
            Vm.SelectedAgent = item;
    }
}
