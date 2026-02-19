using Avalonia.Controls;
using Avalonia.Data.Converters;

namespace thuvu.Desktop.Views;

public partial class FileTreeView : UserControl
{
    public static readonly FuncValueConverter<bool, string> FolderIconConverter =
        new(isDir => isDir ? "📁" : "📄");

    public FileTreeView()
    {
        InitializeComponent();
    }
}
