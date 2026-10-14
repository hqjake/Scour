using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Scour.App.Platform;
using Scour.App.Services;
using Scour.Core.Junk;

namespace Scour.App.ViewModels;

public sealed partial class BrowserProfileViewModel(BrowserProfile profile) : ObservableObject
{
    public BrowserProfile Profile { get; } = profile;
    public string Browser => Profile.Browser;
    public string ProfileName => Profile.ProfileName;

    [ObservableProperty] private bool _history = true;
    [ObservableProperty] private bool _cookies;
    [ObservableProperty] private bool _formData;
    [ObservableProperty] private bool _sessions;
    [ObservableProperty] private string _result = "";

    public PrivacyItems Items =>
        (History ? PrivacyItems.History : 0) | (Cookies ? PrivacyItems.Cookies : 0) |
        (FormData ? PrivacyItems.FormData : 0) | (Sessions ? PrivacyItems.Sessions : 0);
}

public sealed partial class BrowserPrivacyViewModel(AppServices services) : PageViewModel
{
    private readonly BrowserPrivacy _privacy = new(services.FileSystem, services.Folders);

    public override string Title => "Browser privacy";
    public override string Glyph => "\uE72E";
    public override string Group => "Cleaning";

    public ObservableCollection<BrowserProfileViewModel> Profiles { get; } = [];

    protected override Task LoadAsync()
    {
        Refresh();
        return Task.CompletedTask;
    }

    [RelayCommand]
    private void Refresh()
    {
        Profiles.Clear();
        foreach (var p in _privacy.FindProfiles()) Profiles.Add(new BrowserProfileViewModel(p));
        StatusText = Profiles.Count == 0 ? "No supported browsers found." : $"{Format.Plural(Profiles.Count, "browser profile")} found.";
    }

    [RelayCommand]
    private async Task CleanAsync()
    {
        var chosen = Profiles.Where(p => p.Items != PrivacyItems.None).ToList();
        if (chosen.Count == 0) return;

        var running = SystemCleanup.Running(chosen.Select(p => p.Profile.ProcessName));
        if (running.Count > 0)
        {
            Dialogs.Info($"Please close these browsers first: {string.Join(", ", running)}.\n\nTheir data is locked while they run.");
            return;
        }
        bool cookies = chosen.Any(p => p.Cookies);
        if (!Dialogs.Confirm("Clear the selected browsing data?" +
                             (cookies ? "\n\nClearing cookies signs you out of websites." : "") +
                             "\n\nBookmarks, saved passwords and settings are not touched.")) return;

        IsLoading = true;
        try
        {
            foreach (var p in chosen)
            {
                StatusText = $"Cleaning {p.Profile.DisplayName}…";
                var r = await Task.Run(() => _privacy.Clear(p.Profile, p.Items));
                p.Result = r.Errors.Count == 0 ? "Cleaned" : string.Join(" ", r.Errors);
            }
            StatusText = "Done.";
            SoundService.Play(UiSound.Success);
        }
        finally
        {
            IsLoading = false;
        }
    }
}
