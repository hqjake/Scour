using System.Text;
using Scour.Core.Junk;
using Scour.Core.Leftovers;
using Scour.Core.Matching;
using Scour.Core.Platform;
using Scour.Core.Privacy;
using Scour.Core.Registry;
using Scour.Core.Repair;
using Scour.Tests.Fakes;

namespace Scour.Tests;

/// <summary>Things that only show up on real PCs: other languages, code pages, odd folder layouts.</summary>
public class WindowsQuirksTests
{
    private static Encoding Cp850()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(850);
    }

    [Fact]
    public void Oem_output_is_read_in_the_console_code_page()
    {
        var oem = Cp850();
        var bytes = oem.GetBytes("Zugriff verweigert: Größe ändern\r\n");
        Assert.Equal("Zugriff verweigert: Größe ändern\r\n", OutputDecoder.Decode(bytes, oem));

        var d = new OutputDecoder(oem);
        var lines = new List<string>();
        lines.AddRange(d.Feed(oem.GetBytes("Plain ASCII first\r\n")));
        lines.AddRange(d.Feed(bytes));
        lines.AddRange(d.Flush());
        Assert.Equal(["Plain ASCII first", "Zugriff verweigert: Größe ändern"], lines);
    }

    [Fact]
    public void Utf8_and_utf16_output_are_still_recognised_with_an_oem_fallback()
    {
        var oem = Cp850();
        Assert.Equal("Größe", OutputDecoder.Decode(Encoding.UTF8.GetBytes("Größe"), oem));
        Assert.Equal("Größe", OutputDecoder.Decode(Encoding.Unicode.GetBytes("Größe"), oem));
        Assert.Equal("Größe", OutputDecoder.Decode([0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("Größe")], oem));

        // A UTF-8 character split across two reads must not switch the stream to the OEM page.
        var d = new OutputDecoder(oem);
        var utf8 = Encoding.UTF8.GetBytes("ändern\n");
        var lines = d.Feed(utf8.AsSpan(0, 1)).Concat(d.Feed(utf8.AsSpan(1))).ToList();
        Assert.Equal(["ändern"], lines);
    }

    [Theory]
    [InlineData(@"rundll32.exe C:\Program Files\Alive\alive.exe,Run")]
    [InlineData(@"C:\Program Files\Alive\alive.exe --tray")]
    [InlineData("C:\\Program Files\\B\uFFFDro\\x.exe")]
    public void Unquoted_or_undecodable_paths_are_not_called_missing(string command)
    {
        var fs = new InMemoryFileSystem().Dir(@"C:\").File(@"C:\Program Files\Alive\alive.exe");
        Assert.False(new RegistryCleaner(new InMemoryRegistry(), fs, TestFolders.Create()).IsDefinitelyMissing(command));
    }

    [Fact]
    public void Firefox_profiles_listed_in_profiles_ini_are_found()
    {
        var f = TestFolders.Create();
        var ini = """
            [General]
            StartWithLastProfile=1

            [Profile0]
            Name=default-release
            IsRelative=1
            Path=Profiles/ab12.default-release

            [Profile1]
            Name=work
            IsRelative=0
            Path=D:\Firefox\work
            """;
        var fs = new InMemoryFileSystem()
            .File(f.RoamingAppData + @"\Mozilla\Firefox\profiles.ini", Encoding.UTF8.GetBytes(ini))
            .File(f.RoamingAppData + @"\Mozilla\Firefox\Profiles\ab12.default-release\places.sqlite")
            .File(@"D:\Firefox\work\places.sqlite")
            .File(f.LocalAppData + @"\Packages\Mozilla.Firefox_n80bbvh6b1yt2\LocalCache\Roaming\Mozilla\Firefox\Profiles\zz.store\cookies.sqlite");
        var paths = new BrowserPrivacy(fs, f).FindProfiles().Select(p => p.Path).ToList();
        Assert.Equal(3, paths.Count);
        Assert.Contains(@"D:\Firefox\work", paths);
        Assert.Contains(paths, p => p.EndsWith(@"zz.store", StringComparison.Ordinal));
    }

    [Fact]
    public void Newer_opera_profiles_live_in_default_and_side_profiles()
    {
        var f = TestFolders.Create();
        var fs = new InMemoryFileSystem()
            .File(f.RoamingAppData + @"\Opera Software\Opera Stable\Local State")
            .File(f.RoamingAppData + @"\Opera Software\Opera Stable\Default\History")
            .File(f.RoamingAppData + @"\Opera Software\Opera Stable\_side_profiles\Work\Preferences");
        var profiles = new BrowserPrivacy(fs, f).FindProfiles();
        Assert.Equal([f.RoamingAppData + @"\Opera Software\Opera Stable\Default", f.RoamingAppData + @"\Opera Software\Opera Stable\_side_profiles\Work"],
                     profiles.Select(p => p.Path));
    }

    [Fact]
    public void Privacy_undo_restores_a_value_that_was_not_a_dword()
    {
        var key = @"HKCU\Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo";
        var reg = new InMemoryRegistry().Set(key, "Enabled", RegValue.String("yes"));
        var fs = new InMemoryFileSystem();
        var tweaks = new PrivacyTweaks(reg, fs, @"C:\ProgramData\Scour\privacy-backup.json");
        var adid = PrivacyTweaks.All.Single(t => t.Id == "adid");

        tweaks.Apply(adid);
        Assert.Equal(RegValueKind.DWord, reg.GetValue(RegKeyPath.Parse(key), "Enabled")!.Kind);
        // A fresh instance reads the backup back from disk, as after a restart.
        new PrivacyTweaks(reg, fs, @"C:\ProgramData\Scour\privacy-backup.json").Undo(adid);
        Assert.Equal(RegValue.String("yes"), reg.GetValue(RegKeyPath.Parse(key), "Enabled"));
    }

    [Theory]
    [InlineData(@"C:\PROGRA~1\Vendor", true)]
    [InlineData(@"C:\Users\JOHNSM~1\AppData\Local\Vendor", true)]
    [InlineData(@"C:\Program Files\My~1 App", false)]
    [InlineData(@"C:\Program Files\Vendor\App", false)]
    public void Short_8dot3_paths_are_never_deletable(string path, bool isProtected)
    {
        var guard = new SafetyGuard(TestFolders.Create(), [], []);
        Assert.Equal(isProtected, guard.IsProtectedPath(path));
    }

    [Fact]
    public void Words_keep_non_english_letters()
    {
        Assert.Equal(["éditeur", "vidéo"], NameNormalizer.Words("Éditeur Vidéo"));
        Assert.Equal(["яндекс", "браузер"], NameNormalizer.Words("Яндекс.Браузер"));
        Assert.Equal(["my", "app"], NameNormalizer.Words("MyApp"));
    }
}
