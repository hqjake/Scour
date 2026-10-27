using System.Text.Json;
using Scour.Core.Platform;
using Scour.Core.Removal;

namespace Scour.Core.Privacy;

/// <summary>One registry value a tweak sets.</summary>
public sealed record TweakSetting(RegKeyPath Key, string Name, uint Value);

public sealed record PrivacyTweak(
    string Id, string Name, string Description, string Category, bool Recommended, IReadOnlyList<TweakSetting> Settings, string? Note = null);

public enum TweakState
{
    Off,
    Partial,
    On,
}

/// <summary>What a setting looked like before Scour touched it.</summary>
/// <summary>
/// A value as it was before a tweak. <see cref="Kind"/> and <see cref="Raw"/> keep values that weren't a
/// DWORD (a string or QWORD some program wrote) so undo restores them exactly; older backups lack them.
/// </summary>
public sealed record SavedValue(string Key, string Name, bool Existed, uint? Value, RegValueKind? Kind = null, string? Raw = null)
{
    public static SavedValue From(RegKeyPath key, string name, RegValue? v)
    {
        if (v is null) return new(key.ToString(), name, false, null);
        if (v.Kind == RegValueKind.DWord) return new(key.ToString(), name, true, (uint)v.AsInteger()!.Value);
        string raw = v.Data switch
        {
            byte[] b => Convert.ToHexString(b),
            string[] arr => string.Join("\0", arr),
            ulong u => u.ToString(System.Globalization.CultureInfo.InvariantCulture),
            _ => v.AsString() ?? "",
        };
        return new(key.ToString(), name, true, null, v.Kind, raw);
    }

    /// <summary>The saved value, or null if it didn't exist (or an old backup only knew it wasn't a DWORD).</summary>
    public RegValue? ToRegValue()
    {
        if (!Existed) return null;
        if (Value is { } n) return RegValue.DWord(n);
        return (Kind, Raw) switch
        {
            (RegValueKind.String, { } r) => RegValue.String(r),
            (RegValueKind.ExpandString, { } r) => RegValue.Expand(r),
            (RegValueKind.MultiString, { } r) => RegValue.Multi(r.Length == 0 ? [] : r.Split('\0')),
            (RegValueKind.QWord, { } r) when ulong.TryParse(r, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var q) => RegValue.QWord(q),
            (RegValueKind.Binary or RegValueKind.None, { } r) => new RegValue(Kind!.Value, Convert.FromHexString(r)),
            _ => null,
        };
    }
}

/// <summary>
/// Windows privacy settings switched through documented registry values and policies. Before a
/// tweak changes anything, the original values are saved so "Undo" restores them exactly.
/// </summary>
public sealed class PrivacyTweaks(IRegistry registry, IFileSystem fs, string backupFile)
{
    private static RegKeyPath LM(string p) => RegKeyPath.LocalMachine(p);
    private static RegKeyPath CU(string p) => RegKeyPath.CurrentUser(p);
    private const string Cdm = @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager";

    public static readonly IReadOnlyList<PrivacyTweak> All =
    [
        new("telemetry", "Limit diagnostic data", "Sends only the required minimum of diagnostic data to Microsoft and stops the telemetry service.",
            "Diagnostics", true,
            [
                new(LM(@"SOFTWARE\Policies\Microsoft\Windows\DataCollection"), "AllowTelemetry", 0),
                new(LM(@"SOFTWARE\Policies\Microsoft\Windows\DataCollection"), "DoNotShowFeedbackNotifications", 1),
                new(LM(@"SYSTEM\CurrentControlSet\Services\DiagTrack"), "Start", 4),
            ], "Home and Pro editions treat this as \"Required diagnostic data\", the lowest they allow. Takes effect after a restart."),
        new("tailored", "No tailored experiences", "Stops Microsoft using your diagnostic data for personalised tips, ads and recommendations.",
            "Diagnostics", true, [new(CU(@"Software\Microsoft\Windows\CurrentVersion\Privacy"), "TailoredExperiencesWithDiagnosticDataEnabled", 0)]),
        new("feedback", "No feedback requests", "Windows stops asking how it's doing.", "Diagnostics", true,
            [new(CU(@"Software\Microsoft\Siuf\Rules"), "NumberOfSIUFInPeriod", 0)]),
        new("adid", "Turn off advertising ID", "Apps can't use a per-user ID to show you targeted ads.", "Ads", true,
            [
                new(CU(@"Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo"), "Enabled", 0),
                new(LM(@"SOFTWARE\Policies\Microsoft\Windows\AdvertisingInfo"), "DisabledByGroupPolicy", 1),
            ]),
        new("start-suggestions", "No suggestions in Start and Settings", "Removes app promotions from Start and suggested content in the Settings app.", "Ads", true,
            [
                new(CU(Cdm), "SystemPaneSuggestionsEnabled", 0),
                new(CU(Cdm), "SubscribedContent-338388Enabled", 0),
                new(CU(Cdm), "SubscribedContent-338393Enabled", 0),
                new(CU(Cdm), "SubscribedContent-353694Enabled", 0),
                new(CU(Cdm), "SubscribedContent-353696Enabled", 0),
                new(CU(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced"), "Start_IrisRecommendations", 0),
            ]),
        new("tips", "No tips and tricks notifications", "Stops \"Get tips and suggestions when using Windows\" pop-ups and the welcome experience after updates.", "Ads", true,
            [
                new(CU(Cdm), "SubscribedContent-338389Enabled", 0),
                new(CU(Cdm), "SubscribedContent-310093Enabled", 0),
                new(CU(Cdm), "SoftLandingEnabled", 0),
                new(CU(@"Software\Microsoft\Windows\CurrentVersion\UserProfileEngagement"), "ScoobeSystemSettingEnabled", 0),
            ]),
        new("lockscreen", "No lock screen ads and fun facts", "Removes promotional tips and \"fun facts\" from the lock screen.", "Ads", true,
            [
                new(CU(Cdm), "RotatingLockScreenOverlayEnabled", 0),
                new(CU(Cdm), "SubscribedContent-338387Enabled", 0),
            ]),
        new("silent-apps", "Stop automatic promo app installs", "Windows stops silently installing suggested apps and games (pairs with the Bloatware remover).", "Ads", true,
            [
                new(CU(Cdm), "SilentInstalledAppsEnabled", 0),
                new(CU(Cdm), "PreInstalledAppsEnabled", 0),
                new(CU(Cdm), "OemPreInstalledAppsEnabled", 0),
                new(LM(@"SOFTWARE\Policies\Microsoft\Windows\CloudContent"), "DisableWindowsConsumerFeatures", 1),
            ]),
        new("explorer-ads", "No sync provider ads in File Explorer", "Hides OneDrive and Microsoft 365 promotions inside File Explorer.", "Ads", true,
            [new(CU(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced"), "ShowSyncProviderNotifications", 0)]),
        new("bing", "No web results in Start search", "Start menu search looks only at your PC, not Bing.", "Search & AI", true,
            [
                new(CU(@"Software\Policies\Microsoft\Windows\Explorer"), "DisableSearchBoxSuggestions", 1),
                new(CU(@"Software\Microsoft\Windows\CurrentVersion\Search"), "BingSearchEnabled", 0),
            ], "Takes effect after signing out or restarting Explorer."),
        new("copilot", "Turn off Copilot", "Hides the Copilot button and turns off Windows Copilot.", "Search & AI", false,
            [
                new(CU(@"Software\Policies\Microsoft\Windows\WindowsCopilot"), "TurnOffWindowsCopilot", 1),
                new(CU(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced"), "ShowCopilotButton", 0),
            ]),
        new("recall", "Turn off Recall snapshots", "Stops Windows Recall from saving snapshots of your screen (Copilot+ PCs).", "Search & AI", true,
            [
                new(CU(@"Software\Policies\Microsoft\Windows\WindowsAI"), "DisableAIDataAnalysis", 1),
                new(LM(@"SOFTWARE\Policies\Microsoft\Windows\WindowsAI"), "DisableAIDataAnalysis", 1),
            ]),
        new("activity", "Turn off activity history", "Windows stops recording the apps, files and sites you open for Timeline and cloud sync.", "Activity", true,
            [
                new(LM(@"SOFTWARE\Policies\Microsoft\Windows\System"), "EnableActivityFeed", 0),
                new(LM(@"SOFTWARE\Policies\Microsoft\Windows\System"), "PublishUserActivities", 0),
                new(LM(@"SOFTWARE\Policies\Microsoft\Windows\System"), "UploadUserActivities", 0),
            ]),
        new("typing", "No typing and inking personalisation", "Stops Windows collecting what you type and write to build a personal dictionary.", "Activity", true,
            [
                new(CU(@"Software\Microsoft\InputPersonalization"), "RestrictImplicitTextCollection", 1),
                new(CU(@"Software\Microsoft\InputPersonalization"), "RestrictImplicitInkCollection", 1),
                new(CU(@"Software\Microsoft\InputPersonalization\TrainedDataStore"), "HarvestContacts", 0),
                new(CU(@"Software\Microsoft\Personalization\Settings"), "AcceptedPrivacyPolicy", 0),
            ]),
        new("speech", "No online speech recognition", "Voice typing and dictation stop sending audio to Microsoft (offline recognition still works).", "Activity", false,
            [new(CU(@"Software\Microsoft\Speech_OneCore\Settings\OnlineSpeechPrivacy"), "HasAccepted", 0)]),
        new("widgets", "Turn off Widgets news feed", "Removes the news and interests board from the taskbar.", "Activity", false,
            [new(LM(@"SOFTWARE\Policies\Microsoft\Dsh"), "AllowNewsAndInterests", 0)]),
        new("wer", "Turn off error reporting", "Crash reports are no longer sent to Microsoft.", "Diagnostics", false,
            [new(LM(@"SOFTWARE\Microsoft\Windows\Windows Error Reporting"), "Disabled", 1)]),
    ];

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public TweakState GetState(PrivacyTweak t)
    {
        int on = t.Settings.Count(s => registry.GetValue(s.Key, s.Name)?.AsInteger() == s.Value);
        return on == 0 ? TweakState.Off : on == t.Settings.Count ? TweakState.On : TweakState.Partial;
    }

    public void Apply(PrivacyTweak t)
    {
        var backups = LoadBackups();
        if (!backups.ContainsKey(t.Id))
        {
            backups[t.Id] = t.Settings.Select(s => SavedValue.From(s.Key, s.Name, registry.GetValue(s.Key, s.Name))).ToList();
            SaveBackups(backups);
        }
        foreach (var s in t.Settings) registry.SetValue(s.Key, s.Name, RegValue.DWord(s.Value));
    }

    /// <summary>Puts back exactly what was there before; without a saved copy, removes Scour's values so Windows uses its default.</summary>
    public void Undo(PrivacyTweak t)
    {
        var backups = LoadBackups();
        if (backups.TryGetValue(t.Id, out var saved))
        {
            foreach (var v in saved)
            {
                var key = RegKeyPath.Parse(v.Key);
                if (v.ToRegValue() is { } original) registry.SetValue(key, v.Name, original);
                else if (registry.GetValue(key, v.Name) is not null) registry.DeleteValue(key, v.Name);
            }
            backups.Remove(t.Id);
            SaveBackups(backups);
            return;
        }
        foreach (var s in t.Settings)
            if (registry.GetValue(s.Key, s.Name) is not null) registry.DeleteValue(s.Key, s.Name);
    }

    private Dictionary<string, List<SavedValue>> LoadBackups()
    {
        try
        {
            if (fs.FileExists(backupFile))
                return JsonSerializer.Deserialize<Dictionary<string, List<SavedValue>>>(fs.ReadAllText(backupFile), JsonOptions) ?? [];
        }
        catch (JsonException) { /* start over */ }
        return [];
    }

    private void SaveBackups(Dictionary<string, List<SavedValue>> backups)
    {
        var dir = WinPath.GetDirectoryName(backupFile);
        if (dir is not null) fs.CreateDirectory(dir);
        fs.WriteAllText(backupFile, JsonSerializer.Serialize(backups, JsonOptions));
    }
}
