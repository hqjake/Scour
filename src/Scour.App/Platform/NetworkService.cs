using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.Win32;
using Scour.Core.Network;

namespace Scour.App.Platform;

public sealed record AdapterInfo(int Index, string Name, string Description, string Kind, IReadOnlyList<string> Dns, bool IsStatic, string? Address)
{
    public string DnsText => Dns.Count == 0 ? "—" : string.Join(", ", Dns.Take(2));
    public string PresetName => !IsStatic ? "Automatic" : DnsPreset.Match(Dns)?.Name ?? "Custom";
}

public static class NetworkService
{
    // ---- adapters and DNS

    public static List<AdapterInfo> ActiveAdapters()
    {
        var list = new List<AdapterInfo>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            IPInterfaceProperties props;
            int index;
            try
            {
                props = nic.GetIPProperties();
                index = props.GetIPv4Properties()?.Index ?? -1;
            }
            catch (NetworkInformationException) { continue; }
            if (index < 0 || props.GatewayAddresses.Count == 0) continue; // not connected to anything
            var dns = props.DnsAddresses.Where(a => !a.IsIPv6SiteLocal && !a.IsIPv6LinkLocal).Select(a => a.ToString()).ToList();
            var address = props.UnicastAddresses.FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork)?.Address.ToString();
            var kind = nic.NetworkInterfaceType switch
            {
                NetworkInterfaceType.Wireless80211 => "Wi-Fi",
                NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT => "Ethernet",
                _ => nic.NetworkInterfaceType.ToString(),
            };
            list.Add(new AdapterInfo(index, nic.Name, nic.Description, kind, dns, IsStaticDns(nic.Id), address));
        }
        return list;
    }

    private static bool IsStaticDns(string id)
    {
        foreach (var family in new[] { "Tcpip", "Tcpip6" })
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{family}\Parameters\Interfaces\{id}");
            if (key?.GetValue("NameServer") is string s && s.Trim().Length > 0) return true;
        }
        return false;
    }

    public static async Task SetDnsAsync(IEnumerable<int> interfaceIndexes, DnsPreset preset)
    {
        var indexes = string.Join(",", interfaceIndexes);
        var servers = string.Join(",", preset.Servers.Select(s => $"'{s}'"));
        var action = preset.IsAutomatic ? "-ResetServerAddresses" : $"-ServerAddresses @({servers})";
        var script = $"$ErrorActionPreference = 'Stop'; foreach ($i in @({indexes})) {{ Set-DnsClientServerAddress -InterfaceIndex $i {action} }}; Clear-DnsClientCache";
        var (code, output) = await ProcessRunner.PowerShellAsync(script, TimeSpan.FromSeconds(60));
        if (code != 0) throw new InvalidOperationException(output.Trim().Split('\n').FirstOrDefault(l => l.Trim().Length > 0) ?? $"PowerShell exited with {code}");
    }

    private static readonly string[] ProbeNames = ["www.microsoft.com", "www.wikipedia.org", "www.github.com", "www.cloudflare.com"];

    /// <summary>Median time for a DNS lookup against one server, or null if it didn't answer.</summary>
    public static async Task<double?> ProbeAsync(string server, CancellationToken ct = default)
    {
        if (!IPAddress.TryParse(server, out var ip)) return null;
        var times = new List<double>();
        using var udp = new UdpClient(ip.AddressFamily);
        for (int i = 0; i < 4; i++)
        {
            ushort id = (ushort)Random.Shared.Next(1, ushort.MaxValue);
            // Popular names: they're cached at every big resolver, so this times the network path, not the lookup.
            var query = DnsProbe.BuildQuery(id, ProbeNames[i]);
            var sw = Stopwatch.StartNew();
            try
            {
                await udp.SendAsync(query, new IPEndPoint(ip, 53), ct);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(1500);
                while (true)
                {
                    var r = await udp.ReceiveAsync(timeout.Token);
                    if (DnsProbe.IsAnswer(r.Buffer, id)) break;
                }
                if (i > 0) times.Add(sw.Elapsed.TotalMilliseconds); // the first lookup warms up the route
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { /* timed out */ }
            catch (SocketException) { return null; }
        }
        if (times.Count == 0) return null;
        times.Sort();
        return times[times.Count / 2];
    }

    // ---- Wi-Fi

    public static async Task<List<WifiProfile>> SavedWifiAsync()
    {
        var dir = Path.Combine(Path.GetTempPath(), "scour-wifi-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var (code, output) = await ProcessRunner.RunHiddenAsync("netsh.exe", $"wlan export profile key=clear folder=\"{dir}\"", TimeSpan.FromSeconds(30));
            var list = new List<WifiProfile>();
            foreach (var file in Directory.EnumerateFiles(dir, "*.xml"))
            {
                try
                {
                    if (WifiProfiles.Parse(await File.ReadAllTextAsync(file)) is { } p) list.Add(p);
                }
                catch (System.Xml.XmlException) { /* skip */ }
            }
            if (list.Count == 0 && code != 0)
                throw new InvalidOperationException("This PC has no Wi-Fi adapter, or the WLAN service isn't running.");
            return list.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }
        finally
        {
            // The exported files contain the passwords in plain text.
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    // ---- hosts

    public static string HostsPath => Path.Combine(Environment.SystemDirectory, @"drivers\etc\hosts");

    public static string ReadHosts() => File.Exists(HostsPath) ? File.ReadAllText(HostsPath) : HostsFile.Default;

    public static void WriteHosts(string text)
    {
        var backup = HostsPath + ".scour-backup";
        if (File.Exists(HostsPath) && !File.Exists(backup)) File.Copy(HostsPath, backup);
        File.WriteAllText(HostsPath, text);
        try { Process.Start(new ProcessStartInfo(ProcessRunner.ToolPath("ipconfig.exe"), "/flushdns") { CreateNoWindow = true, UseShellExecute = false }); } catch { /* cache expires anyway */ }
    }

    // ---- speed test (Cloudflare's public speed test endpoints)

    private static HttpClient CreateClient() => new() { Timeout = TimeSpan.FromSeconds(30) };

    public static async Task<Dictionary<string, string>> TraceAsync(CancellationToken ct = default)
    {
        using var http = CreateClient();
        return CloudflareTrace.Parse(await http.GetStringAsync("https://www.cloudflare.com/cdn-cgi/trace", ct));
    }

    public static async Task<double?> PingAsync(string host = "1.1.1.1")
    {
        using var ping = new Ping();
        var times = new List<long>();
        for (int i = 0; i < 5; i++)
        {
            try
            {
                var r = await ping.SendPingAsync(host, 1500);
                if (r.Status == IPStatus.Success) times.Add(r.RoundtripTime);
            }
            catch (PingException) { /* blocked */ }
        }
        if (times.Count == 0) return null;
        times.Sort();
        return times[times.Count / 2];
    }

    /// <summary>Downloads for about <paramref name="duration"/> and reports Mbps as it goes.</summary>
    public static async Task<double> DownloadAsync(TimeSpan duration, IProgress<double> progress, CancellationToken ct)
    {
        using var http = CreateClient();
        var sw = Stopwatch.StartNew();
        long total = 0;
        var buffer = new byte[64 * 1024];
        while (sw.Elapsed < duration)
        {
            using var response = await http.GetAsync("https://speed.cloudflare.com/__down?bytes=25000000", HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            int n;
            while ((n = await stream.ReadAsync(buffer, ct)) > 0)
            {
                total += n;
                progress.Report(CloudflareTrace.Mbps(total, sw.Elapsed));
                if (sw.Elapsed >= duration) break;
            }
        }
        return CloudflareTrace.Mbps(total, sw.Elapsed);
    }

    public static async Task<double> UploadAsync(TimeSpan duration, IProgress<double> progress, CancellationToken ct)
    {
        using var http = CreateClient();
        var payload = new byte[4 * 1024 * 1024];
        Random.Shared.NextBytes(payload);
        var sw = Stopwatch.StartNew();
        long total = 0;
        while (sw.Elapsed < duration)
        {
            using var content = new ByteArrayContent(payload);
            using var response = await http.PostAsync("https://speed.cloudflare.com/__up", content, ct);
            response.EnsureSuccessStatusCode();
            total += payload.Length;
            progress.Report(CloudflareTrace.Mbps(total, sw.Elapsed));
        }
        return CloudflareTrace.Mbps(total, sw.Elapsed);
    }
}
