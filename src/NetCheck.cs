using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;

namespace NetSwitch;

public enum NetHealth { Checking, Online, Limited, Offline }

/// <summary>Result of one internet check: pings to public resolvers plus a DNS lookup.</summary>
public sealed record NetCheckResult(NetHealth Health, string Summary, string Detail);

/// <summary>Is the internet reachable? Pings 1.1.1.1 and 8.8.8.8 and resolves a name, all in parallel.</summary>
public static class NetCheck
{
    static readonly string[] Targets = { "1.1.1.1", "8.8.8.8" };
    const string DnsName = "www.msftconnecttest.com";

    public static async Task<NetCheckResult> RunAsync()
    {
        var pings = Targets.Select(PingAsync).ToArray();
        var dns = DnsAsync();
        await Task.WhenAll(pings.Cast<Task>().Append(dns));

        var parts = new List<string>();
        for (int i = 0; i < Targets.Length; i++)
        {
            var ms = pings[i].Result;
            parts.Add(ms is { } v ? $"{Targets[i]} {v} ms" : $"{Targets[i]} no reply");
        }
        var dnsMs = dns.Result;
        parts.Add(dnsMs is { } d ? $"DNS {d} ms" : "DNS fails");

        bool anyPing = pings.Any(p => p.Result != null);
        bool dnsOk = dnsMs != null;
        var (health, summary) = (anyPing, dnsOk) switch
        {
            (true, true) => (NetHealth.Online, "Online"),
            (true, false) => (NetHealth.Limited, "Online, DNS not resolving"),
            (false, true) => (NetHealth.Limited, "Limited, pings blocked"),
            _ => (NetHealth.Offline, "Offline"),
        };
        return new NetCheckResult(health, summary, string.Join("  ·  ", parts));
    }

    static async Task<long?> PingAsync(string ip)
    {
        try
        {
            using var p = new Ping();
            var r = await p.SendPingAsync(IPAddress.Parse(ip), 1500);
            return r.Status == IPStatus.Success ? r.RoundtripTime : null;
        }
        catch { return null; }
    }

    static async Task<long?> DnsAsync()
    {
        try
        {
            var sw = Stopwatch.StartNew();
            var lookup = Dns.GetHostAddressesAsync(DnsName);
            if (await Task.WhenAny(lookup, Task.Delay(3000)) != lookup)
            {
                // Observe the abandoned lookup so a late "No such host" doesn't land in error.log.
                _ = lookup.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                return null;
            }
            var addrs = await lookup;
            return addrs.Length > 0 ? sw.ElapsedMilliseconds : null;
        }
        catch { return null; }
    }

    /// <summary>Total bytes sent + received per adapter name, for activity lights.</summary>
    public static Dictionary<string, long> ByteCounters()
    {
        var d = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                try { var s = nic.GetIPStatistics(); d[nic.Name] = s.BytesReceived + s.BytesSent; } catch { }
            }
        }
        catch { }
        return d;
    }
}
