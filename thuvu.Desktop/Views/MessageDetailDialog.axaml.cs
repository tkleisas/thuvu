using Avalonia.Controls;
using Avalonia.Interactivity;
using thuvu.Desktop.ViewModels;

namespace thuvu.Desktop.Views;

public partial class MessageDetailDialog : Window
{
    public MessageDetailDialog()
    {
        InitializeComponent();
    }

    public MessageDetailDialog(ChatMessageViewModel msg) : this()
    {
        RoleText.Text = $"[{msg.Role}]";
        TimestampText.Text = msg.Timestamp;
        ContentText.Text = msg.Content;

        if (!string.IsNullOrEmpty(msg.ToolName))
        {
            ToolPanel.IsVisible = true;
            ToolNameText.Text = $"🔧 {msg.ToolName}";
            ToolArgsText.Text = msg.ToolArgs ?? "";
            ToolResultText.Text = msg.ToolResult ?? "";
        }

        if (!string.IsNullOrEmpty(msg.ThinkingContent))
        {
            ThinkingPanel.IsVisible = true;
            ThinkingText.Text = msg.ThinkingContent;
        }
    }

    private async void OnCopy(object? sender, RoutedEventArgs e)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard == null) return;

        var text = $"[{RoleText.Text}] {TimestampText.Text}\n\n{ContentText.Text}";
        if (ToolPanel.IsVisible)
            text += $"\n\n--- Tool: {ToolNameText.Text} ---\nArgs: {ToolArgsText.Text}\nResult: {ToolResultText.Text}";
        if (ThinkingPanel.IsVisible)
            text += $"\n\n--- Thinking ---\n{ThinkingText.Text}";

        await clipboard.SetTextAsync(text);
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
