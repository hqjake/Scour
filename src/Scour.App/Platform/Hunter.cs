using System.Diagnostics;
using System.IO;
using System.Text;
using Scour.Core.Platform;
using Scour.Core.Programs;

namespace Scour.App.Platform;

public sealed record HuntResult(uint ProcessId, string? ExecutablePath, string WindowTitle, InstalledProgram? Program);

/// <summary>Identifies the program that owns the window under the mouse pointer.</summary>
public static class Hunter
{
    public static HuntResult? Identify(IEnumerable<InstalledProgram> programs)
    {
        if (!NativeMethods.GetCursorPos(out var pt)) return null;
        var hwnd = NativeMethods.WindowFromPoint(pt);
        if (hwnd == IntPtr.Zero) return null;
        var root = NativeMethods.GetAncestor(hwnd, NativeMethods.GA_ROOT);
        if (root != IntPtr.Zero) hwnd = root;

        NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == 0 || pid == (uint)Environment.ProcessId) return null;

        var title = new StringBuilder(512);
        NativeMethods.GetWindowText(hwnd, title, title.Capacity);
        var exe = ProcessRunner.GetProcessPath(pid);
        return new HuntResult(pid, exe, title.ToString(), exe is null ? null : FindOwner(exe, programs));
    }

    /// <summary>The installed program whose install folder contains <paramref name="exePath"/> (deepest match wins).</summary>
    public static InstalledProgram? FindOwner(string exePath, IEnumerable<InstalledProgram> programs)
    {
        var best = programs.Where(p => p.InstallLocation is not null && WinPath.IsSameOrUnder(exePath, p.InstallLocation))
                           .OrderByDescending(p => p.InstallLocation!.Length)
                           .FirstOrDefault();
        if (best is not null) return best;

        var exeName = Path.GetFileNameWithoutExtension(exePath);
        return programs.FirstOrDefault(p =>
            p.DisplayIcon is not null && WinPath.AreSame(CommandLine.ParseIconLocation(p.DisplayIcon).Path, exePath))
            ?? programs.FirstOrDefault(p => p.DisplayName.Equals(exeName, StringComparison.OrdinalIgnoreCase));
    }

    public static void Kill(uint pid)
    {
        using var p = Process.GetProcessById((int)pid);
        p.Kill(entireProcessTree: true);
    }
}
