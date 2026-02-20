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
        // Handle Ctrl+V paste (check clipboard for images before default text paste)
        if (e.Key == Key.V && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            _ = TryPasteImageAsync(e);
            return;
        }

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

    private void OnRefreshModelsClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ChatViewModel vm)
            vm.RefreshModels();
    }

    private async void OnAttachImageClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ChatViewModel vm) return;
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(
            new Avalonia.Platform.Storage.FilePickerOpenOptions
            {
                Title = "Select Image",
                AllowMultiple = true,
                FileTypeFilter = new[]
                {
                    new Avalonia.Platform.Storage.FilePickerFileType("Images")
                    {
                        Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.gif", "*.webp", "*.bmp" }
                    }
                }
            });

        foreach (var file in files)
        {
            await using var stream = await file.OpenReadAsync();
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms);
            var bytes = ms.ToArray();
            var ext = Path.GetExtension(file.Name).ToLowerInvariant();
            var mime = ext switch
            {
                ".jpg" or ".jpeg" => "image/jpeg",
                ".gif" => "image/gif",
                ".webp" => "image/webp",
                ".bmp" => "image/bmp",
                _ => "image/png"
            };
            vm.AddPendingImage(bytes, mime);
        }
    }

    private void OnRemovePendingImage(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ChatViewModel vm) return;
        if (sender is Button btn && btn.DataContext is ImageData img)
        {
            vm.PendingImages.Remove(img);
        }
    }

    /// <summary>Handle Ctrl+V paste for images from clipboard. Falls through to default text paste if no image found.</summary>
    private async Task TryPasteImageAsync(KeyEventArgs e)
    {
        if (DataContext is not ChatViewModel vm) return;
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var clipboard = topLevel.Clipboard;
        if (clipboard == null) return;

        try
        {
            var formats = await clipboard.GetFormatsAsync();
            var logPath = Path.Combine(AppContext.BaseDirectory, "clipboard_debug.log");
            await File.AppendAllTextAsync(logPath,
                $"\n[{DateTime.Now:HH:mm:ss}] Clipboard formats: {string.Join(", ", formats)}\n");

            // Try image data formats
            foreach (var fmt in new[] { "image/png", "PNG", "image/jpeg", "image/bmp", "Bitmap",
                                         "DeviceIndependentBitmap", "System.Drawing.Bitmap",
                                         "image/x-png", "CF_BITMAP", "CF_DIB" })
            {
                if (!formats.Contains(fmt)) continue;
                var data = await clipboard.GetDataAsync(fmt);
                await File.AppendAllTextAsync(logPath,
                    $"  Format '{fmt}': type={data?.GetType().FullName ?? "null"}, " +
                    $"value={(data is byte[] b ? $"{b.Length} bytes" : data?.ToString()?.Substring(0, Math.Min(200, data.ToString()!.Length)) ?? "null")}\n");

                byte[]? bytes = data switch
                {
                    byte[] arr => arr,
                    MemoryStream ms => ms.ToArray(),
                    Stream s => ReadStreamToBytes(s),
                    _ => null
                };

                if (bytes != null && bytes.Length > 100)
                {
                    vm.AddPendingImage(bytes, "image/png");
                    e.Handled = true;
                    return;
                }
            }

            // Fallback: try Win32 clipboard for bitmap data
            if (OperatingSystem.IsWindows())
            {
                var imgBytes = TryGetImageFromWin32Clipboard();
                if (imgBytes != null && imgBytes.Length > 100)
                {
                    await File.AppendAllTextAsync(logPath,
                        $"  Win32 clipboard fallback: got {imgBytes.Length} bytes\n");
                    vm.AddPendingImage(imgBytes, "image/png");
                    e.Handled = true;
                    return;
                }
            }

            // Check for file paths that point to images
            foreach (var fmt in new[] { "Files", "FileNames", "text/uri-list" })
            {
                if (!formats.Contains(fmt)) continue;
                var data = await clipboard.GetDataAsync(fmt);

                IEnumerable<string>? paths = data switch
                {
                    IEnumerable<Avalonia.Platform.Storage.IStorageItem> items =>
                        items.OfType<Avalonia.Platform.Storage.IStorageFile>()
                             .Select(f => f.Path.LocalPath),
                    IEnumerable<string> strs => strs,
                    string s => s.Split('\n', StringSplitOptions.RemoveEmptyEntries),
                    _ => null
                };

                if (paths == null) continue;

                foreach (var path in paths)
                {
                    var clean = path.Trim().TrimStart("file:///".ToCharArray());
                    var ext = Path.GetExtension(clean).ToLowerInvariant();
                    if (ext is ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp" && File.Exists(clean))
                    {
                        var fileBytes = await File.ReadAllBytesAsync(clean);
                        var mime = ext switch
                        {
                            ".jpg" or ".jpeg" => "image/jpeg",
                            ".gif" => "image/gif",
                            ".webp" => "image/webp",
                            ".bmp" => "image/bmp",
                            _ => "image/png"
                        };
                        vm.AddPendingImage(fileBytes, mime);
                        e.Handled = true;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Clipboard] Error: {ex}");
        }
    }

    private static byte[]? ReadStreamToBytes(Stream s)
    {
        try
        {
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            return ms.ToArray();
        }
        catch { return null; }
    }

    /// <summary>Win32 clipboard fallback: read bitmap data and convert to PNG bytes</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static byte[]? TryGetImageFromWin32Clipboard()
    {
        try
        {
            if (!Win32Clipboard.OpenClipboard(IntPtr.Zero)) return null;
            try
            {
                // Try CF_DIBV5 (17) first, then CF_DIB (8)
                foreach (uint fmt in new uint[] { 17, 8 })
                {
                    if (!Win32Clipboard.IsClipboardFormatAvailable(fmt)) continue;
                    var hData = Win32Clipboard.GetClipboardData(fmt);
                    if (hData == IntPtr.Zero) continue;

                    var ptr = Win32Clipboard.GlobalLock(hData);
                    if (ptr == IntPtr.Zero) continue;
                    try
                    {
                        var size = (int)Win32Clipboard.GlobalSize(hData);
                        if (size <= 0) continue;

                        var dibBytes = new byte[size];
                        System.Runtime.InteropServices.Marshal.Copy(ptr, dibBytes, 0, size);

                        // Convert DIB to BMP file format (add BMP file header)
                        return DibToBmp(dibBytes);
                    }
                    finally
                    {
                        Win32Clipboard.GlobalUnlock(hData);
                    }
                }
            }
            finally
            {
                Win32Clipboard.CloseClipboard();
            }
        }
        catch { }
        return null;
    }

    /// <summary>Prepend BMP file header to a DIB (device-independent bitmap) byte array</summary>
    private static byte[] DibToBmp(byte[] dib)
    {
        // BITMAPINFOHEADER size is first 4 bytes of DIB
        int headerSize = BitConverter.ToInt32(dib, 0);
        int bitsOffset = headerSize; // offset to pixel data after headers

        // Check for color table
        int bitCount = BitConverter.ToInt16(dib, 14);
        int colorsUsed = BitConverter.ToInt32(dib, 32);
        if (bitCount <= 8)
            bitsOffset += (colorsUsed > 0 ? colorsUsed : (1 << bitCount)) * 4;
        else if (bitCount == 16 || bitCount == 32)
            bitsOffset += colorsUsed * 4; // color masks if present

        // BMP file header: 14 bytes
        int fileSize = 14 + dib.Length;
        var bmp = new byte[fileSize];
        bmp[0] = (byte)'B';
        bmp[1] = (byte)'M';
        BitConverter.GetBytes(fileSize).CopyTo(bmp, 2);
        BitConverter.GetBytes(14 + bitsOffset).CopyTo(bmp, 10);
        Array.Copy(dib, 0, bmp, 14, dib.Length);
        return bmp;
    }

    /// <summary>
    /// After MarkdownScrollViewer loads, subscribe to visibility changes
    /// so we can detect rendering failures both on initial load (restored messages)
    /// and after streaming→done transitions.
    /// </summary>
    private void OnMarkdownLoaded(object? sender, RoutedEventArgs e)
    {
        if (sender is not Markdown.Avalonia.MarkdownScrollViewer md) return;

        // Check immediately (for restored messages that are visible from the start)
        CheckMarkdownRendering(md);

        // Also check when visibility changes (streaming→done transition)
        md.PropertyChanged += OnMarkdownPropertyChanged;
    }

    private void OnMarkdownPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Visual.IsVisibleProperty && e.NewValue is true &&
            sender is Markdown.Avalonia.MarkdownScrollViewer md)
        {
            // Delay check until after layout pass completes
            Avalonia.Threading.Dispatcher.UIThread.Post(
                () => CheckMarkdownRendering(md),
                Avalonia.Threading.DispatcherPriority.Loaded);
        }
    }

    private void CheckMarkdownRendering(Markdown.Avalonia.MarkdownScrollViewer md)
    {
        if (md.DataContext is ChatMessageViewModel msg &&
            !string.IsNullOrEmpty(msg.Content) && !msg.IsStreaming &&
            md.IsVisible && md.Bounds.Height < 2)
        {
            msg.MarkdownFailed = true;
        }
    }
}

/// <summary>Win32 clipboard P/Invoke declarations</summary>
internal static class Win32Clipboard
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern bool OpenClipboard(IntPtr hWndNewOwner);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern bool CloseClipboard();
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern bool IsClipboardFormatAvailable(uint format);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern IntPtr GetClipboardData(uint format);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    public static extern IntPtr GlobalLock(IntPtr hMem);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    public static extern bool GlobalUnlock(IntPtr hMem);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    public static extern nuint GlobalSize(IntPtr hMem);
}
