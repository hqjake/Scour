using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Scour.App.Platform;
using Scour.App.Services;
using Scour.App.ViewModels;

namespace Scour.App.Views;

public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

    private MainViewModel Vm => (MainViewModel)DataContext;

    private void Settings_Click(object sender, RoutedEventArgs e) => Vm.CurrentPage = Vm.Settings;

    private void Hunter_MouseDown(object sender, MouseButtonEventArgs e)
    {
        HunterTarget.CaptureMouse();
        Mouse.OverrideCursor = Cursors.Cross;
        e.Handled = true;
    }

    private void Hunter_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!HunterTarget.IsMouseCaptured) return;
        HunterTarget.ReleaseMouseCapture();
        Mouse.OverrideCursor = null;
        e.Handled = true;

        var hit = Vm.Hunt();
        if (hit is null) return; // released over Scour itself or nothing
        ShowHunterMenu(hit);
    }

    private void ShowHunterMenu(HuntResult hit)
    {
        var menu = new ContextMenu { Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint };
        var title = hit.Program?.DisplayName ?? (hit.ExecutablePath is null ? $"Process {hit.ProcessId}" : System.IO.Path.GetFileName(hit.ExecutablePath));
        menu.Items.Add(new MenuItem { Header = title, IsEnabled = false, FontWeight = FontWeights.SemiBold });
        menu.Items.Add(new Separator());

        if (hit.Program is { } program)
        {
            Add(menu, "Uninstall…", () => Vm.UninstallFromHunter(program));
            Add(menu, "Show in program list", () => Vm.ShowInList(program));
        }
        else if (hit.ExecutablePath is not null)
        {
            Add(menu, "Not in the installed list — forced uninstall…", () => Vm.ForcedFromHunter(hit));
        }
        if (hit.ExecutablePath is { } exe)
        {
            Add(menu, "Open file location", () => Shell.OpenFolder(exe));
            Add(menu, "Search online", () => Shell.SearchWeb(hit.Program?.DisplayName ?? System.IO.Path.GetFileNameWithoutExtension(exe)));
        }
        Add(menu, "End process", () =>
        {
            if (Dialogs.Confirm($"End \"{title}\"? Unsaved work in it will be lost."))
            {
                try { Hunter.Kill(hit.ProcessId); }
                catch (Exception ex) { Dialogs.Error(ex.Message); }
            }
        });
        menu.IsOpen = true;
    }

    private static void Add(ContextMenu menu, string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        menu.Items.Add(item);
    }
}
