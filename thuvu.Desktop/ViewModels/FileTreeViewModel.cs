using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;

namespace thuvu.Desktop.ViewModels;

/// <summary>
/// Represents a file or directory in the tree
/// </summary>
public partial class FileTreeNodeViewModel : ObservableObject
{
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _fullPath = string.Empty;
    [ObservableProperty] private bool _isDirectory;
    [ObservableProperty] private bool _isExpanded;

    internal FileTreeViewModel? Owner { get; set; }

    public ObservableCollection<FileTreeNodeViewModel> Children { get; } = new();

    partial void OnIsExpandedChanged(bool value)
    {
        if (value && IsDirectory && Children.Count == 1 && Children[0].Name == "Loading...")
        {
            Owner?.ExpandNode(this);
        }
    }
}

/// <summary>
/// ViewModel for the File Explorer dockable panel
/// </summary>
public partial class FileTreeViewModel : ToolViewModel
{
    [ObservableProperty] private string _rootPath = string.Empty;
    
    public ObservableCollection<FileTreeNodeViewModel> RootNodes { get; } = new();

    public FileTreeViewModel()
    {
        Id = "FileTree";
        Title = "📁 Explorer";
        CanClose = true;
        CanFloat = true;
    }

    [RelayCommand]
    private void Refresh()
    {
        if (string.IsNullOrEmpty(RootPath)) return;
        RootNodes.Clear();
        LoadDirectory(RootPath, RootNodes);
    }

    public event Action<string>? FileOpenRequested;

    [RelayCommand]
    private void OpenFile(FileTreeNodeViewModel node)
    {
        if (node.IsDirectory) return;
        FileOpenRequested?.Invoke(node.FullPath);
    }

    private void LoadDirectory(string path, ObservableCollection<FileTreeNodeViewModel> target)
    {
        try
        {
            foreach (var dir in Directory.GetDirectories(path).OrderBy(d => Path.GetFileName(d)))
            {
                var dirName = Path.GetFileName(dir);
                if (dirName.StartsWith('.') || dirName is "bin" or "obj" or "node_modules") continue;
                
                var dirNode = new FileTreeNodeViewModel
                {
                    Name = dirName,
                    FullPath = dir,
                    IsDirectory = true,
                    Owner = this
                };
                // Lazy load: add placeholder
                dirNode.Children.Add(new FileTreeNodeViewModel { Name = "Loading..." });
                target.Add(dirNode);
            }
            
            foreach (var file in Directory.GetFiles(path).OrderBy(f => Path.GetFileName(f)))
            {
                target.Add(new FileTreeNodeViewModel
                {
                    Name = Path.GetFileName(file),
                    FullPath = file,
                    IsDirectory = false
                });
            }
        }
        catch (UnauthorizedAccessException) { }
        catch (IOException) { }
    }

    public void ExpandNode(FileTreeNodeViewModel node)
    {
        if (!node.IsDirectory) return;
        node.Children.Clear();
        LoadDirectory(node.FullPath, node.Children);
        node.IsExpanded = true;
    }
}
