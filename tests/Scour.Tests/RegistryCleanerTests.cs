using Scour.Core.Platform;
using Scour.Core.Programs;
using Scour.Core.Registry;
using Scour.Tests.Fakes;

namespace Scour.Tests;

public class RegistryCleanerTests
{
    private const string Uninstall = @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    private static (InMemoryRegistry Reg, InMemoryFileSystem Fs) Machine()
    {
        var fs = new InMemoryFileSystem()
            .Dir(@"C:\")
            .File(@"C:\Program Files\Alive\alive.exe")
            .File(@"C:\Program Files\Alive\unins000.exe")
            .File(@"C:\Users\Alex\AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Alive.lnk", LnkBuilder.Build(@"C:\Program Files\Alive\alive.exe"))
            .File(@"C:\Users\Alex\AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Gone\Gone.lnk", LnkBuilder.Build(@"C:\Program Files\Gone\gone.exe"))
            .File(@"C:\Users\Alex\Desktop\Usb.lnk", LnkBuilder.Build(@"E:\Tools\tool.exe"));
        var reg = new InMemoryRegistry()
            // Uninstall entries
            .Set($@"{Uninstall}\Alive_is1", "DisplayName", "Alive")
            .Set($@"{Uninstall}\Alive_is1", "UninstallString", @"""C:\Program Files\Alive\unins000.exe""")
            .Set($@"{Uninstall}\Gone_is1", "DisplayName", "Gone")
            .Set($@"{Uninstall}\Gone_is1", "UninstallString", @"""C:\Program Files\Gone\unins000.exe""")
            .Set($@"{Uninstall}\Driver", "DisplayName", "Some driver")
            .Set($@"{Uninstall}\Driver", "UninstallString", @"C:\Windows\System32\rundll32.exe C:\Windows\System32\x.dll,Uninstall")
            .Set($@"{Uninstall}\{{11111111-2222-3333-4444-555555555555}}", "DisplayName", "Orphan MSI")
            .Set($@"{Uninstall}\{{11111111-2222-3333-4444-555555555555}}", "WindowsInstaller", RegValue.DWord(1))
            .Set($@"{Uninstall}\{{22222222-2222-3333-4444-555555555555}}", "DisplayName", "Good MSI")
            .Set($@"{Uninstall}\{{22222222-2222-3333-4444-555555555555}}", "WindowsInstaller", RegValue.DWord(1))
            .Key($@"HKLM\SOFTWARE\Classes\Installer\Products\{MsiGuid.Pack("{22222222-2222-3333-4444-555555555555}")}")
            // App Paths
            .Set(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\alive.exe", "", @"C:\Program Files\Alive\alive.exe")
            .Set(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\gone.exe", "", @"C:\Program Files\Gone\gone.exe")
            .Set(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\wordpad.exe", "", @"%ProgramFiles%\Windows NT\Accessories\WORDPAD.EXE")
            // Startup
            .Set(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Run", "Gone", @"""C:\Program Files\Gone\gone.exe"" /tray")
            .Set(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Run", "Alive", @"""C:\Program Files\Alive\alive.exe""")
            .Set(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Run", "UsbTool", @"E:\Tools\tool.exe")
            .Set(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Run", "Bare", @"tool.exe --x")
            .Set(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run", "Gone", RegValue.Binary(new byte[12]))
            // Associations
            .Set(@"HKLM\SOFTWARE\Classes\GoneFile\shell\open\command", "", @"""C:\Program Files\Gone\gone.exe"" ""%1""")
            .Set(@"HKLM\SOFTWARE\Classes\AliveFile\shell\open\command", "", @"""C:\Program Files\Alive\alive.exe"" ""%1""")
            .Set(@"HKLM\SOFTWARE\Classes\.gone\OpenWithProgids", "GoneFile", "")
            .Set(@"HKLM\SOFTWARE\Classes\.gone\OpenWithProgids", "NeverExisted", "")
            .Set(@"HKLM\SOFTWARE\Classes\.gone\OpenWithProgids", "AliveFile", "")
            .Set(@"HKLM\SOFTWARE\Classes\.gone\OpenWithProgids", "AppX123abc", "")
            .Set(@"HKCU\Software\Classes\Applications\gone.exe\shell\open\command", "", @"C:\Program Files\Gone\gone.exe ""%1""")
            // Caches
            .Set(@"HKCU\Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\MuiCache", @"C:\Program Files\Gone\gone.exe.FriendlyAppName", "Gone")
            .Set(@"HKCU\Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\MuiCache", @"C:\Program Files\Alive\alive.exe.FriendlyAppName", "Alive")
            .Set(@"HKCU\Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Compatibility Assistant\Store", @"C:\Program Files\Gone\setup.exe", RegValue.Binary([1]))
            .Set(@"HKLM\SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\FirewallRules", "{r1}",
                 @"v2.30|Action=Allow|Dir=In|App=C:\Program Files\Gone\gone.exe|Name=Gone server|")
            .Set(@"HKLM\SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\FirewallRules", "{r2}",
                 @"v2.30|Action=Allow|Dir=In|App=System|Name=File sharing|")
            .Set(@"HKLM\SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\FirewallRules", "{r3}",
                 @"v2.30|Action=Allow|Dir=In|App=%SystemRoot%\system32\svchost.exe|Name=Core|");
        return (reg, fs);
    }

    [Fact]
    public void Finds_only_dead_references()
    {
        var (reg, fs) = Machine();
        var all = RegistryCleaner.Categories.Select(c => c.Category).ToHashSet();
        var issues = new RegistryCleaner(reg, fs, TestFolders.Create()).Scan(all);
        var found = issues.Select(i => i.Item.ValueName is null ? i.Item.Path : $"{i.Item.Path}|{i.Item.ValueName}").ToList();

        string[] expected =
        [
            $@"{Uninstall}\Gone_is1",
            $@"{Uninstall}\{{11111111-2222-3333-4444-555555555555}}",
            @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\gone.exe",
            @"HKCU\Software\Microsoft\Windows\CurrentVersion\Run|Gone",
            @"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run|Gone",
            @"HKLM\SOFTWARE\Classes\GoneFile",
            @"HKLM\SOFTWARE\Classes\.gone\OpenWithProgids|GoneFile",
            @"HKLM\SOFTWARE\Classes\.gone\OpenWithProgids|NeverExisted",
            @"HKCU\Software\Classes\Applications\gone.exe",
            @"HKCU\Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\MuiCache|C:\Program Files\Gone\gone.exe.FriendlyAppName",
            @"HKCU\Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Compatibility Assistant\Store|C:\Program Files\Gone\setup.exe",
            @"HKLM\SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\FirewallRules|{r1}",
            @"C:\Users\Alex\AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Gone\Gone.lnk",
        ];
        Assert.Empty(expected.Except(found));
        Assert.Empty(found.Except(expected));
    }

    [Fact]
    public void Respects_selected_categories()
    {
        var (reg, fs) = Machine();
        var issues = new RegistryCleaner(reg, fs, TestFolders.Create()).Scan(new HashSet<RegistryIssueCategory> { RegistryIssueCategory.BrokenAppPaths });
        Assert.All(issues, i => Assert.Equal(RegistryIssueCategory.BrokenAppPaths, i.Category));
        Assert.Equal(["gone.exe"], issues.Select(i => i.Item.RegistryKey!.Value.Name));
    }

    [Theory]
    [InlineData(@"""C:\Program Files\Gone\gone.exe"" /x", true)]
    [InlineData(@"C:\Program Files\Gone\gone.exe /x", true)]
    [InlineData(@"rundll32.exe ""C:\Program Files\Gone\g.dll"",Run", true)]
    [InlineData(@"C:\Program Files\Alive\alive.exe", false)]
    [InlineData(@"E:\NotConnected\x.exe", false)]
    [InlineData(@"C:\Windows\System32\missing.exe", false)]
    [InlineData(@"%UNKNOWNVAR%\x.exe", false)]
    [InlineData(@"\\server\share\x.exe", false)]
    [InlineData(@"notepad.exe", false)]
    [InlineData("", false)]
    public void Missing_detection_is_conservative(string command, bool missing)
    {
        var (reg, fs) = Machine();
        Assert.Equal(missing, new RegistryCleaner(reg, fs, TestFolders.Create()).IsDefinitelyMissing(command));
    }
}
