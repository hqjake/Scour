using System.ComponentModel;
using System.IO;
using System.IO.Enumeration;
using System.Text;
using Scour.Core.Platform;

namespace Scour.App.Platform;

public sealed class WindowsFileSystem : IFileSystem
{
    private static readonly EnumerationOptions DirOptions = new()
    {
        IgnoreInaccessible = true,
        RecurseSubdirectories = false,
        AttributesToSkip = 0,
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
        try
        {
            return new FileSystemEnumerable<string>(path, (ref FileSystemEntry e) => e.ToFullPath(), DirOptions)
            {
                // The entry's attributes come with the listing; only reparse points need a closer look.
                ShouldIncludePredicate = (ref FileSystemEntry e) =>
                    e.IsDirectory && ((e.Attributes & FileAttributes.ReparsePoint) == 0 || !IsLink(e.ToFullPath())),
            }.ToList();
        }
        catch { return []; }
    }

    /// <summary>
    /// Junctions and symbolic links (such as "Application Data") would loop or double count. Other reparse
    /// points are ordinary folders — notably OneDrive's Desktop/Documents with Files On-Demand — and were
    /// being skipped along with them.
    /// </summary>
    private static bool IsLink(string dir)
    {
        try
        {
            var info = new DirectoryInfo(dir);
            return (info.Attributes & FileAttributes.ReparsePoint) != 0 && info.LinkTarget is not null;
        }
        catch { return true; }
    }

    public IEnumerable<string> EnumerateFiles(string path)
    {
        try { return Directory.EnumerateFiles(path, "*", FileOptions).ToList(); }
        catch { return []; }
    }

    public long GetFileSize(string path) => new FileInfo(path).Length;
    public DateTime GetLastWriteTimeUtc(string path) => File.GetLastWriteTimeUtc(path);
    public byte[] ReadAllBytes(string path)
    {
        // Share with writers: browsers and other apps keep their files open while running.
        using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var buffer = new MemoryStream();
        s.CopyTo(buffer);
        return buffer.ToArray();
    }
    public Stream OpenRead(string path)
    {
        // Reading a OneDrive "online-only" file downloads it; never do that just to compare files.
        const FileAttributes recallOnDataAccess = (FileAttributes)0x400000;
        if ((File.GetAttributes(path) & (recallOnDataAccess | FileAttributes.Offline)) != 0)
            throw new IOException("The file is only stored online.");
        return OpenShared(path);
    }

    private static FileStream OpenShared(string path) => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, System.IO.FileOptions.SequentialScan);

    public string ReadAllText(string path)
    {
        using var reader = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete),
                                            Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }
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
                throw new IOException("Some files in the folder are in use, so it could not be moved to the Recycle Bin.");
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

    // Both walks stop at junctions and symbolic links: the link itself is removed, never what it points to.

    private static IEnumerable<string> SafeEnumerateFilesRecursive(string root) =>
        WalkDirectories(root).Prepend(root).Where(d => !IsLink(d)).SelectMany(d =>
        {
            try { return Directory.EnumerateFiles(d, "*", FileOptions).ToList(); }
            catch { return []; }
        }).ToList();

    private static IEnumerable<string> SafeEnumerateDirectoriesRecursive(string root) => WalkDirectories(root).ToList();

    private static IEnumerable<string> WalkDirectories(string root)
    {
        var stack = new Stack<string>([root]);
        while (stack.Count > 0)
        {
            List<string> children;
            try { children = Directory.EnumerateDirectories(stack.Pop(), "*", DirOptions).ToList(); }
            catch { continue; }
            foreach (var d in children)
            {
                yield return d;
                if (!IsLink(d)) stack.Push(d);
            }
        }
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
