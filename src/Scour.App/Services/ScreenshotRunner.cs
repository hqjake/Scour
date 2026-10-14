using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.Input;
using Scour.App.ViewModels;
using Scour.App.Views;

namespace Scour.App.Services;

/// <summary>
/// "--screenshots &lt;folder&gt;": walks through the app with real data and renders each page to PNG.
/// Used by CI to keep the README screenshots current; doubles as a smoke test.
/// </summary>
public static class ScreenshotRunner
{
    private static StreamWriter? _log;

    public static void Log(string message)
    {
        try { _log?.WriteLine($"{DateTime.Now:HH:mm:ss}  {message}"); } catch { /* logging must never throw */ }
    }

    public static async Task RunAsync(MainWindow window, MainViewModel vm, string dir)
    {
        Directory.CreateDirectory(dir);
        _log = new StreamWriter(Path.Combine(dir, "log.txt"), append: false) { AutoFlush = true };
        Log($"Scour screenshot run on {Environment.OSVersion}, {Environment.ProcessorCount} CPUs, elevated={Environment.IsPrivilegedProcess}");
        try
        {
            await Task.Delay(1500);
            await Shot(window, dir, "dashboard", vm.Dashboard, page => WaitAsync(() => !((DashboardViewModel)page).IsChecking, 120));
            await Shot(window, dir, "uninstaller", vm.Programs, null);
            await Shot<AppUpdatesViewModel>(window, vm, dir, "app-updates", null, 180);
            await Shot<JunkViewModel>(window, vm, dir, "junk-cleaner", p => p.ScanCommand.ExecuteAsync(null), 180);
            await Shot<BrowserPrivacyViewModel>(window, vm, dir, "browser-privacy", null);
            await Shot<FileFinderViewModel>(window, vm, dir, "duplicates", null);
            await Shot<RegistryCleanerViewModel>(window, vm, dir, "registry-cleaner", p => p.ScanCommand.ExecuteAsync(null), 180);
            await Shot<SystemInfoViewModel>(window, vm, dir, "system-info", null, 120);
            await Shot<RepairViewModel>(window, vm, dir, "repair-center", null);
            await Shot<AutorunViewModel>(window, vm, dir, "startup", null);
            await Shot<DiskAnalyzerViewModel>(window, vm, dir, "disk-analyzer", p => p.ScanCommand.ExecuteAsync(null), 600);
            await Shot<UsbViewModel>(window, vm, dir, "usb-tools", null);
            await Shot(window, dir, "settings", vm.Settings, null);

            Log("Switching to the light theme");
            ThemeService.Apply(dark: false);
            await Shot(window, dir, "dashboard-light", vm.Dashboard, null);
            await Shot<DiskAnalyzerViewModel>(window, vm, dir, "disk-analyzer-light", null);
            ThemeService.Apply(dark: true);
            Log("Done");
        }
        catch (Exception e)
        {
            Log("FAILED: " + e);
        }
        finally
        {
            _log.Dispose();
            _log = null;
        }
    }

    private static Task Shot<T>(MainWindow w, MainViewModel vm, string dir, string name, Func<T, Task>? action, int timeoutSeconds = 60)
        where T : PageViewModel
    {
        var page = vm.Pages.OfType<T>().FirstOrDefault();
        if (page is null)
        {
            Log($"{name}: page not found");
            return Task.CompletedTask;
        }
        return Shot(w, dir, name, page, action is null ? null : p => action((T)p), timeoutSeconds);
    }

    private static async Task Shot(MainWindow w, string dir, string name, PageViewModel page, Func<PageViewModel, Task>? action, int timeoutSeconds = 60)
    {
        try
        {
            Log($"{name}: opening");
            ((MainViewModel)w.DataContext).Navigate(page);
            await WaitAsync(() => !page.IsLoading, timeoutSeconds);
            if (action is not null)
            {
                Log($"{name}: running action");
                var run = action(page);
                var finished = await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(timeoutSeconds)));
                if (finished != run) Log($"{name}: action timed out");
                else await run;
                await WaitAsync(() => !page.IsLoading, timeoutSeconds);
            }
            await Task.Delay(1400); // let page transitions and gauge animations settle
            Capture(w, Path.Combine(dir, name + ".png"));
            Log($"{name}: saved ({page.StatusText})");
        }
        catch (Exception e)
        {
            Log($"{name}: ERROR {e}");
        }
    }

    private static async Task WaitAsync(Func<bool> condition, int timeoutSeconds)
    {
        var until = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        await Task.Delay(300);
        while (!condition() && DateTime.UtcNow < until) await Task.Delay(250);
    }

    private static void Capture(Window w, string path)
    {
        var content = (FrameworkElement)w.Content;
        content.UpdateLayout();
        var dpi = VisualTreeHelper.GetDpi(content);
        var bmp = new RenderTargetBitmap(
            (int)Math.Ceiling(content.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(content.ActualHeight * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bmp.Render(content);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        using var f = File.Create(path);
        encoder.Save(f);
    }
}
