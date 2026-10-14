using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Scour.App.Platform;
using Scour.App.Services;
using Scour.Core.SystemInfo;

namespace Scour.App.ViewModels;

public sealed partial class SystemInfoViewModel : PageViewModel
{
    public override string Title => "System information";
    public override string Glyph => "";
    public override string Group => "System";

    public ObservableCollection<InfoSection> Sections { get; } = [];

    [ObservableProperty] private InfoSection? _selected;

    protected override Task LoadAsync() => RefreshAsync();

    [RelayCommand]
    private async Task RefreshAsync()
    {
        IsLoading = true;
        var name = Selected?.Title;
        var progress = new Progress<string>(s => StatusText = $"Reading {s.ToLowerInvariant()}…");
        try
        {
            var sections = await Task.Run(() => SystemInfoCollector.Collect(progress));
            Sections.Clear();
            foreach (var s in sections) Sections.Add(s);
            Selected = Sections.FirstOrDefault(s => s.Title == name) ?? Sections.FirstOrDefault();
            StatusText = "";
            SoundService.Play(UiSound.Scan);
        }
        finally
        {
            IsLoading = false;
        }
    }

    private string Report() => SystemReport.ToText(Sections, Environment.MachineName, DateTime.Now);

    [RelayCommand]
    private void Export()
    {
        if (Sections.Count == 0) return;
        var path = Dialogs.SaveFile("Save system report", "Text file (*.txt)|*.txt", $"{Environment.MachineName} system report.txt");
        if (path is null) return;
        try
        {
            File.WriteAllText(path, Report());
            SoundService.Play(UiSound.Success);
        }
        catch (Exception e) { Dialogs.Error("Could not save the report: " + e.Message); }
    }

    [RelayCommand]
    private void Copy()
    {
        if (Sections.Count == 0) return;
        Clipboard.SetText(Report());
        StatusText = "Report copied to the clipboard.";
        SoundService.Play(UiSound.Success);
    }
}
