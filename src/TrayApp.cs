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
            if (e.Button == MouseButtons.Left) ShowProfileMenu();
            else if (e.Button == MouseButtons.Right) ToggleOverview();
        };
        _menu.Closed += (_, _) => _icon.ContextMenuStrip = null;
        PopupForm.OpenProfilesRequested = OpenEditor;
        PopupForm.ExitRequested = ExitThread;
        PopupForm.ToolRequested = RunTool;

        _last = NetInfo.Snapshot();
        UpdateTooltip();

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

    PopupForm? _overview;
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
                try { await Updater.DownloadAndSwapAsync(r, null); ExitThread(); }
                catch (Exception ex) { Popup(PopupBuilder.Error("Update failed", ex.Message)); }
            },
            Sticky = true,
        });
    }

    void ShowProfileMenu()
    {
        _overview?.Dismiss();
        // NotifyIcon's own ShowContextMenu handles focus so the menu closes properly when clicking away.
        _icon.ContextMenuStrip = _menu;
        typeof(NotifyIcon).GetMethod("ShowContextMenu", BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(_icon, null);
    }

    void ToggleOverview()
    {
        if (_overview is { IsOpen: true }) { _overview.Dismiss(); _overview = null; return; }
        _cfg = _fixedConfig ?? AppConfig.Load();
        var snap = NetInfo.Snapshot();
        var visible = NetInfo.Ordered(snap.Values).Where(a => !_cfg.IsHidden(a.Name));
        _overview = PopupForm.Show(PopupBuilder.Overview(visible, _cfg.Profiles), 30);
    }

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
        _editor.Saved += cfg => { _cfg = cfg; UpdateTooltip(); };
        _editor.TestPopup = c => Popup(c);
        _editor.Show();
        _editor.Activate();
    }

    void Popup(PopupContent c)
    {
        void show() => PopupForm.Show(c, _cfg.PopupSeconds);
        if (_invoker.InvokeRequired) _invoker.BeginInvoke(show); else show();
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

    async Task ApplyAsync(Profile p)
    {
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
                var code = await Elevation.RunElevatedAsync("--apply", p.Name, "--config", _cfg.Path);
                if (code == null) return; // UAC cancelled, nothing to say
                ok = code == 0;
                msg = ReadResult() ?? (ok ? $"Switched to \"{p.Name}\"." : "Switching failed.");
            }
            if (ok)
            {
                // Don't repeat the same news in an "IP changed" pop-up a moment later for a static switch.
                _last = NetInfo.Snapshot();
                Popup(PopupBuilder.Applied(p, before, _cfg.PopupStaysOpen));
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

    string? ReadResult()
    {
        try
        {
            var f = Elevation.ResultFile(_cfg.Path);
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
        var lines = new List<string> { "NetSwitch" };
        foreach (var n in names)
            lines.Add(snap.TryGetValue(n, out var a) ? $"{a.Name}: {a.AddressText}" : $"{n}: not found");
        var text = string.Join("\n", lines);
        _icon.Text = text.Length > 127 ? text[..124] + "…" : text;
    }

    protected override void ExitThreadCore()
    {
        NetworkChange.NetworkAddressChanged -= OnAddressChanged;
        NetworkChange.NetworkAvailabilityChanged -= OnAvailabilityChanged;
        _updateTimer.Stop();
        _editor?.Close();
        _icon.Visible = false;
        _icon.Dispose();
        base.ExitThreadCore();
    }
}
