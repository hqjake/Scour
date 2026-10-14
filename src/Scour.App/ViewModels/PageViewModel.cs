using CommunityToolkit.Mvvm.ComponentModel;

namespace Scour.App.ViewModels;

public abstract partial class PageViewModel : ObservableObject
{
    private bool _loaded;

    public abstract string Title { get; }

    /// <summary>Segoe Fluent Icons glyph for the navigation rail.</summary>
    public abstract string Glyph { get; }

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _statusText = "";

    /// <summary>Called each time the page is shown; loads data the first time.</summary>
    public async Task ActivateAsync()
    {
        if (_loaded) return;
        _loaded = true;
        await LoadAsync();
    }

    protected virtual Task LoadAsync() => Task.CompletedTask;
}
