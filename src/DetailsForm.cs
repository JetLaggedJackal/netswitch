using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Text;

namespace NetSwitch;

/// <summary>Everything about one adapter in one window: Wi-Fi link, addressing and DHCP, DNS with a lookup box, and this PC.</summary>
public sealed class DetailsForm : Form
{
    sealed record Row(string Label, string Value, Action? Link = null);
    sealed record Section(string Title, List<Row> Rows);

    readonly Theme _t = Theme.Current;
    readonly string _adapter;
    readonly Action<string, string> _tool;
    readonly Font _fTitle, _fCard, _fBody, _fSmall;
    readonly ModernTextBox _lookup;
    readonly ModernButton _lookupBtn, _copyAll, _refresh;
    List<Section> _sections = new();
    readonly List<(Rectangle Rect, Row Row)> _hits = new();
    int _hover = -1;
    string? _copied;
    string _lookupResult = "";
    bool _lookupOk = true, _loading = true;
    Rectangle _dnsCard;
    string? _dnsServer;
    // live traffic (refreshed every second)
    readonly System.Windows.Forms.Timer _trafficTimer = new() { Interval = 1000 };
    (long Rx, long Tx, DateTime At)? _lastBytes;
    string _speedNow = "measuring…", _total = "";

    float S => DeviceDpi / 96f;
    int Px(float v) => (int)Math.Round(v * S);

    public DetailsForm(string adapter, Action<string, string> runTool)
    {
        _adapter = adapter;
        _tool = runTool;
        AutoScaleMode = AutoScaleMode.None;
        Text = $"NetSwitch · {adapter} details";
        BackColor = _t.Window;
        ForeColor = _t.Text;
        DoubleBuffered = true;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = true;
        using (var s = typeof(DetailsForm).Assembly.GetManifestResourceStream("NetSwitch.app.ico")) if (s != null) Icon = new Icon(s);
        _fTitle = Theme.UiFont(13f, FontStyle.Bold);
        _fCard = Theme.UiFont(10.5f, FontStyle.Bold);
        _fBody = Theme.UiFont(9.25f);
        _fSmall = Theme.UiFont(8.75f);
        Font = _fBody;

        _lookup = new ModernTextBox(_t) { Text = "", PlaceholderText = "Name or IP to look up, e.g. example.com", Font = _fBody };
        _lookupBtn = new ModernButton(_t, "Look up", primary: true) { Font = _fBody };
        _lookupBtn.Click += async (_, _) => await DoLookup();
        _copyAll = new ModernButton(_t, "Copy all") { Font = _fBody };
        _copyAll.Click += (_, _) => { try { Clipboard.SetText(AllText()); _copyAll.Text = "Copied"; } catch { } };
        _refresh = new ModernButton(_t, "Refresh") { Font = _fBody };
        _refresh.Click += async (_, _) => await Reload();
        Controls.AddRange(new Control[] { _lookup, _lookupBtn, _copyAll, _refresh });
        AcceptButton = _lookupBtn;
        _trafficTimer.Tick += (_, _) => UpdateTraffic();
    }

    void UpdateTraffic()
    {
        if (!NetCheck.ByteCountersSplit().TryGetValue(_adapter, out var now)) return;
        var t = DateTime.UtcNow;
        if (_lastBytes is { } b && (t - b.At).TotalSeconds > 0.2)
        {
            double secs = (t - b.At).TotalSeconds;
            _speedNow = $"↓ {NetDetails.FormatRate(Math.Max(0, now.Rx - b.Rx) / secs)}   ↑ {NetDetails.FormatRate(Math.Max(0, now.Tx - b.Tx) / secs)}";
        }
        _total = $"{Bytes(now.Rx)} received · {Bytes(now.Tx)} sent";
        _lastBytes = (now.Rx, now.Tx, t);
        Invalidate();
    }

    static string Bytes(long b) => b >= 1L << 30 ? $"{b / (double)(1L << 30):0.0} GB" : b >= 1L << 20 ? $"{b / (double)(1L << 20):0.0} MB" : $"{b / 1024.0:0} KB";

    protected override void OnFormClosed(FormClosedEventArgs e) { _trafficTimer.Dispose(); base.OnFormClosed(e); }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        _t.ApplyWindowChrome(this);
        FitToContent();
    }

    /// <summary>Preview only: show this adapter instead of reading the real ones.</summary>
    internal static AdapterInfo? TestAdapter;

    void FitToContent()
    {
        int h = Px(360);
        if (!_loading && _sections.Count > 0)
        {
            var rects = CardRects();
            h = rects.Max(r => r.Bottom) + Px(16) + Px(32) + Pad;
        }
        var wa = Screen.FromControl(this).WorkingArea;
        var size = SizeFromClientSize(new Size(Px(900), h));
        size = new Size(Math.Min(size.Width, wa.Width), Math.Min(size.Height, wa.Height));
        Size = size;
        Location = new Point(wa.X + (wa.Width - Width) / 2, wa.Y + (wa.Height - Height) / 2);
        LayoutControls();
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        await Reload();
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        FitToContent();
        Invalidate();
    }

    async Task Reload()
    {
        _loading = true; Invalidate();
        try { _sections = await Task.Run(Gather); }
        catch (Exception ex) { _sections = new() { new Section("Error", new() { new Row("Problem", ex.Message) }) }; }
        _loading = false;
        _copyAll.Text = "Copy all";
        UpdateTraffic();
        _trafficTimer.Start();
        FitToContent();
        Invalidate();
    }

    List<Section> Gather()
    {
        var snap = NetInfo.Snapshot();
        if (TestAdapter != null) snap[TestAdapter.Name] = TestAdapter;
        var list = new List<Section>();
        if (!snap.TryGetValue(_adapter, out var a))
        {
            list.Add(new Section("Adapter", new() { new Row("Status", $"\"{_adapter}\" is not present right now") }));
            return list;
        }
        // Link (Wi-Fi or cable)
        var link = new List<Row>();
        var w = a.IsWireless ? NetDetails.Wifi(a.Id) ?? (TestAdapter != null ? new WifiInfo("Softech-5G", "9c:5c:8e:04:b1:30", 78, 44, "Wi-Fi 6 (802.11ax)", "WPA3-Personal", 1_200_000, 960_000) : null) : null;
        if (w != null)
        {
            link.Add(new Row("Network", w.Ssid));
            link.Add(new Row("Access point", w.Bssid));
            link.Add(new Row("Signal", $"{w.SignalPercent}% · about {w.Dbm} dBm"));
            if (w.Channel > 0) link.Add(new Row("Band / channel", $"{w.Band} · channel {w.Channel}"));
            if (w.Standard.Length > 0) link.Add(new Row("Standard", w.Standard));
            link.Add(new Row("Rate", $"{Rate(w.RxKbps)} receive · {Rate(w.TxKbps)} send"));
            link.Add(new Row("Security", w.Security));
        }
        else
        {
            link.Add(new Row("Status", a.IsUp ? "Connected" : "Disconnected"));
            if (a.IsWireless && a.IsUp) link.Add(new Row("Wi-Fi", "No Wi-Fi details from Windows"));
            if (NetInfo.FormatSpeed(a.SpeedBps) is { Length: > 0 } sp) link.Add(new Row("Link speed", sp));
        }
        if (a.IsUp) { link.Add(new Row("Speed now", "measuring…")); link.Add(new Row("Total", "")); }
        link.Add(new Row("Adapter", a.Description));
        link.Add(new Row("MAC", a.Mac.Replace('-', ':').ToLowerInvariant()));
        list.Add(new Section(w != null ? "Wi-Fi" : "Link", link));

        // Addressing
        var addr = new List<Row>();
        foreach (var (ip, mask) in a.Addresses) addr.Add(new Row("IPv4", mask.Length > 0 ? $"{ip}/{NetInfo.PrefixLength(mask)} ({mask})" : ip));
        if (a.Addresses.Count == 0) addr.Add(new Row("IPv4", "none"));
        var gw = a.Gateways.FirstOrDefault();
        if (gw != null)
        {
            var mac = NetDetails.MacOf(gw);
            addr.Add(new Row("Gateway", gw + (mac != null ? $" · {mac}" : ""), () => _tool("ping-t", gw)));
        }
        else addr.Add(new Row("Gateway", "none"));
        addr.Add(new Row("Mode", a.Dhcp ? "DHCP" : "Static (set by hand)"));
        if (a.Dhcp)
        {
            addr.Add(new Row("DHCP server", a.DhcpServer ?? "unknown"));
            var (got, ends) = NetDetails.Lease(a.Id);
            if (got != null) addr.Add(new Row("Lease obtained", When(got.Value)));
            if (ends != null)
            {
                var left = ends.Value - DateTime.Now;
                addr.Add(new Row("Lease expires", When(ends.Value) + (left > TimeSpan.Zero ? $" ({NetInfo.FormatDuration((long)left.TotalSeconds)} left)" : " (expired)")));
            }
        }
        var extra = NetDetails.Extra(a);
        addr.Add(new Row("IPv6", extra.Ipv6));
        list.Add(new Section(a.Dhcp ? "Addressing and DHCP" : "Addressing", addr));

        // DNS
        var dns = new List<Row>
        {
            new("Servers", a.Dns.Count == 0 ? "none" : string.Join(", ", a.Dns) + (a.DnsFromDhcp && a.Dhcp ? " (from DHCP)" : " (set by hand)")),
        };
        if (extra.DnsSuffix.Length > 0) dns.Add(new Row("DNS suffix", extra.DnsSuffix));
        _dnsServer = a.Dns.FirstOrDefault();
        list.Add(new Section("DNS", dns));

        // This PC
        var pc = new List<Row>();
        if (extra.Category.Length > 0) pc.Add(new Row("Network profile", extra.Category));
        pc.Add(new Row("Firewall", NetDetails.Firewall()));
        if (extra.Mtu > 0) pc.Add(new Row("MTU", extra.Mtu.ToString()));
        var defaults = snap.Values.Where(x => x.IsUp && x.Gateways.Count > 0).Select(x => $"{x.Gateways[0]} ({x.Name})").ToList();
        pc.Add(new Row("Default routes", defaults.Count == 0 ? "none" : string.Join(", ", defaults)));
        var (conns, listen) = NetDetails.Sockets();
        pc.Add(new Row("Route table", "show all routes", () => Console("route print -4")));
        pc.Add(new Row("Connections", $"{conns} open", () => Console("netstat -ano -p tcp")));
        pc.Add(new Row("Listening ports", $"{listen} TCP", () => Console("netstat -ano | findstr LISTENING")));
        list.Add(new Section("This PC", pc));
        return list;
    }

    static string Rate(long kbps) => kbps >= 1_000_000 ? $"{kbps / 1_000_000.0:0.#} Gbps" : $"{kbps / 1000} Mbps";

    static string When(DateTime d) =>
        d.Date == DateTime.Today ? $"Today {d:HH:mm}" : d.Date == DateTime.Today.AddDays(1) ? $"Tomorrow {d:HH:mm}"
        : d.Date == DateTime.Today.AddDays(-1) ? $"Yesterday {d:HH:mm}" : d.ToString("d MMM HH:mm");

    /// <summary>Opens a console with one of the fixed commands above (never user text).</summary>
    static void Console(string cmd)
    {
        try { Process.Start(new ProcessStartInfo("cmd.exe", $"/k title {cmd.Replace("|", "^|")} & {cmd}") { UseShellExecute = true }); } catch { }
    }

    async Task DoLookup()
    {
        var name = _lookup.Text.Trim();
        _lookupBtn.Enabled = false;
        _lookupResult = "Looking up…"; _lookupOk = true; Invalidate(_dnsCard);
        try
        {
            var r = await NetDetails.LookupAsync(name, _dnsServer);
            _lookupResult = r.Text; _lookupOk = r.Ok;
        }
        catch (Exception ex) { _lookupResult = ex.Message; _lookupOk = false; }
        finally { _lookupBtn.Enabled = true; Invalidate(_dnsCard); }
    }

    string AllText()
    {
        var sb = new StringBuilder($"{_adapter} · {DateTime.Now:yyyy-MM-dd HH:mm}\r\n");
        foreach (var s in _sections)
        {
            sb.Append("\r\n").Append(s.Title).Append("\r\n");
            foreach (var r in s.Rows) sb.Append($"  {r.Label}: {r.Value}\r\n");
        }
        return sb.ToString();
    }

    // ---------- layout & paint ----------

    int Pad => Px(24);
    int RowH => Px(23);

    /// <summary>Two columns; section 0 and 2 on the left, 1 and 3 on the right.</summary>
    List<Rectangle> CardRects()
    {
        int colW = (ClientSize.Width - Pad * 2 - Px(16)) / 2;
        int top = Pad + Px(58);
        int H(int i) => i < _sections.Count ? Px(46) + _sections[i].Rows.Count * RowH + (_sections[i].Title == "DNS" ? Px(96) : 0) + Px(8) : 0;
        int row1 = Math.Max(H(0), H(1));
        var rects = new List<Rectangle>();
        for (int i = 0; i < _sections.Count; i++)
        {
            int x = Pad + (i % 2) * (colW + Px(16));
            int y = i < 2 ? top : top + row1 + Px(16);
            int h = i < 2 ? row1 : Math.Max(H(2), H(3));
            rects.Add(new Rectangle(x, y, colW, h));
        }
        return rects;
    }

    void LayoutControls()
    {
        if (!IsHandleCreated) return;
        int bh = Px(32);
        int bw = Px(110);
        _copyAll.SetBounds(ClientSize.Width - Pad - bw, ClientSize.Height - Pad - bh, bw, bh);
        _refresh.SetBounds(_copyAll.Left - Px(8) - bw, _copyAll.Top, bw, bh);
        var rects = CardRects();
        int di = _sections.FindIndex(s => s.Title == "DNS");
        bool show = di >= 0 && !_loading;
        _lookup.Visible = _lookupBtn.Visible = show;
        if (!show) return;
        _dnsCard = rects[di];
        int y = _dnsCard.Y + Px(46) + _sections[di].Rows.Count * RowH + Px(8);
        int lbw = Px(96);
        _lookup.SetBounds(_dnsCard.X + Px(16), y, _dnsCard.Width - Px(32) - lbw - Px(8), bh);
        _lookupBtn.SetBounds(_lookup.Right + Px(8), y, lbw, bh);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(_t.Window);
        TextRenderer.DrawText(g, $"{_adapter}", _fTitle, new Point(Pad - Px(2), Pad), _t.Text);
        TextRenderer.DrawText(g, _loading ? "Reading the adapter…" : "Click a value to copy it. Blue items open a tool.", _fSmall,
            new Point(Pad, Pad + Px(26)), _t.SubText);
        _hits.Clear();
        if (_loading) return;
        var rects = CardRects();
        var flags = TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter;
        for (int i = 0; i < _sections.Count; i++)
        {
            var r = rects[i];
            using (var b = new SolidBrush(_t.Surface)) using (var p = new Pen(_t.Border)) using (var path = Theme.RoundRect(r, Px(8)))
            { g.FillPath(b, path); g.DrawPath(p, path); }
            TextRenderer.DrawText(g, _sections[i].Title, _fCard, new Rectangle(r.X + Px(16), r.Y + Px(12), r.Width - Px(32), Px(24)), _t.Text, flags);
            int y = r.Y + Px(44);
            int lw = Px(132);
            foreach (var row0 in _sections[i].Rows)
            {
                var row = row0.Label switch { "Speed now" => row0 with { Value = _speedNow }, "Total" => row0 with { Value = _total }, _ => row0 };
                TextRenderer.DrawText(g, row.Label, _fBody, new Rectangle(r.X + Px(16), y, lw, RowH), _t.SubText, flags);
                var vr = new Rectangle(r.X + Px(16) + lw, y, r.Width - Px(32) - lw, RowH);
                int vw = Math.Min(vr.Width, TextRenderer.MeasureText(g, row.Value, _fBody, Size.Empty, flags).Width);
                var hit = new Rectangle(vr.X - Px(4), vr.Y + Px(1), vw + Px(8), RowH - Px(2));
                int idx = _hits.Count;
                _hits.Add((hit, row));
                if (idx == _hover)
                    using (var hb = new SolidBrush(_t.SurfaceHover)) using (var hp = Theme.RoundRect(hit, Px(4))) g.FillPath(hb, hp);
                var color = _copied == row.Label + row.Value ? _t.Success : row.Link != null ? _t.Accent : _t.Text;
                TextRenderer.DrawText(g, _copied == row.Label + row.Value ? "Copied" : row.Value, _fBody, vr, color, flags);
                y += RowH;
            }
            if (_sections[i].Title == "DNS" && _lookupResult.Length > 0)
            {
                var lr = new Rectangle(r.X + Px(16), _lookup.Bottom + Px(8), r.Width - Px(32), r.Bottom - _lookup.Bottom - Px(12));
                TextRenderer.DrawText(g, _lookupResult, _fSmall, lr, _lookupOk ? _t.SubText : _t.Danger, TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
            }
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int h = _hits.FindIndex(x => x.Rect.Contains(e.Location));
        if (h != _hover) { _hover = h; Invalidate(); }
        Cursor = h >= 0 ? Cursors.Hand : Cursors.Default;
    }

    protected override void OnMouseLeave(EventArgs e) { _hover = -1; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        int h = _hits.FindIndex(x => x.Rect.Contains(e.Location));
        if (h < 0) return;
        var row = _hits[h].Row;
        if (row.Link != null) { row.Link(); return; }
        try { Clipboard.SetText(row.Value); _copied = row.Label + row.Value; Invalidate(); } catch { }
    }
}
