using Scour.Core.Network;

namespace Scour.Tests;

public class NetworkTests
{
    [Fact]
    public void Builds_dns_query_and_recognises_answers()
    {
        var q = DnsProbe.BuildQuery(0x1234, "www.example.com");
        Assert.Equal([0x12, 0x34, 0x01, 0x00, 0, 1, 0, 0, 0, 0, 0, 0], q[..12]);
        Assert.Equal(3, q[12]); // "www"
        Assert.Equal([0, 0, 1, 0, 1], q[^5..]);
        Assert.Equal(12 + 1 + 3 + 1 + 7 + 1 + 3 + 1 + 4, q.Length);

        var answer = (byte[])q.Clone();
        answer[2] = 0x81; answer[3] = 0x80; answer[7] = 1; // response, no error, one answer
        Assert.True(DnsProbe.IsAnswer(answer, 0x1234));
        Assert.False(DnsProbe.IsAnswer(answer, 0x9999));
        answer[3] = 0x83; // NXDOMAIN
        Assert.False(DnsProbe.IsAnswer(answer, 0x1234));
        Assert.False(DnsProbe.IsAnswer(q, 0x1234)); // the query itself isn't an answer
    }

    [Fact]
    public void Matches_dns_presets()
    {
        Assert.Equal("Automatic", DnsPreset.Match([])!.Name);
        Assert.Equal("Cloudflare", DnsPreset.Match(["1.0.0.1", "1.1.1.1"])!.Name);
        Assert.Equal("Quad9", DnsPreset.Match(["2620:fe::fe"])!.Name);
        Assert.Null(DnsPreset.Match(["192.168.1.1"]));
    }

    [Fact]
    public void Reads_exported_wifi_profiles()
    {
        const string xml = """
            <?xml version="1.0"?>
            <WLANProfile xmlns="http://www.microsoft.com/networking/WLAN/profile/v1">
              <name>Home Wi-Fi</name>
              <SSIDConfig><SSID><hex>486F6D65</hex><name>Home</name></SSID></SSIDConfig>
              <connectionType>ESS</connectionType>
              <MSM><security>
                <authEncryption><authentication>WPA2PSK</authentication><encryption>AES</encryption></authEncryption>
                <sharedKey><keyType>passPhrase</keyType><protected>false</protected><keyMaterial>correct horse</keyMaterial></sharedKey>
              </security></MSM>
            </WLANProfile>
            """;
        var p = WifiProfiles.Parse(xml)!;
        Assert.Equal(("Home Wi-Fi", "Home", "WPA2-Personal", "correct horse"), (p.Name, p.Ssid, p.Security, p.Password));
        var open = WifiProfiles.Parse(xml.Replace("WPA2PSK", "open").Replace("<keyMaterial>correct horse</keyMaterial>", ""))!;
        Assert.Null(open.Password);
    }

    [Fact]
    public void Edits_hosts_file_without_disturbing_other_lines()
    {
        var hosts = new HostsFile("# comment line\r\n127.0.0.1 localhost\r\n#\t0.0.0.0  ads.example.com tracker.example.com # blocked\r\n\r\n# 1. read the docs\r\n");
        var entries = hosts.Entries;
        Assert.Equal(2, entries.Count);
        Assert.True(entries[0].Enabled);
        Assert.False(entries[1].Enabled);
        Assert.Equal(["ads.example.com", "tracker.example.com"], entries[1].Hosts);
        Assert.Equal("blocked", entries[1].Comment);

        hosts.SetEnabled(entries[1], true);
        hosts.SetEnabled(hosts.Entries[0], false);
        hosts.Add("0.0.0.0", "bad.example.net", "Scour");
        Assert.Throws<ArgumentException>(() => hosts.Add("not-ip", "x.com"));
        Assert.Equal("# comment line\r\n# 127.0.0.1 localhost\r\n0.0.0.0  ads.example.com tracker.example.com # blocked\r\n\r\n# 1. read the docs\r\n0.0.0.0\tbad.example.net\t# Scour\r\n",
                     hosts.ToString());

        hosts.Remove(hosts.Entries.Single(e => e.Hosts[0] == "bad.example.net"));
        Assert.Equal(2, hosts.Entries.Count);
        Assert.All(new HostsFile(HostsFile.Default).Entries, e => Assert.False(e.Enabled)); // the two commented-out samples
    }

    [Fact]
    public void Parses_cloudflare_trace_and_speed()
    {
        var t = CloudflareTrace.Parse("fl=1\nip=203.0.113.9\nloc=GB\ncolo=LHR\n");
        Assert.Equal("203.0.113.9", t["ip"]);
        Assert.Equal("LHR", t["colo"]);
        Assert.Equal(80, CloudflareTrace.Mbps(10_000_000, TimeSpan.FromSeconds(1)));
    }
}
