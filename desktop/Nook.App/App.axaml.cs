using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Nook.App.ViewModels;
using Nook.App.Views;

namespace Nook.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var vm = new MainViewModel(AppServices.CreateDefault());
            desktop.MainWindow = new MainWindow { DataContext = vm };
            _ = vm.BootAsync();
        }
        base.OnFrameworkInitializationCompleted();
    }

    public static void ApplyTheme(string mode)
    {
        if (Current == null) return;
        Current.RequestedThemeVariant = mode switch
        {
            "light" => ThemeVariant.Light,
            "dark" => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };
    }
}
