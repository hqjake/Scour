using Scour.Core.Search;
using Scour.Core.Updates;

namespace Scour.Tests;

public class PaletteKitReleaseTests
{
    private static CommandEntry E(string title, params string[] keywords) => new(title, "", "Pages", "", keywords, title);

    private static readonly CommandEntry[] Entries =
    [
        E("Junk cleaner", "temp", "cache"), E("Disk space analyzer", "wiztree", "treemap"), E("Uninstaller", "remove"),
        E("Registry cleaner"), E("Uninstall Google Chrome"), E("USB tools", "format", "bootable"), E("Flush DNS cache"),
    ];

    [Theory]
    [InlineData("junk", "Junk cleaner")]
    [InlineData("jc", "Junk cleaner")]
    [InlineData("disk", "Disk space analyzer")]
    [InlineData("dskan", "Disk space analyzer")]
    [InlineData("chrome", "Uninstall Google Chrome")]
    [InlineData("format", "USB tools")]
    [InlineData("wiz", "Disk space analyzer")]
    [InlineData("flush dns", "Flush DNS cache")]
    public void Best_match_comes_first(string query, string expected) =>
        Assert.Equal(expected, CommandSearch.Find(Entries, query)[0].Title);

    [Fact]
    public void Nonsense_finds_nothing_and_empty_lists_everything()
    {
        Assert.Empty(CommandSearch.Find(Entries, "zzqx"));
        Assert.Equal(Entries.Length, CommandSearch.Find(Entries, "  ").Count);
    }

    [Fact]
    public void Parses_winget_export_and_finds_missing_apps()
    {
        const string json = """
        {
          "$schema": "https://aka.ms/winget-packages.schema.2.0.json",
          "Sources": [
            { "SourceDetails": { "Name": "winget", "Argument": "https://cdn.winget.microsoft.com/cache" },
              "Packages": [ { "PackageIdentifier": "Mozilla.Firefox" }, { "PackageIdentifier": "7zip.7zip", "Version": "24.07" },
                            { "PackageIdentifier": "mozilla.firefox" } ] },
            { "SourceDetails": { "Name": "msstore" }, "Packages": [ { "PackageIdentifier": "9NBLGGH4NNS1" } ] }
          ]
        }
        """;
        var kit = ReinstallKit.Parse(json);
        Assert.Equal(["Mozilla.Firefox", "7zip.7zip", "9NBLGGH4NNS1"], kit.Select(a => a.Id));
        Assert.Equal("msstore", kit[2].Source);
        Assert.Equal("24.07", kit[1].Version);
        var missing = ReinstallKit.Missing(kit, [new KitApp("MOZILLA.FIREFOX", "winget", null)]);
        Assert.Equal(["7zip.7zip", "9NBLGGH4NNS1"], missing.Select(a => a.Id));
        Assert.Contains("--source msstore", ReinstallKit.InstallArguments(kit[2]));

        var trimmed = ReinstallKit.Parse(ReinstallKit.Filter(json, new HashSet<string>(["7zip.7zip"], StringComparer.OrdinalIgnoreCase)));
        Assert.Equal("7zip.7zip", Assert.Single(trimmed).Id);
    }

    [Fact]
    public void Reads_github_release_and_compares_versions()
    {
        const string json = """
        { "tag_name": "v1.2.0", "name": "Scour 1.2", "body": "New stuff", "draft": false, "prerelease": false,
          "html_url": "https://github.com/hqjake/Scour/releases/tag/v1.2.0",
          "assets": [ { "name": "Scour.exe", "browser_download_url": "https://github.com/hqjake/Scour/releases/download/v1.2.0/Scour.exe",
                        "digest": "sha256:ABCDEF0123" } ] }
        """;
        var r = ReleaseCheck.Parse(json)!;
        Assert.Equal(new Version(1, 2, 0), r.Version);
        Assert.EndsWith("/Scour.exe", r.DownloadUrl);
        Assert.Equal("abcdef0123", r.Sha256);
        Assert.True(ReleaseCheck.IsNewer(r, new Version(1, 1, 9)));
        Assert.False(ReleaseCheck.IsNewer(r, new Version(1, 2, 0, 0)));
        Assert.False(ReleaseCheck.IsNewer(r, new Version(2, 0)));
        Assert.Null(ReleaseCheck.Parse(json.Replace("\"prerelease\": false", "\"prerelease\": true")));
        Assert.True(ReleaseCheck.TryParseVersion("V3-beta.1", out var v3));
        Assert.Equal(new Version(3, 0, 0), v3);
    }

    [Theory]
    [InlineData("Microsoft.VisualStudioCode", "Visual Studio Code")]
    [InlineData("Python.Python.3.12", "Python 3.12")]
    [InlineData("Git.Git", "Git")]
    [InlineData("VideoLAN.VLC", "VLC")]
    [InlineData("9NBLGGH4NNS1", "9NBLGGH4NNS1")]
    public void Friendly_names_from_package_ids(string id, string expected) =>
        Assert.Equal(expected, ReinstallKit.FriendlyName(id));
}
