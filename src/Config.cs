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

    [System.Text.Json.Serialization.JsonIgnore]
    public bool UsesAutoDns => Dhcp && ((DnsAuto ?? true) || Dns.All(string.IsNullOrWhiteSpace));

    public string Summary => Dhcp
        ? $"{Adapter} · DHCP" + (UsesAutoDns ? "" : " · custom DNS")
        : $"{Adapter} · {IpUtil.SplitIp(Ip).ip}";

    public Profile Clone() => new()
    {
        Name = Name, Adapter = Adapter, Dhcp = Dhcp, Ip = Ip, Mask = Mask,
        Gateway = Gateway, Dns = new List<string>(Dns), DnsAuto = DnsAuto,
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
        foreach (var p in cfg.Profiles) p.Dns ??= new();
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
