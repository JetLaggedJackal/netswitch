using System.Drawing.Drawing2D;

namespace NetSwitch;

/// <summary>Settings window: profiles on the left, details on the right, notifications page.</summary>
public sealed class ProfilesForm : Form
{
    public event Action<AppConfig>? Saved;
    public Action<PopupContent>? TestPopup;
    /// <summary>Turns "Start with Windows" on/off right away (may show UAC); returns whether it worked.</summary>
    public Func<bool, Task<bool>>? StartupToggle;
    /// <summary>Quits the whole app (used after an update has been swapped in).</summary>
    public Action? ExitApp;

    readonly Theme _t = Theme.Current;
    readonly AppConfig _cfg;
    readonly List<Profile> _profiles;
    readonly Dictionary<string, AdapterInfo> _snap;

    // sidebar
    readonly ListBox _list = new();
    readonly NavButton _navNotify;
    // pages
    readonly Panel _profilePage = new(), _notifyPage = new(), _emptyPage = new();
    // profile fields
    readonly Label _title, _status;
    readonly ModernTextBox _name, _ip, _mask, _gw, _dns1, _dns2;
    readonly ModernCombo _adapter;
    readonly SegmentedControl _ipMode, _dnsMode;
    readonly Label _ipHint, _dnsHint;
    readonly TableLayoutPanel _ipGrid, _dnsGrid;
    readonly ModernButton _fill;
    // notifications
    readonly ToggleSwitch _notifyOn, _sticky, _startup, _autoUpdate;
    readonly ModernButton _updateBtn;
    readonly Label _updateStatus;
    Updater.Release? _available;
    bool _startupBusy;
    readonly List<AdapterRow> _watchRows = new();
    // footer
    readonly Label _footerMsg;

    Profile? _current;
    bool _loading;

    public ProfilesForm(AppConfig cfg, Icon icon, bool startupEnabled = false)
    {
        _cfg = cfg;
        _profiles = cfg.Profiles.Select(p => p.Clone()).ToList();
        _snap = NetInfo.Snapshot();

        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "NetSwitch";
        Icon = icon;
        Font = Theme.UiFont(9f);
        BackColor = _t.Window;
        ForeColor = _t.Text;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(820, 600);
        MinimumSize = new Size(700, 520);

        // ---------- footer ----------
        var footer = new Panel { Dock = DockStyle.Bottom, Height = 52, BackColor = _t.Window, Padding = new Padding(20, 11, 20, 11) };
        footer.Paint += (_, e) => { using var p = new Pen(_t.Border); e.Graphics.DrawLine(p, 0, 0, footer.Width, 0); };
        var save = new ModernButton(_t, "Save", primary: true) { Width = 90, Margin = new Padding(8, 0, 0, 0) };
        var cancel = new ModernButton(_t, "Cancel") { Width = 90, Margin = new Padding(8, 0, 0, 0) };
        save.Click += (_, _) => SaveAndClose();
        cancel.Click += (_, _) => Close();
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Right, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, WrapContents = false, BackColor = _t.Window };
        buttons.Controls.Add(save);
        buttons.Controls.Add(cancel);
        _footerMsg = Ui.Label(_t, "", 9f, sub: true);
        _footerMsg.AutoSize = false; _footerMsg.Dock = DockStyle.Fill; _footerMsg.TextAlign = ContentAlignment.MiddleLeft;
        footer.Controls.Add(_footerMsg);
        footer.Controls.Add(buttons);
        AcceptButton = save; CancelButton = cancel;

        // ---------- sidebar ----------
        var sidebar = new Panel { Dock = DockStyle.Left, Width = 220, BackColor = _t.Sidebar, Padding = new Padding(8, 10, 8, 8) };

        var profilesCaption = Ui.Label(_t, "PROFILES", 8.25f, FontStyle.Bold, sub: true);
        profilesCaption.AutoSize = false; profilesCaption.Dock = DockStyle.Top; profilesCaption.Height = 26;
        profilesCaption.Padding = new Padding(8, 6, 0, 0);

        _list.Dock = DockStyle.Fill;
        _list.BorderStyle = BorderStyle.None;
        _list.BackColor = _t.Sidebar;
        _list.DrawMode = DrawMode.OwnerDrawFixed;
        _list.IntegralHeight = false;
        _list.ItemHeight = 46;
        _list.DrawItem += DrawProfileItem;
        _list.SelectedIndexChanged += (_, _) => { if (!_loading) ShowProfile(_list.SelectedItem as Profile); };
        _list.MouseUp += (_, _) => { if (_list.SelectedItem is Profile p) ShowProfile(p); };

        var add = new ModernButton(_t, "+  New profile") { Ghost = true, Dock = DockStyle.Bottom, Height = 30 };
        add.Click += (_, _) => AddProfile();

        _navNotify = new NavButton(_t, "\uE713", "Settings") { Dock = DockStyle.Bottom, Height = 34 };
        _navNotify.Click += (_, _) => ShowNotifications();
        var sep = new Panel { Dock = DockStyle.Bottom, Height = 13, BackColor = _t.Sidebar };
        sep.Paint += (_, e) => { using var p = new Pen(_t.Border); e.Graphics.DrawLine(p, 6, 6, sep.Width - 6, 6); };

        sidebar.Controls.Add(_list);
        sidebar.Controls.Add(profilesCaption);
        sidebar.Controls.Add(add);
        sidebar.Controls.Add(sep);
        sidebar.Controls.Add(_navNotify);
        // order bottom items: add above separator above nav
        sidebar.Controls.SetChildIndex(_navNotify, sidebar.Controls.Count - 1);
        sidebar.Controls.SetChildIndex(sep, sidebar.Controls.Count - 2);
        sidebar.Controls.SetChildIndex(add, sidebar.Controls.Count - 3);
        _list.BringToFront();

        // ---------- profile page ----------
        _title = Ui.Label(_t, "", 14f, FontStyle.Bold);
        _status = Ui.Label(_t, "", 8.75f, sub: true);
        var dup = new ModernButton(_t, "Duplicate") { Ghost = true, Width = 96 };
        var del = new ModernButton(_t, "Delete") { Ghost = true, Width = 86 };
        dup.Click += (_, _) => DuplicateProfile();
        del.Click += (_, _) => RemoveProfile();

        var header = new TableLayoutPanel { ColumnCount = 3, RowCount = 2, Dock = DockStyle.Top, AutoSize = true, Margin = new Padding(0, 0, 0, 12), BackColor = _t.Window };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.Controls.Add(_title, 0, 0);
        header.Controls.Add(dup, 1, 0);
        header.Controls.Add(del, 2, 0);
        header.Controls.Add(_status, 0, 1);
        header.SetColumnSpan(_status, 3);
        _title.Margin = new Padding(0, 0, 0, 2);

        _name = new ModernTextBox(_t);
        _adapter = new ModernCombo(_t) { Describe = o => (o.ToString()!, _snap.TryGetValue(o.ToString()!, out var a) ? a.AddressText : "not found") };
        var general = CardWith("General", Grid(2, Ui.Field(_t, "Profile name", _name), Ui.Field(_t, "Network adapter", _adapter)));

        _ipMode = new SegmentedControl(_t, "Automatic (DHCP)", "Manual") { Margin = new Padding(0, 0, 0, 8) };
        _ipHint = Ui.Label(_t, "Your router assigns the IP address, subnet mask and gateway.", 8.5f, sub: true);
        _ipHint.Margin = new Padding(1, 0, 0, 4);
        _ip = new ModernTextBox(_t) { PlaceholderText = "192.168.1.50 or 192.168.1.50/24" };
        _mask = new ModernTextBox(_t) { PlaceholderText = "255.255.255.0 or 24" };
        _gw = new ModernTextBox(_t) { PlaceholderText = "Optional" };
        _fill = new ModernButton(_t, "Fill in from adapter") { Ghost = true, Anchor = AnchorStyles.Left | AnchorStyles.Bottom, Width = 170, Margin = new Padding(0, 0, 0, 10) };
        _fill.Click += (_, _) => FillFromAdapter();
        _ipGrid = Grid(2, Ui.Field(_t, "IP address", _ip), Ui.Field(_t, "Subnet mask", _mask), Ui.Field(_t, "Default gateway", _gw), _fill);
        var ipCard = CardWith("IP settings", _ipMode, _ipHint, _ipGrid);

        _dnsMode = new SegmentedControl(_t, "Automatic", "Manual") { Margin = new Padding(0, 0, 0, 8) };
        _dnsHint = Ui.Label(_t, "", 8.5f, sub: true);
        _dnsHint.Margin = new Padding(1, 0, 0, 4);
        _dns1 = new ModernTextBox(_t) { PlaceholderText = "e.g. 1.1.1.1" };
        _dns2 = new ModernTextBox(_t) { PlaceholderText = "Optional" };
        _dnsGrid = Grid(2, Ui.Field(_t, "Preferred DNS", _dns1), Ui.Field(_t, "Alternate DNS", _dns2));
        var dnsCard = CardWith("DNS servers", _dnsMode, _dnsHint, _dnsGrid);

        BuildPage(_profilePage, header, general, ipCard, dnsCard);

        _name.TextChanged += (_, _) => Edit(p => p.Name = _name.Text.Trim(), refresh: true);
        _adapter.SelectedIndexChanged += (_, _) => Edit(p => p.Adapter = _adapter.SelectedItem?.ToString() ?? "", refresh: true);
        _ipMode.SelectedIndexChanged += (_, _) => { Edit(p => p.Dhcp = _ipMode.SelectedIndex == 0, refresh: true); UpdateVisibility(); };
        _dnsMode.SelectedIndexChanged += (_, _) => { Edit(p => p.DnsAuto = _dnsMode.SelectedIndex == 0, refresh: true); UpdateVisibility(); };
        _ip.TextChanged += (_, _) => Edit(p => p.Ip = _ip.Text.Trim(), refresh: true);
        // "192.168.1.50/24" in the IP box: keep the address, put 255.255.255.0 in the mask box.
        _ip.Leave += (_, _) =>
        {
            var (ip, mask) = IpUtil.SplitIp(_ip.Text);
            if (mask == null || !IpUtil.IsIPv4(ip)) return;
            _ip.Text = ip;
            _mask.Text = mask;
        };
        // "24" or "/24" in the mask box becomes 255.255.255.0.
        _mask.Leave += (_, _) =>
        {
            var t = _mask.Text.Trim();
            if (t.Length == 0 || IpUtil.IsIPv4(t)) return;
            if (IpUtil.NormalizeMask(t) is { } dotted) _mask.Text = dotted;
        };
        _mask.TextChanged += (_, _) => Edit(p => p.Mask = _mask.Text.Trim());
        _gw.TextChanged += (_, _) => Edit(p => p.Gateway = _gw.Text.Trim());
        _dns1.TextChanged += (_, _) => Edit(SetDns);
        _dns2.TextChanged += (_, _) => Edit(SetDns);

        // ---------- settings page ----------
        var nTitle = Ui.Label(_t, "Settings", 14f, FontStyle.Bold);
        var nSub = Ui.Label(_t, "Startup, pop-ups, and which adapters NetSwitch shows and watches.", 8.75f, sub: true);
        nSub.Margin = new Padding(0, 2, 0, 12);

        _startup = new ToggleSwitch(_t, "Start with Windows") { Width = 420, Checked = startupEnabled, Margin = new Padding(0, 0, 0, 2) };
        var startupHint = Ui.Label(_t, "Starts at sign-in as admin, so switching never asks.", 8.5f, sub: true);
        startupHint.MaximumSize = new Size(560, 0);
        startupHint.Margin = new Padding(52, 0, 0, 4);
        _startup.CheckedChanged += async (_, _) => await OnStartupToggled();
        var startupCard = CardWith("Startup", _startup, startupHint);

        _notifyOn = new ToggleSwitch(_t, "Pop up when an IP address changes") { Width = 420, Checked = cfg.NotifyOnIpChange, Margin = new Padding(0, 0, 0, 2) };
        _sticky = new ToggleSwitch(_t, "Keep pop-ups open until I close them") { Width = 420, Checked = cfg.PopupStaysOpen, Margin = new Padding(0, 0, 0, 2) };
        var test = new ModernButton(_t, "Show a test pop-up") { Width = 150, Margin = new Padding(0, 6, 0, 0) };
        test.Click += (_, _) => ShowTestPopup();
        var behaviour = CardWith("Pop-ups", _notifyOn, _sticky, test);

        var rows = new List<Control> { new AdapterHeader(_t) { Dock = DockStyle.Top } };
        var known = NetInfo.Ordered(_snap.Values).Select(a => a.Name)
            .Concat(cfg.WatchAdapters).Concat(cfg.HiddenAdapters).Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var n in known)
        {
            _snap.TryGetValue(n, out var a);
            var detail = a == null ? "Not present right now" : $"{a.AddressText} · {a.Description}";
            var row = new AdapterRow(_t, n, detail, showInMenu: !cfg.IsHidden(n), watched: cfg.IsWatched(n))
            { Dock = DockStyle.Top, Margin = new Padding(0) };
            _watchRows.Add(row);
            rows.Add(row);
        }
        var watch = CardWith("Adapters", rows.ToArray());
        var version = Ui.Label(_t, $"NetSwitch {Updater.CurrentText}", 9f);
        version.Margin = new Padding(0, 0, 0, 6);
        _updateBtn = new ModernButton(_t, "Check for updates") { Width = 170, Margin = new Padding(0, 0, 12, 0) };
        _updateBtn.Click += async (_, _) => await OnUpdateClicked();
        _updateStatus = Ui.Label(_t, "", 8.5f, sub: true);
        _updateStatus.Anchor = AnchorStyles.Left;
        _updateStatus.Margin = new Padding(0, 7, 0, 0);
        var updateRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, BackColor = _t.Panel, Margin = new Padding(0, 0, 0, 6) };
        updateRow.Controls.Add(_updateBtn);
        updateRow.Controls.Add(_updateStatus);
        _autoUpdate = new ToggleSwitch(_t, "Check for updates once a day") { Width = 420, Checked = cfg.AutoCheckUpdates, Margin = new Padding(0, 0, 0, 2) };
        var updatesCard = CardWith("Updates", version, updateRow, _autoUpdate);

        BuildPage(_notifyPage, nTitle, nSub, startupCard, behaviour, watch, updatesCard);

        // ---------- empty state ----------
        var emptyTitle = Ui.Label(_t, "No profiles yet", 14f, FontStyle.Bold);
        var emptySub = Ui.Label(_t, "Create one for each way you connect, for example \"Office (fixed IP)\" and \"Home (DHCP)\".", 9.5f, sub: true);
        emptySub.Margin = new Padding(0, 4, 0, 16);
        var emptyAdd = new ModernButton(_t, "+  New profile", primary: true) { Width = 150 };
        emptyAdd.Click += (_, _) => AddProfile();
        BuildPage(_emptyPage, emptyTitle, emptySub, emptyAdd);

        // ---------- assemble ----------
        var content = new Panel { Dock = DockStyle.Fill, BackColor = _t.Window };
        content.Controls.Add(_profilePage);
        content.Controls.Add(_notifyPage);
        content.Controls.Add(_emptyPage);
        Controls.Add(content);
        Controls.Add(sidebar);
        Controls.Add(footer);
        ResumeLayout(true);

        foreach (var p in _profiles) _list.Items.Add(p);
        if (_list.Items.Count > 0) _list.SelectedIndex = 0; else ShowProfile(null);
        ActiveControl = _name;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        _t.ApplyWindowChrome(this);
        _t.ApplyControlTheme(_list);
        // Scrollable pages get dark scroll bars in dark mode.
        foreach (var page in new[] { _profilePage, _notifyPage, _emptyPage }) _t.ApplyControlTheme(page);
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        _list.ItemHeight = LogicalToDeviceUnits(46);
    }

    // ---------- building blocks ----------

    Card CardWith(string title, params Control[] children)
    {
        var card = new Card(_t) { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(0, 0, 0, 16) };
        var stack = new TableLayoutPanel { ColumnCount = 1, Dock = DockStyle.Top, AutoSize = true, BackColor = _t.Panel };
        var h = Ui.Label(_t, title.ToUpperInvariant(), 8.25f, FontStyle.Bold, sub: true);
        h.Margin = new Padding(0, 0, 0, 6);
        stack.Controls.Add(h);
        foreach (var c in children)
        {
            if (c is TableLayoutPanel or AdapterRow or AdapterHeader) c.Dock = DockStyle.Fill;
            stack.Controls.Add(c);
        }
        stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        card.Controls.Add(stack);
        return card;
    }

    TableLayoutPanel Grid(int cols, params Control[] cells)
    {
        var g = new TableLayoutPanel { ColumnCount = cols, AutoSize = true, Dock = DockStyle.Top, BackColor = _t.Panel, Margin = new Padding(0) };
        for (int i = 0; i < cols; i++) g.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / cols));
        foreach (var c in cells) g.Controls.Add(c);
        return g;
    }

    void BuildPage(Panel page, params Control[] blocks)
    {
        page.Dock = DockStyle.Fill;
        page.AutoScroll = true;
        page.BackColor = _t.Window;
        page.Padding = new Padding(24, 14, 20, 8);
        var stack = new TableLayoutPanel { ColumnCount = 1, Dock = DockStyle.Top, AutoSize = true, BackColor = _t.Window };
        stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (var b in blocks)
        {
            if (b is Card or TableLayoutPanel) b.Dock = DockStyle.Fill;
            stack.Controls.Add(b);
        }
        page.Controls.Add(stack);
        page.Visible = false;
    }

    void ShowPage(Panel page)
    {
        foreach (var p in new[] { _profilePage, _notifyPage, _emptyPage }) p.Visible = p == page;
        _navNotify.Selected = page == _notifyPage;
        _list.Invalidate();
    }

    // ---------- profile list ----------

    void DrawProfileItem(object? sender, DrawItemEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var bg = new SolidBrush(_t.Sidebar)) g.FillRectangle(bg, e.Bounds);
        if (e.Index < 0 || e.Index >= _list.Items.Count) return;
        var p = (Profile)_list.Items[e.Index];
        bool sel = p == _current && _profilePage.Visible;

        var r = new Rectangle(e.Bounds.X + 2, e.Bounds.Y + 3, e.Bounds.Width - 4, e.Bounds.Height - 6);
        if (sel)
        {
            using var path = Theme.RoundRect(r, 6);
            using var b = new SolidBrush(_t.Dark ? _t.SurfaceHover : _t.Surface);
            g.FillPath(b, path);
            using var bar = new SolidBrush(_t.Accent);
            int bh = r.Height / 2;
            using var barPath = Theme.RoundRect(new RectangleF(r.X, r.Y + (r.Height - bh) / 2f, LogicalToDeviceUnits(3), bh), 1.5f);
            g.FillPath(bar, barPath);
        }

        _snap.TryGetValue(p.Adapter, out var a);
        bool active = NetInfo.Matches(p, a);
        int pad = LogicalToDeviceUnits(14);
        using var bold = Theme.UiFont(9f, FontStyle.Bold);
        using var small = Theme.UiFont(8.25f);
        int lineH = TextRenderer.MeasureText("Ag", bold).Height;
        int top = r.Y + (r.Height - lineH * 2) / 2;
        int textW = r.Width - pad * 2 - (active ? LogicalToDeviceUnits(54) : 0);
        TextRenderer.DrawText(g, p.ToString(), bold, new Rectangle(r.X + pad, top, textW, lineH), _t.Text, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        TextRenderer.DrawText(g, p.Summary, small, new Rectangle(r.X + pad, top + lineH, r.Width - pad * 2, lineH), _t.SubText, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);

        if (active)
        {
            var text = "Active";
            var ts = TextRenderer.MeasureText(text, small);
            var pill = new Rectangle(r.Right - pad - ts.Width - LogicalToDeviceUnits(6), top, ts.Width + LogicalToDeviceUnits(6), lineH);
            using var pb = new SolidBrush(Theme.Blend(_t.Success, _t.Sidebar, 0.18f));
            using var pp = Theme.RoundRect(pill, pill.Height / 2f);
            g.FillPath(pb, pp);
            TextRenderer.DrawText(g, text, small, pill, _t.Success, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
    }

    void RefreshListItem(Profile p)
    {
        var i = _list.Items.IndexOf(p);
        if (i < 0) return;
        _loading = true;
        _list.Items[i] = p;
        _loading = false;
        _list.Invalidate();
    }

    // ---------- editing ----------

    void ShowProfile(Profile? p)
    {
        _current = p;
        if (p == null)
        {
            ShowPage(_profiles.Count == 0 ? _emptyPage : _profilePage);
            return;
        }
        _loading = true;
        _adapter.Items.Clear();
        foreach (var n in NetInfo.Ordered(_snap.Values).Select(a => a.Name)) _adapter.Items.Add(n);
        if (!string.IsNullOrEmpty(p.Adapter) && !_adapter.Items.Contains(p.Adapter)) _adapter.Items.Add(p.Adapter);
        _adapter.SelectedItem = p.Adapter;

        _name.Text = p.Name;
        _ipMode.SelectedIndex = p.Dhcp ? 0 : 1;
        _dnsMode.SelectedIndex = p.UsesAutoDns ? 0 : 1;
        _ip.Text = p.Ip;
        _mask.Text = p.Mask;
        _gw.Text = p.Gateway;
        _dns1.Text = p.Dns.ElementAtOrDefault(0) ?? "";
        _dns2.Text = p.Dns.ElementAtOrDefault(1) ?? "";
        _loading = false;

        UpdateHeader();
        UpdateVisibility();
        ShowPage(_profilePage);
    }

    void Edit(Action<Profile> change, bool refresh = false)
    {
        if (_loading || _current == null) return;
        change(_current);
        _footerMsg.Text = "";
        if (refresh) { RefreshListItem(_current); UpdateHeader(); }
    }

    void SetDns(Profile p) => p.Dns = new[] { _dns1.Text.Trim(), _dns2.Text.Trim() }.Where(s => s.Length > 0).ToList();

    void UpdateHeader()
    {
        if (_current == null) return;
        _title.Text = string.IsNullOrWhiteSpace(_current.Name) ? "Untitled profile" : _current.Name;
        if (_snap.TryGetValue(_current.Adapter, out var a))
        {
            var state = a.IsUp ? $"connected · {a.AddressText} ({a.ModeText})" : "disconnected";
            _status.Text = $"{a.Name} is {state}" + (NetInfo.Matches(_current, a) ? " · this profile is active" : "");
        }
        else _status.Text = string.IsNullOrEmpty(_current.Adapter) ? "Choose a network adapter" : $"{_current.Adapter} is not present right now";
    }

    void UpdateVisibility()
    {
        bool manualIp = _ipMode.SelectedIndex == 1;
        _ipGrid.Visible = manualIp;
        _ipHint.Visible = !manualIp;

        if (manualIp)
        {
            // Windows requires manual DNS when the IP is manual.
            if (_dnsMode.SelectedIndex != 1) { var was = _loading; _loading = true; _dnsMode.SelectedIndex = 1; _loading = was; }
            _dnsMode.Enabled = false;
            _dnsHint.Text = "With a manual IP, DNS servers are set by hand too. Leave both empty for none.";
            _dnsHint.Visible = true;
            _dnsGrid.Visible = true;
        }
        else
        {
            _dnsMode.Enabled = true;
            bool manualDns = _dnsMode.SelectedIndex == 1;
            _dnsGrid.Visible = manualDns;
            _dnsHint.Text = "DNS servers also come from your router.";
            _dnsHint.Visible = !manualDns;
        }
    }

    void AddProfile()
    {
        var adapter = _current?.Adapter ?? NetInfo.Primary(_snap.Values)?.Name ?? "";
        var p = new Profile { Name = UniqueName("New profile"), Adapter = adapter, Dhcp = false };
        AddAndSelect(p);
        _name.Focus();
        _name.SelectAll();
    }

    void DuplicateProfile()
    {
        if (_current == null) return;
        var p = _current.Clone();
        p.Name = UniqueName(_current.Name + " copy");
        AddAndSelect(p);
    }

    void AddAndSelect(Profile p)
    {
        _profiles.Add(p);
        _list.Items.Add(p);
        _list.SelectedItem = p;
        ShowProfile(p);
    }

    string UniqueName(string baseName)
    {
        var name = baseName;
        for (int i = 2; _profiles.Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)); i++) name = $"{baseName} {i}";
        return name;
    }

    void RemoveProfile()
    {
        if (_current == null) return;
        var i = _profiles.IndexOf(_current);
        _profiles.Remove(_current);
        _list.Items.Remove(_current);
        _current = null;
        if (_list.Items.Count > 0)
        {
            _list.SelectedIndex = Math.Min(i, _list.Items.Count - 1);
            ShowProfile(_list.SelectedItem as Profile);
        }
        else ShowProfile(null);
    }

    void FillFromAdapter()
    {
        if (_current == null || !_snap.TryGetValue(_current.Adapter, out var a) || a.Addresses.Count == 0)
        {
            _footerMsg.ForeColor = _t.SubText;
            _footerMsg.Text = "That adapter has no IPv4 address right now.";
            return;
        }
        var addr = a.Addresses[0];
        _ip.Text = addr.Ip;
        _mask.Text = string.IsNullOrEmpty(addr.Mask) ? "255.255.255.0" : addr.Mask;
        _gw.Text = a.Gateways.FirstOrDefault() ?? "";
        _dns1.Text = a.Dns.ElementAtOrDefault(0) ?? "";
        _dns2.Text = a.Dns.ElementAtOrDefault(1) ?? "";
    }

    async Task OnUpdateClicked()
    {
        _updateBtn.Enabled = false;
        _updateStatus.ForeColor = _t.SubText;
        try
        {
            if (_available == null)
            {
                _updateStatus.Text = "Checking…";
                var r = await Updater.GetLatestAsync();
                if (r == null) { _updateStatus.Text = "No releases published yet."; return; }
                if (!Updater.IsNewer(r)) { _updateStatus.Text = $"You're up to date ({Updater.CurrentText})."; return; }
                _available = r;
                _updateBtn.Primary = true;
                _updateBtn.Text = $"Update to {r.Version}";
                _updateStatus.Text = $"{r.Size / 1048576.0:0} MB download";
                return;
            }
            var progress = new Progress<int>(p => _updateStatus.Text = $"Downloading… {p}%");
            await Updater.DownloadAndSwapAsync(_available, progress);
            _updateStatus.Text = "Restarting…";
            ExitApp?.Invoke();
        }
        catch (Exception ex)
        {
            _updateStatus.ForeColor = _t.Danger;
            _updateStatus.Text = "Update failed: " + ex.Message;
        }
        finally
        {
            if (!IsDisposed) _updateBtn.Enabled = true;
        }
    }

    public void ShowSettings() => ShowPage(_notifyPage);

    async Task OnStartupToggled()
    {
        if (_startupBusy || StartupToggle == null) return;
        _startupBusy = true;
        var want = _startup.Checked;
        bool ok = false;
        try { ok = await StartupToggle(want); }
        catch { }
        if (!ok) _startup.Checked = !want; // UAC cancelled or failed: put the switch back
        _startupBusy = false;
    }

    void ShowNotifications()
    {
        ShowPage(_notifyPage);
    }

    void ShowTestPopup()
    {
        var a = NetInfo.Primary(_snap.Values);
        if (a == null) return;
        var before = a with { Addresses = new List<(string, string)> { ("192.0.2.10", "255.255.255.0") } };
        TestPopup?.Invoke(PopupBuilder.Change(a.Name, before, a, _profiles, _sticky.Checked));
    }

    void SaveAndClose()
    {
        foreach (var p in _profiles)
        {
            var problem = IpUtil.Validate(p);
            if (problem != null) { Fail(problem, p); return; }
        }
        var dup = _profiles.GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (dup != null) { Fail($"Two profiles are named \"{dup.Key}\". Names must be unique.", dup.Last()); return; }

        _cfg.Profiles = _profiles.Select(p => p.Clone()).ToList();
        _cfg.WatchAdapters = _watchRows.Where(r => r.Watched).Select(r => r.AdapterName).ToList();
        _cfg.HiddenAdapters = _watchRows.Where(r => !r.ShowInMenu).Select(r => r.AdapterName).ToList();
        _cfg.NotifyOnIpChange = _notifyOn.Checked;
        _cfg.PopupStaysOpen = _sticky.Checked;
        _cfg.AutoCheckUpdates = _autoUpdate.Checked;
        try { _cfg.Save(); }
        catch (Exception ex) { Fail("Could not save: " + ex.Message, null); return; }
        Saved?.Invoke(_cfg);
        Close();
    }

    void Fail(string message, Profile? p)
    {
        if (p != null) { _list.SelectedItem = p; ShowProfile(p); }
        _footerMsg.ForeColor = _t.Danger;
        _footerMsg.Text = message;
    }
}

/// <summary>Sidebar navigation entry with an icon glyph.</summary>
public sealed class NavButton : Control
{
    readonly Theme _t;
    readonly string _glyph;
    bool _hover, _selected;
    static readonly Font? IconFont = LoadIconFont();
    public NavButton(Theme t, string glyph, string text)
    {
        _t = t; _glyph = glyph; Text = text;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Cursor = Cursors.Hand;
        BackColor = t.Sidebar;
    }
    public bool Selected { get => _selected; set { _selected = value; Invalidate(); } }
    static Font? LoadIconFont()
    {
        foreach (var n in new[] { "Segoe Fluent Icons", "Segoe MDL2 Assets" })
        {
            using var f = new Font(n, 11f);
            if (f.Name == n) return new Font(n, 11f);
        }
        return null;
    }
    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(_t.Sidebar);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new Rectangle(2, 2, Width - 4, Height - 4);
        if (_selected || _hover)
        {
            using var path = Theme.RoundRect(r, 6);
            using var b = new SolidBrush(_selected ? (_t.Dark ? _t.SurfaceHover : _t.Surface) : Theme.Blend(_t.Surface, _t.Sidebar, 0.6f));
            g.FillPath(b, path);
        }
        if (_selected)
        {
            int bh = r.Height / 2;
            using var bar = new SolidBrush(_t.Accent);
            using var bp = Theme.RoundRect(new RectangleF(r.X, r.Y + (r.Height - bh) / 2f, LogicalToDeviceUnits(3), bh), 1.5f);
            g.FillPath(bar, bp);
        }
        int x = r.X + LogicalToDeviceUnits(14);
        if (IconFont != null)
        {
            TextRenderer.DrawText(g, _glyph, IconFont, new Rectangle(x, r.Y, LogicalToDeviceUnits(22), r.Height), _t.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            x += LogicalToDeviceUnits(28);
        }
        TextRenderer.DrawText(g, Text, Font, new Rectangle(x, r.Y, r.Right - x, r.Height), _t.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }
}

static class AdapterColumns
{
    public const int Width = 120; // logical px per toggle column
}

/// <summary>Column captions above the adapter rows.</summary>
public sealed class AdapterHeader : Control
{
    readonly Theme _t;
    public AdapterHeader(Theme t)
    {
        _t = t;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = t.Panel;
        Height = 22;
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(_t.Panel);
        using var f = Theme.UiFont(8.25f);
        int col = LogicalToDeviceUnits(AdapterColumns.Width);
        var flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter;
        TextRenderer.DrawText(g, "Show in list", f, new Rectangle(Width - col * 2, 0, col, Height), _t.SubText, flags);
        TextRenderer.DrawText(g, "Pop up on change", f, new Rectangle(Width - col, 0, col, Height), _t.SubText, flags);
        TextRenderer.DrawText(g, "Adapter", f, new Rectangle(0, 0, Width - col * 2, Height), _t.SubText, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPadding);
    }
}

/// <summary>Adapter name + details, with "show in menu" and "pop up on change" switches.</summary>
public sealed class AdapterRow : Control
{
    readonly Theme _t;
    readonly ToggleSwitch _menu, _watch;
    readonly string _detail;
    public string AdapterName { get; }
    public bool ShowInMenu => _menu.Checked;
    public bool Watched => _watch.Checked;
    public AdapterRow(Theme t, string name, string detail, bool showInMenu, bool watched)
    {
        _t = t; AdapterName = name; _detail = detail;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = t.Panel;
        Height = 42;
        _menu = new ToggleSwitch(_t, "") { Checked = showInMenu, BackColor = _t.Panel };
        _watch = new ToggleSwitch(_t, "") { Checked = watched, BackColor = _t.Panel };
        Controls.Add(_menu);
        Controls.Add(_watch);
    }
    int ColWidth => LogicalToDeviceUnits(AdapterColumns.Width);

    // Place the switches from the current size and DPI on every layout pass, so DPI scaling
    // can't push them out of their columns.
    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        if (_menu == null || _watch == null) return; // layout runs during construction
        int col = ColWidth;
        var size = new Size(LogicalToDeviceUnits(44), LogicalToDeviceUnits(26));
        int y = (Height - size.Height) / 2;
        _menu.Bounds = new Rectangle(Width - col * 2 + (col - size.Width) / 2, y, size.Width, size.Height);
        _watch.Bounds = new Rectangle(Width - col + (col - size.Width) / 2, y, size.Width, size.Height);
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(_t.Panel);
        using var bold = Theme.UiFont(9f, FontStyle.Bold);
        using var small = Theme.UiFont(8.25f);
        int lh = TextRenderer.MeasureText("Ag", bold).Height;
        int top = (Height - lh * 2) / 2;
        int w = Width - ColWidth * 2 - LogicalToDeviceUnits(8);
        TextRenderer.DrawText(g, AdapterName, bold, new Rectangle(0, top, w, lh), _t.Text, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        TextRenderer.DrawText(g, _detail, small, new Rectangle(0, top + lh, w, lh), _t.SubText, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        using var p = new Pen(_t.Border);
        g.DrawLine(p, 0, 0, Width, 0);
    }
}
