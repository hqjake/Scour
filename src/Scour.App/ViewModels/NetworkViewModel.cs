using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Scour.App.Platform;
using Scour.App.Services;
using Scour.Core.Network;

namespace Scour.App.ViewModels;

public sealed partial class DnsPresetViewModel(DnsPreset preset) : ObservableObject
{
    public DnsPreset Preset { get; } = preset;
    public string Name => Preset.Name;
    public string Description => Preset.Description;
    public string Servers => Preset.IsAutomatic ? "From your router" : string.Join("  ·  ", Preset.Servers.Take(2));

    [ObservableProperty] private string _latency = "";
    [ObservableProperty] private bool _isFastest;
    [ObservableProperty] private bool _isCurrent;
}

public sealed partial class WifiItemViewModel(WifiProfile profile) : ObservableObject
{
    public WifiProfile Profile { get; } = profile;
    public string Name => Profile.Name;
    public string Security => Profile.Security;
    public bool HasPassword => Profile.Password is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PasswordText))]
    private bool _isRevealed;

    public string PasswordText => Profile.Password is null ? "—" : IsRevealed ? Profile.Password : "••••••••••";
}

public sealed partial class HostsItemViewModel(HostsEntry entry, Action<HostsItemViewModel, bool> toggle) : ObservableObject
{
    public HostsEntry Entry { get; } = entry;
    public string Address => Entry.Address;
    public string Hosts => string.Join(" ", Entry.Hosts);
    public string Comment => Entry.Comment;
    public string Meaning => Entry.Address is "0.0.0.0" or "127.0.0.1" or "::1" or "::" ? (Entry.Hosts.Contains("localhost") ? "This PC" : "Blocked") : "Redirected";

    private bool _enabled = entry.Enabled;

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value) return;
            _enabled = value;
            OnPropertyChanged();
            toggle(this, value);
        }
    }
}

/// <summary>DNS switcher, speed test, saved Wi-Fi passwords and the hosts file.</summary>
public sealed partial class NetworkViewModel : PageViewModel
{
    private CancellationTokenSource? _speedCts;
    private HostsFile? _hosts;

    public NetworkViewModel()
    {
        foreach (var p in DnsPreset.All) Presets.Add(new DnsPresetViewModel(p));
    }

    public override string Title => "Network";
    public override string Glyph => "";
    public override string Group => "System";

    // ---- DNS

    public ObservableCollection<AdapterInfo> Adapters { get; } = [];
    public ObservableCollection<DnsPresetViewModel> Presets { get; } = [];

    [ObservableProperty] private string _dnsStatus = "";
    [ObservableProperty] private bool _isTestingDns;

    protected override async Task LoadAsync()
    {
        // Each section loads on its own, so one failing (e.g. no network adapters readable) doesn't leave the
        // whole page empty.
        IsLoading = true;
        try
        {
            await RefreshAdaptersAsync();
            await TestDnsAsync();
        }
        catch (Exception e)
        {
            ErrorLog.Write("Reading network adapters", e);
            DnsStatus = "Couldn't read the network adapters: " + e.Message;
        }
        finally
        {
            IsLoading = false;
        }
        try { LoadHosts(); }
        catch (Exception e) { ErrorLog.Write("Reading the hosts file", e); }
        LoadTraceAsync().Forget("Looking up your connection", report: false);
    }

    private async Task RefreshAdaptersAsync()
    {
        var list = await Task.Run(NetworkService.ActiveAdapters);
        Adapters.Clear();
        foreach (var a in list) Adapters.Add(a);
        var currentNames = list.Select(a => a.PresetName).ToHashSet();
        foreach (var p in Presets) p.IsCurrent = currentNames.Contains(p.Name);
        StatusText = list.Count == 0 ? "No connected network adapters." : $"Connected through {string.Join(", ", list.Select(a => $"{a.Name} ({a.Kind})"))}.";
    }

    [RelayCommand]
    private async Task TestDnsAsync()
    {
        IsTestingDns = true;
        DnsStatus = "Timing each DNS service from here…";
        try
        {
            foreach (var p in Presets) { p.Latency = p.Preset.IsAutomatic ? "" : "…"; p.IsFastest = false; }
            // The router's DNS is whatever "Automatic" currently gives the first adapter.
            var auto = Adapters.FirstOrDefault(a => !a.IsStatic)?.Dns.FirstOrDefault(d => !d.Contains(':'));
            var tasks = Presets.Select(async p =>
            {
                var server = p.Preset.IsAutomatic ? auto : p.Preset.Primary;
                var ms = server is null ? null : await NetworkService.ProbeAsync(server);
                p.Latency = ms is null ? (server is null ? "" : "No answer") : $"{ms:0} ms";
                return (p, ms);
            }).ToList();
            var results = await Task.WhenAll(tasks);
            var fastest = results.Where(r => r.ms is not null).OrderBy(r => r.ms).FirstOrDefault();
            if (fastest.p is not null) fastest.p.IsFastest = true;
            DnsStatus = fastest.p is null
                ? "No DNS service answered — check the connection."
                : $"{fastest.p.Name} is fastest from here. Lower is better; anything under 30 ms feels instant.";
        }
        finally
        {
            IsTestingDns = false;
        }
    }

    [RelayCommand]
    private async Task UseDnsAsync(DnsPresetViewModel? preset)
    {
        if (preset is null || Adapters.Count == 0) return;
        var names = string.Join(", ", Adapters.Select(a => a.Name));
        if (!Dialogs.Confirm($"Use {preset.Name} DNS for {names}?\n\n{preset.Description}\n\nYou can switch back to Automatic here at any time.")) return;
        DnsStatus = $"Switching to {preset.Name}…";
        try
        {
            await NetworkService.SetDnsAsync(Adapters.Select(a => a.Index), preset.Preset);
            await RefreshAdaptersAsync();
            DnsStatus = $"Now using {preset.Name}. The DNS cache was cleared.";
            SoundService.Play(UiSound.Success);
        }
        catch (Exception e)
        {
            DnsStatus = "Couldn't change DNS: " + e.Message;
            SoundService.Play(UiSound.Warning);
        }
    }

    // ---- speed test

    [ObservableProperty] private double _downloadMbps;
    [ObservableProperty] private double _uploadMbps;
    [ObservableProperty] private string _pingText = "—";
    [ObservableProperty] private string _speedPhase = "";
    [ObservableProperty] private bool _isSpeedTesting;
    [ObservableProperty] private string _publicIp = "";
    [ObservableProperty] private string _location = "";

    /// <summary>Gauge position: a log scale so 10 Mbps and 1 Gbps both read sensibly (1 Gbps = full).</summary>
    public double DownloadGauge => Gauge(DownloadMbps);
    public double UploadGauge => Gauge(UploadMbps);
    public string DownloadText => DownloadMbps <= 0 ? "—" : DownloadMbps.ToString(DownloadMbps < 10 ? "0.0" : "0");
    public string UploadText => UploadMbps <= 0 ? "—" : UploadMbps.ToString(UploadMbps < 10 ? "0.0" : "0");

    private static double Gauge(double mbps) => mbps <= 0 ? 0 : Math.Clamp(Math.Log10(mbps + 1) / 3 * 100, 0, 100);

    partial void OnDownloadMbpsChanged(double value)
    {
        OnPropertyChanged(nameof(DownloadGauge));
        OnPropertyChanged(nameof(DownloadText));
    }

    partial void OnUploadMbpsChanged(double value)
    {
        OnPropertyChanged(nameof(UploadGauge));
        OnPropertyChanged(nameof(UploadText));
    }

    private async Task LoadTraceAsync()
    {
        try
        {
            var t = await NetworkService.TraceAsync();
            PublicIp = t.GetValueOrDefault("ip", "");
            Location = string.Join(" · ", new[] { t.GetValueOrDefault("loc"), t.GetValueOrDefault("colo") is { } c ? "via Cloudflare " + c : null }
                                          .Where(x => !string.IsNullOrEmpty(x)));
        }
        catch { /* offline */ }
    }

    [RelayCommand]
    private async Task RunSpeedTestAsync()
    {
        if (IsSpeedTesting)
        {
            _speedCts?.Cancel();
            return;
        }
        _speedCts = new CancellationTokenSource();
        var ct = _speedCts.Token;
        IsSpeedTesting = true;
        DownloadMbps = UploadMbps = 0;
        PingText = "…";
        try
        {
            SpeedPhase = "Measuring latency…";
            var ping = await NetworkService.PingAsync();
            PingText = ping is null ? "n/a" : $"{ping:0}";
            SpeedPhase = "Testing download speed…";
            DownloadMbps = await NetworkService.DownloadAsync(TimeSpan.FromSeconds(8), new Progress<double>(v => DownloadMbps = v), ct);
            SpeedPhase = "Testing upload speed…";
            UploadMbps = await NetworkService.UploadAsync(TimeSpan.FromSeconds(8), new Progress<double>(v => UploadMbps = v), ct);
            SpeedPhase = $"Done. Download {DownloadText} Mbps, upload {UploadText} Mbps — measured against Cloudflare's nearest server.";
            SoundService.Play(UiSound.Success);
        }
        catch (OperationCanceledException)
        {
            SpeedPhase = "Stopped.";
        }
        catch (Exception e)
        {
            SpeedPhase = "Speed test failed: " + e.Message;
        }
        finally
        {
            IsSpeedTesting = false;
        }
    }

    // ---- Wi-Fi

    public ObservableCollection<WifiItemViewModel> Wifi { get; } = [];
    [ObservableProperty] private string _wifiStatus = "Press Show saved networks to list the Wi-Fi networks this PC remembers.";

    [RelayCommand]
    private async Task LoadWifiAsync()
    {
        WifiStatus = "Reading saved networks…";
        try
        {
            var list = await NetworkService.SavedWifiAsync();
            Wifi.Clear();
            foreach (var p in list) Wifi.Add(new WifiItemViewModel(p));
            WifiStatus = list.Count == 0 ? "No saved Wi-Fi networks." : $"{Format.Plural(list.Count, "saved network")}. Passwords stay hidden until you reveal them.";
        }
        catch (Exception e)
        {
            WifiStatus = e.Message;
        }
    }

    [RelayCommand]
    private void CopyPassword(WifiItemViewModel? item)
    {
        if (item?.Profile.Password is not { } pw) return;
        try
        {
            System.Windows.Clipboard.SetText(pw);
            WifiStatus = $"Copied the password for {item.Name}.";
        }
        catch (Exception e)
        {
            WifiStatus = "Couldn't copy: " + e.Message;
        }
    }

    // ---- hosts file

    public ObservableCollection<HostsItemViewModel> HostsEntries { get; } = [];
    [ObservableProperty] private string _hostsStatus = "";
    [ObservableProperty] private bool _hostsDirty;
    [ObservableProperty] private string _newAddress = "0.0.0.0";
    [ObservableProperty] private string _newHost = "";

    [RelayCommand]
    private void LoadHosts()
    {
        try
        {
            _hosts = new HostsFile(NetworkService.ReadHosts());
            ShowHosts();
            HostsDirty = false;
            HostsStatus = HostsEntries.Count == 0
                ? "The hosts file is empty — the normal state. Add a line to block a site (0.0.0.0) or point a name at another address."
                : $"{Format.Plural(HostsEntries.Count(e => e.Enabled), "active line")}. Untick to switch a line off without deleting it.";
        }
        catch (Exception e)
        {
            HostsStatus = "Couldn't read the hosts file: " + e.Message;
        }
    }

    private void ShowHosts()
    {
        HostsEntries.Clear();
        if (_hosts is null) return;
        foreach (var e in _hosts.Entries) HostsEntries.Add(new HostsItemViewModel(e, (item, on) => { _hosts.SetEnabled(item.Entry, on); HostsDirty = true; }));
    }

    [RelayCommand]
    private void AddHost()
    {
        if (_hosts is null) LoadHosts();
        try
        {
            _hosts!.Add(NewAddress.Trim(), NewHost.Trim(), "added by Scour");
            ShowHosts();
            HostsDirty = true;
            NewHost = "";
        }
        catch (ArgumentException e)
        {
            HostsStatus = e.Message;
        }
    }

    [RelayCommand]
    private void RemoveHost(HostsItemViewModel? item)
    {
        if (item is null || _hosts is null) return;
        _hosts.Remove(item.Entry);
        ShowHosts();
        HostsDirty = true;
    }

    [RelayCommand]
    private void SaveHosts()
    {
        if (_hosts is null) return;
        try
        {
            NetworkService.WriteHosts(_hosts.ToString());
            HostsDirty = false;
            HostsStatus = "Saved, and the DNS cache was cleared. The original file is kept as hosts.scour-backup.";
            SoundService.Play(UiSound.Success);
        }
        catch (Exception e)
        {
            HostsStatus = "Couldn't save: " + e.Message + (e is UnauthorizedAccessException ? " (antivirus software sometimes locks the hosts file)" : "");
        }
    }

    [RelayCommand]
    private void ResetHosts()
    {
        if (!Dialogs.ConfirmWarning("Replace the hosts file with Windows' default? Every custom line is removed (the current file is backed up first).")) return;
        _hosts = new HostsFile(HostsFile.Default);
        SaveHosts();
        ShowHosts();
    }
}
