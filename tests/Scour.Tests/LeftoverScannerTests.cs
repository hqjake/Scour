using Scour.Core.Leftovers;
using Scour.Core.Platform;
using Scour.Core.Programs;
using Scour.Tests.Fakes;

namespace Scour.Tests;

public class LeftoverScannerTests
{
    private const string FxDir = @"C:\Program Files\Mozilla Firefox";
    private const string FxExe = FxDir + @"\firefox.exe";
    private const string TbDir = @"C:\Program Files\Mozilla Thunderbird";
    private readonly KnownFolders _folders = TestFolders.Create();

    /// <summary>A machine right after Firefox's own uninstaller ran, with Thunderbird still installed.</summary>
    private (InMemoryRegistry Reg, InMemoryFileSystem Fs, ScanTarget Target) FirefoxLeftovers()
    {
        var fs = new InMemoryFileSystem()
            .File(FxDir + @"\distribution\policies.json")
            .File(TbDir + @"\thunderbird.exe")
            .File(@"C:\Users\Alex\AppData\Roaming\Mozilla\Firefox\Profiles\abc.default\prefs.js", 1000)
            .File(@"C:\Users\Alex\AppData\Roaming\Mozilla\Thunderbird\Profiles\x\prefs.js")
            .File(@"C:\Users\Alex\AppData\Local\Mozilla\Firefox\Profiles\abc.default\cache2\entries\1", 5000)
            .File(@"C:\Users\Alex\AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Firefox.lnk", LnkBuilder.Build(FxExe))
            .File(@"C:\Users\Alex\Desktop\Firefox.lnk", LnkBuilder.Build(FxExe))
            .File(@"C:\Users\Alex\Desktop\Thunderbird.lnk", LnkBuilder.Build(TbDir + @"\thunderbird.exe"))
            .File(@"C:\Users\Alex\Desktop\Firefox notes.txt")
            .Dir(@"C:\Users\Alex\AppData\Roaming\Firefly")
            .Dir(@"C:\ProgramData\Microsoft\Windows")
            .Dir(@"C:\Program Files\Common Files");

        var reg = new InMemoryRegistry()
            .Set(@"HKLM\SOFTWARE\Mozilla\Mozilla Firefox\126.0 (x64 en-US)\Main", "PathToExe", FxExe)
            .Set(@"HKLM\SOFTWARE\Mozilla\Mozilla Thunderbird\115.0\Main", "PathToExe", TbDir + @"\thunderbird.exe")
            .Set(@"HKCU\Software\Mozilla\Firefox\Launcher", "x", "1")
            .Key(@"HKCU\Software\Firefly")
            .Key(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion")
            .Set(@"HKLM\SOFTWARE\Clients\StartMenuInternet\Firefox-308046B0AF4A39CB\shell\open\command", "", $"\"{FxExe}\"")
            .Set(@"HKLM\SOFTWARE\Clients\StartMenuInternet\Microsoft Edge\shell\open\command", "", @"""C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe""")
            .Set(@"HKLM\SOFTWARE\RegisteredApplications", "Firefox-308046B0AF4A39CB", @"Software\Clients\StartMenuInternet\Firefox-308046B0AF4A39CB\Capabilities")
            .Set(@"HKLM\SOFTWARE\RegisteredApplications", "Microsoft Edge", @"Software\Clients\StartMenuInternet\Microsoft Edge\Capabilities")
            .Set(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Run", "Mozilla-Firefox-Background", $"\"{FxExe}\" -background")
            .Set(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Run", "OneDrive", @"""C:\Users\Alex\AppData\Local\Microsoft\OneDrive\OneDrive.exe"" /background")
            .Set(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run", "Mozilla-Firefox-Background", RegValue.Binary(new byte[12]))
            .Set(@"HKLM\SOFTWARE\Classes\FirefoxHTML-308046B0AF4A39CB\shell\open\command", "", $"\"{FxExe}\" -osint -url \"%1\"")
            .Set(@"HKLM\SOFTWARE\Classes\.html\OpenWithProgids", "FirefoxHTML-308046B0AF4A39CB", RegValue.String(""))
            .Set(@"HKLM\SOFTWARE\Classes\.html\OpenWithProgids", "MSEdgeHTM", RegValue.String(""))
            .Set(@"HKCU\Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\MuiCache", FxExe + ".FriendlyAppName", "Firefox")
            .Set(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\firefox.exe", "", FxExe)
            .Set(@"HKLM\SYSTEM\CurrentControlSet\Services\MozillaMaintenance", "ImagePath", @"""C:\Program Files (x86)\Mozilla Maintenance Service\maintenanceservice.exe""")
            .Set(@"HKLM\SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\FirewallRules", "{rule1}",
                 $"v2.30|Action=Allow|Active=TRUE|Dir=In|Protocol=17|App={FxExe}|Name=Firefox|");

        var thunderbird = new InstalledProgram
        {
            RegistryKey = RegKeyPath.Parse(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Mozilla Thunderbird"),
            DisplayName = "Mozilla Thunderbird (x64 en-US)",
            Publisher = "Mozilla",
            InstallLocation = TbDir,
        };
        var firefox = new InstalledProgram
        {
            RegistryKey = RegKeyPath.Parse(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Mozilla Firefox 126.0 (x64 en-US)"),
            DisplayName = "Mozilla Firefox (x64 en-US)",
            Publisher = "Mozilla",
            InstallLocation = FxDir,
        };
        var target = ScanTargetBuilder.FromProgram(firefox, [firefox, thunderbird], fs);
        target.ExecutableNames.Add("firefox.exe"); // captured before the uninstaller ran
        return (reg, fs, target);
    }

    private static HashSet<string> Paths(IEnumerable<LeftoverItem> items) =>
        items.Select(i => i.Kind == LeftoverKind.RegistryValue ? $"{i.Path}|{i.ValueName}" : i.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void Moderate_scan_finds_firefox_leftovers_and_nothing_else()
    {
        var (reg, fs, target) = FirefoxLeftovers();
        var items = new LeftoverScanner(reg, fs, _folders).Scan(target, ScanMode.Moderate);
        var paths = Paths(items);

        string[] expected =
        [
            FxDir,
            @"C:\Users\Alex\AppData\Roaming\Mozilla\Firefox",
            @"C:\Users\Alex\AppData\Local\Mozilla",
            @"C:\Users\Alex\AppData\Local\Mozilla\Firefox",
            @"C:\Users\Alex\AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Firefox.lnk",
            @"C:\Users\Alex\Desktop\Firefox.lnk",
            @"HKLM\SOFTWARE\Mozilla\Mozilla Firefox",
            @"HKCU\Software\Mozilla\Firefox",
            @"HKCU\Software\Mozilla", // only Firefox lives here, so the publisher key goes too (Moderate)
            @"HKLM\SOFTWARE\Clients\StartMenuInternet\Firefox-308046B0AF4A39CB",
            @"HKLM\SOFTWARE\RegisteredApplications|Firefox-308046B0AF4A39CB",
            @"HKCU\Software\Microsoft\Windows\CurrentVersion\Run|Mozilla-Firefox-Background",
            @"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run|Mozilla-Firefox-Background",
            @"HKLM\SOFTWARE\Classes\FirefoxHTML-308046B0AF4A39CB",
            @"HKLM\SOFTWARE\Classes\.html\OpenWithProgids|FirefoxHTML-308046B0AF4A39CB",
            @"HKCU\Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\MuiCache|" + FxExe + ".FriendlyAppName",
            @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\firefox.exe",
            @"HKLM\SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\FirewallRules|{rule1}",
        ];
        foreach (var e in expected) Assert.Contains(e, paths);
        Assert.Empty(paths.Except(expected, StringComparer.OrdinalIgnoreCase));
        Assert.Equal(expected.Length, paths.Count);

        // Shared publisher containers stay because Thunderbird still uses them.
        Assert.DoesNotContain(@"C:\Users\Alex\AppData\Roaming\Mozilla", paths);
        Assert.DoesNotContain(@"HKLM\SOFTWARE\Mozilla", paths);

        // Only an emptied-out publisher folder is "Moderate"; direct evidence is "Safe".
        Assert.Equal(Confidence.Moderate, items.Single(i => i.Path == @"C:\Users\Alex\AppData\Local\Mozilla").Confidence);
        Assert.Equal(Confidence.Safe, items.Single(i => i.Path == FxDir).Confidence);
        Assert.True(items.Single(i => i.Path == @"C:\Users\Alex\AppData\Roaming\Mozilla\Firefox").SizeBytes >= 1000);
    }

    [Fact]
    public void Safe_scan_is_a_subset_with_only_safe_items()
    {
        var (reg, fs, target) = FirefoxLeftovers();
        var scanner = new LeftoverScanner(reg, fs, _folders);
        var safe = scanner.Scan(target, ScanMode.Safe);
        var moderate = scanner.Scan(target, ScanMode.Moderate);
        Assert.All(safe, i => Assert.Equal(Confidence.Safe, i.Confidence));
        Assert.Subset(Paths(moderate), Paths(safe));
        Assert.DoesNotContain(@"HKLM\SOFTWARE\Classes\FirefoxHTML-308046B0AF4A39CB", Paths(safe)); // COM/associations are Moderate-mode sweeps
        Assert.Contains(FxDir, Paths(safe));
    }

    [Fact]
    public void Advanced_scan_never_touches_protected_locations()
    {
        var (reg, fs, target) = FirefoxLeftovers();
        var items = new LeftoverScanner(reg, fs, _folders).Scan(target, ScanMode.Advanced);
        var paths = Paths(items);
        Assert.DoesNotContain(TbDir, paths);
        Assert.DoesNotContain(@"HKLM\SOFTWARE\Mozilla\Mozilla Thunderbird", paths);
        Assert.DoesNotContain(@"C:\Users\Alex\Desktop\Thunderbird.lnk", paths);
        Assert.DoesNotContain(@"HKLM\SYSTEM\CurrentControlSet\Services\MozillaMaintenance", paths);
        Assert.DoesNotContain(paths, p => p.StartsWith(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Run", StringComparison.OrdinalIgnoreCase) && p.Contains("OneDrive"));
        Assert.DoesNotContain(paths, p => p.EndsWith("MSEdgeHTM") || p.Contains("Microsoft Edge"));
        Assert.DoesNotContain(paths, p => p.Contains("Firefly"));
        Assert.DoesNotContain(@"C:\Users\Alex\Desktop\Firefox notes.txt", paths);
    }

    [Fact]
    public void Shared_install_folder_is_downgraded_to_risky()
    {
        var fs = new InMemoryFileSystem()
            .File(@"C:\Program Files\Acme Suite\core.dll")
            .File(@"C:\Program Files\Acme Suite\Plugins\Other\o.dll");
        var reg = new InMemoryRegistry();
        var target = new ScanTarget
        {
            DisplayName = "Acme Suite",
            InstallLocation = @"C:\Program Files\Acme Suite",
            ProtectedPaths = [@"C:\Program Files\Acme Suite\Plugins\Other"],
        };
        var scanner = new LeftoverScanner(reg, fs, _folders);
        Assert.Empty(scanner.Scan(target, ScanMode.Moderate));
        var adv = scanner.Scan(target, ScanMode.Advanced);
        Assert.Single(adv);
        Assert.Equal(Confidence.Risky, adv[0].Confidence);
    }

    [Fact]
    public void Bogus_install_locations_and_generic_names_find_nothing()
    {
        var fs = new InMemoryFileSystem()
            .Dir(@"C:\Program Files\Update")
            .Dir(@"C:\Users\Alex\AppData\Roaming\Tools")
            .File(@"C:\Program Files\Common Files\x.dll");
        var reg = new InMemoryRegistry().Key(@"HKCU\Software\Update").Key(@"HKLM\SOFTWARE\Microsoft\Update");
        var target = new ScanTarget { DisplayName = "Update", Publisher = "Microsoft Corporation", InstallLocation = @"C:\Program Files" };
        var items = new LeftoverScanner(reg, fs, _folders).Scan(target, ScanMode.Advanced);
        Assert.Empty(items);
    }

    [Fact]
    public void Microsoft_products_only_get_exact_subkeys_never_the_microsoft_key()
    {
        var reg = new InMemoryRegistry()
            .Set(@"HKCU\Software\Microsoft\Sticky Widget", "a", "1")
            .Set(@"HKCU\Software\Microsoft\Windows\CurrentVersion", "a", "1")
            .Set(@"HKCU\Software\Microsoft\Sticky Widget Pro Helper", "a", "1");
        var target = new ScanTarget { DisplayName = "Microsoft Sticky Widget", Publisher = "Microsoft Corporation" };
        var items = new LeftoverScanner(reg, new InMemoryFileSystem(), _folders).Scan(target, ScanMode.Advanced);
        var item = Assert.Single(items);
        Assert.Equal(@"HKCU\Software\Microsoft\Sticky Widget", item.Path);
        Assert.Equal(Confidence.Moderate, item.Confidence);
    }

    [Fact]
    public void Msi_registration_is_found_by_packed_product_code()
    {
        const string code = "{11111111-2222-3333-4444-555555555555}";
        var packed = MsiGuid.Pack(code);
        var reg = new InMemoryRegistry()
            .Set($@"HKLM\SOFTWARE\Classes\Installer\Products\{packed}", "ProductName", "Old Tool")
            .Set(@"HKLM\SOFTWARE\Classes\Installer\UpgradeCodes\AAAA", packed, "")
            .Set(@"HKLM\SOFTWARE\Classes\Installer\UpgradeCodes\BBBB", packed, "")
            .Set(@"HKLM\SOFTWARE\Classes\Installer\UpgradeCodes\BBBB", "99999999999999999999999999999999", "")
            .Set($@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\UserData\S-1-5-18\Components\C1", packed, @"C:\x\a.dll");
        var target = new ScanTarget { DisplayName = "Old Tool", ProductCode = code };
        var scanner = new LeftoverScanner(reg, new InMemoryFileSystem(), _folders);

        var moderate = Paths(scanner.Scan(target, ScanMode.Moderate));
        Assert.Contains($@"HKLM\SOFTWARE\Classes\Installer\Products\{packed}", moderate);
        Assert.Contains(@"HKLM\SOFTWARE\Classes\Installer\UpgradeCodes\AAAA", moderate);
        Assert.Contains($@"HKLM\SOFTWARE\Classes\Installer\UpgradeCodes\BBBB|{packed}", moderate);
        Assert.DoesNotContain(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\UserData\S-1-5-18\Components\C1", moderate);

        var advanced = Paths(scanner.Scan(target, ScanMode.Advanced));
        Assert.Contains(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\UserData\S-1-5-18\Components\C1", advanced);
    }

    [Fact]
    public void Leftover_uninstall_entry_and_service_are_reported()
    {
        var reg = new InMemoryRegistry()
            .Set(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Acme", "DisplayName", "Acme Sync")
            .Set(@"HKLM\SYSTEM\CurrentControlSet\Services\AcmeSyncSvc", "ImagePath", @"""C:\Program Files\Acme Sync\svc.exe""")
            .Set(@"HKLM\SYSTEM\CurrentControlSet\Services\Spooler", "ImagePath", @"%SystemRoot%\System32\spoolsv.exe");
        var fs = new InMemoryFileSystem().File(@"C:\Program Files\Acme Sync\svc.exe");
        var target = new ScanTarget
        {
            DisplayName = "Acme Sync",
            InstallLocation = @"C:\Program Files\Acme Sync",
            UninstallKey = RegKeyPath.Parse(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Acme"),
        };
        var items = new LeftoverScanner(reg, fs, _folders).Scan(target, ScanMode.Moderate);
        var svc = items.Single(i => i.Path.EndsWith("AcmeSyncSvc"));
        Assert.Equal("AcmeSyncSvc", svc.ServiceName);
        Assert.Contains(items, i => i.Path.EndsWith(@"Uninstall\Acme") && i.Confidence == Confidence.Safe);
        Assert.DoesNotContain(items, i => i.Path.EndsWith("Spooler"));
    }
}
