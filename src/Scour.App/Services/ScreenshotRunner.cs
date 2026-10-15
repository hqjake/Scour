using System.IO;
using System.Runtime.InteropServices;
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

    public static void OpenLog(string dir)
    {
        Directory.CreateDirectory(dir);
        _log ??= new StreamWriter(Path.Combine(dir, "log.txt"), append: false) { AutoFlush = true };
    }

    public static async Task RunAsync(MainWindow window, MainViewModel vm, string dir)
    {
        OpenLog(dir);
        Log($"Window {window.ActualWidth}x{window.ActualHeight}");
        Log($"Scour screenshot run on {Environment.OSVersion}, {Environment.ProcessorCount} CPUs, elevated={Environment.IsPrivilegedProcess}");
        try
        {
            await Task.Delay(1500);
            await Shot(window, dir, "dashboard", vm.Dashboard, page => WaitAsync(() => !((DashboardViewModel)page).IsChecking, 120));
            await Shot(window, dir, "uninstaller", vm.Programs, _ =>
            {
                // Show the details panel with a recognisable program selected.
                vm.Programs.Selected = vm.Programs.Programs.FirstOrDefault(p => p.Name.StartsWith("Git", StringComparison.OrdinalIgnoreCase))
                                       ?? vm.Programs.Programs.FirstOrDefault();
                return Task.Delay(1500); // icon loads
            });
            await PaletteShot(window, vm, dir);
            await Shot<AppUpdatesViewModel>(window, vm, dir, "app-updates", null, 180);
            await Shot<ReinstallKitViewModel>(window, vm, dir, "reinstall-kit", null, 240);
            await Shot<JunkViewModel>(window, vm, dir, "junk-cleaner", p => p.ScanCommand.ExecuteAsync(null), 180);
            await Shot<BloatwareViewModel>(window, vm, dir, "bloatware", null, 120);
            await Shot<PrivacyViewModel>(window, vm, dir, "privacy-tweaks", null);
            await Shot<BrowserPrivacyViewModel>(window, vm, dir, "browser-privacy", null);
            await Shot<BrowserExtensionsViewModel>(window, vm, dir, "browser-extensions", null);
            await Shot<FileFinderViewModel>(window, vm, dir, "duplicates", null);
            await Shot<RegistryCleanerViewModel>(window, vm, dir, "registry-cleaner", p => p.ScanCommand.ExecuteAsync(null), 180);
            await Shot<SystemInfoViewModel>(window, vm, dir, "system-info", null, 120);
            await Shot<RepairViewModel>(window, vm, dir, "repair-center", null);
            await Shot<AutorunViewModel>(window, vm, dir, "startup", null);
            await Shot<ContextMenuViewModel>(window, vm, dir, "context-menu", null);
            await Shot<NetworkViewModel>(window, vm, dir, "network", null, 90);
            await Shot<DriversViewModel>(window, vm, dir, "drivers", null, 180);
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
            _log?.Dispose();
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
            await CaptureAsync(w, Path.Combine(dir, name + ".png"));
            Log($"{name}: saved ({page.StatusText})");
        }
        catch (Exception e)
        {
            Log($"{name}: ERROR {e}");
        }
    }

    /// <summary>The Ctrl+K palette with a search typed in.</summary>
    private static async Task PaletteShot(MainWindow w, MainViewModel vm, string dir)
    {
        try
        {
            Log("command-palette: opening");
            vm.Navigate(vm.Dashboard);
            vm.Palette.Open();
            await Task.Delay(400);
            vm.Palette.Query = "disk";
            await Task.Delay(1000);
            await CaptureAsync(w, Path.Combine(dir, "command-palette.png"));
            Log($"command-palette: saved ({vm.Palette.Results.Count} results)");
        }
        catch (Exception e)
        {
            Log($"command-palette: ERROR {e}");
        }
        finally
        {
            vm.Palette.Close();
            await Task.Delay(300);
        }
    }

    private static async Task WaitAsync(Func<bool> condition, int timeoutSeconds)
    {
        var until = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        await Task.Delay(300);
        while (!condition() && DateTime.UtcNow < until) await Task.Delay(250);
    }

    private const double ShotWidth = 1440, ShotHeight = 900;

    private static Task CaptureAsync(Window w, string path)
    {
        var content = (FrameworkElement)w.Content;
        content.UpdateLayout();
        Render(content, path);
        return Task.CompletedTask;
    }

    // ---- display resolution (CI machines start at 1024×768, too small for a 1440×900 window)

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public int dmFields, dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettings(string? device, int mode, ref DEVMODE dm);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ChangeDisplaySettings(ref DEVMODE dm, int flags);

    /// <summary>Raises the screen resolution so the screenshot window fits. Only used in --screenshots mode.</summary>
    public static void EnsureScreenSize()
    {
        var dm = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
        if (!EnumDisplaySettings(null, -1, ref dm)) return;
        if (dm.dmPelsWidth >= 1600 && dm.dmPelsHeight >= 1000) return;
        foreach (var (w, h) in new[] { (1920, 1080), (1920, 1200), (1680, 1050), (1600, 1200), (1600, 1024) })
        {
            dm.dmPelsWidth = w;
            dm.dmPelsHeight = h;
            dm.dmFields = 0x80000 | 0x100000; // DM_PELSWIDTH | DM_PELSHEIGHT
            int result = ChangeDisplaySettings(ref dm, 0);
            Log($"Display {w}x{h}: {(result == 0 ? "ok" : "failed (" + result + ")")}");
            if (result == 0) return;
        }
    }

    private static void Render(FrameworkElement content, string path)
    {
        var bmp = new RenderTargetBitmap((int)Math.Round(content.ActualWidth), (int)Math.Round(content.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bmp.Render(content);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        using var f = File.Create(path);
        encoder.Save(f);
    }
}
