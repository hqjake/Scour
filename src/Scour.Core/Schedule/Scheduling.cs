using Scour.Core.Junk;

namespace Scour.Core.Schedule;

public enum CleaningSchedule
{
    Off,
    Daily,
    Weekly,
}

/// <summary>Builds schtasks.exe command lines for Scour's background jobs.</summary>
public static class SchtasksCommands
{
    public const string Folder = "Scour";
    public const string CleanTask = Folder + @"\Automatic cleaning";
    public const string TrayTask = Folder + @"\Start in tray";

    public static string CreateCleaning(string exePath, CleaningSchedule schedule, DayOfWeek day, int hour)
    {
        if (schedule == CleaningSchedule.Off) throw new ArgumentException("Use Delete for Off.");
        if (hour is < 0 or > 23) throw new ArgumentOutOfRangeException(nameof(hour));
        var when = schedule == CleaningSchedule.Daily ? "/SC DAILY" : $"/SC WEEKLY /D {DayCode(day)}";
        return $"/Create /F /TN \"{CleanTask}\" /TR \"{Quote(exePath)} --autoclean\" {when} /ST {hour:00}:00 /RL HIGHEST";
    }

    /// <summary>Starts Scour in the tray at sign-in, elevated without a UAC prompt.</summary>
    public static string CreateTrayAtLogon(string exePath) =>
        $"/Create /F /TN \"{TrayTask}\" /TR \"{Quote(exePath)} --tray\" /SC ONLOGON /DELAY 0000:20 /RL HIGHEST";

    public static string Delete(string taskName) => $"/Delete /F /TN \"{taskName}\"";

    /// <summary>Inside /TR the program path is quoted with backslash-escaped quotes.</summary>
    private static string Quote(string path) => $"\\\"{path}\\\"";

    private static string DayCode(DayOfWeek d) => d switch
    {
        DayOfWeek.Monday => "MON",
        DayOfWeek.Tuesday => "TUE",
        DayOfWeek.Wednesday => "WED",
        DayOfWeek.Thursday => "THU",
        DayOfWeek.Friday => "FRI",
        DayOfWeek.Saturday => "SAT",
        _ => "SUN",
    };
}

public sealed record AutoCleanResult(long BytesFreed, int FilesDeleted, int FilesSkipped, IReadOnlyList<string> Categories);

/// <summary>The unattended clean run by the scheduled task and the tray's "Quick clean".</summary>
public static class AutoCleaner
{
    /// <summary>Only the low-risk categories that are on by default; nothing that needs review.</summary>
    public static IEnumerable<JunkCategory> DefaultCategories(JunkCleaner cleaner) => cleaner.Categories.Where(c => c.EnabledByDefault);

    public static AutoCleanResult Run(JunkCleaner cleaner, IEnumerable<JunkCategory> categories, DateTime nowUtc, CancellationToken ct = default)
    {
        long freed = 0;
        int deleted = 0, skipped = 0;
        var names = new List<string>();
        foreach (var c in categories)
        {
            ct.ThrowIfCancellationRequested();
            var scan = cleaner.Scan(c, nowUtc, ct);
            if (scan.Files.Count == 0) continue;
            var r = cleaner.Clean(scan, ct);
            freed += r.BytesFreed;
            deleted += r.FilesDeleted;
            skipped += r.FilesSkipped;
            if (r.FilesDeleted > 0) names.Add(c.Name);
        }
        return new AutoCleanResult(freed, deleted, skipped, names);
    }
}
