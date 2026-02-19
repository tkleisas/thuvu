using Avalonia.Controls;
using Avalonia.Input;
using thuvu.Desktop.ViewModels;

namespace thuvu.Desktop.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        KeyDown += OnKeyDown;
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
