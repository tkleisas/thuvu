using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using thuvu.Desktop.ViewModels;

namespace thuvu.Desktop.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        KeyDown += OnKeyDown;
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;

        vm.ShowOpenFileDialog = async () =>
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Open File",
                AllowMultiple = false
            });
            if (files.Count > 0)
                return files[0].Path.LocalPath;
            return null;
        };

        vm.ShowSettingsDialog = async () =>
        {
            var settings = new SettingsWindow();
            await settings.ShowDialog(this);
        };
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;

        if (e.KeyModifiers == KeyModifiers.Control)
        {
            switch (e.Key)
            {
                case Key.N:
                    vm.NewChatCommand.Execute(null);
                    e.Handled = true;
                    break;
                case Key.O:
                    vm.OpenFileCommand.Execute(null);
                    e.Handled = true;
                    break;
            }
        }
        else if (e.Key == Key.Escape)
        {
            vm.CancelRequestCommand.Execute(null);
            e.Handled = true;
        }
    }
}
