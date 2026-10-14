using System.ComponentModel;
using System.IO;
using System.Text;
using Scour.Core.Platform;

namespace Scour.App.Platform;

public sealed class WindowsFileSystem : IFileSystem
{
    private static readonly EnumerationOptions DirOptions = new()
    {
        IgnoreInaccessible = true,
        RecurseSubdirectories = false,
        // Junctions such as "Application Data" would loop or double count.
        AttributesToSkip = FileAttributes.ReparsePoint,
    };

    private static readonly EnumerationOptions FileOptions = new()
    {
        IgnoreInaccessible = true,
        RecurseSubdirectories = false,
        AttributesToSkip = 0,
    };

    public bool DirectoryExists(string path) => Directory.Exists(path);
    public bool FileExists(string path) => File.Exists(path);

    public IEnumerable<string> EnumerateDirectories(string path)
    {
        try { return Directory.EnumerateDirectories(path, "*", DirOptions).ToList(); }
        catch { return []; }
    }

    public IEnumerable<string> EnumerateFiles(string path)
    {
        try { return Directory.EnumerateFiles(path, "*", FileOptions).ToList(); }
        catch { return []; }
    }

    public long GetFileSize(string path) => new FileInfo(path).Length;
    public DateTime GetLastWriteTimeUtc(string path) => File.GetLastWriteTimeUtc(path);
    public byte[] ReadAllBytes(string path) => File.ReadAllBytes(path);
    public string ReadAllText(string path) => File.ReadAllText(path, Encoding.UTF8);
    public void WriteAllText(string path, string contents) => File.WriteAllText(path, contents, Encoding.UTF8);
    public void WriteAllBytes(string path, byte[] contents) => File.WriteAllBytes(path, contents);
    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    public void DeleteFile(string path, bool toRecycleBin)
    {
        if (toRecycleBin)
        {
            Recycle(path);
            if (File.Exists(path)) throw new IOException("The file is in use or access was denied.");
            return;
        }
        var attrs = File.GetAttributes(path);
        if ((attrs & FileAttributes.ReadOnly) != 0) File.SetAttributes(path, attrs & ~FileAttributes.ReadOnly);
        File.Delete(path);
    }

    public void DeleteDirectory(string path, bool toRecycleBin)
    {
        if (toRecycleBin)
        {
            Recycle(path);
            if (Directory.Exists(path))
            {
                // Recycle Bin refuses partial moves; remove what we can permanently and report the rest.
                DeletePermanently(path);
            }
            return;
        }
        DeletePermanently(path);
    }

    private static void DeletePermanently(string path)
    {
        int failures = 0;
        string? firstError = null;
        foreach (var file in SafeEnumerateFilesRecursive(path))
        {
            try
            {
                var attrs = File.GetAttributes(file);
                if ((attrs & (FileAttributes.ReadOnly | FileAttributes.System | FileAttributes.Hidden)) != 0)
                    File.SetAttributes(file, FileAttributes.Normal);
                File.Delete(file);
            }
            catch (Exception e)
            {
                failures++;
                firstError ??= $"{Path.GetFileName(file)}: {e.Message}";
            }
        }
        foreach (var dir in SafeEnumerateDirectoriesRecursive(path).OrderByDescending(d => d.Length).Append(path))
        {
            try
            {
                var info = new DirectoryInfo(dir);
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0) { info.Delete(); continue; }
                if ((info.Attributes & FileAttributes.ReadOnly) != 0) info.Attributes &= ~FileAttributes.ReadOnly;
                Directory.Delete(dir, recursive: false);
            }
            catch (Exception e)
            {
                firstError ??= e.Message;
                failures++;
            }
        }
        if (failures > 0) throw new IOException($"{failures} item(s) could not be deleted (in use or access denied). {firstError}");
    }

    private static IEnumerable<string> SafeEnumerateFilesRecursive(string root)
    {
        var opts = new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = true, AttributesToSkip = 0 };
        try { return Directory.EnumerateFiles(root, "*", opts).ToList(); }
        catch { return []; }
    }

    private static IEnumerable<string> SafeEnumerateDirectoriesRecursive(string root)
    {
        var opts = new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint };
        try { return Directory.EnumerateDirectories(root, "*", opts).ToList(); }
        catch { return []; }
    }

    private static void Recycle(string path)
    {
        var op = new NativeMethods.SHFILEOPSTRUCT
        {
            wFunc = NativeMethods.FO_DELETE,
            pFrom = path + "\0\0",
            fFlags = (ushort)(NativeMethods.FOF_ALLOWUNDO | NativeMethods.FOF_NOCONFIRMATION | NativeMethods.FOF_NOERRORUI | NativeMethods.FOF_SILENT),
        };
        NativeMethods.SHFileOperation(ref op);
    }

    public void ScheduleDeleteOnReboot(string path)
    {
        if (!NativeMethods.MoveFileEx(path, null, NativeMethods.MOVEFILE_DELAY_UNTIL_REBOOT))
            throw new Win32Exception();
    }
}
