using System.Text;
using Scour.Core.Platform;

namespace Scour.Tests.Fakes;

public sealed class InMemoryFileSystem : IFileSystem
{
    private readonly HashSet<string> _dirs = new(WinPath.Comparer);
    private readonly Dictionary<string, (byte[] Data, DateTime Modified)> _files = new(WinPath.Comparer);
    public List<string> Recycled { get; } = [];
    public List<string> ScheduledOnReboot { get; } = [];
    public HashSet<string> Locked { get; } = new(WinPath.Comparer);

    public DateTime Now { get; set; } = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);

    private static string N(string p) => WinPath.Normalize(p);

    public InMemoryFileSystem Dir(string path)
    {
        CreateDirectory(path);
        return this;
    }

    public InMemoryFileSystem File(string path, int size = 10, DateTime? modified = null)
    {
        AddFile(path, new byte[size], modified);
        return this;
    }

    public InMemoryFileSystem File(string path, byte[] data)
    {
        AddFile(path, data, null);
        return this;
    }

    private void AddFile(string path, byte[] data, DateTime? modified)
    {
        path = N(path);
        CreateDirectory(WinPath.GetDirectoryName(path)!);
        _files[path] = (data, modified ?? Now);
    }

    public bool DirectoryExists(string path) => _dirs.Contains(N(path));
    public bool FileExists(string path) => _files.ContainsKey(N(path));

    public IEnumerable<string> EnumerateDirectories(string path)
    {
        path = N(path);
        return _dirs.Where(d => WinPath.AreSame(WinPath.GetDirectoryName(d) ?? "", path) && !WinPath.AreSame(d, path)).ToList();
    }

    public IEnumerable<string> EnumerateFiles(string path)
    {
        path = N(path);
        return _files.Keys.Where(f => WinPath.AreSame(WinPath.GetDirectoryName(f) ?? "", path)).ToList();
    }

    public long GetFileSize(string path) => _files[N(path)].Data.Length;
    public DateTime GetLastWriteTimeUtc(string path) => _files[N(path)].Modified;
    public byte[] ReadAllBytes(string path) => _files[N(path)].Data;
    public Stream OpenRead(string path) => new MemoryStream(ReadAllBytes(path), writable: false);
    public string ReadAllText(string path) => Encoding.UTF8.GetString(ReadAllBytes(path));
    public void WriteAllText(string path, string contents) => AddFile(path, Encoding.UTF8.GetBytes(contents), null);
    public void WriteAllBytes(string path, byte[] contents) => AddFile(path, contents, null);

    public void CreateDirectory(string path)
    {
        path = N(path);
        while (path.Length > 0 && _dirs.Add(path))
        {
            var parent = WinPath.GetDirectoryName(path);
            if (parent is null || WinPath.AreSame(parent, path)) break;
            path = parent;
        }
    }

    public void DeleteFile(string path, bool toRecycleBin)
    {
        if (Locked.Contains(N(path))) throw new IOException("The file is in use.");
        _files.Remove(N(path));
        if (toRecycleBin) Recycled.Add(N(path));
    }

    public void DeleteDirectory(string path, bool toRecycleBin)
    {
        path = N(path);
        if (_files.Keys.Any(f => Locked.Contains(f) && WinPath.IsSameOrUnder(f, path))) throw new IOException("A file is in use.");
        foreach (var f in _files.Keys.Where(f => WinPath.IsSameOrUnder(f, path)).ToList()) _files.Remove(f);
        _dirs.RemoveWhere(d => WinPath.IsSameOrUnder(d, path));
        if (toRecycleBin) Recycled.Add(path);
    }

    public void ScheduleDeleteOnReboot(string path) => ScheduledOnReboot.Add(N(path));
}
