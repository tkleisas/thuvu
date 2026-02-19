using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using AvaloniaEdit;
using AvaloniaEdit.TextMate;
using TextMateSharp.Grammars;
using thuvu.Desktop.Models;
using thuvu.Desktop.ViewModels;

namespace thuvu.Desktop.Views;

public partial class EditorView : UserControl
{
    private TextMate.Installation? _textMateInstallation;

    public EditorView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        AttachedToVisualTree += OnAttachedToVisualTree;
        KeyDown += OnKeyDown;
    }

    private async void OnKeyDown(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        if (e.Key == Avalonia.Input.Key.S && e.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Control))
        {
            e.Handled = true;
            if (DataContext is EditorViewModel vm)
            {
                // Sync editor text to ViewModel before saving
                var editor = this.FindControl<TextEditor>("Editor");
                if (editor != null)
                    vm.Content = editor.Document.Text;
                await vm.SaveFileCommand.ExecuteAsync(null);
            }
        }
    }

    private void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        ApplyAppearance();
        AppearanceService.Instance.PropertyChanged += (_, _) =>
            Avalonia.Threading.Dispatcher.UIThread.Post(ApplyAppearance);
    }

    private void ApplyAppearance()
    {
        var editor = this.FindControl<TextEditor>("Editor");
        if (editor == null) return;

        var svc = AppearanceService.Instance;
        if (!string.IsNullOrWhiteSpace(svc.EditorFontFamily))
            editor.FontFamily = new FontFamily(svc.EditorFontFamily);
        if (svc.EditorFontSize > 0)
            editor.FontSize = svc.EditorFontSize;
        if (TryParseColor(svc.EditorForeground, out var fg))
            editor.Foreground = new SolidColorBrush(fg);
        if (TryParseColor(svc.EditorBackground, out var bg))
            editor.Background = new SolidColorBrush(bg);
    }

    private static bool TryParseColor(string hex, out Color color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(hex)) return false;
        return Color.TryParse(hex, out color);
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is not EditorViewModel vm) return;
        var editor = this.FindControl<TextEditor>("Editor");
        if (editor == null || _textMateInstallation != null) return;

        try
        {
            var registryOptions = new RegistryOptions(ThemeName.DarkPlus);
            _textMateInstallation = editor.InstallTextMate(registryOptions);
            var lang = DetectLanguage(vm.FilePath, registryOptions);
            if (lang != null)
                _textMateInstallation.SetGrammar(registryOptions.GetScopeByLanguageId(lang));
        }
        catch { }
    }

    private static string? DetectLanguage(string path, RegistryOptions registry)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".cs" => "csharp",
            ".ts" or ".tsx" => "typescript",
            ".js" or ".jsx" => "javascript",
            ".json" => "json",
            ".xml" or ".csproj" or ".axaml" or ".xaml" => "xml",
            ".html" or ".htm" => "html",
            ".css" => "css",
            ".md" => "markdown",
            ".py" => "python",
            ".sql" => "sql",
            ".yaml" or ".yml" => "yaml",
            ".sh" or ".bash" => "shellscript",
            ".ps1" => "powershell",
            ".go" => "go",
            ".rs" => "rust",
            _ => null
        };
    }
}
