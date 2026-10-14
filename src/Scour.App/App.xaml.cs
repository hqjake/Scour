using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using Scour.App.Services;
using Scour.App.ViewModels;
using Scour.App.Views;

namespace Scour.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandledException;

        var services = new AppServices();
        SoundService.Enabled = services.Settings.SoundsEnabled;
        SoundService.Initialize();
        ThemeService.Apply(services.Settings.DarkTheme);

        // Every button and toggle clicks; every window gets the themed title bar and neon border.
        EventManager.RegisterClassHandler(typeof(ButtonBase), ButtonBase.ClickEvent, new RoutedEventHandler((_, _) => SoundService.Play(UiSound.Click)));
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((s, _) => ThemeService.StyleWindow((Window)s)));

        var window = new MainWindow { DataContext = new MainViewModel(services) };
        MainWindow = window;
        window.Show();
    }

    private static void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Dialogs.Error("Something went wrong:\n\n" + e.Exception.Message);
        e.Handled = true;
    }
}
