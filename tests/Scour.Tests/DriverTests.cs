using Scour.Core.Drivers;

namespace Scour.Tests;

public class DriverTests
{
    [Fact]
    public void Parses_inf_version_section_with_string_tokens()
    {
        const string inf = """
            ; Copyright (c) Contoso
            [Version]
            Signature   = "$WINDOWS NT$"
            Class       = Display   ; adapters
            ClassGUID   = {4d36e968-e325-11ce-bfc1-08002be10318}
            Provider    = %ContosoMfg%
            DriverVer   = 03/14/2025,31.0.15.3623

            [Strings]
            ContosoMfg = "Contoso; Graphics"

            [Strings.0409]
            ContosoMfg = "Contoso Graphics Inc."
            """;
        var info = InfParser.Parse(inf);
        Assert.Equal("Display", info.Class);
        Assert.Equal("Contoso Graphics Inc.", info.Provider);
        Assert.Equal(new DateTime(2025, 3, 14), info.Date);
        Assert.Equal(new Version(31, 0, 15, 3623), info.Version);

        Assert.Equal("Contoso; Graphics", InfParser.Parse(inf.Replace("[Strings.0409]", "[Other]")).Provider);
        Assert.Null(InfParser.Parse("[Version]\nClass=Net\n").Version);
    }

    private static DriverPackage P(string oem, string original, string version, string date, params string[] devices) =>
        new(oem, original, new InfInfo("Display", "Contoso", DateTime.Parse(date, System.Globalization.CultureInfo.InvariantCulture), new Version(version)), 1000, devices);

    [Fact]
    public void Old_versions_are_found_and_the_newest_copy_is_always_kept()
    {
        var packages = new[]
        {
            P("oem1.inf", "contoso.inf", "30.0.0.1", "2024-01-01"),
            P("oem2.inf", "contoso.inf", "31.0.0.5", "2025-03-01", "Contoso RTX"),
            P("oem3.inf", "contoso.inf", "29.0.0.1", "2023-06-01"),
            // Unplugged device: two copies, neither in use — keep the newest.
            P("oem4.inf", "webcam.inf", "1.0.0.0", "2022-01-01"),
            P("oem5.inf", "webcam.inf", "1.2.0.0", "2023-01-01"),
            // Same version twice: neither is "old".
            P("oem6.inf", "audio.inf", "6.0.0.0", "2024-05-05"),
            P("oem7.inf", "audio.inf", "6.0.0.0", "2024-05-05", "Speakers"),
        };
        var s = DriverStore.Classify(packages);
        Assert.Equal(DriverState.Old, s["oem1.inf"]);
        Assert.Equal(DriverState.InUse, s["oem2.inf"]);
        Assert.Equal(DriverState.Old, s["oem3.inf"]);
        Assert.Equal(DriverState.Old, s["oem4.inf"]);
        Assert.Equal(DriverState.NotInUse, s["oem5.inf"]);
        Assert.Equal(DriverState.NotInUse, s["oem6.inf"]);
        Assert.Equal(DriverState.InUse, s["oem7.inf"]);
    }

    [Fact]
    public void Package_ids_and_pnputil_arguments()
    {
        Assert.Equal("nv_dispig.inf", DriverStore.OriginalName("nv_dispig.inf_amd64_1a2b3c4d5e6f7a8b"));
        Assert.Equal("/delete-driver oem12.inf", DriverStore.DeleteArguments("oem12.inf"));
        Assert.Equal(@"/add-driver ""D:\Drivers\*.inf"" /subdirs /install", DriverStore.RestoreArguments(@"D:\Drivers"));
    }
}
