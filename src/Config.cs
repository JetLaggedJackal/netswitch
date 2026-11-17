using System.Text.Json;
using System.Text.Json.Serialization;

namespace NetSwitch;

public sealed class Profile
{
    public string Name { get; set; } = "";
    public string Adapter { get; set; } = "";
    public bool Dhcp { get; set; } = true;
    public string Ip { get; set; } = "";
    public string Mask { get; set; } = "255.255.255.0";
    public string Gateway { get; set; } = "";
    public List<string> Dns { get; set; } = new();
    /// <summary>Only for DHCP profiles: true = DNS from DHCP, false = use <see cref="Dns"/>. Null = automatic.</summary>
    public bool? DnsAuto { get; set; }
    /// <summary>Apply automatically when the adapter joins this Wi-Fi network (SSID). Empty = off.</summary>
    public string AutoSsid { get; set; } = "";
    /// <summary>Apply automatically when the adapter connects (cable plugged in, Wi-Fi joined).</summary>
    public bool AutoOnConnect { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool UsesAutoDns => Dhcp && ((DnsAuto ?? true) || Dns.All(string.IsNullOrWhiteSpace));

    public string Summary => Dhcp
        ? $"{Adapter} · DHCP" + (UsesAutoDns ? "" : " · custom DNS")
        : $"{Adapter} · {IpUtil.SplitIp(Ip).ip}";

    public Profile Clone() => new()
    {
        Name = Name, Adapter = Adapter, Dhcp = Dhcp, Ip = Ip, Mask = Mask,
        Gateway = Gateway, Dns = new List<string>(Dns), DnsAuto = DnsAuto,
        AutoSsid = AutoSsid, AutoOnConnect = AutoOnConnect,
    };

    public override string ToString() => string.IsNullOrWhiteSpace(Name) ? "(unnamed)" : Name;
}

public sealed class PreviousSettings
{
    public Profile Settings { get; set; } = new();
    public DateTime At { get; set; }
}

public sealed class AppConfig
{
    public List<Profile> Profiles { get; set; } = new();
    /// <summary>Adapter names to watch for IP changes.</summary>
    public List<string> WatchAdapters { get; set; } = new();
    /// <summary>Adapters not listed at the top of the tray menu.</summary>
    public List<string> HiddenAdapters { get; set; } = new();
    public bool NotifyOnIpChange { get; set; } = true;
    /// <summary>Keep pop-ups open until closed by hand.</summary>
    public bool PopupStaysOpen { get; set; }
    public int PopupSeconds { get; set; } = 10;
    /// <summary>Look for a new version on GitHub once a day.</summary>
    public bool AutoCheckUpdates { get; set; } = true;
    public DateTime? LastUpdateCheck { get; set; }
    /// <summary>Version the user chose to skip in the daily reminder.</summary>
    public string? SkippedVersion { get; set; }
    /// <summary>Each adapter's settings from just before the last switch, for "Return to previous settings".</summary>
    public Dictionary<string, PreviousSettings> Previous { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>After a switch, go back automatically unless the user clicks Keep in time.</summary>
    public bool SafeSwitch { get; set; }
    public int SafeSwitchSeconds { get; set; } = 30;
    /// <summary>Ctrl+Alt+1..9 switch to profile 1..9, Ctrl+Alt+0 returns to the previous settings.</summary>
    public bool Hotkeys { get; set; } = true;
    /// <summary>Colour the tray icon by internet status.</summary>
    public bool TrayStatus { get; set; } = true;
    /// <summary>Swap the tray clicks: left = adapter overview, right = profile list.</summary>
    public bool SwapClicks { get; set; }
    /// <summary>Pop up on IP conflicts, a changed gateway MAC or a new DHCP server.</summary>
    public bool Alerts { get; set; } = true;
    /// <summary>Show the public IP and ISP in the overview (asks ipinfo.io).</summary>
    public bool PublicIp { get; set; } = true;
    /// <summary>One line per adapter in the overview.</summary>
    public bool CompactOverview { get; set; }
    /// <summary>LAN scanner app opened by "Scan LAN". Empty = first one found.</summary>
    public string LanScanner { get; set; } = "";
    /// <summary>Last seen MAC of each gateway ("adapter|gateway ip" -> mac), for the gateway-changed alert.</summary>
    public Dictionary<string, string> GatewayMacs { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Last seen DHCP server per network ("adapter|gateway ip" -> server), for the new-DHCP-server alert.</summary>
    public Dictionary<string, string> DhcpServers { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonIgnore] public string Path { get; private set; } = DefaultPath;

    public static string DefaultPath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NetSwitch", "config.json");

    static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static bool Exists(string? path = null) => File.Exists(path ?? DefaultPath);

    public static AppConfig Load(string? path = null)
    {
        path ??= DefaultPath;
        AppConfig cfg;
        try
        {
            cfg = File.Exists(path)
                ? JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path), JsonOpts) ?? new AppConfig()
                : new AppConfig();
        }
        catch (Exception)
        {
            // A broken file should never crash the tray app; keep a copy and start clean.
            try { File.Copy(path, path + ".broken", true); } catch { }
            cfg = new AppConfig();
        }
        cfg.Path = path;
        cfg.Profiles ??= new();
        cfg.WatchAdapters ??= new();
        cfg.HiddenAdapters ??= new();
        cfg.Previous = new Dictionary<string, PreviousSettings>(cfg.Previous ?? new(), StringComparer.OrdinalIgnoreCase);
        cfg.GatewayMacs = new Dictionary<string, string>(cfg.GatewayMacs ?? new(), StringComparer.OrdinalIgnoreCase);
        cfg.DhcpServers = new Dictionary<string, string>(cfg.DhcpServers ?? new(), StringComparer.OrdinalIgnoreCase);
        cfg.LanScanner ??= "";
        foreach (var p in cfg.Profiles) { p.Dns ??= new(); p.AutoSsid ??= ""; }
        return cfg;
    }

    public void SaveAs(string path) { Path = path; Save(); }

    public void Save()
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        var tmp = Path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOpts));
        File.Move(tmp, Path, true);
    }

    public bool IsHidden(string adapter) =>
        HiddenAdapters.Any(a => string.Equals(a, adapter, StringComparison.OrdinalIgnoreCase));

    public bool IsWatched(string adapter) =>
        WatchAdapters.Any(a => string.Equals(a, adapter, StringComparison.OrdinalIgnoreCase));
}
