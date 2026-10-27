using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Scour.Core.Platform;

namespace Scour.App.Platform;

public sealed record LockingProcess(int Pid, string Name, string? ServiceName, string? ExecutablePath);

public sealed record FileLock(int Pid, string ProcessName, IntPtr Handle, string LockedPath);

/// <summary>
/// Finds which processes hold a file or folder open. Restart Manager names the processes;
/// the system handle table then pinpoints the exact handles so they can be closed.
/// </summary>
public static class LockFinder
{
    // Access masks of synchronous pipe handles that can hang name queries.
    private static readonly HashSet<uint> HangingAccess = [0x0012019F, 0x001A019F, 0x00120189, 0x00100000, 0x0016019F];

    public static List<string> ExpandTargets(string path, int maxFiles = 3000)
    {
        if (File.Exists(path)) return [path];
        if (!Directory.Exists(path)) return [];
        var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 };
        return Directory.EnumerateFiles(path, "*", opts).Take(maxFiles).ToList();
    }

    public static List<LockingProcess> FindProcesses(IReadOnlyList<string> files)
    {
        var result = new List<LockingProcess>();
        if (files.Count == 0) return result;
        var key = new StringBuilder(64);
        if (ProcessNative.RmStartSession(out var session, 0, key) != 0) return result;
        try
        {
            foreach (var batch in files.Chunk(500))
                if (ProcessNative.RmRegisterResources(session, (uint)batch.Length, batch, 0, null, 0, null) != 0) return result;

            uint count = 0, reasons = 0;
            int rc = ProcessNative.RmGetList(session, out var needed, ref count, null, ref reasons);
            if (rc != 234 /* ERROR_MORE_DATA */ || needed == 0) return result;
            var info = new ProcessNative.RM_PROCESS_INFO[needed];
            count = needed;
            if (ProcessNative.RmGetList(session, out _, ref count, info, ref reasons) != 0) return result;
            foreach (var i in info.Take((int)count))
            {
                int pid = i.Process.dwProcessId;
                result.Add(new LockingProcess(pid, i.strAppName, string.IsNullOrEmpty(i.strServiceShortName) ? null : i.strServiceShortName,
                                              ProcessRunner.GetProcessPath((uint)pid)));
            }
        }
        finally
        {
            ProcessNative.RmEndSession(session);
        }
        return result;
    }

    /// <summary>Open file handles under <paramref name="target"/>, in the given processes (or all if null).</summary>
    public static List<FileLock> FindHandles(string target, IReadOnlySet<int>? pids, CancellationToken ct = default)
    {
        target = WinPath.Normalize(Path.GetFullPath(target));
        var locks = new List<FileLock>();
        var fileType = FileObjectTypeIndex();
        if (fileType is null) return locks;

        var processHandles = new Dictionary<int, IntPtr>();
        var names = new Dictionary<int, string>();
        try
        {
            foreach (var entry in SystemHandles())
            {
                ct.ThrowIfCancellationRequested();
                int pid = (int)entry.UniqueProcessId;
                if (entry.ObjectTypeIndex != fileType || pid == Environment.ProcessId || pid <= 4) continue;
                if (pids is not null && !pids.Contains(pid)) continue;
                if (HangingAccess.Contains(entry.GrantedAccess)) continue;

                if (!processHandles.TryGetValue(pid, out var hProc))
                {
                    hProc = ProcessNative.OpenProcess(ProcessNative.PROCESS_DUP_HANDLE, false, (uint)pid);
                    processHandles[pid] = hProc;
                }
                if (hProc == IntPtr.Zero) continue;

                var path = HandlePath(hProc, entry.HandleValue);
                if (path is null || !WinPath.IsSameOrUnder(path, target)) continue;
                if (!names.TryGetValue(pid, out var name))
                {
                    try { using var p = Process.GetProcessById(pid); name = p.ProcessName; }
                    catch { name = $"PID {pid}"; }
                    names[pid] = name;
                }
                locks.Add(new FileLock(pid, name, entry.HandleValue, path));
            }
        }
        finally
        {
            foreach (var h in processHandles.Values.Where(h => h != IntPtr.Zero)) ProcessNative.CloseHandle(h);
        }
        return locks;
    }

    /// <summary>Closes a handle inside another process. The process may misbehave afterwards.</summary>
    public static void CloseHandle(FileLock l)
    {
        var hProc = ProcessNative.OpenProcess(ProcessNative.PROCESS_DUP_HANDLE, false, (uint)l.Pid);
        if (hProc == IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
        try
        {
            if (!ProcessNative.DuplicateHandle(hProc, l.Handle, IntPtr.Zero, out _, 0, false, ProcessNative.DUPLICATE_CLOSE_SOURCE))
                throw new System.ComponentModel.Win32Exception();
        }
        finally { ProcessNative.CloseHandle(hProc); }
    }

    private static string? HandlePath(IntPtr hProc, IntPtr handle)
    {
        if (!ProcessNative.DuplicateHandle(hProc, handle, ProcessNative.GetCurrentProcess(), out var dup, 0, false, ProcessNative.DUPLICATE_SAME_ACCESS))
            return null;
        var (finished, path) = NameQueries.Query(dup);
        // A query that never returns still uses the duplicate; closing it under the stuck call could hand
        // the same handle value to something else, so it's deliberately left open.
        if (finished) ProcessNative.CloseHandle(dup);
        return path;
    }

    private static string? QueryName(IntPtr dup)
    {
        if (ProcessNative.GetFileType(dup) != ProcessNative.FILE_TYPE_DISK) return null;
        var sb = new StringBuilder(1024);
        uint len = ProcessNative.GetFinalPathNameByHandle(dup, sb, (uint)sb.Capacity, 0);
        if (len == 0 || len >= sb.Capacity) return null;
        var p = sb.ToString();
        if (p.StartsWith(@"\\?\UNC\", StringComparison.Ordinal)) p = @"\\" + p[8..];
        else if (p.StartsWith(@"\\?\", StringComparison.Ordinal)) p = p[4..];
        return p;
    }

    /// <summary>
    /// Runs name queries on one dedicated thread with a time budget each. Some handles (synchronous pipes)
    /// block the query forever: that thread is then abandoned and a fresh one takes over. Using the thread
    /// pool instead let a few stuck queries starve every later one, so scans took minutes and found nothing.
    /// </summary>
    private static class NameQueries
    {
        private const int MaxAbandoned = 16;
        private static readonly object Gate = new();
        private static System.Collections.Concurrent.BlockingCollection<Request>? _queue;
        private static int _abandoned;

        private sealed class Request(IntPtr handle)
        {
            public IntPtr Handle { get; } = handle;
            public string? Result;
            public readonly ManualResetEventSlim Done = new();
        }

        public static (bool Finished, string? Path) Query(IntPtr handle)
        {
            System.Collections.Concurrent.BlockingCollection<Request> queue;
            lock (Gate)
            {
                if (_abandoned >= MaxAbandoned) return (true, null); // too many stuck queries: stop asking
                queue = _queue ??= StartWorker();
            }
            var request = new Request(handle);
            try { queue.Add(request); }
            catch (InvalidOperationException) { return (true, null); } // another caller just retired this worker
            if (request.Done.Wait(250)) return (true, request.Result);
            lock (Gate)
            {
                // The worker is stuck on this handle: give later queries a new one.
                if (ReferenceEquals(_queue, queue))
                {
                    queue.CompleteAdding();
                    _queue = null;
                    _abandoned++;
                }
            }
            return (false, null);
        }

        private static System.Collections.Concurrent.BlockingCollection<Request> StartWorker()
        {
            var queue = new System.Collections.Concurrent.BlockingCollection<Request>();
            var thread = new Thread(() =>
            {
                foreach (var r in queue.GetConsumingEnumerable())
                {
                    try { r.Result = QueryName(r.Handle); }
                    catch { r.Result = null; }
                    r.Done.Set();
                }
            }) { IsBackground = true, Name = "Scour handle names" };
            thread.Start();
            return queue;
        }
    }

    private static ushort? FileObjectTypeIndex()
    {
        // Open a file ourselves and look up its entry to learn this Windows build's "File" type index.
        var temp = Path.GetTempFileName();
        try
        {
            using var fs = new FileStream(temp, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var mine = fs.SafeFileHandle.DangerousGetHandle();
            int self = Environment.ProcessId;
            foreach (var e in SystemHandles())
                if ((int)e.UniqueProcessId == self && e.HandleValue == mine) return e.ObjectTypeIndex;
            return null;
        }
        finally
        {
            try { File.Delete(temp); } catch { /* temp */ }
        }
    }

    private static List<ProcessNative.SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX> SystemHandles()
    {
        int size = 1 << 20;
        while (true)
        {
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                uint status = ProcessNative.NtQuerySystemInformation(ProcessNative.SystemExtendedHandleInformation, buffer, size, out int needed);
                if (status == ProcessNative.STATUS_INFO_LENGTH_MISMATCH)
                {
                    size = Math.Max(size * 2, needed + (1 << 16));
                    if (size > 1 << 30) return [];
                    continue;
                }
                if (status != 0) return [];
                long count = Marshal.ReadIntPtr(buffer).ToInt64();
                int entrySize = Marshal.SizeOf<ProcessNative.SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX>();
                var list = new List<ProcessNative.SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX>((int)Math.Min(count, 4_000_000));
                var start = buffer + IntPtr.Size * 2;
                for (long i = 0; i < count; i++)
                    list.Add(Marshal.PtrToStructure<ProcessNative.SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX>(start + (int)(i * entrySize)));
                return list;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }
}
