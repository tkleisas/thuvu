using System.Collections.ObjectModel;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using thuvu.Desktop.Models;

namespace thuvu.Desktop.Views;

public partial class ProjectStartupDialog : Window
{
    public ProjectConfig? SelectedProject { get; private set; }
    public bool HasRecent => RecentProjects.Count > 0;
    public ObservableCollection<RecentProjectEntry> RecentProjects { get; } = new();

    private static readonly string RecentFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "thuvu", "recent-projects.json");

    public ProjectStartupDialog()
    {
        InitializeComponent();
        DataContext = this;
        LoadRecentProjects();
    }

    private async void NewProject_Click(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select Project Directory",
            AllowMultiple = false
        });

        if (folders.Count == 0) return;

        var dir = folders[0].Path.LocalPath;
        var thuvuFile = Path.Combine(dir, ".thuvu");

        if (File.Exists(thuvuFile))
        {
            // Already has a .thuvu file — load it
            SelectedProject = ProjectConfig.Load(thuvuFile);
        }
        else
        {
            SelectedProject = ProjectConfig.CreateNew(dir);
        }

        AddToRecent(SelectedProject);
        Close();
    }

    private async void OpenProject_Click(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open .thuvu Project File",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("THUVU Project") { Patterns = new[] { "*.thuvu" } },
                new FilePickerFileType("All Files") { Patterns = new[] { "*" } }
            }
        });

        if (files.Count == 0) return;

        var path = files[0].Path.LocalPath;
        try
        {
            SelectedProject = ProjectConfig.Load(path);
            AddToRecent(SelectedProject);
            Close();
        }
        catch (Exception ex)
        {
            StatusText = $"Failed to load project: {ex.Message}";
        }
    }

    private string StatusText
    {
        set => Title = string.IsNullOrEmpty(value)
            ? "T.H.U.V.U. — Select Project"
            : $"T.H.U.V.U. — {value}";
    }

    private void RecentList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not ListBox lb || lb.SelectedItem is not RecentProjectEntry entry) return;

        if (!File.Exists(entry.Path))
        {
            RecentProjects.Remove(entry);
            SaveRecentProjects();
            return;
        }

        try
        {
            SelectedProject = ProjectConfig.Load(entry.Path);
            AddToRecent(SelectedProject);
            Close();
        }
        catch { }
    }

    private void LoadRecentProjects()
    {
        try
        {
            if (!File.Exists(RecentFilePath)) return;
            var json = File.ReadAllText(RecentFilePath);
            var list = JsonSerializer.Deserialize<List<RecentProjectEntry>>(json);
            if (list == null) return;
            foreach (var entry in list)
            {
                if (File.Exists(entry.Path))
                    RecentProjects.Add(entry);
            }
        }
        catch { }
    }

    private void AddToRecent(ProjectConfig project)
    {
        // Remove existing entry with same path
        for (int i = RecentProjects.Count - 1; i >= 0; i--)
        {
            if (RecentProjects[i].Path.Equals(project.FilePath, StringComparison.OrdinalIgnoreCase))
                RecentProjects.RemoveAt(i);
        }

        // Insert at top
        RecentProjects.Insert(0, new RecentProjectEntry
        {
            Name = project.Name,
            Path = project.FilePath
        });

        // Keep max 10
        while (RecentProjects.Count > 10)
            RecentProjects.RemoveAt(RecentProjects.Count - 1);

        SaveRecentProjects();
    }

    private void SaveRecentProjects()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(RecentFilePath)!);
            var json = JsonSerializer.Serialize(RecentProjects.ToList(),
                new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(RecentFilePath, json);
        }
        catch { }
    }
}

public class RecentProjectEntry
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
}
