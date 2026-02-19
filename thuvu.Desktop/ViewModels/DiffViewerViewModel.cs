using CommunityToolkit.Mvvm.ComponentModel;

namespace thuvu.Desktop.ViewModels;

/// <summary>
/// ViewModel for displaying unified diffs
/// </summary>
public partial class DiffViewerViewModel : DocumentViewModel
{
    [ObservableProperty] private string _fileName = string.Empty;
    [ObservableProperty] private string _diffContent = string.Empty;
    [ObservableProperty] private int _additions;
    [ObservableProperty] private int _deletions;

    public DiffViewerViewModel()
    {
        Id = "DiffViewer";
        Title = "Diff";
    }

    public DiffViewerViewModel(string fileName, string diff) : this()
    {
        FileName = fileName;
        DiffContent = diff;
        Title = $"Diff: {Path.GetFileName(fileName)}";
        Id = $"Diff_{fileName.GetHashCode():X}";
        ParseStats();
    }

    private void ParseStats()
    {
        foreach (var line in DiffContent.Split('\n'))
        {
            if (line.StartsWith('+') && !line.StartsWith("+++")) Additions++;
            else if (line.StartsWith('-') && !line.StartsWith("---")) Deletions++;
        }
    }
}
