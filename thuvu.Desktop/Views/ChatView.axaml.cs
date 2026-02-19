using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using System.Collections.ObjectModel;
using System.Globalization;
using thuvu.Desktop.Models;
using thuvu.Desktop.ViewModels;

namespace thuvu.Desktop.Views;

public record SlashCommand(string Command, string Icon, string Description);

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

    private static readonly SlashCommand[] AllCommands =
    [
        new("/help", "❓", "Show available commands"),
        new("/clear", "🗑️", "Clear conversation"),
        new("/system", "⚙️", "Set system prompt"),
        new("/stream", "📡", "Toggle streaming on/off"),
        new("/config", "🔧", "View/manage configuration"),
        new("/set", "⚙️", "Change settings (model, host, stream, timeout)"),
        new("/diff", "📝", "Show git diff"),
        new("/test", "🧪", "Run dotnet tests"),
        new("/run", "▶️", "Run a whitelisted command"),
        new("/commit", "💾", "Commit with test gate"),
        new("/push", "⬆️", "Push to remote"),
        new("/pull", "⬇️", "Pull from remote"),
        new("/rag", "🔍", "RAG operations (index, search, stats, clear)"),
        new("/mcp", "🔌", "MCP operations (enable, run, tools, skills)"),
        new("/models", "🤖", "Model management (list, use, info)"),
        new("/plan", "📋", "Decompose task into subtasks"),
        new("/orchestrate", "🎭", "Multi-agent orchestration"),
        new("/health", "💚", "Check service health"),
        new("/status", "📊", "Show session and token status"),
    ];

    private readonly ObservableCollection<SlashCommand> _filteredCommands = new();
    private Popup? _commandPopup;
    private ListBox? _commandList;

    public ChatView()
    {
        InitializeComponent();

        var inputBox = this.FindControl<TextBox>("InputBox");
        inputBox?.AddHandler(KeyDownEvent, InputBox_KeyDown, RoutingStrategies.Tunnel);
        if (inputBox != null)
            inputBox.TextChanged += InputBox_TextChanged;

        _commandPopup = this.FindControl<Popup>("CommandPopup");
        _commandList = this.FindControl<ListBox>("CommandList");
        if (_commandList != null)
        {
            _commandList.ItemsSource = _filteredCommands;
            _commandList.DoubleTapped += CommandList_DoubleTapped;
        }

        AttachedToVisualTree += OnAttachedToVisualTree;
    }

    private void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        ApplyAppearance();
        AppearanceService.Instance.PropertyChanged += (_, _) =>
            Avalonia.Threading.Dispatcher.UIThread.Post(ApplyAppearance);

        if (DataContext is ChatViewModel vm)
        {
            vm.Messages.CollectionChanged += (_, _) =>
                Avalonia.Threading.Dispatcher.UIThread.Post(ScrollToBottom);
            vm.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(ChatViewModel.IsProcessing) && !vm.IsProcessing)
                    Avalonia.Threading.Dispatcher.UIThread.Post(ScrollToBottom);
            };
        }
    }

    private void ScrollToBottom()
    {
        var scroller = this.FindControl<ScrollViewer>("MessagesScroller");
        scroller?.ScrollToEnd();
    }

    private void InputBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (sender is not TextBox tb) return;
        var text = tb.Text ?? "";

        // Show popup when text starts with / and is a single line
        if (text.StartsWith("/") && !text.Contains('\n'))
        {
            var prefix = text.ToLowerInvariant();
            _filteredCommands.Clear();
            foreach (var cmd in AllCommands)
            {
                if (cmd.Command.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    _filteredCommands.Add(cmd);
            }

            if (_filteredCommands.Count > 0 && _commandPopup != null)
            {
                _commandPopup.IsOpen = true;
                _commandList?.ScrollIntoView(_filteredCommands[0]);
            }
            else if (_commandPopup != null)
            {
                _commandPopup.IsOpen = false;
            }
        }
        else if (_commandPopup != null)
        {
            _commandPopup.IsOpen = false;
        }
    }

    private void AcceptSelectedCommand()
    {
        if (_commandList?.SelectedItem is SlashCommand selected)
        {
            if (DataContext is ChatViewModel vm)
                vm.InputText = selected.Command + " ";
            if (_commandPopup != null)
                _commandPopup.IsOpen = false;

            var inputBox = this.FindControl<TextBox>("InputBox");
            inputBox?.Focus();
            // Move caret to end
            if (inputBox != null)
                inputBox.CaretIndex = inputBox.Text?.Length ?? 0;
        }
    }

    private void CommandList_DoubleTapped(object? sender, TappedEventArgs e)
    {
        AcceptSelectedCommand();
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
        // Handle autocomplete navigation
        if (_commandPopup?.IsOpen == true)
        {
            if (e.Key == Key.Down)
            {
                e.Handled = true;
                var idx = _commandList?.SelectedIndex ?? -1;
                if (idx < _filteredCommands.Count - 1)
                    _commandList!.SelectedIndex = idx + 1;
                return;
            }
            if (e.Key == Key.Up)
            {
                e.Handled = true;
                var idx = _commandList?.SelectedIndex ?? 0;
                if (idx > 0)
                    _commandList!.SelectedIndex = idx - 1;
                return;
            }
            if (e.Key == Key.Tab || (e.Key == Key.Enter && _commandList?.SelectedItem != null))
            {
                e.Handled = true;
                AcceptSelectedCommand();
                return;
            }
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                _commandPopup.IsOpen = false;
                return;
            }
        }

        // Normal Enter to send
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
