using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace NetSwitch;

public sealed record WifiInfo(string Ssid, string Bssid, int SignalPercent, int Channel, string Standard, string Security, long RxKbps, long TxKbps)
{
    public int Dbm => SignalPercent / 2 - 100; // the usual Windows mapping of quality to RSSI
    public string Band => Channel == 0 ? "" : Channel <= 14 ? "2.4 GHz" : "5 GHz";
    public int Bars => SignalPercent >= 80 ? 4 : SignalPercent >= 55 ? 3 : SignalPercent >= 30 ? 2 : SignalPercent > 0 ? 1 : 0;
}

public sealed record PublicIpInfo(string Ip, string Isp, string Place);

/// <summary>Deeper network facts for admins: Wi-Fi link, DHCP lease, this PC, public IP, raw DNS, ARP.</summary>
public static class NetDetails
{
    // ---------------- Wi-Fi (Native Wifi API, so it doesn't depend on the Windows language) ----------------

    [DllImport("wlanapi.dll")] static extern int WlanOpenHandle(uint ver, IntPtr reserved, out uint negotiated, out IntPtr handle);
    [DllImport("wlanapi.dll")] static extern int WlanCloseHandle(IntPtr handle, IntPtr reserved);
    [DllImport("wlanapi.dll")] static extern int WlanEnumInterfaces(IntPtr handle, IntPtr reserved, out IntPtr list);
    [DllImport("wlanapi.dll")] static extern int WlanQueryInterface(IntPtr handle, ref Guid iface, int opcode, IntPtr reserved, out int size, out IntPtr data, IntPtr opType);
    [DllImport("wlanapi.dll")] static extern void WlanFreeMemory(IntPtr p);

    /// <summary>Current Wi-Fi connection of the adapter with this interface id ("{GUID}"), or null.</summary>
    public static WifiInfo? Wifi(string adapterId)
    {
        if (!Guid.TryParse(adapterId, out var want)) return null;
        IntPtr h = IntPtr.Zero, list = IntPtr.Zero;
        try
        {
            if (WlanOpenHandle(2, IntPtr.Zero, out _, out h) != 0) return null;
            if (WlanEnumInterfaces(h, IntPtr.Zero, out list) != 0) return null;
            int n = Marshal.ReadInt32(list);
            for (int i = 0; i < n; i++)
            {
                var item = list + 8 + i * 532; // WLAN_INTERFACE_INFO: GUID + WCHAR[256] + state
                var guid = Marshal.PtrToStructure<Guid>(item);
                if (guid != want) continue;
                if (WlanQueryInterface(h, ref guid, 7 /*current_connection*/, IntPtr.Zero, out _, out var conn, IntPtr.Zero) != 0) return null;
                try
                {
                    // WLAN_CONNECTION_ATTRIBUTES, offsets from wlanapi.h
                    if (Marshal.ReadInt32(conn) != 1 /*connected*/) return null;
                    int ssidLen = Math.Clamp(Marshal.ReadInt32(conn, 520), 0, 32);
                    var ssidBytes = new byte[ssidLen];
                    Marshal.Copy(conn + 524, ssidBytes, 0, ssidLen);
                    var bssid = new byte[6];
                    Marshal.Copy(conn + 560, bssid, 0, 6);
                    int phy = Marshal.ReadInt32(conn, 568);
                    int quality = Marshal.ReadInt32(conn, 576);
                    long rx = (uint)Marshal.ReadInt32(conn, 580), tx = (uint)Marshal.ReadInt32(conn, 584);
                    bool secured = Marshal.ReadInt32(conn, 588) != 0;
                    int auth = Marshal.ReadInt32(conn, 596);
                    int channel = 0;
                    if (WlanQueryInterface(h, ref guid, 8 /*channel_number*/, IntPtr.Zero, out _, out var ch, IntPtr.Zero) == 0)
                    {
                        channel = Marshal.ReadInt32(ch);
                        WlanFreeMemory(ch);
                    }
                    return new WifiInfo(Encoding.UTF8.GetString(ssidBytes), string.Join(":", bssid.Select(b => b.ToString("x2"))),
                        quality, channel, PhyName(phy), secured ? AuthName(auth) : "Open (no password)", rx, tx);
                }
                finally { WlanFreeMemory(conn); }
            }
        }
        catch { }
        finally
        {
            if (list != IntPtr.Zero) WlanFreeMemory(list);
            if (h != IntPtr.Zero) WlanCloseHandle(h, IntPtr.Zero);
        }
        return null;
    }

    static string PhyName(int phy) => phy switch
    {
        4 => "802.11a",
        5 => "802.11b",
        6 => "802.11g",
        7 => "Wi-Fi 4 (802.11n)",
        8 => "Wi-Fi 5 (802.11ac)",
        10 => "Wi-Fi 6 (802.11ax)",
        11 => "Wi-Fi 7 (802.11be)",
        _ => "",
    };

    static string AuthName(int a) => a switch
    {
        1 => "Open",
        2 => "WEP (shared key)",
        3 => "WPA-Enterprise",
        4 => "WPA-Personal",
        6 => "WPA2-Enterprise",
        7 => "WPA2-Personal",
        8 or 11 => "WPA3-Enterprise",
        9 => "WPA3-Personal",
        10 => "Enhanced Open (OWE)",
        _ => "Secured",
    };

    public static string ShortStandard(string s) => s.StartsWith("Wi-Fi ") ? s[..8] : s;

    // ---------------- DHCP lease ----------------

    /// <summary>When the DHCP lease was obtained and when it ends (local time), from the adapter's registry key.</summary>
    public static (DateTime? Obtained, DateTime? Expires) Lease(string adapterId)
    {
        try
        {
            using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\{adapterId}");
            DateTime? T(string name) => k?.GetValue(name) is int v && v > 0 ? DateTimeOffset.FromUnixTimeSeconds((uint)v).LocalDateTime : null;
            return (T("LeaseObtainedTime"), T("LeaseTerminatesTime"));
        }
        catch { return (null, null); }
    }

    // ---------------- this PC ----------------

    public sealed record AdapterExtra(string DnsSuffix, int Mtu, string Ipv6, string Category);

    public static AdapterExtra Extra(AdapterInfo a)
    {
        string suffix = "", ipv6 = "Off";
        int mtu = 0;
        try
        {
            var nic = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n => n.Id == a.Id);
            if (nic != null)
            {
                var props = nic.GetIPProperties();
                suffix = props.DnsSuffix ?? "";
                try { mtu = props.GetIPv4Properties()?.Mtu ?? 0; } catch { }
                if (nic.Supports(NetworkInterfaceComponent.IPv6))
                {
                    var v6 = props.UnicastAddresses.Where(u => u.Address.AddressFamily == AddressFamily.InterNetworkV6).Select(u => u.Address).ToList();
                    var global = v6.FirstOrDefault(x => !x.IsIPv6LinkLocal && !x.IsIPv6SiteLocal && !x.IsIPv6Teredo);
                    ipv6 = global != null ? $"On · {global}" : v6.Count > 0 ? "On · link-local only" : "On · no address";
                }
            }
        }
        catch { }
        return new AdapterExtra(suffix, mtu, ipv6, Category(a.Id));
    }

    /// <summary>Private / Public / Domain, from the Network List Manager.</summary>
    static string Category(string adapterId)
    {
        try
        {
            var type = Type.GetTypeFromCLSID(new Guid("DCB00C01-570F-4A9B-8D69-199FDBA5723B"));
            if (type == null) return "";
            dynamic nlm = Activator.CreateInstance(type)!;
            foreach (dynamic conn in nlm.GetNetworkConnections())
            {
                Guid id = conn.GetAdapterId();
                if (!Guid.TryParse(adapterId, out var want) || id != want) continue;
                int cat = conn.GetNetwork().GetCategory();
                string name = conn.GetNetwork().GetName();
                var c = cat switch { 0 => "Public", 1 => "Private", 2 => "Domain", _ => "" };
                return name.Length > 0 ? $"{c} (\"{name}\")" : c;
            }
        }
        catch { }
        return "";
    }

    /// <summary>"On (Private, Public)" style summary from the firewall's registry settings.</summary>
    public static string Firewall()
    {
        try
        {
            var on = new List<string>();
            var off = new List<string>();
            foreach (var (key, label) in new[] { ("DomainProfile", "Domain"), ("StandardProfile", "Private"), ("PublicProfile", "Public") })
            {
                using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\{key}");
                if (k?.GetValue("EnableFirewall") is int v) (v != 0 ? on : off).Add(label);
            }
            if (on.Count == 0 && off.Count == 0) return "Unknown";
            if (off.Count == 0) return "On for all networks";
            if (on.Count == 0) return "Off for all networks";
            return $"On ({string.Join(", ", on)}) · off ({string.Join(", ", off)})";
        }
        catch { return "Unknown"; }
    }

    public static (int Connections, int Listening) Sockets()
    {
        try
        {
            var g = IPGlobalProperties.GetIPGlobalProperties();
            return (g.GetActiveTcpConnections().Count(c => c.State == TcpState.Established), g.GetActiveTcpListeners().Length);
        }
        catch { return (0, 0); }
    }

    // ---------------- public IP ----------------

    static PublicIpInfo? _pub;
    static DateTime _pubAt;
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };

    public static void ForgetPublicIp() => _pubAt = DateTime.MinValue;

    /// <summary>Public IP and ISP from ipinfo.io, cached for 10 minutes (or until the network changes).</summary>
    public static async Task<PublicIpInfo?> PublicIpAsync()
    {
        if (_pub != null && DateTime.UtcNow - _pubAt < TimeSpan.FromMinutes(10)) return _pub;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "https://ipinfo.io/json");
            req.Headers.UserAgent.ParseAdd("NetSwitch");
            using var resp = await Http.SendAsync(req);
            if (!resp.IsSuccessStatusCode) return _pub;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var r = doc.RootElement;
            string S(string n) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            var org = S("org");
            // "AS13046 A1 Hrvatska d.o.o." -> "A1 Hrvatska d.o.o."
            if (org.StartsWith("AS") && org.IndexOf(' ') is > 0 and var sp) org = org[(sp + 1)..];
            _pub = new PublicIpInfo(S("ip"), org, string.Join(", ", new[] { S("city"), S("country") }.Where(x => x.Length > 0)));
            _pubAt = DateTime.UtcNow;
            return _pub;
        }
        catch { return _pub; }
    }

    // ---------------- DNS lookup ----------------

    public sealed record LookupResult(string Text, bool Ok);

    /// <summary>Resolves a name with the PC's DNS and with 1.1.1.1 directly, and says whether they agree.</summary>
    public static async Task<LookupResult> LookupAsync(string name, string? viaServer)
    {
        name = name.Trim().TrimEnd('.');
        if (name.Length == 0) return new("Type a name, for example example.com.", false);
        if (IPAddress.TryParse(name, out var asIp))
        {
            try
            {
                var sw0 = Stopwatch.StartNew();
                var e = await Dns.GetHostEntryAsync(asIp).WaitAsync(TimeSpan.FromSeconds(4));
                return new($"{asIp} is {e.HostName} · {sw0.ElapsedMilliseconds} ms", true);
            }
            catch { return new($"No name found for {asIp}.", false); }
        }
        var sw = Stopwatch.StartNew();
        string local;
        List<string> mine = new();
        bool ok = false;
        try
        {
            var addrs = await Dns.GetHostAddressesAsync(name).WaitAsync(TimeSpan.FromSeconds(4));
            mine = addrs.Where(a => a.AddressFamily == AddressFamily.InterNetwork).Select(a => a.ToString()).ToList();
            var all = mine.Count > 0 ? mine : addrs.Select(a => a.ToString()).ToList();
            local = all.Count == 0 ? "no addresses" : string.Join(", ", all.Take(3)) + (all.Count > 3 ? $" (+{all.Count - 3})" : "");
            local += $" · {sw.ElapsedMilliseconds} ms" + (viaServer != null ? $" via {viaServer}" : "");
            ok = all.Count > 0;
        }
        catch (Exception ex)
        {
            if (ex is TimeoutException) local = "your DNS did not answer in 4 s";
            else local = ex is SocketException { SocketErrorCode: SocketError.HostNotFound } ? "your DNS says this name doesn't exist" : "your DNS failed: " + ex.Message;
        }
        var outside = await QueryAAsync(name, IPAddress.Parse("1.1.1.1"));
        string compare = outside == null ? "1.1.1.1 did not answer"
            : outside.Count == 0 ? "1.1.1.1 has no address for it"
            : mine.Count > 0 && outside.Intersect(mine).Any() ? "1.1.1.1 gives the same answer"
            : "1.1.1.1 says " + string.Join(", ", outside.Take(3));
        return new(local + "\n" + compare, ok);
    }

    /// <summary>Minimal DNS A query over UDP, to ask a specific server and bypass the PC's resolver.</summary>
    public static async Task<List<string>?> QueryAAsync(string name, IPAddress server)
    {
        try
        {
            var id = (ushort)Random.Shared.Next(ushort.MaxValue);
            var q = new List<byte> { (byte)(id >> 8), (byte)id, 0x01, 0x00, 0, 1, 0, 0, 0, 0, 0, 0 };
            foreach (var label in name.Split('.'))
            {
                var b = Encoding.ASCII.GetBytes(label);
                if (b.Length is 0 or > 63) return null;
                q.Add((byte)b.Length); q.AddRange(b);
            }
            q.AddRange(new byte[] { 0, 0, 1, 0, 1 });
            using var udp = new UdpClient(AddressFamily.InterNetwork);
            await udp.SendAsync(q.ToArray(), new IPEndPoint(server, 53));
            var recv = udp.ReceiveAsync();
            if (await Task.WhenAny(recv, Task.Delay(2500)) != recv)
            {
                _ = recv.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                return null;
            }
            var r = (await recv).Buffer;
            if (r.Length < 12 || r[0] != q[0] || r[1] != q[1]) return null;
            int an = r[6] << 8 | r[7];
            int pos = 12;
            pos = SkipName(r, pos) + 4; // question
            var result = new List<string>();
            for (int i = 0; i < an && pos + 10 <= r.Length; i++)
            {
                pos = SkipName(r, pos);
                int type = r[pos] << 8 | r[pos + 1];
                int len = r[pos + 8] << 8 | r[pos + 9];
                pos += 10;
                if (type == 1 && len == 4 && pos + 4 <= r.Length) result.Add($"{r[pos]}.{r[pos + 1]}.{r[pos + 2]}.{r[pos + 3]}");
                pos += len;
            }
            return result;
        }
        catch { return null; }
    }

    static int SkipName(byte[] r, int pos)
    {
        while (pos < r.Length)
        {
            int len = r[pos];
            if (len == 0) return pos + 1;
            if ((len & 0xC0) == 0xC0) return pos + 2;
            pos += len + 1;
        }
        return pos;
    }

    // ---------------- ARP ----------------

    [DllImport("iphlpapi.dll")] static extern int SendARP(uint destIp, uint srcIp, byte[] mac, ref int macLen);

    /// <summary>MAC address of a device on the local network, e.g. the gateway. Null if it doesn't answer.</summary>
    public static string? MacOf(string ip)
    {
        try
        {
            if (!IPAddress.TryParse(ip, out var a) || a.AddressFamily != AddressFamily.InterNetwork) return null;
            var mac = new byte[6];
            int len = mac.Length;
            if (SendARP(BitConverter.ToUInt32(a.GetAddressBytes(), 0), 0, mac, ref len) != 0 || len < 6) return null;
            if (mac.All(b => b == 0)) return null;
            return string.Join(":", mac.Take(6).Select(b => b.ToString("x2")));
        }
        catch { return null; }
    }

    /// <summary>True when Windows found another device using one of this adapter's IPv4 addresses.</summary>
    public static string? DuplicateAddress(string adapterId)
    {
        try
        {
            var nic = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n => n.Id == adapterId);
            var dup = nic?.GetIPProperties().UnicastAddresses.FirstOrDefault(u =>
                u.Address.AddressFamily == AddressFamily.InterNetwork && u.DuplicateAddressDetectionState == DuplicateAddressDetectionState.Duplicate);
            return dup?.Address.ToString();
        }
        catch { return null; }
    }

    // ---------------- LAN scanners ----------------

    public sealed record Scanner(string Name, string Path);

    /// <summary>Well-known LAN scanners installed on this PC.</summary>
    public static List<Scanner> FindScanners()
    {
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var candidates = new (string Name, string[] Paths)[]
        {
            ("Advanced IP Scanner", new[] { $@"{pf86}\Advanced IP Scanner\advanced_ip_scanner.exe", $@"{pf}\Advanced IP Scanner\advanced_ip_scanner.exe" }),
            ("Angry IP Scanner", new[] { $@"{pf}\Angry IP Scanner\ipscan.exe", $@"{pf86}\Angry IP Scanner\ipscan.exe", $@"{local}\Programs\Angry IP Scanner\ipscan.exe" }),
            ("SoftPerfect Network Scanner", new[] { $@"{pf}\SoftPerfect Network Scanner\netscan.exe", $@"{pf86}\SoftPerfect Network Scanner\netscan.exe" }),
            ("Zenmap (Nmap)", new[] { $@"{pf86}\Nmap\zenmap.exe", $@"{pf}\Nmap\zenmap.exe" }),
        };
        var found = new List<Scanner>();
        foreach (var (name, paths) in candidates)
            if (paths.FirstOrDefault(File.Exists) is { } p) found.Add(new Scanner(name, p));
        return found;
    }

    public static string ScannerName(string path)
    {
        var known = FindScanners().FirstOrDefault(s => string.Equals(s.Path, path, StringComparison.OrdinalIgnoreCase));
        if (known != null) return known.Name;
        try { return FileVersionInfo.GetVersionInfo(path).FileDescription is { Length: > 0 } d ? d : System.IO.Path.GetFileNameWithoutExtension(path); }
        catch { return System.IO.Path.GetFileNameWithoutExtension(path); }
    }

    /// <summary>First and last host address of the adapter's subnet, for scanners that take a range.</summary>
    public static (string First, string Last)? Range(AdapterInfo? a)
    {
        if (a == null || a.Addresses.Count == 0 || !IpUtil.IsIPv4(a.Addresses[0].Mask)) return null;
        uint ip = U(a.Addresses[0].Ip), m = U(a.Addresses[0].Mask);
        if (m >= 0xFFFFFFFE) return null;
        uint net = ip & m, bc = net | ~m;
        if (bc - net > 65536) return null; // don't hand anyone a /15
        return (S(net + 1), S(bc - 1));
        static uint U(string s) { var b = IPAddress.Parse(s).GetAddressBytes(); return (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]); }
        static string S(uint v) => $"{v >> 24}.{(v >> 16) & 255}.{(v >> 8) & 255}.{v & 255}";
    }

    // ---------------- running Windows tools ----------------

    [DllImport("kernel32.dll")] static extern uint GetOEMCP();

    /// <summary>Runs a console tool hidden and returns its exit code and output (decoded with the console code page).</summary>
    public static (int Code, string Output) Run(string exe, string args, int timeoutMs = 60_000)
    {
        Encoding enc;
        try { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); enc = Encoding.GetEncoding((int)GetOEMCP()); }
        catch { enc = Encoding.Default; }
        var psi = new ProcessStartInfo(System.IO.Path.Combine(Environment.SystemDirectory, exe), args)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = enc, StandardErrorEncoding = enc,
        };
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEndAsync();
        var outp = p.StandardOutput.ReadToEndAsync();
        if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch { } return (-1, "timed out"); }
        return (p.ExitCode, outp.Result + err.Result);
    }
}

/// <summary>Quick fixes for an adapter. Renew and reset need admin rights.</summary>
public static class QuickFix
{
    public static readonly string[] All = { "flushdns", "renew", "reset" };

    public static string Title(string fix) => fix switch
    {
        "flushdns" => "Flush DNS cache",
        "renew" => "Renew DHCP lease",
        "reset" => "Reset adapter",
        "all" => "Fix everything",
        _ => fix,
    };

    public static bool NeedsAdmin(string fix) => fix != "flushdns";

    /// <summary>Runs one fix (or "all") and returns what happened, one line per step.</summary>
    public static (bool ok, string message) Run(string fix, string adapter)
    {
        var steps = fix == "all" ? All : new[] { fix };
        var lines = new List<string>();
        bool ok = true;
        foreach (var s in steps)
        {
            var (good, text) = RunOne(s, adapter);
            lines.Add(text);
            ok &= good;
        }
        return (ok, string.Join("\n", lines));
    }

    static (bool, string) RunOne(string fix, string adapter)
    {
        var q = "\"" + adapter.Replace("\"", "") + "\"";
        switch (fix)
        {
            case "flushdns":
            {
                var (c, o) = NetDetails.Run("ipconfig.exe", "/flushdns", 20_000);
                return (c == 0, c == 0 ? "DNS cache flushed" : "Flush DNS failed: " + Last(o));
            }
            case "renew":
            {
                var snap = NetInfo.Snapshot();
                if (snap.TryGetValue(adapter, out var a) && !a.Dhcp) return (true, "Renew skipped: the adapter has a static IP");
                NetDetails.Run("ipconfig.exe", $"/release {q}", 30_000);
                var (c, o) = NetDetails.Run("ipconfig.exe", $"/renew {q}", 60_000);
                return (c == 0, c == 0 ? "DHCP lease renewed" : "Renew failed: " + Last(o));
            }
            case "reset":
            {
                var (c1, o1) = NetDetails.Run("netsh.exe", $"interface set interface name={q} admin=disabled", 30_000);
                if (c1 != 0) return (false, "Reset failed: " + Last(o1));
                Thread.Sleep(1500);
                var (c2, o2) = NetDetails.Run("netsh.exe", $"interface set interface name={q} admin=enabled", 30_000);
                if (c2 != 0) return (false, "Adapter was turned off but didn't come back on: " + Last(o2));
                // Give it time to reconnect before the caller checks the internet.
                for (int i = 0; i < 20; i++)
                {
                    Thread.Sleep(1000);
                    if (NetInfo.Snapshot().TryGetValue(adapter, out var a) && a.IsUp && a.Addresses.Count > 0) break;
                }
                return (true, "Adapter turned off and on again");
            }
        }
        return (false, "Unknown fix " + fix);
    }

    static string Last(string output) =>
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).LastOrDefault(l => l.Length > 0) ?? "no details";
}
