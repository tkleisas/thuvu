using Avalonia.Controls;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.TextMate;
using TextMateSharp.Grammars;
using thuvu.Desktop.ViewModels;

namespace thuvu.Desktop.Views;

public partial class EditorView : UserControl
{
    private TextMate.Installation? _textMateInstallation;
    private bool _suppressTextChanged;

    public EditorView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not EditorViewModel vm) return;
        var editor = this.FindControl<TextEditor>("Editor");
        if (editor == null) return;

        // Setup TextMate syntax highlighting
        try
        {
            var registryOptions = new RegistryOptions(ThemeName.DarkPlus);
            _textMateInstallation = editor.InstallTextMate(registryOptions);

            var lang = DetectLanguage(vm.FilePath, registryOptions);
            if (lang != null)
                _textMateInstallation.SetGrammar(registryOptions.GetScopeByLanguageId(lang));
        }
        catch { }

        // Set content — loaded synchronously in ViewModel constructor
        _suppressTextChanged = true;
        editor.Text = vm.Content ?? "";
        _suppressTextChanged = false;

        // Sync ViewModel → Editor (e.g. reload file)
        vm.PropertyChanged += (s, args) =>
        {
            if (args.PropertyName == nameof(EditorViewModel.Content) && !_suppressTextChanged)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    _suppressTextChanged = true;
                    editor.Text = vm.Content ?? "";
                    _suppressTextChanged = false;
                });
            }
        };

        // Sync Editor → ViewModel (user typing)
        editor.TextChanged += (s, args) =>
        {
            if (!_suppressTextChanged)
            {
                _suppressTextChanged = true;
                vm.Content = editor.Text;
                _suppressTextChanged = false;
            }
        };
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
