using CommunityToolkit.Mvvm.ComponentModel;

namespace Scour.App.ViewModels;

public abstract partial class PageViewModel : ObservableObject
{
    private Task? _load;
    private bool _loadFailed;

    public abstract string Title { get; }

    /// <summary>Segoe Fluent Icons glyph for the navigation rail.</summary>
    public abstract string Glyph { get; }

    /// <summary>Sidebar section this page belongs to.</summary>
    public virtual string Group => "Programs";

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _statusText = "";

    /// <summary>
    /// Called each time the page is shown; loads data the first time. Completes when that first load has
    /// finished, so callers can wait for the page to be ready. A failed load is reported on the page and
    /// tried again the next time the page is shown.
    /// </summary>
    public Task ActivateAsync()
    {
        if (_load is null || (_loadFailed && _load.IsCompleted))
        {
            _loadFailed = false;
            _load = LoadSafelyAsync();
        }
        return _load;
    }

    private async Task LoadSafelyAsync()
    {
        try
        {
            await LoadAsync();
        }
        catch (Exception e)
        {
            Services.ErrorLog.Write($"Loading {Title}", e);
            StatusText = $"Couldn't load this page: {e.Message}";
            IsLoading = false;
            _loadFailed = true;
        }
    }

    protected virtual Task LoadAsync() => Task.CompletedTask;
}
