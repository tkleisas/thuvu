using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using System.Text.Json;
using thuvu.Desktop.Models;
using thuvu.Desktop.ViewModels;

namespace thuvu.Desktop.Views;

public partial class MainWindow : Window
{
    // Tracks last known position/size while in Normal state so we can persist restore bounds
    private PixelPoint _normalPosition;
    private double _normalWidth = 1200;
    private double _normalHeight = 800;

    public MainWindow()
    {
        InitializeComponent();
        KeyDown += OnKeyDown;
        DataContextChanged += OnDataContextChanged;
        Closing += OnWindowClosing;
        Loaded += OnWindowLoaded;

        PositionChanged += (_, e) =>
        {
            if (WindowState == WindowState.Normal)
                _normalPosition = e.Point;
        };
        SizeChanged += (_, e) =>
        {
            if (WindowState == WindowState.Normal)
            {
                _normalWidth = e.NewSize.Width;
                _normalHeight = e.NewSize.Height;
            }
        };
    }

    private void OnWindowLoaded(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        // Seed normal-state fields with current values after the window is shown
        _normalPosition = Position;
        _normalWidth = ClientSize.Width;
        _normalHeight = ClientSize.Height;

        EnsureWindowOnScreen();

        // Restore saved dock proportions after visual tree is fully built
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

    /// <summary>
    /// If the window is not meaningfully visible on any screen (e.g. disconnected monitor),
    /// move it to fit inside the primary screen.
    /// </summary>
    private void EnsureWindowOnScreen()
    {
        var scaling = RenderScaling;
        var winRect = new PixelRect(
            Position.X, Position.Y,
            (int)(ClientSize.Width * scaling),
            (int)(ClientSize.Height * scaling));

        bool isVisible = Screens.All.Any(s =>
        {
            var overlap = s.WorkingArea.Intersect(winRect);
            return overlap.Width >= 100 && overlap.Height >= 100;
        });

        if (isVisible) return;

        var screen = Screens.Primary ?? Screens.All.FirstOrDefault();
        if (screen == null) return;

        var wa = screen.WorkingArea;
        var w = Math.Min(winRect.Width, wa.Width);
        var h = Math.Min(winRect.Height, wa.Height);
        Position = new PixelPoint(
            wa.X + (wa.Width - w) / 2,
            wa.Y + (wa.Height - h) / 2);
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

            // Save window placement using last known normal-state bounds
            var screen = Screens.ScreenFromWindow(this);
            vm.SaveWindowPlacement(new WindowPlacement
            {
                X = _normalPosition.X,
                Y = _normalPosition.Y,
                Width = _normalWidth,
                Height = _normalHeight,
                // Never save Minimized — restore as Normal in that case
                State = WindowState == WindowState.Minimized ? WindowState.Normal : WindowState,
                ScreenX = screen?.Bounds.X ?? 0,
                ScreenY = screen?.Bounds.Y ?? 0,
                ScreenWidth = screen?.Bounds.Width ?? 0,
                ScreenHeight = screen?.Bounds.Height ?? 0,
            });
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
