using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Scour.App.Services;
using Scour.Core.Leftovers;
using Scour.Core.Platform;
using Scour.Core.Programs;

namespace Scour.App.ViewModels;

/// <summary>Removes the remains of programs that are half-uninstalled or missing from the list.</summary>
public sealed partial class ForcedUninstallViewModel(AppServices services) : PageViewModel
{
    public override string Title => "Forced uninstall";
    public override string Glyph => "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand))]
    [NotifyPropertyChangedFor(nameof(MatchedProgramText), nameof(HasMatchedProgram))]
    private string _programName = "";

    [ObservableProperty] private string _publisher = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand))]
    private string _installFolder = "";

    [ObservableProperty] private bool _runUninstallerFirst = true;

    private InstalledProgram? MatchedProgram =>
        string.IsNullOrWhiteSpace(ProgramName)
            ? null
            : services.LastKnownPrograms.FirstOrDefault(p => p.DisplayName.Equals(ProgramName.Trim(), StringComparison.CurrentCultureIgnoreCase));

    public bool HasMatchedProgram => MatchedProgram is not null;

    public string MatchedProgramText => MatchedProgram is { } p
        ? $"\"{p.DisplayName}\" is still in the installed list" + (p.HasUninstaller ? "." : ", but has no uninstaller.")
        : "";

    public IEnumerable<string> KnownNames => services.LastKnownPrograms.Select(p => p.DisplayName).Distinct().OrderBy(n => n);

    [RelayCommand]
    private void BrowseFolder()
    {
        var dir = Dialogs.PickFolder("Choose the program's install folder");
        if (dir is null) return;
        InstallFolder = dir;
        if (string.IsNullOrWhiteSpace(ProgramName)) ProgramName = Path.GetFileName(dir);
    }

    [RelayCommand]
    private void BrowseExecutable()
    {
        var file = Dialogs.PickFile("Choose one of the program's files", "Programs (*.exe)|*.exe|All files (*.*)|*.*");
        if (file is null) return;
        Prefill(file);
    }

    /// <summary>Fills the form from an executable (also used by Hunter mode).</summary>
    public void Prefill(string exePath, InstalledProgram? program = null)
    {
        InstallFolder = program?.InstallLocation ?? Path.GetDirectoryName(exePath) ?? "";
        try
        {
            var info = FileVersionInfo.GetVersionInfo(exePath);
            ProgramName = program?.DisplayName ?? FirstNonEmpty(info.ProductName, info.FileDescription, Path.GetFileNameWithoutExtension(exePath));
            Publisher = program?.Publisher ?? info.CompanyName ?? "";
        }
        catch
        {
            ProgramName = program?.DisplayName ?? Path.GetFileNameWithoutExtension(exePath);
        }
    }

    private static string FirstNonEmpty(params string?[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim() ?? "";

    private bool CanScan() => !string.IsNullOrWhiteSpace(ProgramName) || Directory.Exists(InstallFolder);

    [RelayCommand(CanExecute = nameof(CanScan))]
    private async Task ScanAsync()
    {
        var folder = string.IsNullOrWhiteSpace(InstallFolder) ? null : WinPath.Normalize(InstallFolder.Trim());
        if (folder is not null && !Directory.Exists(folder))
        {
            Dialogs.Error("That folder doesn't exist. Leave it empty to search by name only.");
            return;
        }
        var guard = new SafetyGuard(services.Folders, [], []);
        if (folder is not null && guard.IsProtectedPath(folder))
        {
            Dialogs.Error("That's a system or shared folder, not a program's own folder.");
            return;
        }
        var name = string.IsNullOrWhiteSpace(ProgramName) ? Path.GetFileName(folder!) : ProgramName.Trim();
        var matched = MatchedProgram;

        var target = await Task.Run(() => ScanTargetBuilder.Forced(name, string.IsNullOrWhiteSpace(Publisher) ? null : Publisher.Trim(),
                                                                   folder, services.LastKnownPrograms.Where(p => p != matched),
                                                                   services.FileSystem, services.SelfProtectedPaths()));
        var job = new UninstallJob
        {
            Title = name,
            Program = matched,
            RunUninstaller = matched is not null && matched.HasUninstaller && RunUninstallerFirst,
            Target = matched is null ? target : null,
        };
        Dialogs.ShowUninstallWizard(new UninstallWizardViewModel(services, [job], quick: false));
        services.NotifyProgramsChanged();
    }
}
