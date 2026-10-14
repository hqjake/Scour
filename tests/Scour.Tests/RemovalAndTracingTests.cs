using System.Text;
using Scour.Core.Autorun;
using Scour.Core.Junk;
using Scour.Core.Leftovers;
using Scour.Core.Platform;
using Scour.Core.Removal;
using Scour.Core.Tracing;
using Scour.Tests.Fakes;

namespace Scour.Tests;

public class RemovalAndTracingTests
{
    private static readonly DateTime Now = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
    private readonly KnownFolders _folders = TestFolders.Create();

    [Fact]
    public void Reg_file_roundtrips_every_value_kind()
    {
        var src = new InMemoryRegistry()
            .Set(@"HKCU\Software\Acme", "", "default \"quoted\" C:\\path")
            .Set(@"HKCU\Software\Acme", "Count", RegValue.DWord(0xDEADBEEF))
            .Set(@"HKCU\Software\Acme", "Big", RegValue.QWord(0x0102030405060708))
            .Set(@"HKCU\Software\Acme", "Path", RegValue.Expand(@"%ProgramFiles%\Acme"))
            .Set(@"HKCU\Software\Acme", "List", RegValue.Multi("one", "two", "three"))
            .Set(@"HKCU\Software\Acme", "Blob", RegValue.Binary(Enumerable.Range(0, 100).Select(i => (byte)i).ToArray()))
            .Set(@"HKCU\Software\Acme\Sub\Deeper", "x", "y");

        var writer = new RegFileWriter(src);
        writer.AddKeyTree(RegKeyPath.Parse(@"HKCU\Software\Acme"));
        var bytes = writer.ToBytes();
        Assert.Equal(0xFF, bytes[0]);
        var text = RegFileReader.Decode(bytes);
        Assert.StartsWith("Windows Registry Editor Version 5.00", text);
        Assert.Contains("\\\r\n  ", text); // long binary wrapped like regedit does

        var dst = new InMemoryRegistry();
        RegFileReader.Apply(dst, RegFileReader.Parse(text));
        var key = RegKeyPath.Parse(@"HKCU\Software\Acme");
        foreach (var name in src.GetValueNames(key))
            Assert.Equal(src.GetValue(key, name)!.Fingerprint(), dst.GetValue(key, name)!.Fingerprint());
        Assert.Equal("y", dst.GetString(RegKeyPath.Parse(@"HKCU\Software\Acme\Sub\Deeper"), "x"));
    }

    [Fact]
    public void Remover_backs_up_registry_deletes_everything_and_restores()
    {
        var reg = new InMemoryRegistry()
            .Set(@"HKCU\Software\Acme\Settings", "Theme", "dark")
            .Set(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Run", "Acme", @"C:\Program Files\Acme\acme.exe")
            .Set(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Run", "Other", @"C:\other.exe");
        var fs = new InMemoryFileSystem()
            .File(@"C:\Program Files\Acme\data.bin", 500)
            .File(@"C:\Users\Alex\Desktop\Acme.lnk");
        var backups = new BackupStore(fs, @"C:\ProgramData\Scour\Backups");
        var items = new List<LeftoverItem>
        {
            LeftoverItem.ForKey(RegKeyPath.Parse(@"HKCU\Software\Acme"), Confidence.Safe, "t"),
            LeftoverItem.ForValue(RegKeyPath.Parse(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Run"), "Acme", null, Confidence.Safe, "t"),
            new() { Kind = LeftoverKind.Folder, Path = @"C:\Program Files\Acme", SizeBytes = 500, Confidence = Confidence.Safe },
            LeftoverItem.ForFile(@"C:\Users\Alex\Desktop\Acme.lnk", Confidence.Safe, "t"),
        };

        var report = new LeftoverRemover(reg, fs, backups).Remove("Acme", items, new RemovalOptions(), Now);
        Assert.Equal(4, report.Removed.Count);
        Assert.Empty(report.Failed);
        Assert.Equal(500, report.BytesFreed);
        Assert.False(reg.Has(@"HKCU\Software\Acme"));
        Assert.Null(reg.GetValue(RegKeyPath.Parse(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Run"), "Acme"));
        Assert.NotNull(reg.GetValue(RegKeyPath.Parse(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Run"), "Other"));
        Assert.False(fs.DirectoryExists(@"C:\Program Files\Acme"));
        Assert.Contains(@"C:\Program Files\Acme", fs.Recycled);

        var listed = Assert.Single(backups.List());
        Assert.Equal("Acme", listed.ProgramName);
        Assert.Equal(4, listed.Items.Count);
        backups.RestoreRegistry(listed, reg);
        Assert.Equal("dark", reg.GetString(RegKeyPath.Parse(@"HKCU\Software\Acme\Settings"), "Theme"));
        Assert.Equal(@"C:\Program Files\Acme\acme.exe", reg.GetString(RegKeyPath.Parse(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Run"), "Acme"));
    }

    [Fact]
    public void Remover_reports_locked_files_and_schedules_them_for_reboot()
    {
        var fs = new InMemoryFileSystem().File(@"C:\Program Files\Acme\a.dll").File(@"C:\Program Files\Acme\sub\b.dll");
        fs.Locked.Add(@"C:\Program Files\Acme\a.dll");
        var remover = new LeftoverRemover(new InMemoryRegistry(), fs, null);
        var report = remover.Remove("Acme", [LeftoverItem.ForFolder(@"C:\Program Files\Acme", Confidence.Safe, "t")],
                                    new RemovalOptions { FilesToRecycleBin = false }, Now);
        var failure = Assert.Single(report.Failed);
        Assert.Contains("in use", failure.Error);
        Assert.Equal(1, remover.ScheduleOnReboot(report.Failed));
        Assert.Contains(@"C:\Program Files\Acme\a.dll", fs.ScheduledOnReboot);
        Assert.Equal(@"C:\Program Files\Acme", fs.ScheduledOnReboot[^1]); // root folder last
    }

    [Fact]
    public void Snapshot_diff_reports_only_new_items_collapsed_to_top_level()
    {
        var reg = new InMemoryRegistry()
            .Set(@"HKLM\SOFTWARE\Existing", "a", "1")
            .Key(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
        var fs = new InMemoryFileSystem().File(@"C:\Program Files\Existing\e.exe").Dir(_folders.RoamingAppData);
        var taker = new SnapshotTaker(reg, fs, _folders);
        var before = taker.Take();

        reg.Set(@"HKLM\SOFTWARE\Acme\Widget\Settings", "x", "1")
           .Set(@"HKLM\SOFTWARE\Existing", "b", "2")
           .Set(@"HKLM\SOFTWARE\Existing", "a", "changed")
           .Set(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Acme Widget", "DisplayName", "Acme Widget")
           .Set(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Run", "AcmeTray", @"C:\Program Files\Acme Widget\tray.exe");
        fs.File(@"C:\Program Files\Acme Widget\widget.exe")
          .File(@"C:\Program Files\Acme Widget\lib\core.dll")
          .File(@"C:\Program Files\Existing\plugin-acme.dll")
          .File(@"C:\Users\Alex\AppData\Roaming\Acme\Cache\junk.tmp");
        var after = taker.Take();

        var diff = SnapshotDiff.Compare(before, after);
        var paths = diff.Items.Select(i => i.Kind == LeftoverKind.RegistryValue ? $"{i.Path}|{i.ValueName}" : i.Path).ToList();
        Assert.Contains(@"HKLM\SOFTWARE\Acme", paths);
        Assert.DoesNotContain(@"HKLM\SOFTWARE\Acme\Widget", paths);
        Assert.Contains(@"HKLM\SOFTWARE\Existing|b", paths);
        Assert.Contains(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Acme Widget", paths);
        Assert.Contains(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Run|AcmeTray", paths);
        Assert.Contains(@"C:\Program Files\Acme Widget", paths);
        Assert.DoesNotContain(@"C:\Program Files\Acme Widget\lib", paths);
        Assert.Contains(@"C:\Program Files\Existing\plugin-acme.dll", paths);
        Assert.Contains(@"C:\Users\Alex\AppData\Roaming\Acme", paths);
        Assert.Equal(1, diff.ModifiedValueCount);
        Assert.Equal("Acme Widget", Assert.Single(diff.NewUninstallKeys).Name);
    }

    [Fact]
    public void Trace_store_roundtrips()
    {
        var fs = new InMemoryFileSystem();
        var store = new TraceStore(fs, @"C:\ProgramData\Scour\Traces");
        store.Save(new TraceLog
        {
            Id = "t1", Name = "Acme", CreatedUtc = Now,
            UninstallKey = RegKeyPath.Parse(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Acme"),
            Items = [LeftoverItem.ForFolder(@"C:\Program Files\Acme", Confidence.Safe, "Folder created during installation")],
        });
        var log = Assert.Single(store.List());
        Assert.Equal("Acme", log.Name);
        Assert.Equal(RegHive.LocalMachine, log.UninstallKey!.Value.Hive);
        Assert.Equal(@"C:\Program Files\Acme", log.Items[0].Path);
        store.Delete(log);
        Assert.Empty(store.List());
    }

    [Fact]
    public void Autorun_lists_toggles_and_deletes_entries()
    {
        var reg = new InMemoryRegistry()
            .Set(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Run", "Acme", @"""C:\Program Files\Acme\acme.exe"" /tray")
            .Set(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Run", "Gone", @"C:\Gone\gone.exe")
            .Set(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run", "Gone", RegValue.Binary([3, 0, 0, 0, 1, 2, 3, 4, 5, 6, 7, 8]));
        var fs = new InMemoryFileSystem()
            .File(@"C:\Program Files\Acme\acme.exe")
            .File(_folders.UserStartup + @"\Notes.lnk", LnkBuilder.Build(@"C:\Program Files\Notes\notes.exe"))
            .File(_folders.UserStartup + @"\desktop.ini");
        var mgr = new AutorunManager(reg, fs, _folders);

        var entries = mgr.GetEntries();
        Assert.Equal(["Acme", "Gone", "Notes"], entries.Select(e => e.Name));
        var acme = entries[0];
        Assert.True(acme.Enabled);
        Assert.True(acme.TargetExists);
        Assert.False(entries[1].Enabled);
        Assert.False(entries[1].TargetExists);
        Assert.Equal(@"C:\Program Files\Notes\notes.exe", entries[2].TargetPath);

        mgr.SetEnabled(acme, false, Now);
        Assert.False(mgr.GetEntries()[0].Enabled);
        mgr.SetEnabled(acme, true, Now);
        Assert.True(mgr.GetEntries()[0].Enabled);

        mgr.Delete(entries[1]);
        mgr.Delete(entries[2]);
        Assert.Equal(["Acme"], mgr.GetEntries().Select(e => e.Name));
    }

    [Fact]
    public void Junk_cleaner_expands_wildcards_and_respects_age()
    {
        var fs = new InMemoryFileSystem()
            .File(_folders.UserTemp + @"\old.tmp", 100, Now.AddDays(-3))
            .File(_folders.UserTemp + @"\sub\old2.tmp", 50, Now.AddDays(-3))
            .File(_folders.UserTemp + @"\fresh.tmp", 999, Now.AddMinutes(-5))
            .File(_folders.LocalAppData + @"\Google\Chrome\User Data\Default\Cache\Cache_Data\f_0001", 300)
            .File(_folders.LocalAppData + @"\Google\Chrome\User Data\Profile 1\Cache\Cache_Data\f_0002", 200)
            .File(_folders.LocalAppData + @"\Google\Chrome\User Data\Default\Bookmarks", 10);
        var cleaner = new JunkCleaner(fs, _folders);

        var temp = cleaner.Scan(cleaner.Categories.Single(c => c.Id == "user-temp"), Now);
        Assert.Equal(150, temp.TotalBytes);
        Assert.Equal(2, temp.Files.Count);

        var chrome = cleaner.Scan(cleaner.Categories.Single(c => c.Id == "chrome"), Now);
        Assert.Equal(500, chrome.TotalBytes);

        var result = cleaner.Clean(temp);
        Assert.Equal(2, result.FilesDeleted);
        Assert.True(fs.FileExists(_folders.UserTemp + @"\fresh.tmp"));
        Assert.False(fs.DirectoryExists(_folders.UserTemp + @"\sub"));
        Assert.True(fs.DirectoryExists(_folders.UserTemp));
    }
}
