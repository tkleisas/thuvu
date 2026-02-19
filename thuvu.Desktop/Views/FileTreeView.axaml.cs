using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Input;
using thuvu.Desktop.ViewModels;

namespace thuvu.Desktop.Views;

public partial class FileTreeView : UserControl
{
    public static readonly FuncValueConverter<bool, string> FolderIconConverter =
        new(isDir => isDir ? "📁" : "📄");

    public FileTreeView()
    {
        InitializeComponent();
    }

    private void FileTree_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is TreeView tv && tv.SelectedItem is FileTreeNodeViewModel node && !node.IsDirectory)
        {
            if (DataContext is FileTreeViewModel vm)
                vm.OpenFileCommand.Execute(node);
        }
    }
}
