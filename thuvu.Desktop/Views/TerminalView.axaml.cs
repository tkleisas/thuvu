using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Iciclecreek.Terminal;
using thuvu.Desktop.Models;
using thuvu.Desktop.ViewModels;

namespace thuvu.Desktop.Views;

public partial class TerminalView : UserControl
{
    public TerminalView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        AttachedToVisualTree += OnAttachedToVisualTree;
    }

    private void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        ApplyAppearance();
        AppearanceService.Instance.PropertyChanged += (_, _) =>
            Avalonia.Threading.Dispatcher.UIThread.Post(ApplyAppearance);
    }

    private void ApplyAppearance()
    {
        if (Terminal == null) return;

        var svc = AppearanceService.Instance;
        if (!string.IsNullOrWhiteSpace(svc.TerminalFontFamily))
            Terminal.FontFamily = new FontFamily(svc.TerminalFontFamily);
        if (svc.TerminalFontSize > 0)
            Terminal.FontSize = svc.TerminalFontSize;
        if (TryParseColor(svc.TerminalForeground, out var fg))
            Terminal.Foreground = new SolidColorBrush(fg);
        if (TryParseColor(svc.TerminalBackground, out var bg))
            Terminal.Background = new SolidColorBrush(bg);
    }

    private static bool TryParseColor(string hex, out Color color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(hex)) return false;
        return Color.TryParse(hex, out color);
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is TerminalViewModel vm && Terminal != null)
        {
            Terminal.Process = vm.ShellPath;
            if (!string.IsNullOrEmpty(vm.WorkingDirectory))
            {
                if (OperatingSystem.IsWindows())
                    Terminal.Args = new List<string> { "-NoExit", "-Command", $"Set-Location '{vm.WorkingDirectory}'" };
                else
                    Terminal.Args = new List<string> { "-c", $"cd \"{vm.WorkingDirectory}\" && exec $SHELL" };
            }
        }
    }
}
