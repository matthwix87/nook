using Avalonia.Controls;
using Avalonia.Input;
using Nook.App.ViewModels;

namespace Nook.App.Views;

public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

    void OnScrimPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is MainViewModel vm) vm.IsSettingsOpen = false;
    }
}
