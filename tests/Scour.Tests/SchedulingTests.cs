using Scour.Core.Junk;
using Scour.Core.Schedule;
using Scour.Tests.Fakes;

namespace Scour.Tests;

public class SchedulingTests
{
    [Fact]
    public void Builds_schtasks_command_lines()
    {
        const string exe = @"C:\Program Files\Scour\Scour.exe";
        Assert.Equal(@"/Create /F /TN ""Scour\Automatic cleaning"" /TR ""\""C:\Program Files\Scour\Scour.exe\"" --autoclean"" /SC WEEKLY /D SUN /ST 09:00 /RL HIGHEST",
                     SchtasksCommands.CreateCleaning(exe, CleaningSchedule.Weekly, DayOfWeek.Sunday, 9));
        Assert.Contains("/SC DAILY /ST 23:00", SchtasksCommands.CreateCleaning(exe, CleaningSchedule.Daily, DayOfWeek.Monday, 23));
        Assert.Contains("/SC ONLOGON", SchtasksCommands.CreateTrayAtLogon(exe));
        Assert.Contains("--tray", SchtasksCommands.CreateTrayAtLogon(exe));
        Assert.Equal(@"/Delete /F /TN ""Scour\Start in tray""", SchtasksCommands.Delete(SchtasksCommands.TrayTask));
        Assert.Throws<ArgumentException>(() => SchtasksCommands.CreateCleaning(exe, CleaningSchedule.Off, DayOfWeek.Monday, 1));
    }

    [Fact]
    public void Auto_clean_uses_only_default_categories_and_respects_age()
    {
        var f = TestFolders.Create();
        var now = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
        var fs = new InMemoryFileSystem()
            .File(f.UserTemp + @"\old.tmp", 1000, now.AddDays(-5))
            .File(f.UserTemp + @"\new.tmp", 1000, now.AddMinutes(-1))
            .File(f.LocalAppData + @"\npm-cache\_cacache\x", 5000, now.AddDays(-5)); // developer cache: off by default
        var cleaner = new JunkCleaner(fs, f);
        var r = AutoCleaner.Run(cleaner, AutoCleaner.DefaultCategories(cleaner), now);
        Assert.Equal(1000, r.BytesFreed);
        Assert.Equal(1, r.FilesDeleted);
        Assert.True(fs.FileExists(f.UserTemp + @"\new.tmp"));
        Assert.True(fs.FileExists(f.LocalAppData + @"\npm-cache\_cacache\x"));
        Assert.Equal(["User temporary files"], r.Categories);
    }
}
