using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using thuvu.Desktop.Models;
using thuvu.Desktop.ViewModels;
using thuvu.Desktop.Views;

namespace thuvu.Desktop;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override async void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Show project startup dialog first
            var startupDialog = new ProjectStartupDialog();
            var dummyWindow = new Avalonia.Controls.Window
            {
                Width = 0, Height = 0,
                ShowInTaskbar = false,
                WindowStartupLocation = Avalonia.Controls.WindowStartupLocation.Manual,
                Position = new PixelPoint(-10000, -10000),
                SystemDecorations = Avalonia.Controls.SystemDecorations.None,
                Opacity = 0
            };
            desktop.MainWindow = dummyWindow;
            dummyWindow.Show();

            await startupDialog.ShowDialog(dummyWindow);

            if (startupDialog.SelectedProject == null)
            {
                // User closed dialog without selecting — exit
                desktop.Shutdown();
                return;
            }

            var mainWindow = new MainWindow
            {
                DataContext = new MainWindowViewModel(startupDialog.SelectedProject)
            };
            desktop.MainWindow = mainWindow;
            mainWindow.Show();
            dummyWindow.Close();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
