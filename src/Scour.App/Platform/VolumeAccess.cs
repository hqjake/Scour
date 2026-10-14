using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Scour.App.Platform;

/// <summary>Opens a drive for raw sector reads (needed to read the NTFS Master File Table directly).</summary>
public static class VolumeAccess
{
    private const uint GENERIC_READ = 0x80000000;
    private const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2;
    private const uint OPEN_EXISTING = 3;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    /// <param name="driveRoot">"C:\" or "C:".</param>
    public static Stream OpenVolume(string driveRoot)
    {
        var letter = driveRoot.TrimEnd('\\').TrimEnd(':');
        var handle = CreateFile($@"\\.\{letter}:", GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (handle.IsInvalid) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        // No managed buffering: every read the scanner issues is already sector aligned.
        return new FileStream(handle, FileAccess.Read, bufferSize: 0);
    }
}
