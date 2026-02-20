using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using System.Text.Json;
using thuvu.Desktop.ViewModels;

namespace thuvu.Desktop.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        KeyDown += OnKeyDown;
        DataContextChanged += OnDataContextChanged;
        Closing += OnWindowClosing;
        Loaded += OnWindowLoaded;
    }

    private void OnWindowLoaded(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        // Restore saved proportions after visual tree is fully built
        if (DataContext is MainWindowViewModel vm)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                var saved = vm.LoadLayoutState();
                if (saved != null)
                    ApplyProportionsToVisualTree(MainDockControl, saved);
            }, Avalonia.Threading.DispatcherPriority.Loaded);
        }
    }

    private void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
        {
            // Collect proportions from visual tree before saving
            var state = new Dictionary<string, double>();
            CollectProportionsFromVisualTree(MainDockControl, state);
            vm.SaveLayoutState(state);
            vm.SaveAllSessions();
        }
    }

    /// <summary>Walk the visual tree and collect ProportionalStackPanel.Proportion attached property values</summary>
    private static void CollectProportionsFromVisualTree(Visual parent, Dictionary<string, double> state)
    {
        foreach (var descendant in parent.GetVisualDescendants())
        {
            if (descendant is ContentPresenter cp && cp.DataContext is Dock.Model.Core.IDockable dockable)
            {
                var proportion = ProportionalStackPanel.GetProportion(cp);
                if (dockable.Id != null && double.IsFinite(proportion))
                    state[dockable.Id] = proportion;
            }
        }
    }

    /// <summary>Walk the visual tree and set ProportionalStackPanel.Proportion attached property values</summary>
    private static void ApplyProportionsToVisualTree(Visual parent, Dictionary<string, double> state)
    {
        foreach (var descendant in parent.GetVisualDescendants())
        {
            if (descendant is ContentPresenter cp && cp.DataContext is Dock.Model.Core.IDockable dockable)
            {
                if (dockable.Id != null && state.TryGetValue(dockable.Id, out var proportion))
                    ProportionalStackPanel.SetProportion(cp, proportion);
            }

            if (descendant is Avalonia.Layout.Layoutable layoutable)
                layoutable.InvalidateArrange();
        }
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;

        vm.ShowOpenFileDialog = async () =>
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Open File",
                AllowMultiple = false
            });
            if (files.Count > 0)
                return files[0].Path.LocalPath;
            return null;
        };

        vm.ShowSettingsDialog = async () =>
        {
            var settings = new SettingsWindow(vm.Project);
            await settings.ShowDialog(this);
        };
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;

        if (e.KeyModifiers == KeyModifiers.Control)
        {
            switch (e.Key)
            {
                case Key.T:
                    vm.NewChatCommand.Execute(null);
                    e.Handled = true;
                    break;
                case Key.N:
                    vm.NewChatCommand.Execute(null);
                    e.Handled = true;
                    break;
                case Key.O:
                    vm.OpenFileCommand.Execute(null);
                    e.Handled = true;
                    break;
            }
        }
        else if (e.Key == Key.Escape)
        {
            vm.CancelRequestCommand.Execute(null);
            e.Handled = true;
        }
    }
}
