using System.Buffers.Binary;
using System.Text;
using System.Xml.Linq;

namespace Scour.Core.Network;

public sealed record DnsPreset(string Name, string Description, IReadOnlyList<string> Servers)
{
    /// <summary>The servers Windows uses; empty means "automatic" (from the router / DHCP).</summary>
    public bool IsAutomatic => Servers.Count == 0;
    public string Primary => Servers.Count > 0 ? Servers[0] : "";

    public static readonly IReadOnlyList<DnsPreset> All =
    [
        new("Automatic", "Whatever your router or network hands out (Windows' default).", []),
        new("Cloudflare", "Fast and private — doesn't log your IP address.",
            ["1.1.1.1", "1.0.0.1", "2606:4700:4700::1111", "2606:4700:4700::1001"]),
        new("Cloudflare for Families", "Cloudflare that also blocks malware sites.",
            ["1.1.1.2", "1.0.0.2", "2606:4700:4700::1112", "2606:4700:4700::1002"]),
        new("Google", "Reliable and fast almost everywhere.",
            ["8.8.8.8", "8.8.4.4", "2001:4860:4860::8888", "2001:4860:4860::8844"]),
        new("Quad9", "Blocks known malicious domains; Swiss non-profit.",
            ["9.9.9.9", "149.112.112.112", "2620:fe::fe", "2620:fe::9"]),
        new("AdGuard", "Blocks ads and trackers for every app on the PC.",
            ["94.140.14.14", "94.140.15.15", "2a10:50c0::ad1:ff", "2a10:50c0::ad2:ff"]),
    ];

    /// <summary>Which preset a list of servers matches (by its first IPv4 server), or null for a custom set.</summary>
    public static DnsPreset? Match(IEnumerable<string> servers)
    {
        var list = servers.ToList();
        if (list.Count == 0) return All[0];
        return All.Skip(1).FirstOrDefault(p => list.Any(s => p.Servers.Contains(s, StringComparer.OrdinalIgnoreCase)));
    }
}

/// <summary>A minimal DNS client message, enough to time a lookup against a server.</summary>
public static class DnsProbe
{
    public static byte[] BuildQuery(ushort id, string name)
    {
        var ms = new MemoryStream();
        Span<byte> header = stackalloc byte[12];
        BinaryPrimitives.WriteUInt16BigEndian(header, id);
        header[2] = 0x01; // recursion desired
        BinaryPrimitives.WriteUInt16BigEndian(header[4..], 1); // one question
        ms.Write(header);
        foreach (var label in name.TrimEnd('.').Split('.'))
        {
            var bytes = Encoding.ASCII.GetBytes(label);
            if (bytes.Length is 0 or > 63) throw new ArgumentException("Bad host name: " + name);
            ms.WriteByte((byte)bytes.Length);
            ms.Write(bytes);
        }
        ms.WriteByte(0);
        ms.Write([0, 1, 0, 1]); // type A, class IN
        return ms.ToArray();
    }

    /// <summary>True when the bytes are a successful answer to the query with this id.</summary>
    public static bool IsAnswer(ReadOnlySpan<byte> response, ushort id)
    {
        if (response.Length < 12 || BinaryPrimitives.ReadUInt16BigEndian(response) != id) return false;
        bool isResponse = (response[2] & 0x80) != 0;
        int rcode = response[3] & 0x0F;
        int answers = BinaryPrimitives.ReadUInt16BigEndian(response[6..]);
        return isResponse && rcode == 0 && answers > 0;
    }
}

public sealed record WifiProfile(string Name, string Ssid, string Security, string? Password);

/// <summary>Reads the XML files written by "netsh wlan export profile key=clear" (the same on every Windows language).</summary>
public static class WifiProfiles
{
    public static WifiProfile? Parse(string xml)
    {
        var doc = XDocument.Parse(xml);
        var root = doc.Root;
        if (root is null || root.Name.LocalName != "WLANProfile") return null;
        XElement? Find(XElement? e, string name) => e?.Descendants().FirstOrDefault(d => d.Name.LocalName == name);
        var name = root.Elements().FirstOrDefault(e => e.Name.LocalName == "name")?.Value ?? "";
        var ssid = Find(Find(root, "SSIDConfig"), "name")?.Value ?? name;
        var auth = Find(root, "authentication")?.Value ?? "";
        var key = Find(root, "keyMaterial")?.Value;
        return new WifiProfile(name, ssid, Describe(auth), string.IsNullOrEmpty(key) ? null : key);
    }

    private static string Describe(string auth) => auth switch
    {
        "open" => "Open (no password)",
        "WPA2PSK" => "WPA2-Personal",
        "WPA3SAE" => "WPA3-Personal",
        "WPAPSK" => "WPA-Personal",
        "WPA2" => "WPA2-Enterprise",
        "WPA3ENT192" or "WPA3ENT" => "WPA3-Enterprise",
        "OWE" => "Enhanced Open",
        "" => "",
        _ => auth,
    };
}

public sealed record HostsEntry(int Line, string Address, IReadOnlyList<string> Hosts, bool Enabled, string Comment);

/// <summary>Edits the hosts file line by line, leaving comments and formatting it doesn't touch exactly as they were.</summary>
public sealed class HostsFile
{
    private readonly List<string> _lines;

    public HostsFile(string text)
    {
        _lines = text.Replace("\r\n", "\n").Split('\n').ToList();
        if (_lines.Count > 0 && _lines[^1].Length == 0) _lines.RemoveAt(_lines.Count - 1);
    }

    public IReadOnlyList<HostsEntry> Entries
    {
        get
        {
            var list = new List<HostsEntry>();
            for (int i = 0; i < _lines.Count; i++)
                if (ParseLine(_lines[i]) is { } e) list.Add(e with { Line = i });
            return list;
        }
    }

    /// <summary>An entry line, or a commented-out entry ("# 127.0.0.1 example.com"). Plain comments return null.</summary>
    private static HostsEntry? ParseLine(string line)
    {
        var t = line.Trim();
        bool enabled = true;
        if (t.StartsWith('#'))
        {
            enabled = false;
            t = t.TrimStart('#').Trim();
        }
        string comment = "";
        int hash = t.IndexOf('#');
        if (hash >= 0)
        {
            comment = t[(hash + 1)..].Trim();
            t = t[..hash].Trim();
        }
        var parts = t.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !System.Net.IPAddress.TryParse(parts[0], out _)) return null;
        if (parts.Skip(1).Any(h => !IsHostName(h))) return null; // prose that happens to start with an IP
        return new HostsEntry(0, parts[0], parts[1..], enabled, comment);
    }

    private static bool IsHostName(string h) =>
        h.Length <= 253 && h.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' or '*');

    public void SetEnabled(HostsEntry entry, bool enabled)
    {
        var current = ParseLine(_lines[entry.Line]) ?? throw new InvalidOperationException("The hosts file changed.");
        if (current.Enabled == enabled) return;
        _lines[entry.Line] = enabled ? _lines[entry.Line].TrimStart().TrimStart('#').TrimStart() : "# " + _lines[entry.Line];
    }

    public void Add(string address, string host, string? comment = null)
    {
        if (!System.Net.IPAddress.TryParse(address, out _)) throw new ArgumentException($"\"{address}\" isn't an IP address.");
        if (string.IsNullOrWhiteSpace(host) || !IsHostName(host.Trim())) throw new ArgumentException($"\"{host}\" isn't a valid host name.");
        _lines.Add($"{address}\t{host.Trim()}" + (string.IsNullOrWhiteSpace(comment) ? "" : $"\t# {comment.Trim()}"));
    }

    public void Remove(HostsEntry entry) => _lines.RemoveAt(entry.Line);

    public override string ToString() => string.Join("\r\n", _lines) + "\r\n";

    public static string Default => """
        # Copyright (c) 1993-2009 Microsoft Corp.
        #
        # This is a sample HOSTS file used by Microsoft TCP/IP for Windows.
        #
        # localhost name resolution is handled within DNS itself.
        #	127.0.0.1       localhost
        #	::1             localhost

        """.Replace("\n", "\r\n");
}

/// <summary>Parses https://1.1.1.1/cdn-cgi/trace ("key=value" lines).</summary>
public static class CloudflareTrace
{
    public static Dictionary<string, string> Parse(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim().Split('=', 2))
            .Where(p => p.Length == 2)
            .GroupBy(p => p[0])
            .ToDictionary(g => g.Key, g => g.First()[1]);

    public static double Mbps(long bytes, TimeSpan elapsed) => elapsed.TotalSeconds <= 0 ? 0 : bytes * 8 / elapsed.TotalSeconds / 1_000_000;
}
