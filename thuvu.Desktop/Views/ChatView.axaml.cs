using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Media;
using System.Globalization;

namespace thuvu.Desktop.Views;

public partial class ChatView : UserControl
{
    public static readonly FuncValueConverter<string?, IBrush?> RoleBrushConverter =
        new(role => role switch
        {
            "user" => new SolidColorBrush(Color.FromArgb(30, 0, 120, 255)),
            "assistant" => new SolidColorBrush(Color.FromArgb(20, 255, 255, 255)),
            _ => new SolidColorBrush(Color.FromArgb(15, 255, 165, 0))
        });

    public ChatView()
    {
        InitializeComponent();
    }
}
