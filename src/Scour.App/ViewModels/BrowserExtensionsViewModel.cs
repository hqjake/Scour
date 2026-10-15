using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Scour.App.Services;
using Scour.Core.Junk;
using Scour.Core.Leftovers;

namespace Scour.App.ViewModels;

public sealed class ExtensionItemViewModel(BrowserExtension extension)
{
    public BrowserExtension Extension { get; } = extension;
    public string Name => Extension.Name;
    public string Where => $"{Extension.Profile.DisplayName} · v{Extension.Version}" + (Extension.Enabled ? "" : " · turned off");
    public string Description => Extension.Description;
    public string Abilities => string.Join("  ·  ", Extension.Abilities);
    public string Risk => Extension.Risk switch
    {
        ExtensionRisk.High => "Powerful",
        ExtensionRisk.Medium => "Some access",
        _ => "Low access",
    };
    public string RiskKey => Extension.Risk.ToString();
    public string Source => Extension.Source switch
    {
        ExtensionSource.Policy => "Forced by policy",
        ExtensionSource.Sideloaded => "Added by a program",
        ExtensionSource.Developer => "Developer mode",
        _ => "",
    };
    public double Opacity => Extension.Enabled ? 1 : 0.6;
}

/// <summary>Every browser extension on the PC, what it can do, and the forced installs users can't remove themselves.</summary>
public sealed partial class BrowserExtensionsViewModel(AppServices services) : PageViewModel
{
    private BrowserExtensions Scanner => new(services.FileSystem, services.Registry, new BrowserPrivacy(services.FileSystem, services.Folders));

    public override string Title => "Browser extensions";
    public override string Glyph => "";
    public override string Group => "Cleaning";

    public ObservableCollection<ExtensionItemViewModel> Extensions { get; } = [];
    public ObservableCollection<ForcedInstall> Forced { get; } = [];

    [ObservableProperty] private string _summary = "";

    protected override Task LoadAsync() => RefreshAsync();

    [RelayCommand]
    private async Task RefreshAsync()
    {
        IsLoading = true;
        StatusText = "Reading browser profiles…";
        try
        {
            var scanner = Scanner;
            var (list, forced) = await Task.Run(() => (scanner.FindAll(), scanner.ForcedInstalls()));
            Extensions.Clear();
            foreach (var e in list) Extensions.Add(new ExtensionItemViewModel(e));
            Forced.Clear();
            foreach (var f in forced) Forced.Add(f);
            int powerful = list.Count(e => e.Risk == ExtensionRisk.High);
            Summary = list.Count == 0 ? "No extensions found." : $"{Format.Plural(list.Count, "extension")}, {powerful} with powerful access";
            StatusText = forced.Count > 0
                ? $"{Format.Plural(forced.Count, "extension")} {(forced.Count == 1 ? "is" : "are")} forced in through the registry — remove the entry if you didn't set it up."
                : "To remove a normal extension, open it in its browser. Extensions with powerful access are fine when you trust who made them.";
            SoundService.Play(UiSound.Scan);
        }
        catch (Exception e)
        {
            StatusText = e.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private void Manage(ExtensionItemViewModel? item)
    {
        if (item is null) return;
        var e = item.Extension;
        var (exe, url) = e.Profile.ProcessName switch
        {
            "msedge" => ("msedge.exe", $"edge://extensions/?id={e.Id}"),
            "brave" => ("brave.exe", $"brave://extensions/?id={e.Id}"),
            "vivaldi" => ("vivaldi.exe", $"vivaldi://extensions/?id={e.Id}"),
            "opera" => ("opera.exe", "opera://extensions"),
            "firefox" => ("firefox.exe", "about:addons"),
            _ => ("chrome.exe", $"chrome://extensions/?id={e.Id}"),
        };
        try
        {
            Process.Start(new ProcessStartInfo(exe, url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Dialogs.Error($"Couldn't open {e.Profile.Browser}: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task RemoveForcedAsync(ForcedInstall? item)
    {
        if (item is null) return;
        var name = Extensions.FirstOrDefault(x => x.Extension.Id == item.ExtensionId)?.Name ?? item.ExtensionId;
        if (!Dialogs.ConfirmWarning($"Remove the registry entry that forces \"{name}\" into {item.Browser}?\n\n{item.Location}\n\n" +
                                    "If your workplace or school manages this PC, it may have been put there on purpose. A backup is saved first.")) return;
        try
        {
            var leftover = new LeftoverItem
            {
                Kind = item.ValueName is null ? LeftoverKind.RegistryKey : LeftoverKind.RegistryValue,
                Path = item.Key.ToString(),
                RegistryKey = item.Key,
                ValueName = item.ValueName,
            };
            services.Backups.Create($"Extension {name}", [leftover], services.Registry, filesToRecycleBin: false, DateTime.UtcNow);
            Scanner.Remove(item);
            SoundService.Play(UiSound.Success);
            await RefreshAsync();
            StatusText = $"Removed. {item.Browser} drops the extension the next time it starts; the backup is under Backups.";
        }
        catch (Exception e)
        {
            Dialogs.Error("Couldn't remove it: " + e.Message);
        }
    }
}
