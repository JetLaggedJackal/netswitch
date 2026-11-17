using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Reflection;

namespace NetSwitch;

public sealed class TrayApp : ApplicationContext
{
    readonly NotifyIcon _icon;
    readonly ContextMenuStrip _menu;
    readonly System.Windows.Forms.Timer _debounce;
    readonly SynchronizationContext _ui;
    readonly Control _invoker = new(); // owns a handle on the UI thread
    AppConfig _cfg;
    Dictionary<string, AdapterInfo> _last;
    ProfilesForm? _editor;
    bool _busy;

    readonly AppConfig? _fixedConfig; // preview only

    public TrayApp(AppConfig? previewConfig = null)
    {
        _fixedConfig = previewConfig;
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _invoker.CreateControl();
        bool firstRun = previewConfig == null && !AppConfig.Exists();
        _cfg = previewConfig ?? AppConfig.Load();
        if (firstRun) SeedDefaults();

        _menu = new ContextMenuStrip { ShowCheckMargin = true, ShowImageMargin = false, Font = Theme.UiFont(9.75f), Padding = new Padding(0, 4, 0, 4) };
        _menu.Items.Add("NetSwitch"); // a menu with no items refuses to open
        _menu.Opening += (_, e) => { BuildMenu(); e.Cancel = false; };
        _menu.HandleCreated += (_, _) => MenuRenderer.RoundCorners(_menu);

        _icon = new NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "NetSwitch",
            Visible = true,
        };
        // Left click: profile menu (pick one to switch). Right click: adapter overview pop-up.
        _icon.MouseUp += (_, e) =>
        {
            if (e.Button is not (MouseButtons.Left or MouseButtons.Right)) return;
            bool left = e.Button == MouseButtons.Left;
            if (left != _cfg.SwapClicks) ShowProfileMenu(); else ToggleOverview();
        };
        _menu.Closed += (_, _) => _icon.ContextMenuStrip = null;
        PopupForm.OpenProfilesRequested = OpenEditor;
        PopupForm.ExitRequested = ExitThread;
        PopupForm.ToolRequested = RunTool;

        _last = NetInfo.Snapshot();
        _baseIcon = _icon.Icon;
        UpdateTooltip();

        // Hotkeys: Ctrl+Alt+1..9 = profile 1..9, Ctrl+Alt+0 = back to the previous settings.
        _hotkeys = new Hotkeys();
        _hotkeys.Pressed += OnHotkey;
        if (previewConfig == null) _hotkeys.Enable(_cfg.Hotkeys);

        // Internet status on the tray icon, refreshed every 30 s and after network changes.
        _statusTimer = new System.Windows.Forms.Timer { Interval = 30_000 };
        _statusTimer.Tick += async (_, _) => { await RefreshTrayStatusAsync(); await CheckAlertsAsync(); };
        if (previewConfig == null) { _statusTimer.Start(); _ = RefreshTrayStatusAsync(); }

        // Auto-switch rules: check once shortly after start (e.g. signed in at the office), then on every network change.
        if (previewConfig == null)
        {
            var first = new System.Windows.Forms.Timer { Interval = 8000 };
            first.Tick += async (_, _) => { first.Stop(); first.Dispose(); await RunAutoRulesAsync(null, NetInfo.Snapshot()); };
            first.Start();
        }

        _debounce = new System.Windows.Forms.Timer { Interval = 2000 };
        _debounce.Tick += (_, _) => { _debounce.Stop(); OnNetworkSettled(); };
        NetworkChange.NetworkAddressChanged += OnAddressChanged;
        NetworkChange.NetworkAvailabilityChanged += OnAvailabilityChanged;

        // Daily update check: first one a minute after start, then every few hours we look whether a day has passed.
        _updateTimer = new System.Windows.Forms.Timer { Interval = 60_000 };
        _updateTimer.Tick += async (_, _) => { _updateTimer.Interval = 3 * 3600_000; await DailyUpdateCheckAsync(); };
        if (previewConfig == null) _updateTimer.Start();

        if (firstRun)
        {
            Popup(PopupBuilder.Info("NetSwitch is running",
                "Click the tray icon to switch profiles. Set up your profiles in the window that just opened."));
            OpenEditor();
        }
    }

    void SeedDefaults()
    {
        var primary = NetInfo.Primary(NetInfo.Snapshot().Values);
        if (primary != null)
        {
            _cfg.Profiles.Add(new Profile { Name = "Automatic (DHCP)", Adapter = primary.Name, Dhcp = true });
            _cfg.WatchAdapters.Add(primary.Name);
        }
        try { _cfg.Save(); } catch { }
    }

    static Icon LoadIcon()
    {
        using var s = typeof(TrayApp).Assembly.GetManifestResourceStream("NetSwitch.app.ico");
        return s != null ? new Icon(s, SystemInformation.SmallIconSize) : SystemIcons.Application;
    }

    // ---------------- menu ----------------

    readonly System.Windows.Forms.Timer _updateTimer;

    async Task DailyUpdateCheckAsync()
    {
        var cfg = AppConfig.Load();
        if (!cfg.AutoCheckUpdates) return;
        if (cfg.LastUpdateCheck is { } last && DateTime.UtcNow - last < TimeSpan.FromHours(23)) return;
        Updater.Release? r;
        try { r = await Updater.GetLatestAsync(); }
        catch { return; } // offline: try again later
        cfg.LastUpdateCheck = DateTime.UtcNow;
        try { cfg.Save(); } catch { }
        _cfg = cfg;
        if (r == null || !Updater.IsNewer(r) || cfg.SkippedVersion == r.Version.ToString()) return;

        var notes = r.Notes.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "";
        Popup(new PopupContent
        {
            Kind = PopupKind.Info,
            Title = "Update available",
            Heading = $"NetSwitch {r.Version}",
            SubHeading = $"You have {Updater.CurrentText}",
            Message = notes.Length > 0 ? notes : null,
            ActionText = "Update now",
            Action = async () =>
            {
                if (Updater.IsUpdating) return;
                Popup(PopupBuilder.Info("Updating NetSwitch", $"Downloading {r.Version}. NetSwitch restarts by itself when it's done."));
                try { if (await Updater.DownloadAndSwapAsync(r, null)) ExitThread(); }
                catch (Exception ex) { Popup(PopupBuilder.Error("Update failed", ex.Message)); }
            },
            Sticky = true,
        });
    }

    // Left click = profile picker, right click = adapter overview. Both are the same kind of pop-up in the
    // same corner; opening one closes the other, clicking the same button again closes it.
    PopupForm? _panel;
    string? _panelKind;

    void TogglePanel(string kind, Func<PopupContent> build)
    {
        bool same = _panelKind == kind && _panel is { IsOpen: true };
        if (_panel is { IsOpen: true }) _panel.Dismiss();
        _panel = null; _panelKind = null;
        if (same) return;
        _cfg = _fixedConfig ?? AppConfig.Load(); // pick up edits made outside the app
        _panel = PopupForm.Show(build(), 30);
        _panelKind = kind;
    }

    void ShowProfileMenu() => TogglePanel("profiles", () =>
        PopupBuilder.Profiles(_cfg.Profiles, NetInfo.Snapshot(), _busy, p => _ = ApplyAsync(p),
            _cfg.Previous.Where(kv => kv.Value.Settings != null).OrderByDescending(kv => kv.Value.At).Select(kv => (kv.Key, kv.Value)).FirstOrDefault(),
            adapter => _ = RestorePreviousAsync(adapter), _cfg.Hotkeys));

    void ToggleOverview() => TogglePanel("overview", () =>
    {
        var visible = NetInfo.Ordered(NetInfo.Snapshot().Values).Where(a => !_cfg.IsHidden(a.Name));
        return PopupBuilder.Overview(visible, _cfg.Profiles, AdapterActions, a => NetDetails.Wifi(a.Id),
            new() { ("Scan LAN", () => ScanLan(null)), ("Monitor…", () => OpenMonitor(null)) }, _cfg.PublicIp);
    });

    void BuildMenu()
    {
        _cfg = _fixedConfig ?? AppConfig.Load(); // pick up edits made outside the app
        var theme = Theme.Current;
        _menu.Renderer = new MenuRenderer(theme);
        _menu.BackColor = theme.Surface;
        var snap = NetInfo.Snapshot();
        _menu.SuspendLayout();
        _menu.Items.Clear();

        ToolStripMenuItem Item(string text, EventHandler? onClick = null)
        {
            var i = new ToolStripMenuItem(text, null, onClick) { Padding = new Padding(4, 5, 4, 5) };
            return i;
        }

        // Profiles, one click to apply.
        foreach (var p in _cfg.Profiles)
        {
            snap.TryGetValue(p.Adapter, out var a);
            var detail = p.Dhcp ? "DHCP" : IpUtil.SplitIp(p.Ip).ip;
            var profile = p;
            var item = new ProfileMenuItem(p.Name, async (_, _) => await ApplyAsync(profile)) { Padding = new Padding(4, 5, 4, 5) };
            item.Checked = NetInfo.Matches(p, a);
            item.Enabled = !_busy;
            bool active = item.Checked;
            // Don't repeat the address when the profile is already named after it.
            if (string.Equals(detail, p.Name.Trim(), StringComparison.OrdinalIgnoreCase)) detail = "Fixed IP";
            item.Detail = (active ? "Active · " : "") + $"{detail} · {p.Adapter}";
            item.Font = new Font(_menu.Font, FontStyle.Bold);
            _menu.Items.Add(item);
        }
        if (_cfg.Profiles.Count == 0)
            { var none = Item("No profiles yet"); none.Enabled = false; _menu.Items.Add(none); }

        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(Item("Settings…", (_, _) => OpenEditor()));
        _menu.ResumeLayout();
    }

    void ShowAdapterDetails(AdapterInfo a)
    {
        var c = PopupBuilder.Change(a.Name, null, a, _cfg.Profiles, _cfg.PopupStaysOpen);
        Popup(new PopupContent
        {
            Kind = c.Kind, Title = "Adapter details", Heading = c.Heading, SubHeading = c.SubHeading,
            Big = c.Big, BigSuffix = c.BigSuffix, Rows = c.Rows, CopyText = c.CopyText, Sticky = c.Sticky,
        });
    }

    void SaveQuietly()
    {
        try { _cfg.Save(); }
        catch (Exception ex) { Popup(PopupBuilder.Error("Could not save settings", ex.Message)); }
    }

    void OpenEditor()
    {
        if (_editor is { IsDisposed: false }) { _editor.Activate(); return; }
        _editor = new ProfilesForm(AppConfig.Load(), _icon.Icon ?? SystemIcons.Application, Startup.IsEnabled());
        _editor.StartupToggle = ToggleStartupAsync;
        _editor.ExitApp = ExitThread;
        _editor.Saved += cfg => { _cfg = cfg; _hotkeys.Enable(cfg.Hotkeys); _ = RefreshTrayStatusAsync(); UpdateTooltip(); };
        _editor.Uninstall = UninstallAsync;
        _editor.TestPopup = c => Popup(c);
        _editor.Show();
        _editor.Activate();
    }

    void Popup(PopupContent c)
    {
        void show() => PopupForm.Show(c, _cfg.PopupSeconds);
        if (_invoker.InvokeRequired) _invoker.BeginInvoke(show); else show();
    }

    // ---------------- quick static / DHCP on any adapter ----------------

    List<PopupAction> AdapterActions(AdapterInfo a)
    {
        var ip = a.Addresses.Count > 0 ? a.Addresses[0].Ip : null;
        var list = new List<PopupAction>
        {
            new("Set static IP…", "", () => QuickStatic(a)),
            new("Switch to DHCP", a.Dhcp ? "already on" : "", () => _ = ApplyQuickAsync(new Profile { Name = $"DHCP on {a.Name}", Adapter = a.Name, Dhcp = true, DnsAuto = true })),
        };
        if (PreviousFor(a.Name) is { } prev)
            list.Add(new("Return to previous settings", NetInfo.Describe(prev.Settings), () => _ = RestorePreviousAsync(a.Name)));
        list.Add(PopupAction.Separator);
        bool up = a.IsUp;
        list.Add(new("Renew DHCP lease", a.Dhcp ? "release + renew" : "static IP", up && a.Dhcp ? () => _ = RunFixAsync("renew", a.Name) : null));
        list.Add(new("Flush DNS cache", "", () => _ = RunFixAsync("flushdns", a.Name)));
        list.Add(new("Reset adapter", "off, then on", () => _ = RunFixAsync("reset", a.Name)));
        list.Add(new("Fix everything", "all three", () => _ = RunFixAsync("all", a.Name)));
        list.Add(PopupAction.Separator);
        list.Add(new("Details…", a.IsWireless ? "Wi-Fi, DHCP, DNS" : "DHCP, DNS, this PC", () => OpenDetails(a.Name)));
        list.Add(new("Scan LAN", ScannerLabel(), up && a.Addresses.Count > 0 ? () => ScanLan(a) : null));
        list.Add(new("Copy IP", ip ?? "", ip == null ? null : () => RunTool("copy", ip)));
        return list;
    }

    // ---------------- quick fixes ----------------

    bool _fixing;

    async Task RunFixAsync(string fix, string adapter)
    {
        if (_fixing || _busy) return;
        _fixing = true;
        try
        {
            var title = QuickFix.Title(fix);
            if (fix is "reset" or "all") Popup(PopupBuilder.Info(title + "…", $"Working on {adapter}. The connection drops for a few seconds."));
            bool ok; string msg;
            bool admin = fix == "all" || QuickFix.NeedsAdmin(fix);
            if (!admin || Elevation.IsElevated)
                (ok, msg) = await Task.Run(() => QuickFix.Run(fix, adapter));
            else
            {
                var code = await Elevation.RunElevatedAsync("--fix", fix, "--adapter", adapter, "--config", _cfg.Path);
                if (code == null) return; // UAC cancelled
                ok = code == 0;
                msg = ReadResult() ?? (ok ? "Done." : "It didn't work.");
            }
            NetDetails.ForgetPublicIp();
            var net = await Task.Run(NetCheck.RunAsync);
            _netStatus = net; SetTrayIcon(_cfg.TrayStatus ? net.Health : null); UpdateTooltip();
            var internet = net.Health switch
            {
                NetHealth.Online => "Internet is online",
                NetHealth.Limited => "Internet: " + net.Summary.ToLowerInvariant(),
                _ => "Still no internet",
            };
            var content = new PopupContent
            {
                Kind = !ok ? PopupKind.Error : net.Health == NetHealth.Online ? PopupKind.Success : PopupKind.Info,
                Title = ok ? $"{title}: done" : $"{title}: problem",
                Heading = adapter,
                Message = msg + "\n\n" + internet + " (" + net.Detail + ").",
                Sticky = !ok || net.Health != NetHealth.Online,
            };
            Popup(content);
        }
        catch (Exception ex) { Popup(PopupBuilder.Error("Fix failed", ex.Message)); }
        finally { _fixing = false; }
    }

    // ---------------- details, monitor, LAN scanner ----------------

    DetailsForm? _details;
    MonitorForm? _monitor;

    void OpenDetails(string adapter)
    {
        if (_details is { IsDisposed: false }) _details.Close();
        _details = new DetailsForm(adapter, RunTool);
        _details.Show();
        _details.Activate();
    }

    void OpenMonitor(string? target)
    {
        if (_monitor is { IsDisposed: false }) { _monitor.Activate(); return; }
        var gw = NetInfo.Primary(NetInfo.Snapshot().Values)?.Gateways.FirstOrDefault();
        _monitor = new MonitorForm(gw, target);
        _monitor.Show();
        _monitor.Activate();
    }

    string ScannerLabel()
    {
        var path = ScannerPath();
        return path == null ? "choose in Settings" : NetDetails.ScannerName(path);
    }

    string? ScannerPath()
    {
        if (_cfg.LanScanner.Length > 0 && File.Exists(_cfg.LanScanner)) return _cfg.LanScanner;
        return NetDetails.FindScanners().FirstOrDefault()?.Path;
    }

    void ScanLan(AdapterInfo? a)
    {
        a ??= NetInfo.Primary(NetInfo.Snapshot().Values);
        var path = ScannerPath();
        if (path == null)
        {
            Popup(new PopupContent
            {
                Kind = PopupKind.Info, Title = "No LAN scanner yet",
                Message = "NetSwitch opens the LAN scanner you like. Install one (Advanced IP Scanner or Angry IP Scanner are free) or pick any .exe in Settings > Network tools.",
                Sticky = true,
            });
            return;
        }
        try
        {
            var psi = new ProcessStartInfo(path) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(path)! };
            // Angry IP Scanner takes the range on its command line; the others remember or detect the subnet themselves.
            if (NetDetails.Range(a) is { } r && Path.GetFileName(path).Equals("ipscan.exe", StringComparison.OrdinalIgnoreCase))
            { psi.ArgumentList.Add("-f:range"); psi.ArgumentList.Add(r.First); psi.ArgumentList.Add(r.Last); }
            Process.Start(psi);
        }
        catch (Exception ex) { Popup(PopupBuilder.Error("Could not open the LAN scanner", $"{path}\n{ex.Message}")); }
    }

    QuickIpForm? _quick;

    void QuickStatic(AdapterInfo a)
    {
        _panel?.Dismiss();
        if (_quick is { IsDisposed: false }) { _quick.Activate(); return; }
        // Modeless, so nothing else (pop-ups closing, timers) runs inside a nested message loop.
        var f = _quick = new QuickIpForm(a);
        f.FormClosed += (_, _) =>
        {
            if (f.DialogResult == DialogResult.OK && f.Result is { } p) _ = ApplyQuickAsync(p);
            _quick = null;

        };
        f.Show();
        f.Activate();
    }

    // ---------------- safe return to the previous settings ----------------

    static Profile Named(Profile p, string name) { var c = p.Clone(); c.Name = name; return c; }

    /// <summary>Stores the adapter's settings from before a switch. Returns them, or null if there was nothing to keep.</summary>
    Profile? RememberPrevious(AdapterInfo before)
    {
        var prev = NetInfo.CurrentAsProfile(before, "Previous settings");
        if (prev == null) return null;
        try
        {
            var cfg = AppConfig.Load(_cfg.Path); // fresh copy, so we don't overwrite edits made elsewhere
            cfg.Previous[before.Name] = new PreviousSettings { Settings = prev, At = DateTime.Now };
            cfg.Save();
            _cfg = _fixedConfig ?? cfg;
        }
        catch { }
        return prev;
    }

    PreviousSettings? PreviousFor(string adapter) =>
        _cfg.Previous.TryGetValue(adapter, out var p) && p.Settings != null ? p : null;

    Task RestorePreviousAsync(string adapter) =>
        PreviousFor(adapter) is { } prev ? ApplyQuickAsync(Named(prev.Settings, "Previous settings"), isRevert: true) : Task.CompletedTask;

    System.Windows.Forms.Timer? _safeTimer;
    PopupForm? _safePopup;

    /// <summary>Asks to keep the new settings; goes back by itself if nobody answers in time (e.g. the connection was lost).</summary>
    void StartSafeSwitch(Profile applied, Profile previous)
    {
        _safeTimer?.Dispose();
        _safePopup?.Dismiss();
        int left = Math.Clamp(_cfg.SafeSwitchSeconds, 10, 600);
        bool kept = false;
        _safePopup = PopupForm.Show(new PopupContent
        {
            Kind = PopupKind.Success,
            Title = $"Keep \"{applied.Name}\"?",
            Heading = applied.Adapter,
            Big = applied.Dhcp ? "Automatic (DHCP)" : IpUtil.SplitIp(applied.Ip).ip,
            Message = $"Safe switching is on. NetSwitch goes back to {NetInfo.Describe(previous)} in {(left % 60 == 0 ? $"{left / 60} min" : $"{left} seconds")} unless you click Keep.",
            ActionText = "Keep",
            Action = () => { kept = true; _safeTimer?.Stop(); },
            Sticky = true,
        }, left);
        _safeTimer = new System.Windows.Forms.Timer { Interval = left * 1000 };
        _safeTimer.Tick += async (_, _) =>
        {
            _safeTimer.Stop();
            if (kept) return;
            _safePopup?.Dismiss();
            await ApplyQuickAsync(Named(previous, "Previous settings"), isRevert: true);
        };
        _safeTimer.Start();
    }

    /// <summary>Applies a one-off profile without saving it: the elevated helper reads it from a side file.</summary>
    Task ApplyQuickAsync(Profile p, bool isRevert = false)
    {
        var quick = new AppConfig { Profiles = { p } };
        var path = Path.Combine(Path.GetDirectoryName(_cfg.Path)!, "quick.json");
        try { quick.SaveAs(path); }
        catch (Exception ex) { Popup(PopupBuilder.Error("Could not switch", ex.Message)); return Task.CompletedTask; }
        return ApplyAsync(p, path, isRevert);
    }

    // ---------------- network tools ----------------

    /// <summary>Opens a console running ping/tracert/pathping against an address, or the router page.</summary>
    static void RunTool(string tool, string ip)
    {
        if (!System.Net.IPAddress.TryParse(ip, out var addr)) return; // only ever pass a real address to cmd
        var a = addr.ToString();
        try
        {
            switch (tool)
            {
                case "copy": Clipboard.SetText(a); return;
                case "browser":
                    var host = addr.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? $"[{a}]" : a;
                    Process.Start(new ProcessStartInfo($"http://{host}/") { UseShellExecute = true });
                    return;
            }
            var cmd = tool switch
            {
                "ping-t" => $"ping -t {a}",
                "ping" => $"ping {a}",
                "tracert" => $"tracert -d {a}",
                "pathping" => $"pathping -n {a}",
                _ => null,
            };
            if (cmd == null) return;
            Process.Start(new ProcessStartInfo("cmd.exe", $"/k title {cmd} & {cmd}") { UseShellExecute = true });
        }
        catch { }
    }

    // ---------------- switching ----------------

    async Task ApplyAsync(Profile p, string? configPath = null, bool isRevert = false)
    {
        configPath ??= _cfg.Path;
        if (_busy) return;
        var problem = IpUtil.Validate(p);
        if (problem != null) { Popup(PopupBuilder.Error("Profile has a problem", problem)); return; }

        var before = NetInfo.Snapshot().GetValueOrDefault(p.Adapter);
        _busy = true;
        _icon.Text = "NetSwitch – switching…";
        try
        {
            bool ok; string msg;
            if (Elevation.IsElevated)
            {
                (ok, msg) = await Task.Run(() => NetApply.Apply(p));
            }
            else
            {
                var code = await Elevation.RunElevatedAsync("--apply", p.Name, "--config", configPath);
                if (code == null) return; // UAC cancelled, nothing to say
                ok = code == 0;
                msg = ReadResult(configPath) ?? (ok ? $"Switched to \"{p.Name}\"." : "Switching failed.");
            }
            if (ok)
            {
                // Don't repeat the same news in an "IP changed" pop-up a moment later for a static switch.
                _last = NetInfo.Snapshot();
                var previous = before == null ? null : RememberPrevious(before);
                if (isRevert)
                    Popup(PopupBuilder.Info("Back to previous settings", $"{p.Adapter} is on {NetInfo.Describe(p)} again."));
                else if (previous != null && _cfg.SafeSwitch)
                    StartSafeSwitch(p, previous);
                else
                {
                    var c = PopupBuilder.Applied(p, before, _cfg.PopupStaysOpen);
                    if (previous != null)
                        c = c with { ActionText = "Undo", Action = () => _ = ApplyQuickAsync(Named(previous, "Previous settings"), isRevert: true) };
                    Popup(c);
                }
            }
            else Popup(PopupBuilder.Error("Could not switch profile", msg));
        }
        finally
        {
            _busy = false;
            UpdateTooltip();
        }
    }

    async Task<bool> ToggleStartupAsync(bool enable)
    {
        bool ok; string msg;
        if (Elevation.IsElevated)
        {
            (ok, msg) = enable ? Startup.Install(CurrentUser(), Elevation.ExePath) : Startup.Remove();
        }
        else
        {
            var code = enable
                ? await Elevation.RunElevatedAsync("--startup-on", CurrentUser(), "--config", _cfg.Path)
                : await Elevation.RunElevatedAsync("--startup-off", "--config", _cfg.Path);
            if (code == null) return false;
            ok = code == 0;
            msg = ReadResult() ?? "";
        }
        if (!ok) Popup(PopupBuilder.Error("Could not change the startup setting", msg));
        else if (enable)
            Popup(PopupBuilder.Info("Start with Windows is on",
                "NetSwitch will start when you sign in, with admin rights, so switching profiles won't ask for permission."));
        return ok;
    }

    static string CurrentUser() => $@"{Environment.UserDomainName}\{Environment.UserName}";

    string? ReadResult(string? configPath = null)
    {
        try
        {
            var f = Elevation.ResultFile(configPath ?? _cfg.Path);
            if (!File.Exists(f)) return null;
            var text = File.ReadAllText(f).Trim();
            File.Delete(f);
            return text.Length > 0 ? text : null;
        }
        catch { return null; }
    }

    // ---------------- IP change watching ----------------

    void OnAddressChanged(object? s, EventArgs e) => _ui.Post(_ => { _debounce.Stop(); _debounce.Start(); }, null);
    void OnAvailabilityChanged(object? s, NetworkAvailabilityEventArgs e) => OnAddressChanged(s, e);

    void OnNetworkSettled()
    {
        var now = NetInfo.Snapshot();
        var previousSnap = _last;
        _ = RunAutoRulesAsync(previousSnap, now);
        NetDetails.ForgetPublicIp();
        _ = RefreshTrayStatusAsync();
        _ = CheckAlertsAsync();
        foreach (var name in _cfg.WatchAdapters)
        {
            _last.TryGetValue(name, out var before);
            now.TryGetValue(name, out var after);
            var b = before?.AddressText ?? "not present";
            var a = after?.AddressText ?? "not present";
            if (b != a && _cfg.NotifyOnIpChange)
                Popup(PopupBuilder.Change(name, before, after, _cfg.Profiles, _cfg.PopupStaysOpen));
        }
        _last = now;
        UpdateTooltip();
    }

    void UpdateTooltip()
    {
        var snap = _last;
        var names = _cfg.WatchAdapters.Concat(_cfg.Profiles.Select(p => p.Adapter))
            .Where(n => !string.IsNullOrEmpty(n)).Distinct(StringComparer.OrdinalIgnoreCase);
        var lines = new List<string> { _netStatus == null ? "NetSwitch" : $"NetSwitch · Internet: {_netStatus.Summary}" };
        foreach (var n in names)
            lines.Add(snap.TryGetValue(n, out var a) ? $"{a.Name}: {a.AddressText}" : $"{n}: not found");
        var text = string.Join("\n", lines);
        _icon.Text = text.Length > 127 ? text[..124] + "…" : text;
    }

    // ---------------- uninstall ----------------

    async Task UninstallAsync(bool deleteSettings)
    {
        if (Startup.IsEnabled() && !await ToggleStartupAsync(false)) return; // UAC cancelled: stop here
        try { Installer.ScheduleUninstall(deleteSettings); }
        catch (Exception ex) { Popup(PopupBuilder.Error("Could not uninstall", ex.Message)); return; }
        ExitThread();
    }

    // ---------------- hotkeys ----------------

    readonly Hotkeys _hotkeys;

    void OnHotkey(int digit)
    {
        _cfg = _fixedConfig ?? AppConfig.Load();
        if (digit == 0)
        {
            var last = _cfg.Previous.Where(kv => kv.Value.Settings != null).OrderByDescending(kv => kv.Value.At).FirstOrDefault();
            if (last.Key == null) { Popup(PopupBuilder.Info("Nothing to go back to", "Ctrl+Alt+0 returns to the settings from before your last switch.")); return; }
            _ = RestorePreviousAsync(last.Key);
            return;
        }
        if (digit > _cfg.Profiles.Count) { Popup(PopupBuilder.Info($"No profile {digit}", $"Ctrl+Alt+{digit} switches to profile number {digit} in your list. You have {_cfg.Profiles.Count}.")); return; }
        _ = ApplyAsync(_cfg.Profiles[digit - 1]);
    }

    // ---------------- internet status on the tray icon ----------------

    readonly Icon? _baseIcon;
    Icon? _statusIcon;
    readonly System.Windows.Forms.Timer _statusTimer;
    NetCheckResult? _netStatus;
    bool _statusBusy;

    async Task RefreshTrayStatusAsync()
    {
        if (_statusBusy) return;
        if (!_cfg.TrayStatus) { SetTrayIcon(null); _netStatus = null; UpdateTooltip(); return; }
        _statusBusy = true;
        try
        {
            _netStatus = await Task.Run(NetCheck.RunAsync);
            SetTrayIcon(_netStatus.Health);
            UpdateTooltip();
        }
        catch { }
        finally { _statusBusy = false; }
    }

    /// <summary>Whole tray icon in a status colour: blue = online, amber = DNS not resolving, red = no internet.</summary>
    void SetTrayIcon(NetHealth? health)
    {
        if (_baseIcon == null) return;
        if (health == null) { _icon.Icon = _baseIcon; return; }
        bool dnsFails = _netStatus?.Detail.Contains("DNS fails") == true;
        var color = health switch
        {
            NetHealth.Offline => Color.FromArgb(0xD1, 0x34, 0x38),      // red
            _ when dnsFails => Color.FromArgb(0xE0, 0x9B, 0x1A),         // amber
            _ => Color.FromArgb(0x00, 0x67, 0xC0),                       // blue
        };
        var icon = DrawStatusIcon(color, SystemInformation.SmallIconSize.Width);
        _icon.Icon = icon;
        _statusIcon?.Dispose();
        _statusIcon = icon;
    }

    /// <summary>Rounded tile with two swap arrows, like the app icon, in any colour.</summary>
    internal static Icon DrawStatusIcon(Color color, int size)
    {
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            float s = size;
            using (var path = Theme.RoundRect(new RectangleF(0.5f, 0.5f, s - 1.5f, s - 1.5f), s * 0.22f))
            using (var b = new SolidBrush(color)) g.FillPath(b, path);
            using var pen = new Pen(Color.White, Math.Max(1.5f, s * 0.1f))
            {
                StartCap = System.Drawing.Drawing2D.LineCap.Round,
                EndCap = System.Drawing.Drawing2D.LineCap.Round,
                LineJoin = System.Drawing.Drawing2D.LineJoin.Round,
            };
            float l = s * 0.24f, r = s * 0.76f, h = s * 0.13f;
            float y1 = s * 0.36f, y2 = s * 0.64f;
            // Top arrow pointing right, bottom arrow pointing left.
            g.DrawLine(pen, l, y1, r, y1);
            g.DrawLines(pen, new[] { new PointF(r - h, y1 - h), new PointF(r, y1), new PointF(r - h, y1 + h) });
            g.DrawLine(pen, l, y2, r, y2);
            g.DrawLines(pen, new[] { new PointF(l + h, y2 - h), new PointF(l, y2), new PointF(l + h, y2 + h) });
        }
        var hIcon = bmp.GetHicon();
        var icon = (Icon)Icon.FromHandle(hIcon).Clone();
        DestroyIcon(hIcon);
        return icon;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);

    // ---------------- auto-switch rules ----------------

    readonly Dictionary<string, string?> _lastSsid = new(StringComparer.OrdinalIgnoreCase);
    bool _autoBusy;

    /// <summary>Applies a profile whose rule matches: its Wi-Fi network was just joined, or its adapter just connected.</summary>
    async Task RunAutoRulesAsync(Dictionary<string, AdapterInfo>? before, Dictionary<string, AdapterInfo> now)
    {
        if (_autoBusy || _busy || _fixedConfig != null) return;
        var cfg = AppConfig.Load();
        var rules = cfg.Profiles.Where(p => p.AutoOnConnect || !string.IsNullOrWhiteSpace(p.AutoSsid)).ToList();
        if (rules.Count == 0) return;
        _autoBusy = true;
        try
        {
            foreach (var group in rules.GroupBy(p => p.Adapter, StringComparer.OrdinalIgnoreCase))
            {
                if (!now.TryGetValue(group.Key, out var a) || !a.IsUp) { _lastSsid[group.Key] = null; continue; }
                Profile? pick = null;
                if (group.Any(p => !string.IsNullOrWhiteSpace(p.AutoSsid)))
                {
                    var ssid = await Task.Run(() => NetInfo.WifiSsid(a.Name));
                    _lastSsid.TryGetValue(a.Name, out var was);
                    _lastSsid[a.Name] = ssid;
                    if (ssid != null && !string.Equals(ssid, was, StringComparison.Ordinal))
                        pick = group.FirstOrDefault(p => string.Equals(p.AutoSsid.Trim(), ssid, StringComparison.Ordinal));
                }
                bool justConnected = before == null || !before.TryGetValue(a.Name, out var b) || !b.IsUp;
                if (pick == null && justConnected) pick = group.FirstOrDefault(p => p.AutoOnConnect);
                if (pick == null || NetInfo.Matches(pick, a)) continue;
                Popup(PopupBuilder.Info("Switching automatically",
                    !string.IsNullOrWhiteSpace(pick.AutoSsid) && pick.AutoSsid.Trim() == _lastSsid.GetValueOrDefault(a.Name)
                        ? $"Joined \"{pick.AutoSsid.Trim()}\", so NetSwitch is applying \"{pick.Name}\"."
                        : $"{a.Name} connected, so NetSwitch is applying \"{pick.Name}\"."));
                await ApplyAsync(pick);
            }
        }
        finally { _autoBusy = false; }
    }

    // ---------------- alerts ----------------

    bool _alertBusy;
    readonly HashSet<string> _conflictShown = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>IP conflict, gateway MAC changed, DHCP server changed. Each change alerts once.</summary>
    async Task CheckAlertsAsync()
    {
        if (_alertBusy || _fixedConfig != null) return;
        var cfg = AppConfig.Load();
        if (!cfg.Alerts) return;
        _alertBusy = true;
        try
        {
            var alerts = new List<PopupContent>();
            bool changed = false;
            var adapters = NetInfo.Snapshot().Values.Where(a => a.IsUp && a.Addresses.Count > 0 && NetInfo.LooksPhysical(a) && !cfg.IsHidden(a.Name)).ToList();
            foreach (var a in adapters)
            {
                var (dup, gwMac) = await Task.Run(() => (NetDetails.DuplicateAddress(a.Id), a.Gateways.FirstOrDefault() is { } g ? NetDetails.MacOf(g) : null));
                var name = a.Name;
                if (dup != null)
                {
                    if (_conflictShown.Add($"{name}|{dup}"))
                        alerts.Add(Alert(PopupKind.Error, "IP address conflict",
                            $"Another device on {name} is also using {dup}. One of you needs a different address.",
                            a.Dhcp ? ("Get new IP", () => _ = RunFixAsync("renew", name)) : ("Set static IP…", () => QuickStatic(a)), name));
                }
                else _conflictShown.RemoveWhere(k => k.StartsWith(name + "|", StringComparison.OrdinalIgnoreCase));

                var gw = a.Gateways.FirstOrDefault();
                if (gw == null) continue;
                var key = $"{name}|{gw}";
                if (gwMac != null)
                {
                    // Remember every MAC seen for this gateway, so moving between two sites that both use
                    // 192.168.1.1 alerts only the first time, not on every switch.
                    var known = Known(cfg.GatewayMacs, key);
                    if (!known.Contains(gwMac, StringComparer.OrdinalIgnoreCase))
                    {
                        if (known.Count > 0)
                            alerts.Add(Alert(PopupKind.Info, "Gateway MAC changed",
                                $"{gw} on {name} now answers from {gwMac}; before it was {known[^1]}. Expected after a router swap or on a new network; otherwise someone may be intercepting traffic (ARP spoofing).",
                                null, name));
                        cfg.GatewayMacs[key] = Remember(known, gwMac); changed = true;
                    }
                }
                if (a.Dhcp && a.DhcpServer is { Length: > 0 } srv)
                {
                    var known = Known(cfg.DhcpServers, key);
                    if (!known.Contains(srv))
                    {
                        if (known.Count > 0)
                            alerts.Add(Alert(PopupKind.Info, "New DHCP server",
                                $"{name} got its address from {srv}, not the usual {known[^1]}. If nothing changed on your network, look for a rogue DHCP server.",
                                null, name));
                        cfg.DhcpServers[key] = Remember(known, srv); changed = true;
                    }
                }
            }
            if (changed)
            {
                var fresh = AppConfig.Load(); // save only our two maps, on top of whatever is on disk now
                fresh.GatewayMacs = cfg.GatewayMacs; fresh.DhcpServers = cfg.DhcpServers;
                try { fresh.Save(); } catch { }
            }
            foreach (var c in alerts) Popup(c);
        }
        catch { }
        finally { _alertBusy = false; }
    }

    static List<string> Known(Dictionary<string, string> map, string key) =>
        map.TryGetValue(key, out var v) ? v.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList() : new();

    static string Remember(List<string> known, string value) => string.Join(",", known.Append(value).TakeLast(5));

    PopupContent Alert(PopupKind kind, string title, string message, (string, Action)? action, string adapter)
    {
        var buttons = new List<(string, Action)> { ("Details…", () => OpenDetails(adapter)) };
        return new PopupContent
        {
            Kind = kind, Title = title, Message = message, Sticky = true, HideSettings = true,
            ActionText = action?.Item1, Action = action?.Item2,
            Buttons = buttons,
        };
    }

    protected override void ExitThreadCore()
    {
        _hotkeys.Dispose();
        _statusTimer.Stop();
        NetworkChange.NetworkAddressChanged -= OnAddressChanged;
        NetworkChange.NetworkAvailabilityChanged -= OnAvailabilityChanged;
        _updateTimer.Stop();
        _editor?.Close();
        _details?.Close();
        _monitor?.Close();
        _icon.Visible = false;
        _icon.Dispose();
        base.ExitThreadCore();
    }
}
