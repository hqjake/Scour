namespace Scour.Core.Health;

public enum HealthFix
{
    CleanJunk,
    FreeSpace,
    Startup,
    Registry,
    Restart,
    Antivirus,
    Updates,
    DriveHealth,
    Battery,
}

public enum Severity
{
    Info,
    Warning,
    Critical,
}

public sealed record DriveHealth(string Name, bool? Healthy, bool Warning, int? WearPercent, int? TemperatureC);

public sealed record AntivirusStatus(string Name, bool Enabled, bool UpToDate);

public sealed class HealthInputs
{
    public double SystemDriveFreePercent { get; init; } = 100;
    public long SystemDriveFreeBytes { get; init; } = long.MaxValue;
    public long? JunkBytes { get; init; }
    public int? EnabledStartupItems { get; init; }
    public int? RegistryProblems { get; init; }
    public IReadOnlyList<DriveHealth> Drives { get; init; } = [];
    public IReadOnlyList<AntivirusStatus>? Antivirus { get; init; }
    public TimeSpan Uptime { get; init; }
    public DateTime? LastCleanupUtc { get; init; }
    public int? UpdatesAvailable { get; init; }
    public BatteryHealth? Battery { get; init; }
    public DateTime NowUtc { get; init; } = DateTime.UtcNow;
}

public sealed record HealthIssue(string Title, string Detail, int Penalty, HealthFix Fix, Severity Severity, string FixLabel);

public sealed record HealthReport(int Score, string Grade, IReadOnlyList<HealthIssue> Issues);

/// <summary>Turns a handful of checks into a 0–100 score with a prioritised to-do list.</summary>
public static class HealthScore
{
    private const long GB = 1L << 30;
    private const long MB = 1L << 20;

    public static HealthReport Evaluate(HealthInputs i)
    {
        var issues = new List<HealthIssue>();
        void Add(string title, string detail, int penalty, HealthFix fix, Severity severity, string fixLabel) =>
            issues.Add(new HealthIssue(title, detail, penalty, fix, severity, fixLabel));

        // Disk space
        int spacePenalty = i.SystemDriveFreePercent switch { < 5 => 30, < 10 => 20, < 15 => 10, _ => 0 };
        if (i.SystemDriveFreeBytes < 10 * GB) spacePenalty = Math.Max(spacePenalty, 15);
        if (spacePenalty > 0)
            Add("System drive is almost full", $"Only {Size(i.SystemDriveFreeBytes)} ({i.SystemDriveFreePercent:0}%) free. Windows slows down and updates fail when space runs out.",
                spacePenalty, HealthFix.FreeSpace, spacePenalty >= 20 ? Severity.Critical : Severity.Warning, "Find what's using space");

        // Junk
        if (i.JunkBytes is { } junk)
        {
            int p = junk switch { > 10 * GB => 15, > 2 * GB => 10, > 500 * MB => 5, _ => 0 };
            if (p > 0) Add("Junk files are piling up", $"{Size(junk)} of temporary files and caches can be removed.", p, HealthFix.CleanJunk, Severity.Warning, "Clean junk");
        }

        // Startup
        if (i.EnabledStartupItems is { } startup)
        {
            int p = startup switch { > 15 => 10, > 8 => 5, _ => 0 };
            if (p > 0) Add("Many programs start with Windows", $"{startup} programs launch at sign-in, slowing startup.", p, HealthFix.Startup, Severity.Info, "Review startup");
        }

        // Battery: worth knowing, but only a worn-out one costs points (it's not something cleaning fixes).
        if (i.Battery is { HealthPercent: < 60 } battery)
            Add($"Battery holds {battery.HealthPercent}% of its original charge", "It runs out much sooner than when new. A replacement battery restores the running time.",
                battery.HealthPercent < 40 ? 8 : 4, HealthFix.Battery, battery.HealthPercent < 40 ? Severity.Warning : Severity.Info, "See battery details");

        // Registry
        if (i.RegistryProblems is { } reg)
        {
            int p = reg switch { > 50 => 8, > 10 => 4, > 0 => 2, _ => 0 };
            if (p > 0) Add("Broken registry entries", $"{reg} entries point at programs or files that no longer exist.", p, HealthFix.Registry, Severity.Info, "Fix registry");
        }

        // Drives
        foreach (var d in i.Drives)
        {
            if (d.Healthy == false)
                Add($"Drive \"{d.Name}\" reports a failure", "Back up your files now and plan to replace this drive.", 30, HealthFix.DriveHealth, Severity.Critical, "See drive details");
            else if (d.Warning)
                Add($"Drive \"{d.Name}\" reports a warning", "The drive's self-check found a problem. Back up important files.", 15, HealthFix.DriveHealth, Severity.Warning, "See drive details");
            if (d.WearPercent is { } wear && wear >= 70)
                Add($"Drive \"{d.Name}\" is {wear}% worn", "SSDs wear out with writes. Consider replacing it before it reaches 100%.", wear >= 90 ? 20 : 8,
                    HealthFix.DriveHealth, wear >= 90 ? Severity.Critical : Severity.Warning, "See drive details");
            if (d.TemperatureC is { } t && t >= 70)
                Add($"Drive \"{d.Name}\" is running hot ({t} °C)", "Check airflow; heat shortens drive life and causes throttling.", 8, HealthFix.DriveHealth, Severity.Warning, "See drive details");
        }

        // Antivirus
        if (i.Antivirus is { } av)
        {
            if (!av.Any(a => a.Enabled))
                Add("No active antivirus", "Real-time protection is off. Turn on Windows Security or your antivirus.", 25, HealthFix.Antivirus, Severity.Critical, "Open Windows Security");
            else if (av.Where(a => a.Enabled).All(a => !a.UpToDate))
                Add("Antivirus definitions are out of date", "Update your antivirus so it recognises new threats.", 5, HealthFix.Antivirus, Severity.Warning, "Open Windows Security");
        }

        // Uptime
        int uptimePenalty = i.Uptime.TotalDays switch { > 14 => 6, > 7 => 3, _ => 0 };
        if (uptimePenalty > 0)
            Add("PC hasn't restarted in a while", $"Up for {(int)i.Uptime.TotalDays} days. Restarting applies updates and clears leaked memory.", uptimePenalty, HealthFix.Restart, Severity.Info, "Restart");

        // Cleanup cadence
        if (i.LastCleanupUtc is null || i.NowUtc - i.LastCleanupUtc.Value > TimeSpan.FromDays(30))
            Add("No cleanup in the last month", "Schedule automatic cleaning in Settings so junk never builds up.", 4, HealthFix.CleanJunk, Severity.Info, "Clean now");

        // App updates
        if (i.UpdatesAvailable is { } updates)
        {
            int p = updates switch { > 10 => 6, > 0 => 3, _ => 0 };
            if (p > 0) Add("Apps need updating", $"{updates} apps have updates, often including security fixes.", p, HealthFix.Updates, Severity.Info, "Update apps");
        }

        int score = Math.Clamp(100 - issues.Sum(x => x.Penalty), 0, 100);
        string grade = score switch { >= 90 => "Excellent", >= 75 => "Good", >= 50 => "Fair", _ => "Needs attention" };
        return new HealthReport(score, grade, issues.OrderByDescending(x => x.Severity).ThenByDescending(x => x.Penalty).ToList());
    }

    /// <summary>
    /// Decodes Security Center's AntiVirusProduct.productState: bit 12 = real-time protection on,
    /// bit 4 = definitions out of date.
    /// </summary>
    public static (bool Enabled, bool UpToDate) DecodeProductState(int state) => ((state & 0x1000) != 0, (state & 0x10) == 0);

    private static string Size(long bytes) => bytes >= GB ? $"{bytes / (double)GB:0.#} GB" : $"{bytes / (double)MB:0} MB";
}
