using System.Text;
using Scour.Core.Gaming;
using Scour.Core.Platform;
using Scour.Tests.Fakes;

namespace Scour.Tests;

public class GameLibraryTests
{
    private static byte[] Text(string s) => Encoding.UTF8.GetBytes(s);

    [Fact]
    public void Vdf_reads_nested_blocks_escapes_and_comments()
    {
        var vdf = Vdf.Parse("""
            // comment
            "libraryfolders"
            {
                "0" { "path" "C:\\Program Files (x86)\\Steam" "apps" { "570" "123" } }
                "1"
                {
                    "path"		"D:\\SteamLibrary"
                }
            }
            """);
        var libs = vdf.Child("libraryfolders")!.Children;
        Assert.Equal([@"C:\Program Files (x86)\Steam", @"D:\SteamLibrary"], libs.Select(l => l.Node.Value("path")));
        Assert.Equal("123", libs[0].Node.Child("apps")!.Value("570"));
    }

    [Fact]
    public void Vdf_survives_truncated_files()
    {
        var vdf = Vdf.Parse("\"AppState\" { \"appid\" \"10\" \"name\"");
        Assert.Equal("10", vdf.Child("AppState")!.Value("appid"));
    }

    [Fact]
    public void Finds_games_from_every_store()
    {
        var f = TestFolders.Create();
        var reg = new InMemoryRegistry()
            .Set(@"HKCU\Software\Valve\Steam", "SteamPath", "c:/program files (x86)/steam")
            .Set(@"HKLM\SOFTWARE\WOW6432Node\GOG.com\Games\1207658924", "gameName", "The Witcher")
            .Set(@"HKLM\SOFTWARE\WOW6432Node\GOG.com\Games\1207658924", "path", @"D:\GOG Games\The Witcher")
            .Set(@"HKLM\SOFTWARE\WOW6432Node\Ubisoft\Launcher\Installs\5595", "InstallDir", "D:/Ubisoft/Far Cry 6/");
        var fs = new InMemoryFileSystem()
            .File(@"C:\Program Files (x86)\Steam\steamapps\libraryfolders.vdf",
                  Text("\"libraryfolders\" { \"0\" { \"path\" \"C:\\\\Program Files (x86)\\\\Steam\" } \"1\" { \"path\" \"E:\\\\SteamLibrary\" } }"))
            .File(@"C:\Program Files (x86)\Steam\steamapps\appmanifest_570.acf", Text("\"AppState\" { \"appid\" \"570\" \"name\" \"Dota 2\" \"installdir\" \"dota 2 beta\" }"))
            .File(@"C:\Program Files (x86)\Steam\steamapps\appmanifest_228980.acf", Text("\"AppState\" { \"appid\" \"228980\" \"name\" \"Steamworks Common Redistributables\" \"installdir\" \"Steamworks Shared\" }"))
            .Dir(@"C:\Program Files (x86)\Steam\steamapps\common\dota 2 beta")
            .Dir(@"C:\Program Files (x86)\Steam\steamapps\common\Steamworks Shared")
            .File(@"E:\SteamLibrary\steamapps\appmanifest_1091500.acf", Text("\"AppState\" { \"appid\" \"1091500\" \"name\" \"Cyberpunk 2077\" \"installdir\" \"Cyberpunk 2077\" }"))
            .Dir(@"E:\SteamLibrary\steamapps\common\Cyberpunk 2077")
            .File(@"E:\SteamLibrary\steamapps\appmanifest_99.acf", Text("\"AppState\" { \"appid\" \"99\" \"name\" \"Uninstalled\" \"installdir\" \"Gone\" }"))
            .File(f.ProgramData + @"\Epic\EpicGamesLauncher\Data\Manifests\A.item",
                  Text("""{ "DisplayName": "Fortnite", "AppName": "Fortnite", "InstallLocation": "C:/Program Files/Epic Games/Fortnite", "AppCategories": ["public", "games"] }"""))
            .File(f.ProgramData + @"\Epic\EpicGamesLauncher\Data\Manifests\B.item",
                  Text("""{ "DisplayName": "Unreal Engine", "AppName": "UE_5.4", "InstallLocation": "C:/Program Files/Epic Games/UE_5.4", "AppCategories": ["engines"] }"""))
            .File(f.ProgramData + @"\Epic\EpicGamesLauncher\Data\Manifests\C.item", Text("{ broken"))
            .Dir(@"C:\Program Files\Epic Games\Fortnite")
            .Dir(@"C:\Program Files\Epic Games\UE_5.4")
            .Dir(@"D:\GOG Games\The Witcher")
            .Dir(@"D:\Ubisoft\Far Cry 6")
            .Dir(@"C:\Riot Games\VALORANT")
            .Dir(@"C:\Riot Games\Riot Client")
            .Dir(@"D:\XboxGames\Halo Infinite")
            .Dir(@"D:\My Games\Indie");

        var games = new GameLibrary(reg, fs, f).Find([@"C:\", @"D:\"], [@"D:\My Games\Indie"]);
        Assert.Equal(["Cyberpunk 2077", "Dota 2", "Far Cry 6", "Fortnite", "Halo Infinite", "Indie", "The Witcher", "VALORANT"],
                     games.Select(g => g.Name));
        Assert.Equal("steam:570", games.Single(g => g.Name == "Dota 2").Id);
        Assert.Equal(@"C:\Program Files\Epic Games\Fortnite", games.Single(g => g.Name == "Fortnite").InstallDir);
    }

    [Theory]
    [InlineData(@"E:\SteamLibrary\steamapps\common\Cyberpunk 2077\bin\x64\Cyberpunk2077.exe", "Cyberpunk 2077")]
    [InlineData(@"E:\SteamLibrary\steamapps\common\Cyberpunk 2077\REDprelauncher.exe", null)]
    [InlineData(@"E:\SteamLibrary\steamapps\common\Cyberpunk 2077\bin\x64\CrashReporter.exe", null)]
    [InlineData(@"E:\SteamLibrary\steamapps\common\Cyberpunk 2077\EasyAntiCheat\EasyAntiCheat_EOS_Setup.exe", null)]
    [InlineData(@"E:\SteamLibrary\steamapps\common\Cyberpunk 2077Mods\tool.exe", null)]
    [InlineData(@"C:\Windows\notepad.exe", null)]
    [InlineData(null, null)]
    public void Recognises_the_game_but_not_its_helpers(string? exe, string? game)
    {
        GameInfo[] games = [new("steam:1091500", "Cyberpunk 2077", "Steam", @"E:\SteamLibrary\steamapps\common\Cyberpunk 2077")];
        Assert.Equal(game, GameLibrary.GameFor(exe, games)?.Name);
    }
}
