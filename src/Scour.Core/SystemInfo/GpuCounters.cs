using System.Globalization;
using System.Text.RegularExpressions;

namespace Scour.Core.SystemInfo;

/// <summary>
/// Turns Windows' "GPU Engine" performance counters into one usage figure per graphics adapter, the way Task
/// Manager does: per engine type (3D, video decode, copy…) the processes' shares are added up, and the
/// busiest engine type is the adapter's usage.
/// </summary>
public static partial class GpuCounters
{
    // pid_1234_luid_0x00000000_0x0000D1B5_phys_0_eng_0_engtype_3D
    [GeneratedRegex(@"luid_0x(?<hi>[0-9a-f]{8})_0x(?<lo>[0-9a-f]{8})_phys_(?<phys>\d+)(?:_eng_\d+_engtype_(?<type>.*))?$", RegexOptions.IgnoreCase)]
    private static partial Regex InstanceName();

    /// <summary>"0x00000000_0x0000D1B5": the adapter's LUID (high, low), as D3DKMT reports it.</summary>
    public static string LuidKey(int high, uint low) => $"0x{high:X8}_0x{low:X8}";

    public static Dictionary<string, double> UsageByAdapter(IReadOnlyDictionary<string, double> engineCounters)
    {
        var sums = new Dictionary<(string Luid, string Type), double>();
        foreach (var (name, value) in engineCounters)
        {
            var m = InstanceName().Match(name);
            if (!m.Success || double.IsNaN(value) || value < 0) continue;
            var key = (Luid(m), m.Groups["type"].Value.ToUpperInvariant());
            sums[key] = sums.GetValueOrDefault(key) + value;
        }
        return sums.GroupBy(s => s.Key.Luid).ToDictionary(g => g.Key, g => Math.Min(100, g.Max(s => s.Value)));
    }

    /// <summary>"GPU Adapter Memory" instances ("luid_0x…_0x…_phys_0") → bytes, summed per adapter.</summary>
    public static Dictionary<string, double> ByAdapter(IReadOnlyDictionary<string, double> adapterCounters)
    {
        var result = new Dictionary<string, double>();
        foreach (var (name, value) in adapterCounters)
        {
            var m = InstanceName().Match(name);
            if (!m.Success || double.IsNaN(value) || value < 0) continue;
            var luid = Luid(m);
            result[luid] = result.GetValueOrDefault(luid) + value;
        }
        return result;
    }

    private static string Luid(Match m) => LuidKey(
        unchecked((int)uint.Parse(m.Groups["hi"].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture)),
        uint.Parse(m.Groups["lo"].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture));

    /// <summary>ACPI thermal zones report Kelvin; anything outside a believable range is a firmware placeholder.</summary>
    public static double? ThermalZoneCelsius(double kelvin)
    {
        var c = kelvin - 273.15;
        return c is > 5 and < 125 ? Math.Round(c, 1) : null;
    }
}
