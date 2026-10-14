using System.Diagnostics;
using System.IO;
using System.ServiceProcess;
using System.Text;
using Microsoft.Win32;
using Scour.Core.Programs;

namespace Scour.App.Platform;

public enum StartupKind
{
    Automatic,
    AutomaticDelayed,
    Manual,
    Disabled,
    Boot,
    System,
}

public sealed class ServiceInfo
{
    public required string Name { get; init; }
    public required string DisplayName { get; init; }
    public string Description { get; init; } = "";
    public ServiceControllerStatus Status { get; init; }
    public StartupKind Startup { get; init; }
    public string ImagePath { get; init; } = "";
    public string? BinaryPath { get; init; }
    public string Account { get; init; } = "";
    public string Company { get; init; } = "";
    public bool IsMicrosoft => Company.Contains("Microsoft", StringComparison.OrdinalIgnoreCase);
    public bool CanStop { get; init; }
}

public static class ServiceManager
{
    private static readonly Dictionary<string, string> CompanyCache = new(StringComparer.OrdinalIgnoreCase);

    public static List<ServiceInfo> List()
    {
        var result = new List<ServiceInfo>();
        foreach (var sc in ServiceController.GetServices())
        {
            using (sc)
            {
                try { result.Add(Describe(sc)); }
                catch { /* service vanished or access denied */ }
            }
        }
        return result.OrderBy(s => s.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static ServiceInfo Describe(ServiceController sc)
    {
        using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{sc.ServiceName}");
        var image = key?.GetValue("ImagePath") as string ?? "";
        var description = ResolveIndirect(key?.GetValue("Description") as string ?? "");
        bool delayed = key?.GetValue("DelayedAutostart") is int d && d == 1;
        var serviceDll = key?.OpenSubKey("Parameters")?.GetValue("ServiceDll") as string;
        var binary = Environment.ExpandEnvironmentVariables(serviceDll ?? CommandLine.Split(image).Executable);
        var mode = sc.StartType;
        return new ServiceInfo
        {
            Name = sc.ServiceName,
            DisplayName = sc.DisplayName,
            Description = description,
            Status = sc.Status,
            Startup = mode switch
            {
                ServiceStartMode.Automatic => delayed ? StartupKind.AutomaticDelayed : StartupKind.Automatic,
                ServiceStartMode.Manual => StartupKind.Manual,
                ServiceStartMode.Disabled => StartupKind.Disabled,
                ServiceStartMode.Boot => StartupKind.Boot,
                _ => StartupKind.System,
            },
            ImagePath = image,
            BinaryPath = File.Exists(binary) ? binary : null,
            Account = key?.GetValue("ObjectName") as string ?? "",
            Company = Company(binary),
            CanStop = sc.CanStop,
        };
    }

    private static string Company(string path)
    {
        if (CompanyCache.TryGetValue(path, out var c)) return c;
        try { c = File.Exists(path) ? FileVersionInfo.GetVersionInfo(path).CompanyName ?? "" : ""; }
        catch { c = ""; }
        CompanyCache[path] = c;
        return c;
    }

    private static string ResolveIndirect(string s)
    {
        if (!s.StartsWith('@')) return s;
        var sb = new StringBuilder(1024);
        return ProcessNative.SHLoadIndirectString(s, sb, sb.Capacity, IntPtr.Zero) == 0 ? sb.ToString() : "";
    }

    public static async Task StartAsync(string name) => await Task.Run(() =>
    {
        using var sc = new ServiceController(name);
        sc.Start();
        sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
    });

    public static async Task StopAsync(string name) => await Task.Run(() =>
    {
        using var sc = new ServiceController(name);
        sc.Stop(stopDependentServices: true);
        sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
    });

    public static async Task RestartAsync(string name)
    {
        await StopAsync(name);
        await StartAsync(name);
    }

    public static async Task SetStartupAsync(string name, StartupKind kind)
    {
        var arg = kind switch
        {
            StartupKind.Automatic => "auto",
            StartupKind.AutomaticDelayed => "delayed-auto",
            StartupKind.Manual => "demand",
            StartupKind.Disabled => "disabled",
            _ => throw new ArgumentException("Boot and system start types can't be set here."),
        };
        var (code, output) = await ProcessRunner.RunHiddenAsync("sc.exe", $"config \"{name}\" start= {arg}", TimeSpan.FromSeconds(30));
        if (code != 0) throw new InvalidOperationException(output.Trim());
    }
}
