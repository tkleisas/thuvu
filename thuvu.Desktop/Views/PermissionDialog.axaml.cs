using Avalonia.Controls;
using Avalonia.Interactivity;

namespace thuvu.Desktop.Views;

public partial class PermissionDialog : Window
{
    public string Result { get; private set; } = "deny";

    public PermissionDialog()
    {
        InitializeComponent();
    }

    public PermissionDialog(string toolName, string args) : this()
    {
        ToolNameText.Text = toolName;
        ArgsText.Text = args.Length > 200 ? args[..200] + "..." : args;
    }

    private void OnAlways(object? sender, RoutedEventArgs e)
    {
        Result = "always";
        Close();
    }

    private void OnSession(object? sender, RoutedEventArgs e)
    {
        Result = "session";
        Close();
    }

    private void OnOnce(object? sender, RoutedEventArgs e)
    {
        Result = "once";
        Close();
    }

    private void OnDeny(object? sender, RoutedEventArgs e)
    {
        Result = "deny";
        Close();
    }
}
