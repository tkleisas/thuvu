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
public record FileEntry(string RelativePath, string Icon, string FullPath);

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

    /// <summary>Converts context usage percentage (0-100) to a width within 80px bar</summary>
    public static readonly FuncValueConverter<double, double> PercentToWidthConverter =
        new(pct => Math.Max(0, Math.Min(80, pct / 100.0 * 80)));

    /// <summary>Converts context usage percentage to a color (green → yellow → red)</summary>
    public static readonly FuncMultiValueConverter<object?, IBrush?> PercentToColorConverter =
        new(values =>
        {
            var pct = values.OfType<double>().FirstOrDefault();
            return pct switch
            {
                >= 90 => new SolidColorBrush(Color.FromRgb(220, 50, 50)),   // red
                >= 70 => new SolidColorBrush(Color.FromRgb(220, 160, 30)),  // yellow
                _ => new SolidColorBrush(Color.FromRgb(60, 180, 80))        // green
            };
        });

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
    private readonly ObservableCollection<FileEntry> _filteredFiles = new();
    private Popup? _commandPopup;
    private ListBox? _commandList;
    private Popup? _filePopup;
    private ListBox? _fileList;

    // Track @ trigger position in text
    private int _atTriggerIndex = -1;

    private static readonly HashSet<string> _excludedDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", "node_modules", ".git", ".vs", ".idea", "packages",
        "TestResults", "wwwroot\\lib", "dist", "build", ".thuvu"
    };

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

        _filePopup = this.FindControl<Popup>("FilePopup");
        _fileList = this.FindControl<ListBox>("FileList");
        if (_fileList != null)
        {
            _fileList.ItemsSource = _filteredFiles;
            _fileList.DoubleTapped += FileList_DoubleTapped;
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
        var caretPos = tb.CaretIndex;

        // Check for @ file trigger at caret position
        var atIndex = FindAtTrigger(text, caretPos);
        if (atIndex >= 0)
        {
            _atTriggerIndex = atIndex;
            var partial = text[(atIndex + 1)..caretPos];
            UpdateFileSuggestions(partial);

            if (_filteredFiles.Count > 0 && _filePopup != null)
            {
                _filePopup.IsOpen = true;
                _fileList?.ScrollIntoView(_filteredFiles[0]);
                if (_fileList != null) _fileList.SelectedIndex = 0;
            }
            else if (_filePopup != null)
            {
                _filePopup.IsOpen = false;
            }

            // Close command popup if open
            if (_commandPopup != null) _commandPopup.IsOpen = false;
            return;
        }

        _atTriggerIndex = -1;
        if (_filePopup != null) _filePopup.IsOpen = false;

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

    /// <summary>Find the @ trigger position relative to caret. Returns -1 if not in @ context.</summary>
    private static int FindAtTrigger(string text, int caretPos)
    {
        // Search backward from caret for @
        for (int i = caretPos - 1; i >= 0; i--)
        {
            var ch = text[i];
            if (ch == '@')
            {
                // @ must be at start or preceded by whitespace
                if (i == 0 || char.IsWhiteSpace(text[i - 1]))
                    return i;
                return -1;
            }
            // Stop searching if we hit whitespace (the @ isn't in current word)
            if (ch == '\n' || ch == '\r')
                return -1;
        }
        return -1;
    }

    private void UpdateFileSuggestions(string partial)
    {
        _filteredFiles.Clear();
        var workDir = (DataContext as ChatViewModel)?.AgentService?.WorkDirectory;
        if (string.IsNullOrEmpty(workDir) || !Directory.Exists(workDir)) return;

        // Normalize partial path separators
        var searchPartial = partial.Replace('/', Path.DirectorySeparatorChar);

        try
        {
            var files = EnumerateProjectFiles(workDir, 3)
                .Where(f => f.Contains(searchPartial, StringComparison.OrdinalIgnoreCase))
                .Take(20)
                .ToList();

            foreach (var relPath in files)
            {
                var icon = Path.HasExtension(relPath) ? "📄" : "📁";
                var ext = Path.GetExtension(relPath).ToLowerInvariant();
                icon = ext switch
                {
                    ".cs" => "🟣",
                    ".ts" or ".js" => "🟡",
                    ".json" => "📋",
                    ".xml" or ".axaml" or ".xaml" => "🔶",
                    ".md" => "📝",
                    ".csproj" or ".sln" => "🔧",
                    _ => icon
                };
                _filteredFiles.Add(new FileEntry(relPath, icon, Path.Combine(workDir, relPath)));
            }
        }
        catch
        {
            // Ignore filesystem errors during autocomplete
        }
    }

    private IEnumerable<string> EnumerateProjectFiles(string rootDir, int maxDepth)
    {
        return EnumerateFilesRecursive(rootDir, rootDir, 0, maxDepth);
    }

    private IEnumerable<string> EnumerateFilesRecursive(string rootDir, string currentDir, int depth, int maxDepth)
    {
        if (depth > maxDepth) yield break;

        string[] entries;
        try { entries = Directory.GetFileSystemEntries(currentDir); }
        catch { yield break; }

        foreach (var entry in entries)
        {
            var name = Path.GetFileName(entry);
            if (name.StartsWith('.')) continue;

            var relativePath = Path.GetRelativePath(rootDir, entry);

            if (Directory.Exists(entry))
            {
                if (_excludedDirs.Contains(name)) continue;
                // Yield directory itself
                yield return relativePath + Path.DirectorySeparatorChar;
                // Recurse
                foreach (var child in EnumerateFilesRecursive(rootDir, entry, depth + 1, maxDepth))
                    yield return child;
            }
            else
            {
                yield return relativePath;
            }
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

    private void FileList_DoubleTapped(object? sender, TappedEventArgs e)
    {
        AcceptSelectedFile();
    }

    private void AcceptSelectedFile()
    {
        if (_fileList?.SelectedItem is FileEntry selected && DataContext is ChatViewModel vm)
        {
            var text = vm.InputText ?? "";
            if (_atTriggerIndex >= 0 && _atTriggerIndex < text.Length)
            {
                var inputBox = this.FindControl<TextBox>("InputBox");
                var caretPos = inputBox?.CaretIndex ?? text.Length;

                // Replace @partial with @filepath
                var before = text[.._atTriggerIndex];
                var after = caretPos <= text.Length ? text[caretPos..] : "";
                var inserted = $"@{selected.RelativePath} ";
                vm.InputText = before + inserted + after;

                if (_filePopup != null) _filePopup.IsOpen = false;
                _atTriggerIndex = -1;

                inputBox?.Focus();
                if (inputBox != null)
                    inputBox.CaretIndex = before.Length + inserted.Length;
            }
        }
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
        // Handle file autocomplete navigation
        if (_filePopup?.IsOpen == true)
        {
            if (e.Key == Key.Down)
            {
                e.Handled = true;
                var idx = _fileList?.SelectedIndex ?? -1;
                if (idx < _filteredFiles.Count - 1)
                    _fileList!.SelectedIndex = idx + 1;
                return;
            }
            if (e.Key == Key.Up)
            {
                e.Handled = true;
                var idx = _fileList?.SelectedIndex ?? 0;
                if (idx > 0)
                    _fileList!.SelectedIndex = idx - 1;
                return;
            }
            if (e.Key == Key.Tab || (e.Key == Key.Enter && _fileList?.SelectedItem != null))
            {
                e.Handled = true;
                AcceptSelectedFile();
                return;
            }
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                _filePopup.IsOpen = false;
                _atTriggerIndex = -1;
                return;
            }
        }

        // Handle command autocomplete navigation
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

    private async void OnMessageDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Border { DataContext: ChatMessageViewModel msg })
        {
            var dialog = new MessageDetailDialog(msg);
            var topLevel = TopLevel.GetTopLevel(this) as Window;
            if (topLevel != null)
                await dialog.ShowDialog(topLevel);
            else
                dialog.Show();
        }
    }
}
