using System.Windows;
using Microsoft.Win32;
using Scour.App.ViewModels;
using Scour.App.Views;

namespace Scour.App.Services;

/// <summary>Thin wrapper over message boxes and windows so view models stay free of view types.</summary>
public static class Dialogs
{
    private static Window? Owner => Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? Application.Current?.MainWindow;

    public static bool Confirm(string message, string title = "Scour")
    {
        var owner = Owner;
        var r = owner is null
            ? MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Question)
            : MessageBox.Show(owner, message, title, MessageBoxButton.YesNo, MessageBoxImage.Question);
        return r == MessageBoxResult.Yes;
    }

    public static bool ConfirmWarning(string message, string title = "Scour")
    {
        var owner = Owner;
        var r = owner is null
            ? MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No)
            : MessageBox.Show(owner, message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        return r == MessageBoxResult.Yes;
    }

    public static void Info(string message, string title = "Scour")
    {
        var owner = Owner;
        if (owner is null) MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information);
        else MessageBox.Show(owner, message, title, MessageBoxButton.OK, MessageBoxImage.Information);
    }

    public static void Error(string message, string title = "Scour")
    {
        var owner = Owner;
        if (owner is null) MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error);
        else MessageBox.Show(owner, message, title, MessageBoxButton.OK, MessageBoxImage.Error);
    }

    public static string? PickFolder(string title)
    {
        var dlg = new OpenFolderDialog { Title = title };
        return dlg.ShowDialog(Owner) == true ? dlg.FolderName : null;
    }

    public static string? PickFile(string title, string filter)
    {
        var dlg = new OpenFileDialog { Title = title, Filter = filter };
        return dlg.ShowDialog(Owner) == true ? dlg.FileName : null;
    }

    public static string? SaveFile(string title, string filter, string fileName)
    {
        var dlg = new SaveFileDialog { Title = title, Filter = filter, FileName = fileName };
        return dlg.ShowDialog(Owner) == true ? dlg.FileName : null;
    }

    public static void ShowUninstallWizard(UninstallWizardViewModel vm)
    {
        var w = new UninstallWindow { DataContext = vm, Owner = Application.Current.MainWindow };
        w.ShowDialog();
    }

    public static void ShowTraceInstall(TraceInstallViewModel vm)
    {
        var w = new TraceInstallWindow { DataContext = vm, Owner = Application.Current.MainWindow };
        w.ShowDialog();
    }
}
