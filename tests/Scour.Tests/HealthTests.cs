using Scour.Core.Health;

namespace Scour.Tests;

public class HealthTests
{
    private static readonly DateTime Now = new(2026, 10, 14, 12, 0, 0, DateTimeKind.Utc);

    private static HealthInputs Healthy() => new()
    {
        SystemDriveFreePercent = 55, SystemDriveFreeBytes = 300L << 30, JunkBytes = 100L << 20, EnabledStartupItems = 4,
        RegistryProblems = 0, Drives = [new("Samsung 990", true, false, 3, 41)], Antivirus = [new("Windows Defender", true, true)],
        Uptime = TimeSpan.FromDays(1), LastCleanupUtc = Now.AddDays(-3), UpdatesAvailable = 0, NowUtc = Now,
    };

    [Fact]
    public void Healthy_pc_scores_100()
    {
        var r = HealthScore.Evaluate(Healthy());
        Assert.Equal(100, r.Score);
        Assert.Equal("Excellent", r.Grade);
        Assert.Empty(r.Issues);
    }

    [Fact]
    public void Problems_lower_the_score_and_are_ordered_by_severity()
    {
        var i = Healthy();
        var bad = new HealthInputs
        {
            SystemDriveFreePercent = 4, SystemDriveFreeBytes = 6L << 30, JunkBytes = 12L << 30, EnabledStartupItems = 20, RegistryProblems = 60,
            Drives = [new("Old HDD", true, true, null, 72)], Antivirus = [new("Defender", false, true)], Uptime = TimeSpan.FromDays(20),
            LastCleanupUtc = null, UpdatesAvailable = 12, NowUtc = Now,
        };
        var r = HealthScore.Evaluate(bad);
        Assert.Equal(0, r.Score);
        Assert.Equal("Needs attention", r.Grade);
        Assert.Equal(Severity.Critical, r.Issues[0].Severity);
        Assert.Contains(r.Issues, x => x.Fix == HealthFix.Antivirus);
        Assert.Contains(r.Issues, x => x.Fix == HealthFix.Restart);
        Assert.True(r.Issues.Zip(r.Issues.Skip(1)).All(p => p.First.Severity >= p.Second.Severity));
    }

    [Fact]
    public void Unknown_inputs_are_not_penalised()
    {
        var r = HealthScore.Evaluate(new HealthInputs { SystemDriveFreePercent = 60, SystemDriveFreeBytes = 200L << 30, LastCleanupUtc = Now, NowUtc = Now });
        Assert.Equal(100, r.Score);
    }

    [Fact]
    public void Third_party_antivirus_counts_as_protection()
    {
        var i = new HealthInputs
        {
            SystemDriveFreePercent = 60, SystemDriveFreeBytes = 200L << 30, LastCleanupUtc = Now, NowUtc = Now,
            Antivirus = [new("Windows Defender", false, true), new("Bitdefender", true, true)],
        };
        Assert.Equal(100, HealthScore.Evaluate(i).Score);
    }

    [Theory]
    [InlineData(0x061100, true, true)]   // Defender on, up to date
    [InlineData(0x060100, false, true)]  // off
    [InlineData(0x061110, true, false)]  // on, out of date
    [InlineData(0x041000, true, true)]
    public void Decodes_security_center_state(int state, bool enabled, bool upToDate) =>
        Assert.Equal((enabled, upToDate), HealthScore.DecodeProductState(state));
}

public class BatteryHealthTests
{
    [Fact]
    public void Health_is_full_charge_against_design_summed_over_batteries()
    {
        var b = Scour.Core.Health.BatteryHealth.From([50_000, 20_000], [40_000, 16_000], [0, 312])!;
        Assert.Equal(80, b.HealthPercent);
        Assert.Equal("Good", b.Rating);
        Assert.Equal(312, b.CycleCount);
        Assert.Equal(100, Scour.Core.Health.BatteryHealth.From([50_000], [52_000], [])!.HealthPercent);
    }

    [Fact]
    public void Nonsense_readings_give_no_result()
    {
        Assert.Null(Scour.Core.Health.BatteryHealth.From([], [], []));
        Assert.Null(Scour.Core.Health.BatteryHealth.From([50_000], [0], []));
        Assert.Null(Scour.Core.Health.BatteryHealth.From([100], [50_000], []));
    }

    [Fact]
    public void Only_a_worn_battery_is_an_issue()
    {
        Scour.Core.Health.HealthReport Eval(long full) => Scour.Core.Health.HealthScore.Evaluate(new Scour.Core.Health.HealthInputs
        {
            Battery = new Scour.Core.Health.BatteryHealth(50_000, full, null),
            Uptime = TimeSpan.FromHours(1),
            LastCleanupUtc = DateTime.UtcNow,
        });
        Assert.DoesNotContain(Eval(45_000).Issues, i => i.Fix == Scour.Core.Health.HealthFix.Battery);
        Assert.Contains(Eval(25_000).Issues, i => i.Fix == Scour.Core.Health.HealthFix.Battery && i.Severity == Scour.Core.Health.Severity.Info);
        Assert.Contains(Eval(15_000).Issues, i => i.Fix == Scour.Core.Health.HealthFix.Battery && i.Severity == Scour.Core.Health.Severity.Warning);
    }
}
