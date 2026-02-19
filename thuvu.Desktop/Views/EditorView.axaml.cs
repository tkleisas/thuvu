using Avalonia.Controls;
using AvaloniaEdit;
using AvaloniaEdit.TextMate;
using TextMateSharp.Grammars;
using thuvu.Desktop.ViewModels;

namespace thuvu.Desktop.Views;

public partial class EditorView : UserControl
{
    private TextMate.Installation? _textMateInstallation;

    public EditorView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is EditorViewModel vm)
        {
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
            catch { /* TextMate grammars may not be available */ }

            editor.Text = vm.Content;
            editor.TextChanged += (s, args) =>
            {
                vm.Content = editor.Text;
            };
        }
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
