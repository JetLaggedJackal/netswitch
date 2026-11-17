namespace NetSwitch;

/// <summary>Shows a UI piece on its own, for screenshots and design checks: NetSwitch --preview editor|notify|popup|menu</summary>
static class Preview
{
    public static int Run(string what)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        var cfg = new AppConfig();
        var snap = NetInfo.Snapshot();
        var a = snap.Values.FirstOrDefault(x => x.IsUp && x.Addresses.Count > 0) ?? snap.Values.FirstOrDefault();
        var name = a?.Name ?? "Ethernet";
        cfg.Profiles.Add(new Profile { Name = "Office (fixed IP)", Adapter = name, Dhcp = false, Ip = "192.168.10.50", Mask = "255.255.255.0", Gateway = "192.168.10.1", Dns = new() { "192.168.10.1", "1.1.1.1" } });
        cfg.Profiles.Add(new Profile { Name = "Home (DHCP)", Adapter = name, Dhcp = true });
        cfg.Profiles.Add(new Profile { Name = "Lab bench", Adapter = name, Dhcp = false, Ip = "10.0.0.2/24" });
        cfg.WatchAdapters.Add(name);
        using var s = typeof(Preview).Assembly.GetManifestResourceStream("NetSwitch.app.ico");
        var icon = s != null ? new Icon(s) : SystemIcons.Application;

        switch (what)
        {
            case "popup":
            case "applied":
            case "error":
                var after = a ?? new AdapterInfo(name, "Intel(R) Ethernet Connection I219-V", true, true,
                    new List<(string, string)> { ("192.168.1.73", "255.255.255.0") }, new List<string> { "192.168.1.1" }, new List<string> { "192.168.1.1", "1.1.1.1" });
                var before = after with { Addresses = new List<(string, string)> { ("10.0.0.5", "255.255.255.0") } };
                var c = what == "popup" ? PopupBuilder.Change(name, before, after, cfg.Profiles, true)
                    : what == "applied" ? PopupBuilder.Applied(cfg.Profiles[0], after, true)
                    : PopupBuilder.Error("Could not switch profile", "netsh failed: The filename, directory name, or volume label syntax is incorrect.");
                var host = new Form { ShowInTaskbar = false, Opacity = 0, Size = new Size(1, 1) };
                host.Shown += (_, _) => PopupForm.Show(c, 600);
                Application.Run(host);
                break;
            case "overview":
                var fake = new List<AdapterInfo>
                {
                    new("Wi-Fi", "MediaTek Wi-Fi 6 MT7921", true, true, new List<(string, string)> { ("192.168.1.73", "255.255.255.0") }, new List<string> { "192.168.1.1" }, new List<string>()),
                    new("ZeroTier One [ce3bd3f908edbf0a]", "ZeroTier Virtual Port", true, false, new List<(string, string)> { ("172.30.173.86", "255.255.0.0") }, new List<string>(), new List<string>()),
                    new("vEthernet (Default Switch)", "Hyper-V Virtual Ethernet Adapter", true, false, new List<(string, string)> { ("172.20.240.1", "255.255.240.0") }, new List<string>(), new List<string>()),
                    new("Ethernet 5", "Cisco AnyConnect", false, true, new List<(string, string)>(), new List<string>(), new List<string>()),
                };
                var oh = new Form { ShowInTaskbar = false, Opacity = 0, Size = new Size(1, 1) };
                oh.Shown += (_, _) => PopupForm.Show(PopupBuilder.Overview(fake, cfg.Profiles, _ => new List<PopupAction> { new("Set static IP…"), PopupAction.Separator }), 600);
                Application.Run(oh);
                break;
            case "icons":
                var iform = new Form { BackColor = Color.FromArgb(32, 32, 32), ClientSize = new Size(300, 120), Text = "icons" };
                int ix = 10;
                foreach (var ic0 in new[] { Color.FromArgb(0, 0x67, 0xC0), Color.FromArgb(0xE0, 0x9B, 0x1A), Color.FromArgb(0xD1, 0x34, 0x38) })
                {
                    foreach (var sz in new[] { 16, 32 })
                    {
                        var ic = TrayApp.DrawStatusIcon(ic0, sz);
                        iform.Controls.Add(new PictureBox { Image = ic.ToBitmap(), Location = new Point(ix, 10), Size = new Size(sz, sz) });
                        ix += sz + 10;
                    }
                }
                Application.Run(iform);
                break;
            case "quick":
                var qa = new AdapterInfo("Wi-Fi", "MediaTek Wi-Fi 6 MT7921", true, true, new List<(string, string)> { ("192.168.1.73", "255.255.255.0") }, new List<string> { "192.168.1.1" }, new List<string> { "192.168.1.1" });
                Application.Run(new QuickIpForm(qa));
                break;
            case "profiles":
                var ph = new Form { ShowInTaskbar = false, Opacity = 0, Size = new Size(1, 1) };
                var psnap = new Dictionary<string, AdapterInfo>(StringComparer.OrdinalIgnoreCase);
                var prevS = new PreviousSettings { Settings = new Profile { Name = "Previous settings", Adapter = "Wi-Fi", Dhcp = true }, At = DateTime.Now.AddMinutes(-3) };
                ph.Shown += (_, _) => PopupForm.Show(PopupBuilder.Profiles(cfg.Profiles, psnap, false, _ => { }, ("Wi-Fi", prevS), _ => { }), 600);
                Application.Run(ph);
                break;
            case "menu":
                var app = new TrayApp(cfg);
                var menu = (ContextMenuStrip)typeof(TrayApp).GetField("_menu", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(app)!;
                var t = new System.Windows.Forms.Timer { Interval = 1500 };
                t.Tick += (_, _) => { t.Stop(); menu.Show(new Point(900, 300)); };
                t.Start();
                Application.Run(app);
                break;
            default:
                var f = new ProfilesForm(cfg, icon);
                if (what == "notify") f.Shown += (_, _) => f.GetType().GetMethod("ShowNotifications", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(f, null);
                f.TestPopup = p => PopupForm.Show(p, 10);
                Application.Run(f);
                break;
        }
        return 0;
    }
}
