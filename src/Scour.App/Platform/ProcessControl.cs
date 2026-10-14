using System.Diagnostics;
using System.IO;
using System.Management;
using System.Security.Cryptography;

namespace Scour.App.Platform;

public static class ProcessControl
{
    public static void Suspend(int pid) => WithHandle(pid, ProcessNative.PROCESS_SUSPEND_RESUME, h => Check(ProcessNative.NtSuspendProcess(h)));
    public static void Resume(int pid) => WithHandle(pid, ProcessNative.PROCESS_SUSPEND_RESUME, h => Check(ProcessNative.NtResumeProcess(h)));

    public static void Kill(int pid, bool tree)
    {
        using var p = Process.GetProcessById(pid);
        p.Kill(entireProcessTree: tree);
    }

    public static void SetPriority(int pid, ProcessPriorityClass priority)
    {
        using var p = Process.GetProcessById(pid);
        p.PriorityClass = priority;
    }

    public static ProcessPriorityClass? GetPriority(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.PriorityClass;
        }
        catch { return null; }
    }

    public static ulong? GetAffinity(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return (ulong)p.ProcessorAffinity.ToInt64();
        }
        catch { return null; }
    }

    public static void SetAffinity(int pid, ulong mask)
    {
        if (mask == 0) throw new ArgumentException("Select at least one processor.");
        using var p = Process.GetProcessById(pid);
        p.ProcessorAffinity = new IntPtr(unchecked((long)mask));
    }

    public static string? GetCommandLine(int pid)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher($"SELECT CommandLine FROM Win32_Process WHERE ProcessId = {pid}");
            foreach (var o in searcher.Get()) return o["CommandLine"] as string;
        }
        catch { /* access denied or WMI unavailable */ }
        return null;
    }

    public static string Sha256(string path)
    {
        using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant();
    }

    public static void OpenVirusTotal(string path) => Shell.OpenUrl($"https://www.virustotal.com/gui/file/{Sha256(path)}");

    private static void WithHandle(int pid, uint access, Action<IntPtr> action)
    {
        var h = ProcessNative.OpenProcess(access, false, (uint)pid);
        if (h == IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
        try { action(h); }
        finally { ProcessNative.CloseHandle(h); }
    }

    private static void Check(int status)
    {
        if (status < 0) throw new InvalidOperationException($"The operation failed (NTSTATUS 0x{status:X8}).");
    }
}
