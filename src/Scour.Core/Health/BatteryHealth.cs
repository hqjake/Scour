namespace Scour.Core.Health;

/// <summary>How worn a laptop battery is: what it holds now against what it held new.</summary>
public sealed record BatteryHealth(long DesignCapacityMWh, long FullChargeCapacityMWh, int? CycleCount)
{
    /// <summary>Capacity left, as a percentage of new (capped at 100: new batteries often report a little more).</summary>
    public int HealthPercent => DesignCapacityMWh <= 0 ? 0 : (int)Math.Round(Math.Min(100, 100.0 * FullChargeCapacityMWh / DesignCapacityMWh));

    public string Rating => HealthPercent switch
    {
        >= 80 => "Good",
        >= 60 => "Worn",
        >= 40 => "Weak — consider replacing it",
        _ => "Replace soon",
    };

    /// <summary>Sums several batteries (some laptops have two); null if the readings make no sense.</summary>
    public static BatteryHealth? From(IReadOnlyList<long> design, IReadOnlyList<long> full, IReadOnlyList<long> cycles)
    {
        long d = design.Where(v => v > 0).Sum(), f = full.Where(v => v > 0).Sum();
        if (d <= 0 || f <= 0 || f > d * 2) return null; // missing or nonsense values (some firmware reports garbage)
        int? c = cycles.Any(v => v > 0) ? (int)cycles.Where(v => v > 0).Max() : null;
        return new BatteryHealth(d, f, c);
    }
}
