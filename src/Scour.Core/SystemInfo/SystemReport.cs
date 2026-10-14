using System.Globalization;
using System.Text;

namespace Scour.Core.SystemInfo;

public sealed record InfoRow(string Label, string Value);

/// <summary>A titled block of rows, e.g. one RAM stick or one drive.</summary>
public sealed class InfoGroup(string title)
{
    public string Title { get; } = title;
    public List<InfoRow> Rows { get; } = [];

    /// <summary>Adds a row unless the value is empty/unknown.</summary>
    public InfoGroup Add(string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) Rows.Add(new InfoRow(label, value.Trim()));
        return this;
    }
}

/// <summary>One page of the system information view (CPU, Memory, …).</summary>
public sealed class InfoSection(string title, string glyph)
{
    public string Title { get; } = title;
    public string Glyph { get; } = glyph;
    public string Summary { get; set; } = "";
    public List<InfoGroup> Groups { get; } = [];
    public string? Error { get; set; }

    public InfoGroup Group(string title)
    {
        var g = new InfoGroup(title);
        Groups.Add(g);
        return g;
    }
}

public static class SystemReport
{
    public static string ToText(IEnumerable<InfoSection> sections, string machineName, DateTime generated)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Scour system report — {machineName}");
        sb.AppendLine($"Generated {generated.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}");
        foreach (var s in sections)
        {
            sb.AppendLine().AppendLine(new string('=', 60)).AppendLine(s.Title.ToUpperInvariant());
            if (!string.IsNullOrEmpty(s.Summary)) sb.AppendLine(s.Summary);
            sb.AppendLine(new string('=', 60));
            if (s.Error is not null) sb.AppendLine("Not available: " + s.Error);
            foreach (var g in s.Groups)
            {
                sb.AppendLine().AppendLine($"[{g.Title}]");
                int width = g.Rows.Count == 0 ? 0 : g.Rows.Max(r => r.Label.Length);
                foreach (var r in g.Rows) sb.AppendLine($"  {r.Label.PadRight(width)}  {r.Value}");
            }
        }
        return sb.ToString();
    }
}

/// <summary>Turns raw WMI/SMBIOS codes into readable text.</summary>
public static class HardwareCodes
{
    public static string MemoryType(int smbiosType) => smbiosType switch
    {
        18 => "DDR",
        19 => "DDR2",
        20 => "DDR",
        21 => "DDR2",
        22 => "DDR2 FB-DIMM",
        24 => "DDR3",
        26 => "DDR4",
        27 => "LPDDR",
        28 => "LPDDR2",
        29 => "LPDDR3",
        30 => "LPDDR4",
        34 => "DDR5",
        35 => "LPDDR5",
        _ => "",
    };

    public static string MemoryFormFactor(int code) => code switch
    {
        8 => "DIMM",
        12 => "SO-DIMM",
        13 => "SRIMM",
        _ => "",
    };

    public static string Chassis(int code) => code switch
    {
        3 or 4 or 5 or 6 or 7 or 15 or 16 or 24 => "Desktop",
        8 or 9 or 10 or 14 or 31 or 32 => "Laptop",
        11 => "Handheld",
        13 => "All-in-one",
        17 or 23 or 28 => "Server",
        30 => "Tablet",
        35 => "Mini PC",
        36 => "Stick PC",
        _ => "",
    };

    public static string CpuArchitecture(int code) => code switch
    {
        0 => "x86",
        5 => "ARM",
        6 => "Itanium",
        9 => "x64",
        12 => "ARM64",
        _ => "",
    };

    public static string DiskBus(int code) => code switch
    {
        1 => "SCSI",
        2 => "ATAPI",
        3 => "ATA",
        4 => "IEEE 1394",
        5 => "SSA",
        6 => "Fibre Channel",
        7 => "USB",
        8 => "RAID",
        9 => "iSCSI",
        10 => "SAS",
        11 => "SATA",
        12 => "SD",
        13 => "MMC",
        15 => "File-backed virtual",
        16 => "Storage Spaces",
        17 => "NVMe",
        _ => "Unknown",
    };

    public static string DiskMedia(int code) => code switch
    {
        3 => "HDD",
        4 => "SSD",
        5 => "SCM",
        _ => "",
    };

    public static string DiskHealth(int code) => code switch
    {
        0 => "Healthy",
        1 => "Warning",
        2 => "Unhealthy",
        _ => "Unknown",
    };

    public static string BatteryStatus(int code) => code switch
    {
        1 => "Discharging",
        2 => "Plugged in",
        3 => "Fully charged",
        4 => "Low",
        5 => "Critical",
        6 => "Charging",
        7 => "Charging (high)",
        8 => "Charging (low)",
        9 => "Charging (critical)",
        11 => "Partially charged",
        _ => "",
    };

    /// <summary>"20240115000000.000000-000" → 2024-01-15.</summary>
    public static DateTime? WmiDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length < 8) return null;
        if (DateTime.TryParseExact(value[..Math.Min(14, value.Length)], ["yyyyMMddHHmmss", "yyyyMMdd"], CultureInfo.InvariantCulture,
                                   DateTimeStyles.None, out var d))
            return d;
        return DateTime.TryParseExact(value[..8], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out d) ? d : null;
    }

    /// <summary>WmiMonitorID stores names as arrays of UTF-16 code units padded with zeros.</summary>
    public static string FromCodeUnits(IEnumerable<ushort>? units) =>
        units is null ? "" : new string(units.TakeWhile(u => u != 0).Select(u => (char)u).ToArray()).Trim();

    /// <summary>Diagonal in inches from a monitor's image size in centimetres.</summary>
    public static double DiagonalInches(double widthCm, double heightCm) =>
        Math.Round(Math.Sqrt(widthCm * widthCm + heightCm * heightCm) / 2.54, 1);

    public static string Mhz(long mhz) => mhz <= 0 ? "" : mhz >= 1000 ? $"{mhz / 1000.0:0.00} GHz" : $"{mhz} MHz";

    public static string Bytes(long bytes)
    {
        if (bytes <= 0) return "";
        string[] u = ["B", "KB", "MB", "GB", "TB", "PB"];
        double v = bytes;
        int i = 0;
        while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return i == 0 ? $"{bytes} B" : $"{v:0.#} {u[i]}";
    }

    /// <summary>Disk sizes are sold in decimal units ("1 TB" = 10^12 bytes).</summary>
    public static string DecimalSize(long bytes)
    {
        if (bytes <= 0) return "";
        double gb = bytes / 1e9;
        return gb >= 1000 ? $"{gb / 1000:0.##} TB" : $"{gb:0} GB";
    }
}
