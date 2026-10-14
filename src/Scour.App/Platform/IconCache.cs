using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Scour.Core.Platform;
using Scour.Core.Programs;

namespace Scour.App.Platform;

/// <summary>Extracts and caches program icons. Safe to call from background threads (results are frozen).</summary>
public sealed class IconCache(KnownFolders folders)
{
    private readonly ConcurrentDictionary<string, ImageSource?> _cache = new(StringComparer.OrdinalIgnoreCase);

    public ImageSource? ForProgram(InstalledProgram p)
    {
        foreach (var candidate in Candidates(p))
        {
            var icon = ForFile(candidate.Path, candidate.Index);
            if (icon is not null) return icon;
        }
        return null;
    }

    private IEnumerable<(string Path, int Index)> Candidates(InstalledProgram p)
    {
        if (p.DisplayIcon is not null)
        {
            var (path, index) = CommandLine.ParseIconLocation(p.DisplayIcon);
            yield return (folders.Expand(path), index);
        }
        if (p.UninstallString is not null)
        {
            var exe = folders.Expand(CommandLine.Split(p.UninstallString).Executable);
            if (!Path.GetFileName(exe).StartsWith("msiexec", StringComparison.OrdinalIgnoreCase)) yield return (exe, 0);
        }
    }

    public ImageSource? ForFile(string path, int index = 0)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        return _cache.GetOrAdd($"{path}|{index}", _ => Load(path, index));
    }

    private static ImageSource? Load(string path, int index)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var large = new IntPtr[1];
            if (NativeMethods.ExtractIconEx(path, index, large, null, 1) > 0 && large[0] != IntPtr.Zero)
                return ToImage(large[0]);

            var info = new NativeMethods.SHFILEINFO();
            var res = NativeMethods.SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf(info), NativeMethods.SHGFI_ICON | NativeMethods.SHGFI_LARGEICON);
            if (res != IntPtr.Zero && info.hIcon != IntPtr.Zero) return ToImage(info.hIcon);
        }
        catch { /* bad icon resource */ }
        return null;
    }

    private static ImageSource? ToImage(IntPtr hIcon)
    {
        try
        {
            var src = Imaging.CreateBitmapSourceFromHIcon(hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }
        finally
        {
            NativeMethods.DestroyIcon(hIcon);
        }
    }
}
