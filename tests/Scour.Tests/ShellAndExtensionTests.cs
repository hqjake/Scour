using System.Text;
using Scour.Core.Junk;
using Scour.Core.Platform;
using Scour.Core.Shell;
using Scour.Tests.Fakes;

namespace Scour.Tests;

public class ShellAndExtensionTests
{
    private const string VsCodeHandler = "{CE3B2E2A-1A41-4B5A-9E5F-6B0B3E5C1F11}";
    private const string SevenZip = "{23170F69-40C1-278A-1000-000100020000}";
    private const string WindowsHandler = "{09A47860-11B0-4DA5-AFA5-26D86198A780}";

    private static InMemoryRegistry MenuRegistry()
    {
        var reg = new InMemoryRegistry();
        var lm = RegKeyPath.LocalMachine(@"SOFTWARE\Classes");
        var cu = RegKeyPath.CurrentUser(@"Software\Classes");
        // A program verb with an accelerator, a Windows verb and a hidden one.
        reg.SetValue(cu.Child(@"*\shell\VSCode"), "", RegValue.String("Open w&ith Code"));
        reg.SetValue(cu.Child(@"*\shell\VSCode\command"), "", RegValue.String("\"C:\\Users\\Alex\\AppData\\Local\\Programs\\Microsoft VS Code\\Code.exe\" \"%1\""));
        reg.SetValue(lm.Child(@"Directory\Background\shell\cmd"), "", RegValue.String("@shell32.dll,-8506"));
        reg.SetValue(lm.Child(@"Directory\Background\shell\cmd\command"), "", RegValue.Expand("cmd.exe /s /k pushd \"%V\""));
        reg.SetValue(lm.Child(@"Directory\shell\Hidden"), "ProgrammaticAccessOnly", RegValue.String(""));
        reg.SetValue(lm.Child(@"Directory\shell\Hidden\command"), "", RegValue.String("x.exe"));
        reg.SetValue(lm.Child(@"Drive\shell\Scan"), "Extended", RegValue.String(""));
        reg.SetValue(lm.Child(@"Drive\shell\Scan\command"), "", RegValue.String(@"C:\Program Files\AV\scan.exe %1"));
        // Handlers: named key with CLSID default, CLSID-named key, a Windows one.
        reg.SetValue(lm.Child(@"*\shellex\ContextMenuHandlers\7-Zip"), "", RegValue.String(SevenZip));
        reg.SetValue(lm.Child(@"Directory\shellex\ContextMenuHandlers\7-Zip"), "", RegValue.String(SevenZip));
        reg.SetValue(lm.Child($@"CLSID\{SevenZip}"), "", RegValue.String("7-Zip Shell Extension"));
        reg.SetValue(lm.Child($@"CLSID\{SevenZip}\InprocServer32"), "", RegValue.String(@"C:\Program Files\7-Zip\7-zip.dll"));
        reg.SetValue(lm.Child(@"*\shellex\ContextMenuHandlers\Sharing"), "", RegValue.String(WindowsHandler));
        reg.SetValue(lm.Child($@"CLSID\{WindowsHandler}\InprocServer32"), "", RegValue.Expand(@"%SystemRoot%\system32\ntshrui.dll"));
        reg.CreateKey(lm.Child($@"*\shellex\ContextMenuHandlers\{VsCodeHandler}"));
        reg.SetValue(lm.Child(@"*\shellex\ContextMenuHandlers\Broken"), "", RegValue.String("not a clsid"));
        return reg;
    }

    [Fact]
    public void Lists_context_menu_entries_with_names_targets_and_origin()
    {
        var menu = new ContextMenu(MenuRegistry(), TestFolders.Create(), s => s == "@shell32.dll,-8506" ? "Open in Terminal" : null);
        var entries = menu.Scan();

        var code = Assert.Single(entries, e => e.Name == "Open with Code");
        Assert.Equal("Files", code.AppliesTo);
        Assert.False(code.IsWindows);
        Assert.True(code.IsEnabled);

        Assert.True(Assert.Single(entries, e => e.Name == "Open in Terminal").IsWindows);
        Assert.DoesNotContain(entries, e => e.Name == "Hidden");
        Assert.True(Assert.Single(entries, e => e.Name == "Scan").ShiftOnly);
        Assert.Equal(2, entries.Count(e => e.Name == "7-Zip Shell Extension"));
        Assert.True(Assert.Single(entries, e => e.Clsid == WindowsHandler).IsWindows);
        // Unregistered CLSID: no dll known, so it's treated as Windows' own and never offered first.
        Assert.Contains(entries, e => e.Clsid == VsCodeHandler);
        Assert.DoesNotContain(entries, e => e.Name == "Broken");
        // Program entries come first.
        Assert.False(entries[0].IsWindows);
    }

    [Fact]
    public void Disabling_is_reversible()
    {
        var reg = MenuRegistry();
        var menu = new ContextMenu(reg, TestFolders.Create());
        var code = menu.Scan().Single(e => e.Name == "Open with Code");
        menu.SetEnabled(code, false);
        Assert.True(reg.ValueExists(code.Key, "LegacyDisable"));
        Assert.False(menu.Scan().Single(e => e.Name == "Open with Code").IsEnabled);
        menu.SetEnabled(code, true);
        Assert.False(reg.ValueExists(code.Key, "LegacyDisable"));

        var zip = menu.Scan().First(e => e.Clsid == SevenZip);
        menu.SetEnabled(zip, false);
        Assert.True(reg.ValueExists(ContextMenu.BlockedMachine, SevenZip));
        Assert.All(menu.Scan().Where(e => e.Clsid == SevenZip), e => Assert.False(e.IsEnabled)); // blocked everywhere it appears
        menu.SetEnabled(zip, true);
        Assert.All(menu.Scan().Where(e => e.Clsid == SevenZip), e => Assert.True(e.IsEnabled));
    }

    private static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);

    [Fact]
    public void Reads_chromium_and_firefox_extensions_and_rates_them()
    {
        var f = TestFolders.Create();
        var chrome = f.LocalAppData + @"\Google\Chrome\User Data\Default";
        const string ublock = "cjpalhdlnbpafiamejdnhcphjbkeiagm", shady = "abcdefghijklmnopabcdefghijklmnop", builtin = "nmmhkkegccagdldgiimedpiccmgmieda";
        var fs = new InMemoryFileSystem()
            .File(chrome + @"\Preferences", Utf8($$"""
                { "extensions": { "settings": {
                    "{{ublock}}": { "state": 1, "location": 1 },
                    "{{shady}}": { "state": 1, "location": 1 },
                    "{{builtin}}": { "location": 5 } } } }
                """))
            .File(chrome + $@"\Extensions\{ublock}\1.60.0_0\manifest.json", Utf8("""
                { "name": "__MSG_extName__", "version": "1.60.0", "default_locale": "en",
                  "permissions": ["storage", "declarativeNetRequest"], "host_permissions": ["<all_urls>"] }
                """))
            .File(chrome + $@"\Extensions\{ublock}\1.60.0_0\_locales\en\messages.json", Utf8("""{ "extName": { "message": "uBlock Origin" } }"""))
            .File(chrome + $@"\Extensions\{ublock}\1.59.0_0\manifest.json", Utf8("""{ "name": "old", "version": "1.59.0" }"""))
            .File(chrome + $@"\Extensions\{shady}\2.0_0\manifest.json", Utf8("""
                { "name": "Coupon Helper", "version": "2.0", "permissions": ["nativeMessaging", "tabs"] }
                """))
            .File(chrome + $@"\Extensions\{builtin}\1.0_0\manifest.json", Utf8("""{ "name": "Web Store", "version": "1.0" }"""))
            .File(f.RoamingAppData + @"\Mozilla\Firefox\Profiles\x1.default-release\places.sqlite")
            .File(f.RoamingAppData + @"\Mozilla\Firefox\Profiles\x1.default-release\extensions.json", Utf8("""
                { "addons": [
                  { "id": "addon@darkreader.org", "type": "extension", "location": "app-profile", "active": false, "version": "4.9",
                    "defaultLocale": { "name": "Dark Reader" }, "userPermissions": { "permissions": ["storage"], "origins": ["<all_urls>"] } },
                  { "id": "formautofill@mozilla.org", "type": "extension", "location": "app-builtin", "active": true },
                  { "id": "theme@x", "type": "theme", "location": "app-profile", "active": true } ] }
                """));
        var reg = new InMemoryRegistry();
        reg.SetValue(RegKeyPath.LocalMachine(@"SOFTWARE\Policies\Google\Chrome\ExtensionInstallForcelist"), "1",
                     RegValue.String(shady + ";https://clients2.google.com/service/update2/crx"));

        var scanner = new BrowserExtensions(fs, reg, new BrowserPrivacy(fs, f));
        var all = scanner.FindAll();
        Assert.Equal(["Coupon Helper", "Dark Reader", "uBlock Origin"], all.Select(e => e.Name).Order());

        var ub = all.Single(e => e.Name == "uBlock Origin");
        Assert.Equal("1.60.0", ub.Version); // newest version folder
        Assert.Equal(ExtensionSource.Store, ub.Source);
        Assert.Equal(ExtensionRisk.High, ub.Risk); // all sites

        var coupon = all.Single(e => e.Name == "Coupon Helper");
        Assert.Equal(ExtensionSource.Policy, coupon.Source);
        Assert.Contains("Talk to programs installed on this PC", coupon.Abilities);
        Assert.Equal(all[0], coupon); // highest risk first, then name

        Assert.False(all.Single(e => e.Name == "Dark Reader").Enabled);

        var forced = Assert.Single(scanner.ForcedInstalls());
        Assert.Equal(shady, forced.ExtensionId);
        scanner.Remove(forced);
        Assert.Empty(scanner.ForcedInstalls());
    }

    [Fact]
    public void Plain_extensions_are_low_risk() =>
        Assert.Equal(ExtensionRisk.Low, BrowserExtensions.Assess(["storage", "alarms"]).Risk);
}
