using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Scour.App.Platform;
using Scour.App.Services;
using Scour.Core.Privacy;

namespace Scour.App.ViewModels;

public sealed partial class DeviceUseViewModel(DeviceUse use, string name, Action<DeviceUseViewModel, bool> setAllowed) : ObservableObject
{
    public DeviceUse Use { get; } = use;
    public string Name { get; } = name;
    public string Detail => Use.IsDesktopApp ? Use.Id : "Microsoft Store app";
    public string LastUsed => Use.InUse ? "In use now" : Use.LastUsedUtc is { } t ? Ago(t) : "";
    public bool InUse => Use.InUse;
    /// <summary>Desktop apps share one switch, shown at the top of the section.</summary>
    public bool CanToggle => !Use.IsDesktopApp;

    [ObservableProperty] private bool _allowed = use.Allowed;

    partial void OnAllowedChanged(bool value) => setAllowed(this, value);

    private static string Ago(DateTime utc)
    {
        var local = utc.ToLocalTime();
        var span = DateTime.Now - local;
        if (span < TimeSpan.FromMinutes(1)) return "Just now";
        if (span < TimeSpan.FromHours(1)) return $"{(int)span.TotalMinutes} min ago";
        if (local.Date == DateTime.Today) return "Today " + local.ToString("t");
        if (local.Date == DateTime.Today.AddDays(-1)) return "Yesterday " + local.ToString("t");
        return local.ToString("g");
    }
}

public sealed partial class CapabilityViewModel : ObservableObject
{
    private readonly DeviceAccess _access;
    private readonly Action<string> _report;
    private bool _suppress;

    public CapabilityViewModel(Capability capability, DeviceAccess access, Action<string> report)
    {
        Capability = capability;
        _access = access;
        _report = report;
    }

    public Capability Capability { get; }
    public string Title => Capability switch { Capability.Camera => "Camera", Capability.Microphone => "Microphone", _ => "Location" };
    public string Glyph => Capability switch { Capability.Camera => "", Capability.Microphone => "", _ => "" };
    public ObservableCollection<DeviceUseViewModel> Apps { get; } = [];

    [ObservableProperty] private bool _allowed;
    [ObservableProperty] private bool _desktopAppsAllowed;
    [ObservableProperty] private bool _blockedByPolicy;
    [ObservableProperty] private string _summary = "";

    public void Load(IEnumerable<(DeviceUse Use, string Name)> uses)
    {
        var s = _access.Switches(Capability);
        _suppress = true;
        Allowed = s.Allowed;
        DesktopAppsAllowed = s.DesktopAppsAllowed;
        BlockedByPolicy = s.BlockedByPolicy;
        _suppress = false;
        Apps.Clear();
        foreach (var (use, name) in uses) Apps.Add(new DeviceUseViewModel(use, name, SetAppAllowed));
        int inUse = Apps.Count(a => a.InUse);
        Summary = BlockedByPolicy ? "Turned off for everyone on this PC (by an administrator or policy)."
                : inUse > 0 ? $"In use now by {string.Join(", ", Apps.Where(a => a.InUse).Select(a => a.Name))}."
                : Apps.Count == 0 ? "No app has used it." : $"{Format.Plural(Apps.Count, "app")} used it.";
    }

    partial void OnAllowedChanged(bool value) => Write(() => _access.SetAllowed(Capability, value),
        value ? $"Apps can ask to use the {Title.ToLowerInvariant()} again." : $"No app can use the {Title.ToLowerInvariant()} now.", "all", value);

    partial void OnDesktopAppsAllowedChanged(bool value) => Write(() => _access.SetDesktopAppsAllowed(Capability, value),
        value ? $"Desktop apps can use the {Title.ToLowerInvariant()} again." : $"Desktop apps can no longer use the {Title.ToLowerInvariant()}.", "desktop", value);

    private void SetAppAllowed(DeviceUseViewModel app, bool allowed) => Write(() => _access.SetAppAllowed(Capability, app.Use.Id, allowed),
        $"{app.Name} {(allowed ? "can use" : "is blocked from")} the {Title.ToLowerInvariant()}.", "app:" + app.Use.Id, allowed);

    private void Write(Action action, string done, string target, bool allowed)
    {
        if (_suppress) return;
        try
        {
            action();
            _report(done);
            // Undo data: capability|target|value to restore.
            ChangeHistory.Record("Camera & microphone", done, null,
                                 new Scour.Core.Removal.UndoInfo(ChangeHistory.DeviceAccess, $"{Capability}|{target}|{(!allowed ? "allow" : "deny")}"));
        }
        catch (Exception e)
        {
            _report("Couldn't change that: " + e.Message);
        }
    }
}

/// <summary>Which apps used the camera, microphone and location, and the switches to block them.</summary>
public sealed partial class DeviceAccessViewModel(AppServices services) : PageViewModel
{
    private readonly DeviceAccess _access = new(services.Registry);

    public override string Title => "Camera & microphone";
    public override string Glyph => "";
    public override string Group => "Cleaning";

    public ObservableCollection<CapabilityViewModel> Capabilities { get; } = [];

    protected override Task LoadAsync() => RefreshAsync();

    [RelayCommand]
    private async Task RefreshAsync()
    {
        IsLoading = true;
        try
        {
            var data = await Task.Run(() => Enum.GetValues<Capability>()
                .Select(c => (c, _access.Uses(c).Select(u => (u, AppName(u))).ToList())).ToList());
            if (Capabilities.Count == 0)
                foreach (var c in Enum.GetValues<Capability>())
                    Capabilities.Add(new CapabilityViewModel(c, _access, m => StatusText = m));
            foreach (var (c, uses) in data) Capabilities.First(x => x.Capability == c).Load(uses);
            StatusText = "Windows records when each app last used these. Switches here are the same ones as in Settings → Privacy.";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private static readonly Dictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase);

    private static string AppName(DeviceUse u)
    {
        if (!u.IsDesktopApp) return AppxService.FriendlyName(u.Id.Split('_')[0]);
        lock (Names)
        {
            if (Names.TryGetValue(u.Id, out var n)) return n;
            try { n = File.Exists(u.Id) ? FileVersionInfo.GetVersionInfo(u.Id).FileDescription : null; }
            catch { n = null; }
            n = string.IsNullOrWhiteSpace(n) ? Path.GetFileNameWithoutExtension(u.Id) : n.Trim();
            Names[u.Id] = n;
            return n;
        }
    }
}
