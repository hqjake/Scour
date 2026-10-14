using Scour.Core.Matching;
using Scour.Core.Platform;
using Scour.Core.Programs;
using Scour.Core.Shortcuts;
using Scour.Tests.Fakes;

namespace Scour.Tests;

public class PrimitivesTests
{
    [Fact]
    public void RegKeyPath_parses_and_navigates()
    {
        var p = RegKeyPath.Parse(@"HKEY_LOCAL_MACHINE\SOFTWARE\Mozilla\Firefox");
        Assert.Equal(RegHive.LocalMachine, p.Hive);
        Assert.Equal("Firefox", p.Name);
        Assert.Equal(@"HKLM\SOFTWARE\Mozilla", p.Parent!.Value.ToString());
        Assert.Equal(3, p.Depth);
        Assert.True(p.IsSameOrUnder(RegKeyPath.Parse(@"HKLM\software\mozilla")));
        Assert.False(RegKeyPath.Parse(@"HKLM\SOFTWARE\MozillaX").IsSameOrUnder(RegKeyPath.Parse(@"HKLM\SOFTWARE\Mozilla")));
        Assert.Equal(RegKeyPath.Parse(@"HKLM\SOFTWARE\A"), RegKeyPath.Parse(@"hklm\software\a"));
        Assert.True(RegKeyPath.TryParse(@"Computer\HKEY_CURRENT_USER\Software", out var cu));
        Assert.Equal(RegHive.CurrentUser, cu.Hive);
    }

    [Theory]
    [InlineData(@"""C:\Program Files\App\unins000.exe"" /SILENT", @"C:\Program Files\App\unins000.exe", "/SILENT")]
    [InlineData(@"C:\Program Files\App\uninst.exe /S", @"C:\Program Files\App\uninst.exe", "/S")]
    [InlineData(@"MsiExec.exe /X{AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE}", "MsiExec.exe", "/X{AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE}")]
    [InlineData(@"C:\Tools\x.exe", @"C:\Tools\x.exe", "")]
    [InlineData(@"""C:\Program Files\App\helper.exe""", @"C:\Program Files\App\helper.exe", "")]
    public void CommandLine_split(string input, string exe, string args)
    {
        var (e, a) = CommandLine.Split(input);
        Assert.Equal(exe, e);
        Assert.Equal(args, a);
    }

    [Fact]
    public void CommandLine_parses_icon_locations_and_extracts_paths()
    {
        Assert.Equal((@"C:\x\app.exe", 2), CommandLine.ParseIconLocation(@"""C:\x\app.exe"",2"));
        Assert.Equal((@"C:\x\app.ico", 0), CommandLine.ParseIconLocation(@"C:\x\app.ico"));
        var paths = CommandLine.ExtractPaths(@"""C:\Program Files\A\a.exe"" --config C:\Data\a.cfg").ToList();
        Assert.Contains(@"C:\Program Files\A\a.exe", paths);
        Assert.Contains(@"C:\Data\a.cfg", paths);
    }

    [Fact]
    public void MsiGuid_pack_roundtrips()
    {
        const string code = "{90160000-008C-0000-1000-0000000FF1CE}";
        var packed = MsiGuid.Pack(code);
        Assert.Equal("00006109C80000000100000000F01FEC", packed);
        Assert.Equal(code, MsiGuid.Unpack(packed));
        Assert.True(MsiGuid.TryNormalize("90160000-008c-0000-1000-0000000ff1ce", out var n));
        Assert.Equal(code, n);
    }

    [Theory]
    [InlineData("Mozilla Firefox (x64 en-US)", "Mozilla Firefox")]
    [InlineData("7-Zip 23.01 (x64)", "7-Zip")]
    [InlineData("Python 3.12.1 (64-bit)", "Python")]
    [InlineData("Notepad++ (64-bit x64)", "Notepad++")]
    [InlineData("VLC media player", "VLC media player")]
    [InlineData("WinRAR 7.01 (64-bit)", "WinRAR")]
    [InlineData("Git version 2.45.1", "Git version")]
    public void CleanDisplayName(string input, string expected) =>
        Assert.Equal(expected, NameNormalizer.CleanDisplayName(input));

    [Theory]
    [InlineData("Mozilla Corporation", "Mozilla")]
    [InlineData("Google LLC", "Google")]
    [InlineData("The Document Foundation", "Document")]
    [InlineData("Igor Pavlov", "Igor Pavlov")]
    [InlineData("Valve Corp.", "Valve")]
    public void CleanPublisher(string input, string expected) =>
        Assert.Equal(expected, NameNormalizer.CleanPublisher(input));

    [Fact]
    public void Normalize_keeps_plus_meaningful()
    {
        Assert.Equal("notepadpp", NameNormalizer.Normalize("Notepad++"));
        Assert.NotEqual(NameNormalizer.Normalize("Notepad"), NameNormalizer.Normalize("Notepad++"));
        Assert.Equal("7zip", NameNormalizer.Normalize("7-Zip"));
    }

    [Fact]
    public void NameMatcher_grades_candidates()
    {
        var m = new NameMatcher("Mozilla Firefox (x64 en-US)", "Mozilla", @"C:\Program Files\Mozilla Firefox");
        Assert.Equal(MatchStrength.Exact, m.Match("Mozilla Firefox"));
        Assert.Equal(MatchStrength.Exact, m.Match("Firefox"));
        Assert.Equal(MatchStrength.Strong, m.Match("FirefoxHTML-308046B0AF4A39CB"));
        Assert.Equal(MatchStrength.None, m.Match("Thunderbird"));
        Assert.Equal(MatchStrength.None, m.Match("Microsoft"));
        Assert.True(m.MatchesPublisher("Mozilla"));
        Assert.False(m.MatchesPublisher("Microsoft"));
    }

    [Fact]
    public void NameMatcher_ignores_generic_names()
    {
        var m = new NameMatcher("Update", null, null);
        Assert.False(m.HasNameKeys);
        Assert.Equal(MatchStrength.None, m.Match("Update"));

        var np = new NameMatcher("Notepad++ (64-bit x64)", "Notepad++ Team", @"C:\Program Files\Notepad++");
        Assert.Equal(MatchStrength.Exact, np.Match("Notepad++"));
        Assert.Equal(MatchStrength.None, np.Match("Notepad"));
    }

    [Fact]
    public void WinPath_helpers_are_case_insensitive_and_boundary_aware()
    {
        Assert.True(WinPath.IsSameOrUnder(@"C:\Program Files\App\x.dll", @"c:\program files\app"));
        Assert.False(WinPath.IsSameOrUnder(@"C:\Program Files\App2", @"C:\Program Files\App"));
        Assert.Equal(@"C:\", WinPath.GetDirectoryName(@"C:\Foo"));
        Assert.Equal(@"C:\Foo", WinPath.Normalize(@"""C:/Foo/"""));
    }

    [Fact]
    public void LnkParser_reads_target_and_strings()
    {
        var data = LnkBuilder.Build(@"C:\Program Files\App\app.exe", @"C:\Program Files\App", "--fast");
        var info = LnkParser.Parse(data);
        Assert.Equal(@"C:\Program Files\App\app.exe", info.TargetPath);
        Assert.Equal(@"C:\Program Files\App", info.WorkingDirectory);
        Assert.Equal("--fast", info.Arguments);
        Assert.Null(LnkParser.TryParse([1, 2, 3]));
    }
}
