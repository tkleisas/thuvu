using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using System.Globalization;
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
