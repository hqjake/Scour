using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using Scour.Core.Platform;
using Scour.Core.Removal;

namespace Scour.App.Platform;

/// <summary>System Restore checkpoints.</summary>
public static class RestorePoint
{
    private const string Key = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore";
    private const string Frequency = "SystemRestorePointCreationFrequency";

    public static async Task<(bool Ok, string Message)> CreateAsync(string description, CancellationToken ct = default)
    {
        // Windows silently skips a checkpoint if one was made in the last 24 hours; lift that
        // limit for this one call and put the original setting back afterwards.
        object? previous = null;
        try
        {
            using var k = Registry.LocalMachine.CreateSubKey(Key, writable: true);
            previous = k.GetValue(Frequency);
            k.SetValue(Frequency, 0, RegistryValueKind.DWord);
        }
        catch { /* not fatal */ }

        try
        {
            var safe = description.Replace("'", "''");
            var (code, output) = await ProcessRunner.PowerShellAsync(
                $"Checkpoint-Computer -Description '{safe}' -RestorePointType 'APPLICATION_UNINSTALL' -ErrorAction Stop",
                TimeSpan.FromMinutes(5), ct);
            if (code == 0) return (true, "Restore point created.");
            var msg = output.Trim();
            if (msg.Contains("disabled", StringComparison.OrdinalIgnoreCase) || msg.Contains("0x80070422"))
                msg = "System Protection is turned off for the system drive.";
            return (false, $"Could not create a restore point: {FirstLine(msg)}");
        }
        catch (Exception e)
        {
            return (false, $"Could not create a restore point: {e.Message}");
        }
        finally
        {
            try
            {
                using var k = Registry.LocalMachine.CreateSubKey(Key, writable: true);
                if (previous is null) k.DeleteValue(Frequency, false);
                else k.SetValue(Frequency, previous, RegistryValueKind.DWord);
            }
            catch { /* best effort */ }
        }
    }

    private static string FirstLine(string s)
    {
        var line = s.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "unknown error";
        return line.Length > 200 ? line[..200] + "…" : line;
    }
}

public sealed class WindowsServiceController : IServiceController
{
    public void StopAndDelete(string serviceName)
    {
        Run("stop", serviceName);
        Thread.Sleep(1000);
        Run("delete", serviceName);
    }

    private static void Run(string verb, string service)
    {
        var psi = new ProcessStartInfo("sc.exe", $"{verb} \"{service}\"") { CreateNoWindow = true, UseShellExecute = false };
        using var p = Process.Start(psi);
        p?.WaitForExit(15_000);
    }
}

public static class RecycleBin
{
    public static (long Bytes, long Items) Query()
    {
        var info = new NativeMethods.SHQUERYRBINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.SHQUERYRBINFO>() };
        return NativeMethods.SHQueryRecycleBin(null, ref info) == 0 ? (info.i64Size, info.i64NumItems) : (0, 0);
    }

    public static bool Empty() =>
        NativeMethods.SHEmptyRecycleBin(IntPtr.Zero, null,
            NativeMethods.SHERB_NOCONFIRMATION | NativeMethods.SHERB_NOPROGRESSUI | NativeMethods.SHERB_NOSOUND) == 0;
}

/// <summary>Opening things in Explorer, regedit and the browser.</summary>
public static class Shell
{
    public static void OpenFolder(string path)
    {
        if (File.Exists(path)) Start("explorer.exe", $"/select,\"{path}\"");
        else if (Directory.Exists(path)) Start("explorer.exe", $"\"{path}\"");
        else if (Path.GetDirectoryName(path) is { } parent && Directory.Exists(parent)) Start("explorer.exe", $"\"{parent}\"");
    }

    public static void OpenRegistryKey(RegKeyPath key)
    {
        // Regedit opens at the last key it showed, so point that at our key first.
        using (var k = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Applets\Regedit"))
            k.SetValue("LastKey", "Computer\\" + key.LongPath);
        Start("regedit.exe", "-m");
    }

    public static void OpenUrl(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

    public static void SearchWeb(string query) => OpenUrl("https://www.bing.com/search?q=" + Uri.EscapeDataString(query));

    public static void OpenRecycleBin() => Start("explorer.exe", "shell:RecycleBinFolder");

    private static void Start(string exe, string args) => Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = true });
}
