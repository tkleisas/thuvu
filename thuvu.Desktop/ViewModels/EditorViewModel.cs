using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace thuvu.Desktop.ViewModels;

/// <summary>
/// ViewModel for a code editor tab using AvaloniaEdit
/// </summary>
public partial class EditorViewModel : DocumentViewModel
{
    [ObservableProperty] private string _filePath = string.Empty;
    [ObservableProperty] private string _content = string.Empty;
    [ObservableProperty] private bool _isDirty;
    [ObservableProperty] private string _syntaxHighlighting = "Text";

    public string RelativePath
    {
        get
        {
            try
            {
                var cwd = Directory.GetCurrentDirectory();
                if (FilePath.StartsWith(cwd, StringComparison.OrdinalIgnoreCase))
                    return Path.GetRelativePath(cwd, FilePath);
            }
            catch { }
            return FilePath;
        }
    }

    partial void OnFilePathChanged(string value) => OnPropertyChanged(nameof(RelativePath));

    public EditorViewModel()
    {
        Id = "Editor";
        Title = "Editor";
    }

    public EditorViewModel(string filePath) : this()
    {
        FilePath = filePath;
        Id = $"Editor_{filePath.GetHashCode():X}";
        Title = Path.GetFileName(filePath);
        SyntaxHighlighting = DetectHighlighting(filePath);
    }

    [RelayCommand]
    private async Task LoadFile()
    {
        if (string.IsNullOrEmpty(FilePath) || !File.Exists(FilePath)) return;
        Content = await File.ReadAllTextAsync(FilePath);
        IsDirty = false;
    }

    [RelayCommand]
    private async Task SaveFile()
    {
        if (string.IsNullOrEmpty(FilePath)) return;
        await File.WriteAllTextAsync(FilePath, Content);
        IsDirty = false;
        Title = Path.GetFileName(FilePath);
    }

    partial void OnContentChanged(string value)
    {
        IsDirty = true;
        Title = Path.GetFileName(FilePath) + " •";
    }

    private static string DetectHighlighting(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".cs" => "C#",
            ".ts" or ".tsx" => "JavaScript",
            ".js" or ".jsx" => "JavaScript",
            ".xml" or ".axaml" or ".xaml" or ".csproj" => "XML",
            ".json" => "JavaScript",
            ".html" or ".htm" or ".razor" => "HTML",
            ".css" => "CSS",
            ".md" => "MarkDown",
            ".py" => "Python",
            ".sql" => "TSQL",
            _ => "Text"
        };
    }
}
