using Scour.Core.Updates;

namespace Scour.Tests;

public class WingetTests
{
    [Fact]
    public void Parses_upgrade_tables_with_spinner_noise_and_explicit_section()
    {
        string[] output =
        [
            "   - \r   \\ \r   | \r",
            "Name                                      Id                           Version      Available    Source",
            "-------------------------------------------------------------------------------------------------------",
            "Mozilla Firefox (x64 en-US)               Mozilla.Firefox              126.0        127.0        winget",
            "Microsoft Visual C++ 2015-2022 Redistrib… Microsoft.VCRedist.2015+.x64 14.38.33135.0 14.40.33810.0 winget",
            "7-Zip 23.01 (x64)                         7zip.7zip                    23.01        24.07        winget",
            "Some Tool                                 Vendor.Tool                  < 2.0        2.1          winget",
            "4 upgrades available.",
            "",
            "The following packages have an upgrade available, but require explicit targeting for upgrade:",
            "Name    Id              Version   Available Source",
            "--------------------------------------------------",
            "Discord Discord.Discord 1.0.9147  1.0.9150  winget",
        ];
        var p = WingetParser.ParseUpgradeTable(output);
        Assert.Equal(["Mozilla.Firefox", "Microsoft.VCRedist.2015+.x64", "7zip.7zip", "Vendor.Tool", "Discord.Discord"], p.Select(x => x.Id));
        Assert.Equal("Microsoft Visual C++ 2015-2022 Redistrib", p[1].Name);
        Assert.Equal("14.40.33810.0", p[1].Available);
        Assert.Equal("< 2.0", p[3].Version);
        Assert.False(p[0].RequiresExplicit);
        Assert.True(p[4].RequiresExplicit);
        Assert.Contains("--include-unknown", WingetParser.UpgradeArguments(p[3]));
        Assert.DoesNotContain("--include-unknown", WingetParser.UpgradeArguments(p[0]));
    }

    [Fact]
    public void Handles_wide_characters_and_localized_headers()
    {
        // Japanese names are two columns wide per character; winget pads by display width.
        string[] output =
        [
            "名前         ID               バージョン 利用可能 ソース",
            "-------------------------------------------------------",
            "秀丸エディタ Hidemaru.Editor  9.20       9.35     winget",
            "Notepad++    Notepad++.Notepad++ 8.6     8.7      winget",
        ];
        var p = WingetParser.ParseUpgradeTable(output);
        Assert.Equal("秀丸エディタ", p[0].Name);
        Assert.Equal("Hidemaru.Editor", p[0].Id);
        Assert.Equal("9.35", p[0].Available);
    }

    [Fact]
    public void Empty_or_no_upgrades_output_yields_nothing()
    {
        Assert.Empty(WingetParser.ParseUpgradeTable(["No installed package found matching input criteria."]));
        Assert.Empty(WingetParser.ParseUpgradeTable([]));
    }

    [Fact]
    public void Describes_exit_codes()
    {
        Assert.Equal("Updated", WingetParser.DescribeExitCode(0));
        Assert.Contains("close it", WingetParser.DescribeExitCode(unchecked((int)0x8A150101)));
        Assert.True(WingetParser.IsSuccess(unchecked((int)0x8A150109)));
        Assert.False(WingetParser.IsSuccess(1));
    }
}
