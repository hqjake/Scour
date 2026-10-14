using Scour.Core.Bloat;
using Scour.Core.Platform;
using Scour.Core.Privacy;
using Scour.Core.Programs;
using Scour.Tests.Fakes;

namespace Scour.Tests;

public class BloatAndPrivacyTests
{
    private static AppxRef A(string name) => new(name, name + "_1.0.0.0_x64__8wekyb3d8bbwe", name + "_8wekyb3d8bbwe");

    [Fact]
    public void Finds_bloat_but_never_protected_packages()
    {
        var installed = new[]
        {
            A("Microsoft.BingNews"), A("king.com.CandyCrushSaga"), A("Microsoft.WindowsStore"), A("Microsoft.DesktopAppInstaller"),
            A("Microsoft.WindowsCalculator"), A("Microsoft.XboxIdentityProvider"), A("Microsoft.GamingApp"), A("SpotifyAB.SpotifyMusic"),
            A("Microsoft.VCLibs.140.00"), A("AD2F1837.HPJumpStarts"),
        };
        var programs = new[]
        {
            new InstalledProgram { RegistryKey = RegKeyPath.Parse(@"HKLM\SOFTWARE\X\McAfee"), DisplayName = "McAfee LiveSafe" },
            new InstalledProgram { RegistryKey = RegKeyPath.Parse(@"HKLM\SOFTWARE\X\Git"), DisplayName = "Git" },
        };
        var found = BloatCatalog.Find(installed, programs);
        var names = found.SelectMany(f => f.Packages.Select(p => p.Name)).ToList();
        Assert.Equal(["Microsoft.BingNews", "Microsoft.GamingApp", "king.com.CandyCrushSaga", "SpotifyAB.SpotifyMusic", "AD2F1837.HPJumpStarts"], names);
        Assert.Contains(found, f => f.Program?.DisplayName == "McAfee LiveSafe");
        Assert.DoesNotContain(found, f => f.Program?.DisplayName == "Git");
        // Each package is claimed by one entry only (Candy Crush matches two patterns).
        Assert.Equal(names.Count, names.Distinct().Count());
    }

    [Fact]
    public void Catalog_never_targets_core_windows_apps()
    {
        string[] core = ["Microsoft.WindowsStore", "Microsoft.DesktopAppInstaller", "Microsoft.WindowsCalculator", "Microsoft.WindowsNotepad",
                         "Microsoft.Paint", "Microsoft.ScreenSketch", "Microsoft.SecHealthUI", "Microsoft.Windows.Photos", "Microsoft.WindowsTerminal",
                         "Microsoft.VCLibs.140.00.UWPDesktop", "Microsoft.UI.Xaml.2.8", "Microsoft.WindowsAppRuntime.1.5", "Microsoft.HEIFImageExtension"];
        foreach (var c in core)
            Assert.True(BloatCatalog.IsProtected(c), c);
        Assert.Empty(BloatCatalog.Find(core.Select(A), []));
    }

    [Fact]
    public void Privacy_tweaks_apply_report_state_and_undo_exactly()
    {
        var reg = new InMemoryRegistry()
            .Set(@"HKCU\Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo", "Enabled", RegValue.DWord(1));
        var fs = new InMemoryFileSystem();
        var tweaks = new PrivacyTweaks(reg, fs, @"C:\ProgramData\Scour\privacy-backup.json");
        var adid = PrivacyTweaks.All.Single(t => t.Id == "adid");

        Assert.Equal(TweakState.Off, tweaks.GetState(adid));
        tweaks.Apply(adid);
        Assert.Equal(TweakState.On, tweaks.GetState(adid));
        Assert.Equal(0L, reg.GetValue(RegKeyPath.Parse(@"HKCU\Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo"), "Enabled")!.AsInteger());

        // Applying twice keeps the original backup.
        tweaks.Apply(adid);
        tweaks.Undo(adid);
        Assert.Equal(1L, reg.GetValue(RegKeyPath.Parse(@"HKCU\Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo"), "Enabled")!.AsInteger());
        Assert.Null(reg.GetValue(RegKeyPath.Parse(@"HKLM\SOFTWARE\Policies\Microsoft\Windows\AdvertisingInfo"), "DisabledByGroupPolicy"));
        Assert.Equal(TweakState.Partial, tweaks.GetState(new PrivacyTweak("x", "", "", "", false,
            [adid.Settings[0] with { Value = 1 }, adid.Settings[1]])));
    }

    [Fact]
    public void Undo_without_backup_removes_values()
    {
        var reg = new InMemoryRegistry().Set(@"HKLM\SOFTWARE\Policies\Microsoft\Dsh", "AllowNewsAndInterests", RegValue.DWord(0));
        var tweaks = new PrivacyTweaks(reg, new InMemoryFileSystem(), @"C:\b.json");
        var widgets = PrivacyTweaks.All.Single(t => t.Id == "widgets");
        Assert.Equal(TweakState.On, tweaks.GetState(widgets));
        tweaks.Undo(widgets);
        Assert.Equal(TweakState.Off, tweaks.GetState(widgets));
    }

    [Fact]
    public void Tweak_catalog_is_well_formed()
    {
        Assert.Equal(PrivacyTweaks.All.Count, PrivacyTweaks.All.Select(t => t.Id).Distinct().Count());
        Assert.All(PrivacyTweaks.All, t => Assert.NotEmpty(t.Settings));
        // Never touches anything outside policies/user settings that Windows documents as switches.
        Assert.All(PrivacyTweaks.All.SelectMany(t => t.Settings), s => Assert.True(s.Key.Depth >= 3));
    }
}
