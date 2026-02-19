using Avalonia.Controls;
using thuvu.Desktop.Models;
using thuvu.Desktop.ViewModels;

namespace thuvu.Desktop.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow(ProjectConfig? projectConfig = null)
    {
        InitializeComponent();
        var vm = new SettingsViewModel();
        vm.ProjectConfig = projectConfig;
        DataContext = vm;
    }
}
