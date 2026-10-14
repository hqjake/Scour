using System.Windows;
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
