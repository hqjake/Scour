namespace Scour.Core.Platform;

/// <summary>
/// File system surface used by the engine. Paths are Windows paths; enumeration methods
/// swallow access errors and return whatever they could read.
/// </summary>
public interface IFileSystem
{
    bool DirectoryExists(string path);
    bool FileExists(string path);
    IEnumerable<string> EnumerateDirectories(string path);
    IEnumerable<string> EnumerateFiles(string path);
    long GetFileSize(string path);
    DateTime GetLastWriteTimeUtc(string path);
    byte[] ReadAllBytes(string path);
    Stream OpenRead(string path);
    string ReadAllText(string path);
    void WriteAllText(string path, string contents);
    void WriteAllBytes(string path, byte[] contents);
    void CreateDirectory(string path);

    void DeleteFile(string path, bool toRecycleBin);
    void DeleteDirectory(string path, bool toRecycleBin);

    /// <summary>Marks a path for deletion at next boot (used for files that are in use).</summary>
    void ScheduleDeleteOnReboot(string path);
}

public static class FileSystemExtensions
{
    public static bool Exists(this IFileSystem fs, string path) => fs.FileExists(path) || fs.DirectoryExists(path);

    /// <summary>Total size of a directory tree, stopping after <paramref name="maxFiles"/> files.</summary>
    public static long GetDirectorySize(this IFileSystem fs, string path, int maxFiles = 200_000, CancellationToken ct = default)
    {
        long total = 0;
        int count = 0;
        var stack = new Stack<string>();
        stack.Push(path);
        while (stack.Count > 0 && count < maxFiles)
        {
            ct.ThrowIfCancellationRequested();
            var dir = stack.Pop();
            foreach (var f in fs.EnumerateFiles(dir))
            {
                try { total += fs.GetFileSize(f); } catch { /* vanished or locked */ }
                if (++count >= maxFiles) break;
            }
            foreach (var d in fs.EnumerateDirectories(dir)) stack.Push(d);
        }
        return total;
    }
}
