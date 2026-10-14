using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;
using Scour.Core.Processes;

namespace Scour.App.Platform;

public sealed class ProcessSample
{
    public required int Pid { get; init; }
    public required int ParentPid { get; init; }
    public required string Name { get; init; }
    public string? Path { get; init; }
    public DateTime StartTime { get; init; }
    public double CpuPercent { get; init; }
    public long PrivateBytes { get; init; }
    public long WorkingSet { get; init; }
    public double IoBytesPerSec { get; init; }
    public double NetBytesPerSec { get; init; }
    public int Threads { get; init; }
}

public sealed record SystemSample(double CpuPercent, ulong MemoryUsed, ulong MemoryTotal, double IoBytesPerSec, double NetBytesPerSec, int ProcessCount);

/// <summary>Samples per-process and system-wide CPU, memory, I/O and network usage.</summary>
public sealed class ProcessMonitor : IDisposable
{
    private readonly RateTracker<(int, long)> _cpu = new();
    private readonly RateTracker<(int, long)> _io = new();
    private readonly RateTracker<(int, long)> _net = new();
    private readonly RateTracker<string> _sys = new();
    private readonly Dictionary<(int, long), string?> _paths = new();
    private readonly int _cores = Environment.ProcessorCount;
    private long _lastIdle, _lastKernel, _lastUser;

    public NetworkMonitor Network { get; } = new();

    public (List<ProcessSample> Processes, SystemSample System) Sample()
    {
        long now = DateTime.UtcNow.Ticks;
        var list = new List<ProcessSample>();
        var alive = new HashSet<(int, long)>();
        double totalIo = 0;

        foreach (var (pid, ppid, name, threads) in Snapshot())
        {
            if (pid == 0) continue;
            long creation = 0, kernel = 0, user = 0;
            long priv = 0, ws = 0, io = 0;
            string? path = null;
            var h = ProcessNative.OpenProcess(ProcessNative.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
            if (h != IntPtr.Zero)
            {
                try
                {
                    ProcessNative.GetProcessTimes(h, out creation, out _, out kernel, out user);
                    if (ProcessNative.GetProcessMemoryInfo(h, out var mem, (uint)Marshal.SizeOf<ProcessNative.PROCESS_MEMORY_COUNTERS_EX>()))
                    {
                        priv = (long)mem.PrivateUsage;
                        ws = (long)mem.WorkingSetSize;
                    }
                    if (ProcessNative.GetProcessIoCounters(h, out var ioc)) io = (long)(ioc.ReadTransferCount + ioc.WriteTransferCount + ioc.OtherTransferCount);
                }
                finally { ProcessNative.CloseHandle(h); }
            }
            var key = (pid, creation);
            alive.Add(key);
            if (!_paths.TryGetValue(key, out path))
            {
                path = ProcessRunner.GetProcessPath((uint)pid);
                _paths[key] = path;
            }
            double cpu = _cpu.Update(key, kernel + user, now) / TimeSpan.TicksPerSecond / _cores * 100.0;
            double ioRate = _io.Update(key, io, now);
            double netRate = Network.IsRunning ? _net.Update(key, Network.TotalBytes(pid), now) : 0;
            totalIo += ioRate;
            list.Add(new ProcessSample
            {
                Pid = pid,
                ParentPid = ppid,
                Name = name,
                Path = path,
                StartTime = creation == 0 ? DateTime.MinValue : DateTime.FromFileTimeUtc(creation),
                CpuPercent = Math.Min(100, cpu),
                PrivateBytes = priv,
                WorkingSet = ws,
                IoBytesPerSec = ioRate,
                NetBytesPerSec = netRate,
                Threads = threads,
            });
        }
        _cpu.Retain(alive);
        _io.Retain(alive);
        _net.Retain(alive);
        foreach (var k in _paths.Keys.Where(k => !alive.Contains(k)).ToList()) _paths.Remove(k);

        double sysCpu = 0;
        if (ProcessNative.GetSystemTimes(out var idle, out var k2, out var u2))
        {
            long dIdle = idle - _lastIdle, dTotal = (k2 - _lastKernel) + (u2 - _lastUser);
            if (_lastKernel != 0 && dTotal > 0) sysCpu = Math.Clamp(100.0 * (dTotal - dIdle) / dTotal, 0, 100);
            (_lastIdle, _lastKernel, _lastUser) = (idle, k2, u2);
        }
        var memStatus = new ProcessNative.MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<ProcessNative.MEMORYSTATUSEX>() };
        ProcessNative.GlobalMemoryStatusEx(ref memStatus);
        double net = _sys.Update("net", NicBytes(), now);

        return (list, new SystemSample(sysCpu, memStatus.ullTotalPhys - memStatus.ullAvailPhys, memStatus.ullTotalPhys, totalIo, net, list.Count));
    }

    private static long NicBytes()
    {
        long total = 0;
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                var s = nic.GetIPStatistics();
                total += s.BytesReceived + s.BytesSent;
            }
        }
        catch { /* adapters changing */ }
        return total;
    }

    private static List<(int Pid, int ParentPid, string Name, int Threads)> Snapshot()
    {
        var result = new List<(int, int, string, int)>();
        var snap = NativeMethods.CreateToolhelp32Snapshot(NativeMethods.TH32CS_SNAPPROCESS, 0);
        if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return result;
        try
        {
            var e = new NativeMethods.PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<NativeMethods.PROCESSENTRY32W>() };
            if (!NativeMethods.Process32FirstW(snap, ref e)) return result;
            do { result.Add(((int)e.th32ProcessID, (int)e.th32ParentProcessID, e.szExeFile, (int)e.cntThreads)); }
            while (NativeMethods.Process32NextW(snap, ref e));
        }
        finally { NativeMethods.CloseHandle(snap); }
        return result;
    }

    public void Dispose() => Network.Dispose();
}

/// <summary>Per-process network bytes from a kernel ETW session (needs admin rights).</summary>
public sealed class NetworkMonitor : IDisposable
{
    private readonly ConcurrentDictionary<int, long> _bytes = new();
    private TraceEventSession? _session;

    public bool IsRunning { get; private set; }
    public string? Error { get; private set; }

    public void Start()
    {
        if (IsRunning || _session is not null) return;
        try
        {
            _session = new TraceEventSession("Scour-Network") { StopOnDispose = true };
            _session.EnableKernelProvider(KernelTraceEventParser.Keywords.NetworkTCPIP);
            var k = _session.Source.Kernel;
            k.TcpIpRecv += d => Add(d.ProcessID, d.size);
            k.TcpIpSend += d => Add(d.ProcessID, d.size);
            k.TcpIpRecvIPV6 += d => Add(d.ProcessID, d.size);
            k.TcpIpSendIPV6 += d => Add(d.ProcessID, d.size);
            k.UdpIpRecv += d => Add(d.ProcessID, d.size);
            k.UdpIpSend += d => Add(d.ProcessID, d.size);
            k.UdpIpRecvIPV6 += d => Add(d.ProcessID, d.size);
            k.UdpIpSendIPV6 += d => Add(d.ProcessID, d.size);
            var thread = new Thread(() =>
            {
                try { _session.Source.Process(); }
                catch (Exception e) { Error = e.Message; IsRunning = false; }
            }) { IsBackground = true, Name = "Scour ETW" };
            thread.Start();
            IsRunning = true;
        }
        catch (Exception e)
        {
            Error = e.Message;
            _session?.Dispose();
            _session = null;
        }
    }

    private void Add(int pid, int size)
    {
        if (pid > 0 && size > 0) _bytes.AddOrUpdate(pid, size, (_, v) => v + size);
    }

    public long TotalBytes(int pid) => _bytes.TryGetValue(pid, out var v) ? v : 0;

    public void Dispose()
    {
        IsRunning = false;
        try { _session?.Dispose(); } catch { /* already stopped */ }
        _session = null;
    }
}
