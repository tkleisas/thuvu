using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using System.Globalization;
using thuvu.Desktop.Models;
using thuvu.Desktop.ViewModels;

namespace thuvu.Desktop.Views;

public partial class ChatView : UserControl
{
    public static readonly FuncValueConverter<string?, IBrush?> RoleBrushConverter =
        new(role => role switch
        {
            "user" => new SolidColorBrush(Color.FromArgb(30, 0, 120, 255)),
            "assistant" => new SolidColorBrush(Color.FromArgb(20, 255, 255, 255)),
            _ => new SolidColorBrush(Color.FromArgb(15, 255, 165, 0))
        });

    public static readonly FuncValueConverter<string?, bool> IsAssistantConverter =
        new(role => role == "assistant");

    public static readonly FuncValueConverter<string?, bool> IsNotAssistantConverter =
        new(role => role != "assistant");

    public ChatView()
    {
        InitializeComponent();

        // Use tunnel routing to intercept Enter before TextBox consumes it with AcceptsReturn
        var inputBox = this.FindControl<TextBox>("InputBox");
        inputBox?.AddHandler(KeyDownEvent, InputBox_KeyDown, RoutingStrategies.Tunnel);

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
        var svc = AppearanceService.Instance;
        if (!string.IsNullOrWhiteSpace(svc.ChatFontFamily))
            FontFamily = new FontFamily(svc.ChatFontFamily);
        if (svc.ChatFontSize > 0)
            FontSize = svc.ChatFontSize;
        if (TryParseColor(svc.ChatForeground, out var fg))
            Foreground = new SolidColorBrush(fg);
    }

    private static bool TryParseColor(string hex, out Color color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(hex)) return false;
        return Color.TryParse(hex, out color);
    }

    private void InputBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            if (DataContext is ChatViewModel vm && vm.CanSend)
            {
                e.Handled = true;
                vm.SendMessageCommand.Execute(null);
            }
        }
    }
}
