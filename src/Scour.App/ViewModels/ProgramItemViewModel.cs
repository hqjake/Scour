using System.IO;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Scour.App.Platform;
using Scour.App.Services;
using Scour.Core.Programs;

namespace Scour.App.ViewModels;

public sealed partial class ProgramItemViewModel(InstalledProgram program, IconCache icons, bool uninstallerMissing) : ObservableObject
{
    private bool _iconRequested;
    private ImageSource? _icon;

    public InstalledProgram Program { get; } = program;

    public string Name => Program.DisplayName;
    public string? Version => Program.DisplayVersion;
    public string? Publisher => Program.Publisher;
    public DateTime? InstallDate => Program.InstallDate;
    public string InstallDateText => Program.InstallDate?.ToString("d") ?? "";
    public long SizeBytes => Program.EstimatedSizeBytes ?? 0;
    public string SizeText => Format.Bytes(SizeBytes);
    public string Type => Program.IsMsi ? "MSI" : "EXE";
    public string Scope => Program.Scope == InstallScope.User ? "Current user" : "All users";
    public string Architecture => Program.Architecture;
    public string? InstallLocation => Program.InstallLocation;
    public string RegistryKey => Program.RegistryKey.ToString();
    public bool HasInstallLocation => Program.InstallLocation is not null && Directory.Exists(Program.InstallLocation);

    /// <summary>The registered uninstaller is missing or there is none — only a forced removal will work.</summary>
    public bool IsBroken { get; } = uninstallerMissing;

    public string? HelpLink => Program.HelpLink ?? Program.UrlInfoAbout;

    /// <summary>Loaded lazily on a background thread the first time a row is shown.</summary>
    public ImageSource? Icon
    {
        get
        {
            if (!_iconRequested)
            {
                _iconRequested = true;
                Task.Run(() => icons.ForProgram(Program)).ContinueWith(t =>
                {
                    if (t.Status == TaskStatus.RanToCompletion && t.Result is not null)
                    {
                        _icon = t.Result;
                        OnPropertyChanged(nameof(Icon));
                        OnPropertyChanged(nameof(HasIcon));
                    }
                }, TaskScheduler.FromCurrentSynchronizationContext());
            }
            return _icon;
        }
    }

    public bool HasIcon => _icon is not null;

    public bool Matches(string search) =>
        Name.Contains(search, StringComparison.CurrentCultureIgnoreCase) ||
        (Publisher?.Contains(search, StringComparison.CurrentCultureIgnoreCase) ?? false) ||
        (Version?.Contains(search, StringComparison.CurrentCultureIgnoreCase) ?? false);
}
