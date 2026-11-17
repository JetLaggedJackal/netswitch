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
        var pings = Targets.Select(t => PingAsync(t, 1500)).ToArray();
        var dns = DnsAsync();
        await Task.WhenAll(pings.Cast<Task>().Append(dns));
        // One lost or slow ping on Wi-Fi is normal. Before calling it a problem, ask both again
        // with more time, so a single dropped packet doesn't flip the status (and the tray icon).
        if (pings.All(p => p.Result == null))
        {
            var retry = Targets.Select(t => PingAsync(t, 3000)).ToArray();
            await Task.WhenAll(retry);
            pings = retry;
        }

        var parts = new List<string>();
        for (int i = 0; i < Targets.Length; i++)
        {
            var ms = pings[i].Result;
            parts.Add(ms is { } v ? $"{Targets[i]} {v} ms" : $"{Targets[i]} no reply");
        }
        var dnsMs = dns.Result;
        parts.Add(dnsMs is { } d ? $"DNS {d} ms" : "DNS fails");
        Record(pings[0].Result);
        if (LossPercent is { } loss) parts.Add($"loss {loss}%");

        bool anyPing = pings.Any(p => p.Result != null);
        bool dnsOk = dnsMs != null;
        var (health, summary) = (anyPing, dnsOk) switch
        {
            (true, true) => (NetHealth.Online, "Online"),
            (true, false) => (NetHealth.Limited, "Online, DNS not resolving"),
            (false, true) => (NetHealth.Limited, "Limited, pings not answered"),
            _ => (NetHealth.Offline, "Offline"),
        };
        return new NetCheckResult(health, summary, string.Join("  ·  ", parts));
    }

    // Recent results for 1.1.1.1, for the overview's sparkline and loss figure.
    static readonly object HistLock = new();
    static readonly List<long?> Hist = new();
    const int HistMax = 40;

    static void Record(long? ms)
    {
        lock (HistLock)
        {
            Hist.Add(ms);
            if (Hist.Count > HistMax) Hist.RemoveAt(0);
        }
    }

    public static List<long?> History { get { lock (HistLock) return new List<long?>(Hist); } }

    /// <summary>Share of recent checks where 1.1.1.1 didn't answer, once there are enough samples.</summary>
    public static int? LossPercent
    {
        get
        {
            lock (HistLock)
                return Hist.Count < 5 ? null : (int)Math.Round(100.0 * Hist.Count(x => x == null) / Hist.Count);
        }
    }

    public static async Task<long?> PingAsync(string ip, int timeout = 1500)
    {
        try
        {
            using var p = new Ping();
            var r = await p.SendPingAsync(ip, timeout);
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
