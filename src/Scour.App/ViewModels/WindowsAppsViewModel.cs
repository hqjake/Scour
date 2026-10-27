using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Scour.App.Platform;
using Scour.App.Services;

namespace Scour.App.ViewModels;

public sealed partial class WindowsAppsViewModel : PageViewModel
{
    private readonly AppServices _s;

    public WindowsAppsViewModel(AppServices services)
    {
        _s = services;
        View = CollectionViewSource.GetDefaultView(Apps);
        View.Filter = o => o is AppxPackage p && (ShowSystemApps || p.IsRemovable) &&
                           (string.IsNullOrWhiteSpace(SearchText) ||
                            p.DisplayName.Contains(SearchText, StringComparison.CurrentCultureIgnoreCase) ||
                            p.Name.Contains(SearchText, StringComparison.CurrentCultureIgnoreCase) ||
                            p.Publisher.Contains(SearchText, StringComparison.CurrentCultureIgnoreCase));
    }

    public override string Title => "Windows apps";
    public override string Glyph => "";

    public ObservableCollection<AppxPackage> Apps { get; } = [];
    public ICollectionView View { get; }

    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private bool _showSystemApps;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveCommand), nameof(OpenFolderCommand))]
    private AppxPackage? _selected;

    partial void OnSearchTextChanged(string value) => Refilter();
    partial void OnShowSystemAppsChanged(bool value) => Refilter();

    private void Refilter()
    {
        View.Refresh();
        StatusText = Format.Plural(View.Cast<object>().Count(), "app");
    }

    protected override Task LoadAsync() => RefreshAsync();

    [RelayCommand]
    private async Task RefreshAsync()
    {
        IsLoading = true;
        StatusText = "Loading Windows apps…";
        try
        {
            var apps = await AppxService.ListAsync();
            Apps.Clear();
            foreach (var a in apps) Apps.Add(a);
            Refilter();
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

    private bool CanRemove() => Selected is { IsRemovable: true };

    [RelayCommand(CanExecute = nameof(CanRemove))]
    private async Task RemoveAsync()
    {
        if (Selected is not { } app) return;
        if (!Dialogs.Confirm($"Remove \"{app.DisplayName}\"?\n\nIts settings and data folder will be removed as well.")) return;
        IsLoading = true;
        StatusText = $"Removing {app.DisplayName}…";
        try
        {
            await AppxService.RemoveAsync(app);
            var data = AppxService.DataFolder(_s.Folders.LocalAppData, app.FamilyName);
            if (Directory.Exists(data))
            {
                try { _s.FileSystem.DeleteDirectory(data, _s.Settings.FilesToRecycleBin); }
                catch (Exception e) { StatusText = $"Removed, but its data folder is in use: {e.Message}"; }
            }
            Apps.Remove(app);
            SoundService.Play(UiSound.Success);
            Refilter();
        }
        catch (Exception e)
        {
            Dialogs.Error($"Could not remove {app.DisplayName}:\n\n{e.Message}");
            StatusText = "";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private bool HasFolder() => Selected?.InstallLocation is { Length: > 0 };

    [RelayCommand(CanExecute = nameof(HasFolder))]
    private void OpenFolder()
    {
        if (Selected?.InstallLocation is { } dir) Shell.OpenFolder(dir);
    }
}
