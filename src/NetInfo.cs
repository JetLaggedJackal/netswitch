using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace NetSwitch;

public sealed record AdapterInfo(
    string Name,
    string Description,
    bool IsUp,
    bool Dhcp,
    IReadOnlyList<(string Ip, string Mask)> Addresses,
    IReadOnlyList<string> Gateways,
    IReadOnlyList<string> Dns)
{
    public string Mac { get; init; } = "";
    public long SpeedBps { get; init; }
    public bool IsWireless { get; init; }
    public string? DhcpServer { get; init; }
    public long LeaseSeconds { get; init; }

    public string AddressText => !IsUp ? "disconnected"
        : Addresses.Count == 0 ? "no address"
        : string.Join(", ", Addresses.Select(a => a.Ip));

    public string ModeText => Dhcp ? "DHCP" : "static";
}

public static class NetInfo
{
    /// <summary>Snapshot of all physical-ish adapters (Ethernet / Wi-Fi / others with IPv4), keyed by name.</summary>
    public static Dictionary<string, AdapterInfo> Snapshot()
    {
        var result = new Dictionary<string, AdapterInfo>(StringComparer.OrdinalIgnoreCase);
        NetworkInterface[] nics;
        try { nics = NetworkInterface.GetAllNetworkInterfaces(); }
        catch { return result; }

        foreach (var nic in nics)
        {
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                continue;
            try
            {
                var props = nic.GetIPProperties();
                IPv4InterfaceProperties? v4 = null;
                try { v4 = props.GetIPv4Properties(); } catch { }
                if (v4 == null) continue; // IPv4 not bound to this adapter

                var addrs = props.UnicastAddresses
                    .Where(u => u.Address.AddressFamily == AddressFamily.InterNetwork)
                    .Select(u => (u.Address.ToString(), u.IPv4Mask?.ToString() ?? ""))
                    .ToList();
                var gws = props.GatewayAddresses
                    .Where(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any))
                    .Select(g => g.Address.ToString()).ToList();
                var dns = props.DnsAddresses
                    .Where(d => d.AddressFamily == AddressFamily.InterNetwork)
                    .Select(d => d.ToString()).ToList();

                string? dhcpServer = null;
                try { dhcpServer = props.DhcpServerAddresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)?.ToString(); } catch { }
                long lease = 0;
                try
                {
                    lease = props.UnicastAddresses.Where(u => u.Address.AddressFamily == AddressFamily.InterNetwork)
                        .Select(u => u.DhcpLeaseLifetime).FirstOrDefault();
                }
                catch { }
                var macBytes = nic.GetPhysicalAddress().GetAddressBytes();

                result[nic.Name] = new AdapterInfo(nic.Name, nic.Description,
                    nic.OperationalStatus == OperationalStatus.Up, v4.IsDhcpEnabled, addrs, gws, dns)
                {
                    Mac = string.Join("-", macBytes.Select(b => b.ToString("X2"))),
                    SpeedBps = SafeSpeed(nic),
                    IsWireless = nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211,
                    DhcpServer = v4.IsDhcpEnabled ? dhcpServer : null,
                    LeaseSeconds = v4.IsDhcpEnabled ? lease : 0,
                };
            }
            catch { /* adapter vanished mid-enumeration */ }
        }
        return result;
    }

    static readonly string[] VirtualHints =
        { "virtual", "hyper-v", "vmware", "zerotier", "wintun", "tap-", "tap ", "vpn", "anyconnect", "wireguard", "tailscale", "bluetooth", "loopback", "pseudo" };

    /// <summary>Best guess whether this is a real network card (Wi-Fi, Ethernet, USB/dock) rather than a VPN or VM adapter.</summary>
    public static bool LooksPhysical(AdapterInfo a) =>
        !a.Name.StartsWith("vEthernet", StringComparison.OrdinalIgnoreCase)
        && !VirtualHints.Any(h => a.Description.Contains(h, StringComparison.OrdinalIgnoreCase));

    /// <summary>Order for lists: connected physical adapters first.</summary>
    public static IEnumerable<AdapterInfo> Ordered(IEnumerable<AdapterInfo> list) =>
        list.OrderByDescending(a => a.IsUp).ThenByDescending(LooksPhysical).ThenByDescending(a => a.Gateways.Count).ThenBy(a => a.Name);

    /// <summary>The adapter most likely to be "the" network connection.</summary>
    public static AdapterInfo? Primary(IEnumerable<AdapterInfo> list) =>
        list.Where(a => a.IsUp && a.Addresses.Count > 0)
            .OrderByDescending(LooksPhysical).ThenByDescending(a => a.Gateways.Count).ThenByDescending(a => a.IsWireless)
            .FirstOrDefault();

    static long SafeSpeed(NetworkInterface n) { try { return n.Speed; } catch { return 0; } }

    public static int PrefixLength(string mask)
    {
        if (!IpUtil.IsIPv4(mask)) return 0;
        return System.Net.IPAddress.Parse(mask).GetAddressBytes().Sum(b => System.Numerics.BitOperations.PopCount(b));
    }

    public static string FormatSpeed(long bps) => bps <= 0 ? "" :
        bps >= 1_000_000_000 ? $"{bps / 1_000_000_000.0:0.#} Gbps" : $"{bps / 1_000_000.0:0} Mbps";

    public static string FormatDuration(long seconds)
    {
        if (seconds <= 0 || seconds > 10L * 365 * 86400) return "";
        var t = TimeSpan.FromSeconds(seconds);
        return t.TotalDays >= 1 ? $"{(int)t.TotalDays} d {t.Hours} h" : t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes} min" : $"{t.Minutes} min";
    }

    /// <summary>Name of the Wi-Fi network the adapter is connected to (best effort, via netsh wlan).</summary>
    public static string? WifiSsid(string adapterName)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "netsh.exe"), "wlan show interfaces")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
            using var p = System.Diagnostics.Process.Start(psi)!;
            var text = p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);
            // Blocks per interface; the first "key : value" line of each block is the interface name.
            string? current = null;
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim();
                var i = line.IndexOf(" : ", StringComparison.Ordinal);
                if (i < 0) { if (line.Length == 0) current = null; continue; }
                var key = line[..i].Trim(); var val = line[(i + 3)..].Trim();
                current ??= val;
                if (key == "SSID" && string.Equals(current, adapterName, StringComparison.OrdinalIgnoreCase))
                    return val.Length > 0 ? val : null;
            }
        }
        catch { }
        return null;
    }

    /// <summary>Does the adapter's current state match this profile?</summary>
    public static bool Matches(Profile p, AdapterInfo? a)
    {
        if (a == null) return false;
        if (p.Dhcp) return a.Dhcp;
        if (a.Dhcp) return false;
        var ip = IpUtil.SplitIp(p.Ip).ip;
        return a.Addresses.Any(x => x.Ip == ip);
    }
}

public static class IpUtil
{
    public static bool IsIPv4(string? s) =>
        !string.IsNullOrWhiteSpace(s) && IPAddress.TryParse(s.Trim(), out var a)
        && a.AddressFamily == AddressFamily.InterNetwork && s.Trim().Count(c => c == '.') == 3;

    /// <summary>Accepts "192.168.1.5" or "192.168.1.5/24".</summary>
    public static (string ip, string? prefixMask) SplitIp(string s)
    {
        s = (s ?? "").Trim();
        var slash = s.IndexOf('/');
        if (slash < 0) return (s, null);
        return (s[..slash].Trim(), NormalizeMask(s[(slash + 1)..]));
    }

    /// <summary>Accepts "255.255.255.0", "24" or "/24"; returns dotted mask or null if invalid.</summary>
    public static string? NormalizeMask(string? s)
    {
        s = (s ?? "").Trim().TrimStart('/');
        if (int.TryParse(s, out var bits))
        {
            if (bits < 1 || bits > 32) return null;
            uint m = bits == 32 ? uint.MaxValue : ~(uint.MaxValue >> bits);
            return $"{m >> 24}.{(m >> 16) & 255}.{(m >> 8) & 255}.{m & 255}";
        }
        if (!IsIPv4(s)) return null;
        var b = IPAddress.Parse(s).GetAddressBytes();
        uint v = (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]);
        uint inv = ~v; // valid mask: contiguous ones => ~v + 1 is a power of two
        if (v == 0 || (inv & (inv + 1)) != 0) return null;
        return s;
    }

    /// <summary>Returns null when OK, otherwise a human readable problem.</summary>
    public static string? Validate(Profile p)
    {
        if (string.IsNullOrWhiteSpace(p.Name)) return "Profile needs a name.";
        if (string.IsNullOrWhiteSpace(p.Adapter)) return $"\"{p.Name}\": choose an adapter.";
        if (p.Dhcp && p.UsesAutoDns) return null;
        if (p.Dhcp) return ValidateDns(p);
        var (ip, prefMask) = SplitIp(p.Ip);
        if (!IsIPv4(ip)) return $"\"{p.Name}\": \"{p.Ip}\" is not a valid IPv4 address.";
        if ((prefMask ?? NormalizeMask(p.Mask)) == null) return $"\"{p.Name}\": \"{p.Mask}\" is not a valid subnet mask.";
        if (!string.IsNullOrWhiteSpace(p.Gateway) && !IsIPv4(p.Gateway)) return $"\"{p.Name}\": gateway \"{p.Gateway}\" is not a valid IPv4 address.";
        return ValidateDns(p);
    }

    static string? ValidateDns(Profile p)
    {
        foreach (var d in p.Dns.Where(d => !string.IsNullOrWhiteSpace(d)))
            if (!IsIPv4(d)) return $"\"{p.Name}\": DNS \"{d}\" is not a valid IPv4 address.";
        return null;
    }
}
