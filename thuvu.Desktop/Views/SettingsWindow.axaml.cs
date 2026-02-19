using Avalonia.Controls;
using thuvu.Desktop.ViewModels;

namespace thuvu.Desktop.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
        DataContext = new SettingsViewModel();
    }
}
