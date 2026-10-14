using Scour.Core.Platform;
using Scour.Core.Programs;
using Scour.Tests.Fakes;

namespace Scour.Tests;

public class InventoryTests
{
    private const string Uninstall = @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    private const string Uninstall32 = @"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall";

    [Fact]
    public void Reads_programs_and_filters_hidden_entries()
    {
        var reg = new InMemoryRegistry()
            .Set($@"{Uninstall}\7-Zip", "DisplayName", "7-Zip 23.01 (x64)")
            .Set($@"{Uninstall}\7-Zip", "Publisher", "Igor Pavlov")
            .Set($@"{Uninstall}\7-Zip", "InstallLocation", @"C:\Program Files\7-Zip\")
            .Set($@"{Uninstall}\7-Zip", "UninstallString", @"""C:\Program Files\7-Zip\Uninstall.exe""")
            .Set($@"{Uninstall}\7-Zip", "EstimatedSize", RegValue.DWord(5_000))
            .Set($@"{Uninstall}\7-Zip", "InstallDate", "20240115")
            .Set($@"{Uninstall}\Hidden", "DisplayName", "Some driver")
            .Set($@"{Uninstall}\Hidden", "SystemComponent", RegValue.DWord(1))
            .Set($@"{Uninstall}\KB123", "DisplayName", "Update for Thing (KB1234567)")
            .Set($@"{Uninstall}\KB123", "ParentKeyName", "Thing")
            .Set($@"{Uninstall}\NoName", "UninstallString", "x.exe")
            .Set($@"{Uninstall32}\{{11111111-2222-3333-4444-555555555555}}", "DisplayName", "Old Tool")
            .Set($@"{Uninstall32}\{{11111111-2222-3333-4444-555555555555}}", "UninstallString", "MsiExec.exe /I{11111111-2222-3333-4444-555555555555}")
            .Set($@"{Uninstall32}\{{11111111-2222-3333-4444-555555555555}}", "WindowsInstaller", RegValue.DWord(1))
            .Set(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\Zoom", "DisplayName", "Zoom Workplace")
            .Set(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\Zoom", "DisplayIcon", @"C:\Users\Alex\AppData\Roaming\Zoom\bin\Zoom.exe,0");

        var inv = new ProgramInventory(reg, TestFolders.Create());
        var programs = inv.GetPrograms();
        Assert.Equal(["7-Zip 23.01 (x64)", "Old Tool", "Zoom Workplace"], programs.Select(p => p.DisplayName));

        var zip = programs[0];
        Assert.Equal(@"C:\Program Files\7-Zip", zip.InstallLocation);
        Assert.False(zip.InstallLocationInferred);
        Assert.Equal(5_000 * 1024L, zip.EstimatedSizeBytes);
        Assert.Equal(new DateTime(2024, 1, 15), zip.InstallDate);
        Assert.False(zip.IsMsi);

        var old = programs[1];
        Assert.True(old.IsMsi);
        Assert.True(old.Is32BitEntry);
        Assert.Equal("{11111111-2222-3333-4444-555555555555}", old.ProductCode);

        var zoom = programs[2];
        Assert.Equal(InstallScope.User, zoom.Scope);
        Assert.Equal(@"C:\Users\Alex\AppData\Roaming\Zoom", zoom.InstallLocation); // "bin" climbed
        Assert.True(zoom.InstallLocationInferred);

        var all = inv.GetPrograms(new InventoryOptions { IncludeSystemComponents = true, IncludeUpdates = true });
        Assert.Equal(5, all.Count);
    }

    [Fact]
    public void Does_not_infer_install_dir_from_system_or_cache_folders()
    {
        var reg = new InMemoryRegistry()
            .Set($@"{Uninstall}\A", "DisplayName", "A")
            .Set($@"{Uninstall}\A", "UninstallString", @"C:\Windows\System32\rundll32.exe foo.dll,Uninstall")
            .Set($@"{Uninstall}\B", "DisplayName", "B")
            .Set($@"{Uninstall}\B", "UninstallString", @"""C:\ProgramData\Package Cache\{x}\setup.exe"" /uninstall")
            .Set($@"{Uninstall}\C", "DisplayName", "C")
            .Set($@"{Uninstall}\C", "InstallLocation", @"C:\Program Files");
        var programs = new ProgramInventory(reg, TestFolders.Create()).GetPrograms();
        Assert.All(programs, p => Assert.Null(p.InstallLocation));
    }

    [Fact]
    public void Builds_uninstall_commands()
    {
        var msi = new InstalledProgram
        {
            RegistryKey = RegKeyPath.Parse($@"{Uninstall}\{{11111111-2222-3333-4444-555555555555}}"),
            DisplayName = "Old Tool",
            UninstallString = "MsiExec.exe /I{11111111-2222-3333-4444-555555555555}",
            ProductCode = "{11111111-2222-3333-4444-555555555555}",
            IsMsi = true,
        };
        Assert.Equal("/x {11111111-2222-3333-4444-555555555555}", UninstallCommandBuilder.Build(msi, false)!.Arguments);
        Assert.Contains("/qb", UninstallCommandBuilder.Build(msi, true)!.Arguments);

        var inno = new InstalledProgram
        {
            RegistryKey = RegKeyPath.Parse($@"{Uninstall}\App_is1"),
            DisplayName = "App",
            UninstallString = @"""C:\Program Files\App\unins000.exe""",
        };
        var quiet = UninstallCommandBuilder.Build(inno, true)!;
        Assert.True(quiet.IsQuiet);
        Assert.Equal(@"C:\Program Files\App\unins000.exe", quiet.Executable);
        Assert.Contains("/VERYSILENT", quiet.Arguments);

        var nsis = new InstalledProgram
        {
            RegistryKey = RegKeyPath.Parse($@"{Uninstall}\N"),
            DisplayName = "N",
            UninstallString = @"""C:\Program Files\N\uninstall.exe""",
            QuietUninstallString = @"""C:\Program Files\N\uninstall.exe"" /S",
        };
        Assert.Equal("/S", UninstallCommandBuilder.Build(nsis, true)!.Arguments);
        Assert.False(UninstallCommandBuilder.Build(nsis, false)!.IsQuiet);

        var none = new InstalledProgram { RegistryKey = RegKeyPath.Parse($@"{Uninstall}\X"), DisplayName = "X" };
        Assert.Null(UninstallCommandBuilder.Build(none, false));
    }
}
