using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace NetSwitch;

public enum PopupKind { Info, Success, Error, Change }

public sealed record PopupContent
{
    public PopupKind Kind { get; init; } = PopupKind.Info;
    public string Title { get; init; } = "";
    public string? Heading { get; init; }          // e.g. adapter name
    public string? SubHeading { get; init; }       // e.g. adapter description
    public string? Big { get; init; }              // e.g. new IP
    public string? BigSuffix { get; init; }        // e.g. "/24"
    public string? Previous { get; init; }         // e.g. "was 10.0.0.5"
    public string? Message { get; init; }          // free text (errors etc.)
    public List<(string Label, string Value)> Rows { get; init; } = new();
    public string? CopyText { get; init; }
    public bool Sticky { get; init; }              // stays until closed
    /// <summary>Adapter overview: one two-line entry per adapter; clicking an entry copies its IP,
    /// clicking its gateway opens network tools for it.</summary>
    public List<PopupEntry> Entries { get; init; } = new();
    public bool ShowExit { get; init; }
    public bool CloseOnOutsideClick { get; init; }
    /// <summary>Show a live internet check (pings + DNS) above the entries.</summary>
    public bool LiveInternet { get; init; }
    /// <summary>Entry dots blink with each adapter's traffic.</summary>
    public bool LiveActivity { get; init; }
    /// <summary>Optional main button (replaces "Copy IP").</summary>
    public string? ActionText { get; init; }
    public Action? Action { get; init; }
    /// <summary>Extra buttons, shown left of Settings…</summary>
    public List<(string Text, Action Run)> Buttons { get; init; } = new();
    public bool HideSettings { get; init; }
    /// <summary>Show public IP and ISP under the internet status (needs LiveInternet).</summary>
    public bool PublicIp { get; init; }
    /// <summary>One line per entry.</summary>
    public bool Compact { get; init; }
}

/// <summary>One adapter in the overview. The second line reads: Line · gw Gateway · Rest.</summary>
public sealed record PopupEntry(string Name, string Line, string? Copy, bool Up, string? Gateway = null, string Rest = "")
{
    /// <summary>When set, clicking the entry runs this and closes the pop-up instead of copying.</summary>
    public Action? Click { get; init; }
    /// <summary>Hint shown on the right while hovered (default "Copy IP").</summary>
    public string? Hint { get; init; }
    /// <summary>Hint shown on the right all the time, in the accent colour (e.g. "Active").</summary>
    public string? Badge { get; init; }
    /// <summary>When set, clicking the entry opens this menu (null text = separator). Takes precedence over Copy.</summary>
    public List<PopupAction>? Actions { get; init; }
    /// <summary>Small outlined label after the name, e.g. "Internet" or "VPN".</summary>
    public string? Tag { get; init; }
    /// <summary>Folded into the "N more" line until expanded (disconnected or virtual adapters).</summary>
    public bool Folded { get; init; }
    /// <summary>Amber line under the entry, e.g. packet errors.</summary>
    public string? Warning { get; init; }
    /// <summary>Optional third line, e.g. Wi-Fi network and signal.</summary>
    public string? Line2 { get; init; }
    /// <summary>Signal bars (0-4) drawn before <see cref="Line2"/>; -1 = none.</summary>
    public int Signal { get; init; } = -1;
}

public sealed record PopupAction(string? Text, string Detail = "", Action? Run = null)
{
    public static readonly PopupAction Separator = new((string?)null);
}

/// <summary>Custom notification window: bottom-right, no focus stealing, stacks, rich content.</summary>
public sealed class PopupForm : Form
{
    static readonly List<PopupForm> Open = new();
    public static Action? OpenProfilesRequested;
    public static Action? ExitRequested;
    readonly ModernButton? _exit;
    readonly System.Windows.Forms.Timer _outside = new() { Interval = 80 };
    readonly List<Rectangle> _entryRects = new();
    int _hoverEntry = -1, _copiedEntry = -1, _hoverGw = -1;
    readonly Dictionary<int, Rectangle> _gwRects = new();
    // live internet status and activity lights
    NetCheckResult _net = new(NetHealth.Checking, "Checking internet…", "Pinging 1.1.1.1 and 8.8.8.8, resolving DNS");
    Rectangle _netRect;
    readonly System.Windows.Forms.Timer _netTimer = new() { Interval = 4000 };
    readonly System.Windows.Forms.Timer _actTimer = new() { Interval = 250 };
    Dictionary<string, long> _lastBytes = new();
    readonly HashSet<int> _blinkOn = new();
    // live speeds
    Dictionary<string, (long Rx, long Tx)> _rateBase = new();
    DateTime _rateAt;
    readonly Dictionary<string, string> _rates = new(StringComparer.OrdinalIgnoreCase);
    // folded adapters, IP click targets, pin
    bool _showFolded;
    Rectangle _moreRect;
    bool _hoverMore;
    readonly Dictionary<int, Rectangle> _ipRects = new();
    int _hoverIp = -1;
    Rectangle _pinRect;
    bool _hoverPin;
    /// <summary>Pinned panels ignore clicks outside them. Remembered for the session.</summary>
    public static bool Pinned;
    internal static Dictionary<string, string>? TestRates; // preview only
    bool _netBusy, _actBusy;
    ContextMenuStrip? _toolsMenu;
    public static Action<string, string>? ToolRequested; // (tool, ip)

    readonly PopupContent _c;
    readonly Theme _t = Theme.Current;
    readonly System.Windows.Forms.Timer _life = new();
    readonly System.Windows.Forms.Timer _fade = new() { Interval = 15 };
    readonly Font _fTitle, _fHeading, _fSub, _fBig, _fBody, _fLabel, _fSuffix;
    readonly ModernButton? _copy;
    readonly ModernButton _open;
    readonly List<ModernButton> _extra = new();
    PublicIpInfo? _pub;
    Rectangle _closeRect;
    bool _closeHover, _fadingOut;
    int _contentHeight;

    float S => DeviceDpi / 96f;
    int Px(float v) => (int)Math.Round(v * S);

    public static PopupForm Show(PopupContent c, int seconds)
    {
        var f = new PopupForm(c, seconds);
        f.ShowPopup();
        return f;
    }

    public bool IsOpen => !IsDisposed && !_fadingOut;
    public void Dismiss() => BeginClose();

    PopupForm(PopupContent c, int seconds)
    {
        _c = c;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = _t.Surface;
        DoubleBuffered = true;
        AutoScaleMode = AutoScaleMode.None;
        Opacity = 0;

        _fTitle = Theme.UiFont(9f, FontStyle.Bold);
        _fHeading = Theme.UiFont(10.5f, FontStyle.Bold);
        _fSub = Theme.UiFont(8.75f);
        _fBig = Theme.UiFont(19f, FontStyle.Bold);
        _fBody = Theme.UiFont(9.25f);
        _fLabel = Theme.UiFont(9.25f);
        _fSuffix = Theme.UiFont(12f);
        Font = _fBody;

        if (c.ActionText != null && c.Action != null)
        {
            _copy = new ModernButton(_t, c.ActionText, primary: true) { Font = _fBody, TabStop = false };
            _copy.Click += (_, _) => { BeginClose(); c.Action(); };
            Controls.Add(_copy);
        }
        else if (!string.IsNullOrEmpty(c.CopyText))
        {
            _copy = new ModernButton(_t, "Copy IP", primary: true) { Font = _fBody, TabStop = false };
            _copy.Click += (_, _) =>
            {
                try { Clipboard.SetText(c.CopyText!); _copy.Text = "Copied"; } catch { }
            };
            Controls.Add(_copy);
        }
        if (c.ShowExit)
        {
            _exit = new ModernButton(_t, "Exit") { Font = _fBody, TabStop = false, Ghost = true };
            _exit.Click += (_, _) => { BeginClose(); ExitRequested?.Invoke(); };
            Controls.Add(_exit);
        }
        if (c.CloseOnOutsideClick)
        {
            bool wasDown = true; // ignore the click that opened us
            _outside.Tick += (_, _) =>
            {
                bool down = (MouseButtons & (MouseButtons.Left | MouseButtons.Right)) != 0;
                bool inMenu = _toolsMenu is { Visible: true } m && m.Bounds.Contains(Cursor.Position);
                if (down && !wasDown && !inMenu && !Pinned && !Bounds.Contains(Cursor.Position)) BeginClose();
                wasDown = down;
            };
        }
        _open = new ModernButton(_t, "Settings…") { Font = _fBody, TabStop = false, Visible = !c.HideSettings };
        _open.Click += (_, _) => { OpenProfilesRequested?.Invoke(); BeginClose(); };
        Controls.Add(_open);
        foreach (var (text, run) in c.Buttons)
        {
            var b = new ModernButton(_t, text) { Font = _fBody, TabStop = false };
            // Post: the action may open a window or close this pop-up.
            b.Click += (_, _) => { BeginClose(); var ctx = SynchronizationContext.Current; if (ctx != null) ctx.Post(_ => run(), null); else run(); };
            Controls.Add(b);
            _extra.Add(b);
        }

        _life.Interval = Math.Max(3, seconds) * 1000;
        _life.Tick += (_, _) =>
        {
            if (Bounds.Contains(Cursor.Position)) return; // wait while hovered
            _life.Stop(); BeginClose();
        };
        _fade.Tick += (_, _) => StepFade();
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x08000000 /*WS_EX_NOACTIVATE*/ | 0x00000080 /*WS_EX_TOOLWINDOW*/ | 0x00000008 /*WS_EX_TOPMOST*/;
            cp.ClassStyle |= 0x00020000; // CS_DROPSHADOW (fallback for older Windows)
            return cp;
        }
    }

    void ShowPopup()
    {
        CreateHandle();
        _t.ApplyWindowChrome(this);
        LayoutContent();
        Open.Add(this);
        Restack();
        // Show without activating.
        ShowWindow(Handle, 4 /*SW_SHOWNOACTIVATE*/);
        SetWindowPos(Handle, new IntPtr(-1) /*HWND_TOPMOST*/, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010 /*NOSIZE|NOMOVE|NOACTIVATE*/);
        Visible = true;
        _fade.Start();
        if (!_c.Sticky) _life.Start();
        if (_c.CloseOnOutsideClick) _outside.Start();
        if (_c.LiveInternet)
        {
            _netTimer.Tick += async (_, _) => await RefreshInternet();
            _netTimer.Start();
            _ = RefreshInternet();
        }
        if (_c.LiveActivity)
        {
            _actTimer.Tick += async (_, _) => await RefreshActivity();
            _actTimer.Start();
        }
    }

    async Task RefreshInternet()
    {
        if (_netBusy || IsDisposed) return;
        _netBusy = true;
        try
        {
            var r = await Task.Run(NetCheck.RunAsync);
            if (IsDisposed) return;
            _net = r;
            Invalidate(_netRect);
            if (_c.PublicIp && r.Health != NetHealth.Offline)
            {
                var pub = await NetDetails.PublicIpAsync();
                if (IsDisposed) return;
                if (pub != null && pub != _pub) { _pub = pub; Invalidate(_netRect); }
                if (pub == null && !_pubFailed) { _pubFailed = true; Invalidate(_netRect); }
            }
        }
        catch { }
        finally { _netBusy = false; }
    }

    async Task RefreshActivity()
    {
        if (_actBusy || IsDisposed) return;
        _actBusy = true;
        try
        {
            var split = await Task.Run(NetCheck.ByteCountersSplit);
            if (IsDisposed) return;
            var now = split.ToDictionary(kv => kv.Key, kv => kv.Value.Rx + kv.Value.Tx, StringComparer.OrdinalIgnoreCase);
            bool changed = false;
            var t = DateTime.UtcNow;
            double secs = (t - _rateAt).TotalSeconds;
            if (_rateBase.Count == 0) { _rateBase = split; _rateAt = t; }
            else if (secs >= 1)
            {
                foreach (var (n, (rx, tx)) in split)
                {
                    if (!_rateBase.TryGetValue(n, out var b0)) continue;
                    double down = Math.Max(0, rx - b0.Rx) / secs, upr = Math.Max(0, tx - b0.Tx) / secs;
                    var text = _c.Compact
                        ? $"↓{NetDetails.FormatRateShort(down)}  ↑{NetDetails.FormatRateShort(upr)}"
                        : $"↓ {NetDetails.FormatRate(down)}   ↑ {NetDetails.FormatRate(upr)}";
                    if (!_rates.TryGetValue(n, out var old) || old != text) { _rates[n] = text; changed = true; }
                }
                _rateBase = split; _rateAt = t;
            }
            for (int i = 0; i < _c.Entries.Count; i++)
            {
                var name = _c.Entries[i].Name;
                bool busy = now.TryGetValue(name, out var b) && _lastBytes.TryGetValue(name, out var was) && b != was;
                // Blink like a port LED: alternate while traffic flows, stay steady when idle.
                bool on = busy && !_blinkOn.Contains(i);
                if (on ? _blinkOn.Add(i) : _blinkOn.Remove(i)) changed = true;
            }
            _lastBytes = now;
            if (changed) Invalidate();
        }
        catch { }
        finally { _actBusy = false; }
    }

    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);

    static void Restack()
    {
        var wa = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 720);
        int y = wa.Bottom;
        for (int i = Open.Count - 1; i >= 0; i--)
        {
            var f = Open[i];
            int gap = f.Px(12);
            y -= f.Height + gap;
            if (y < wa.Top) { f.BeginClose(); continue; }
            f.Location = new Point(wa.Right - f.Width - gap, y);
        }
    }

    void StepFade()
    {
        if (_fadingOut)
        {
            Opacity -= 0.12;
            if (Opacity <= 0.02) { _fade.Stop(); Close(); }
        }
        else
        {
            Opacity = Math.Min(1, Opacity + 0.15);
            if (Opacity >= 1) _fade.Stop();
        }
    }

    void BeginClose()
    {
        if (_fadingOut) return;
        _fadingOut = true;
        _life.Stop();
        _fade.Start();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        Open.Remove(this);
        Restack();
        _life.Dispose(); _fade.Dispose(); _outside.Dispose();
        // Dispose the menu later: we may be closing from inside one of its click handlers.
        var menu = _toolsMenu; _toolsMenu = null;
        if (menu != null) { var ctx = SynchronizationContext.Current; if (ctx != null) ctx.Post(_ => menu.Dispose(), null); else menu.Dispose(); } _netTimer.Dispose(); _actTimer.Dispose();
        base.OnFormClosed(e);
    }

    // ---------- layout & paint ----------

    int Pad => Px(18);

    void LayoutContent()
    {
        int width = Px(400);
        using var g = CreateGraphics();
        int y = Pad;
        y += Px(22); // title row
        if (_c.Heading != null) y += Px(6) + TextRenderer.MeasureText(_c.Heading, _fHeading).Height;
        if (_c.SubHeading != null) y += Measure(g, _c.SubHeading, _fSub, width).Height;
        if (_c.Big != null) y += Px(8) + TextRenderer.MeasureText(_c.Big, _fBig).Height;
        if (_c.Previous != null) y += TextRenderer.MeasureText(_c.Previous, _fSub).Height;
        if (_c.Message != null) y += Px(8) + Measure(g, _c.Message, _fBody, width).Height;
        if (_c.Rows.Count > 0)
        {
            y += Px(14);
            foreach (var (_, v) in _c.Rows) y += Measure(g, v, _fBody, width - LabelWidth()).Height + Px(5);
        }
        if (_c.LiveInternet)
        {
            y += Px(10);
            int nh = TextRenderer.MeasureText("Ag", _fHeading).Height + TextRenderer.MeasureText("Ag", _fSub).Height * (_c.PublicIp ? 2 : 1) + Px(14);
            _netRect = new Rectangle(Pad - Px(8), y, width - Pad * 2 + Px(16), nh);
            y += nh;
        }
        _entryRects.Clear();
        _moreRect = Rectangle.Empty;
        if (_c.Entries.Count > 0)
        {
            y += Px(10);
            int hh = TextRenderer.MeasureText("Ag", _fHeading).Height;
            int sh = TextRenderer.MeasureText("Ag", _fSub).Height;
            int rowW = width - Pad * 2 + Px(16);
            bool moreDone = false;
            foreach (var en in _c.Entries)
            {
                if (en.Folded && !moreDone)
                {
                    moreDone = true;
                    _moreRect = new Rectangle(Pad - Px(8), y, rowW, sh + Px(12));
                    y += _moreRect.Height + Px(2);
                }
                if (en.Folded && !_showFolded) { _entryRects.Add(Rectangle.Empty); continue; }
                int eh = _c.Compact ? hh + Px(8)
                    : hh + sh + Px(10) + (en.Line2 != null ? sh : 0) + (en.Warning != null ? sh : 0);
                if (_c.Compact && en.Warning != null) eh += sh;
                _entryRects.Add(new Rectangle(Pad - Px(8), y, rowW, eh));
                y += eh + Px(2);
            }
        }
        _contentHeight = y;
        int btnH = Px(30);
        y += Px(12);
        int x = width - Pad;
        if (_exit != null)
        {
            var w = Math.Max(Px(60), TextRenderer.MeasureText(_exit.Text, _exit.Font).Width + Px(24));
            _exit.SetBounds(Pad - Px(8), y, w, btnH);
        }
        var order = new List<ModernButton?> { _c.HideSettings ? null : _open, _copy };
        order.AddRange(Enumerable.Reverse(_extra));
        foreach (var b in order)
        {
            if (b == null) continue;
            var w = Math.Max(_extra.Contains(b) ? Px(72) : Px(88), TextRenderer.MeasureText(b.Text, b.Font).Width + Px(24));
            x -= w;
            b.SetBounds(x, y, w, btnH);
            x -= Px(8);
        }
        y += btnH + Pad;
        Size = new Size(width, y);
        _closeRect = new Rectangle(width - Pad - Px(20) + Px(4), Pad - Px(3), Px(24), Px(24));
        _pinRect = _c.CloseOnOutsideClick ? new Rectangle(_closeRect.X - Px(28), _closeRect.Y, Px(24), Px(24)) : Rectangle.Empty;
    }

    int LabelWidth() => Px(92);

    Rectangle _pubRect;
    bool _hoverPub, _pubCopied, _pubFailed;

    /// <summary>Latency to 1.1.1.1 over the last couple of minutes; gaps are lost pings.</summary>
    void DrawSparkline(Graphics g, Rectangle r)
    {
        var h = NetCheck.History;
        if (h.Count < 2) return;
        var vals = h.Where(x => x != null).Select(x => (float)x!.Value).ToList();
        if (vals.Count == 0) return;
        float max = Math.Max(20, vals.Max() * 1.15f);
        float step = r.Width / (float)(Math.Max(h.Count, 20) - 1);
        float x0 = r.Right - step * (h.Count - 1);
        using var pen = new Pen(_t.Accent, Math.Max(1.2f, S * 1.1f)) { LineJoin = LineJoin.Round };
        var run = new List<PointF>();
        void Flush() { if (run.Count > 1) g.DrawLines(pen, run.ToArray()); run.Clear(); }
        for (int i = 0; i < h.Count; i++)
        {
            float x = x0 + i * step;
            if (h[i] is not { } v)
            {
                Flush();
                using var lost = new Pen(_t.Danger, Math.Max(1f, S)); // lost ping: short red tick
                g.DrawLine(lost, x, r.Bottom - Px(6), x, r.Bottom);
                continue;
            }
            run.Add(new PointF(x, r.Bottom - Px(2) - (r.Height - Px(4)) * Math.Min(1f, v / max)));
        }
        Flush();
    }

    Size Measure(Graphics g, string text, Font f, int width) =>
        TextRenderer.MeasureText(g, text, f, new Size(width - Pad * 2, int.MaxValue), TextFormatFlags.WordBreak);

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(_t.Surface);
        using (var border = new Pen(_t.Border)) g.DrawRectangle(border, 0, 0, Width - 1, Height - 1);

        int x = Pad, y = Pad, w = Width - Pad * 2;
        var accent = _c.Kind switch
        {
            PopupKind.Success => _t.Success,
            PopupKind.Error => _t.Danger,
            _ => _t.Accent,
        };
        // Left accent stripe.
        using (var stripe = new SolidBrush(accent)) g.FillRectangle(stripe, 0, 0, Px(4), Height);

        // Title row: title, time, close.
        var titleRect = new Rectangle(x - Px(2), y, w - Px(80), Px(20));
        TextRenderer.DrawText(g, _c.Title, _fTitle, titleRect, _t.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        var time = DateTime.Now.ToString("HH:mm");
        var tw = TextRenderer.MeasureText(time, _fSub).Width;
        TextRenderer.DrawText(g, time, _fSub, new Rectangle((_pinRect.IsEmpty ? _closeRect.Left : _pinRect.Left) - tw - Px(6), y, tw, Px(20)), _t.SubText, TextFormatFlags.VerticalCenter);
        if (!_pinRect.IsEmpty) DrawPin(g, _pinRect);
        if (_closeHover)
            using (var hb = new SolidBrush(_t.SurfaceHover)) using (var hp = Theme.RoundRect(_closeRect, 4)) g.FillPath(hb, hp);
        using (var cp = new Pen(_t.SubText, Math.Max(1.2f, S * 1.2f)))
        {
            int c = Px(5);
            var m = new Point(_closeRect.X + _closeRect.Width / 2, _closeRect.Y + _closeRect.Height / 2);
            g.DrawLine(cp, m.X - c, m.Y - c, m.X + c, m.Y + c);
            g.DrawLine(cp, m.X + c, m.Y - c, m.X - c, m.Y + c);
        }
        y += Px(22);

        if (_c.Heading != null)
        {
            y += Px(6);
            var h = TextRenderer.MeasureText(_c.Heading, _fHeading).Height;
            TextRenderer.DrawText(g, _c.Heading, _fHeading, new Rectangle(x, y, w, h), _t.Text, TextFormatFlags.EndEllipsis);
            y += h;
        }
        if (_c.SubHeading != null)
        {
            var h = Measure(g, _c.SubHeading, _fSub, Width).Height;
            TextRenderer.DrawText(g, _c.SubHeading, _fSub, new Rectangle(x, y, w, h), _t.SubText, TextFormatFlags.WordBreak);
            y += h;
        }
        if (_c.Big != null)
        {
            y += Px(8);
            var bs = TextRenderer.MeasureText(g, _c.Big, _fBig, Size.Empty, TextFormatFlags.NoPadding);
            var h = TextRenderer.MeasureText(_c.Big, _fBig).Height;
            TextRenderer.DrawText(g, _c.Big, _fBig, new Point(x - Px(2), y), _t.Text);
            if (_c.BigSuffix != null)
                TextRenderer.DrawText(g, _c.BigSuffix, _fSuffix, new Rectangle(x + bs.Width + Px(6), y, w, h), _t.SubText, TextFormatFlags.VerticalCenter);
            y += h;
        }
        if (_c.Previous != null)
        {
            var h = TextRenderer.MeasureText(_c.Previous, _fSub).Height;
            TextRenderer.DrawText(g, _c.Previous, _fSub, new Rectangle(x, y, w, h), _t.SubText, TextFormatFlags.Left);
            y += h;
        }
        if (_c.Message != null)
        {
            y += Px(8);
            var h = Measure(g, _c.Message, _fBody, Width).Height;
            TextRenderer.DrawText(g, _c.Message, _fBody, new Rectangle(x, y, w, h), _c.Kind == PopupKind.Error ? _t.Danger : _t.Text, TextFormatFlags.WordBreak);
            y += h;
        }
        if (_c.Rows.Count > 0)
        {
            y += Px(7);
            using (var sep = new Pen(_t.Border)) g.DrawLine(sep, x, y, x + w, y);
            y += Px(7);
            int lw = LabelWidth();
            foreach (var (label, value) in _c.Rows)
            {
                var h = Measure(g, value, _fBody, Width - lw).Height;
                TextRenderer.DrawText(g, label, _fLabel, new Rectangle(x, y, lw, h), _t.SubText, TextFormatFlags.Left);
                TextRenderer.DrawText(g, value, _fBody, new Rectangle(x + lw, y, w - lw, h), _t.Text, TextFormatFlags.WordBreak);
                y += h + Px(5);
            }
        }
        if (_c.LiveInternet)
        {
            var r = _netRect;
            using (var bg = new SolidBrush(_t.SurfaceHover)) using (var bp = Theme.RoundRect(r, Px(6))) g.FillPath(bg, bp);
            var col = _net.Health switch
            {
                NetHealth.Online => _t.Success,
                NetHealth.Limited => Color.FromArgb(0xE0, 0x9B, 0x1A),
                NetHealth.Offline => _t.Danger,
                _ => _t.SubText,
            };
            int lh = TextRenderer.MeasureText("Ag", _fHeading).Height;
            int nd = Px(10);
            using (var db = new SolidBrush(col)) g.FillEllipse(db, r.X + Px(8), r.Y + Px(7) + (lh - nd) / 2, nd, nd);
            var nf = TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
            int ntx = r.X + Px(26);
            int sparkW = Px(84);
            TextRenderer.DrawText(g, "Internet  ·  " + _net.Summary, _fHeading, new Rectangle(ntx, r.Y + Px(7), r.Right - ntx - Px(16) - sparkW, lh), _t.Text, nf);
            DrawSparkline(g, new Rectangle(r.Right - Px(10) - sparkW, r.Y + Px(7), sparkW, lh));
            int slh = TextRenderer.MeasureText("Ag", _fSub).Height;
            TextRenderer.DrawText(g, _net.Detail, _fSub, new Rectangle(ntx, r.Y + Px(7) + lh, r.Right - ntx - Px(8), slh), _t.SubText, nf);
            if (_c.PublicIp)
            {
                var pr = new Rectangle(ntx, r.Y + Px(7) + lh + slh, r.Right - ntx - Px(8), slh);
                if (_pub == null)
                    TextRenderer.DrawText(g, _net.Health == NetHealth.Offline ? "Public IP: not available while offline"
                        : _pubFailed ? "Public IP: couldn't look it up" : "Public IP: looking up…", _fSub, pr, _t.SubText, nf);
                else
                {
                    var np2 = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
                    var lead = "Public IP ";
                    int lw0 = TextRenderer.MeasureText(g, lead, _fSub, Size.Empty, np2).Width;
                    int iw = TextRenderer.MeasureText(g, _pub.Ip, _fSub, Size.Empty, np2).Width;
                    TextRenderer.DrawText(g, lead, _fSub, new Rectangle(pr.X, pr.Y, lw0, pr.Height), _t.SubText, np2);
                    _pubRect = new Rectangle(pr.X + lw0 - Px(3), pr.Y - Px(1), iw + Px(6), pr.Height + Px(2));
                    if (_hoverPub)
                        using (var hb = new SolidBrush(_t.Border)) using (var hp = Theme.RoundRect(_pubRect, Px(4))) g.FillPath(hb, hp);
                    TextRenderer.DrawText(g, _pub.Ip, _fSub, new Rectangle(pr.X + lw0, pr.Y, iw, pr.Height), _pubCopied ? _t.Success : _t.Accent, np2);
                    var tail = string.Join("  ·  ", new[] { _pubCopied ? "copied" : "", _pub.Isp, _pub.Place }.Where(x => x.Length > 0));
                    if (tail.Length > 0)
                        TextRenderer.DrawText(g, "  ·  " + tail, _fSub, new Rectangle(pr.X + lw0 + iw, pr.Y, Math.Max(0, pr.Right - pr.X - lw0 - iw), pr.Height), _t.SubText, nf);
                }
            }
        }
        PaintEntries(g);
    }

    void PaintEntries(Graphics g)
    {
        var flags = TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
        var np = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
        int lh = TextRenderer.MeasureText("Ag", _fHeading).Height, sh = TextRenderer.MeasureText("Ag", _fSub).Height;
        if (!_moreRect.IsEmpty)
        {
            var folded = _c.Entries.Where(en => en.Folded).ToList();
            if (_hoverMore)
                using (var hb = new SolidBrush(_t.SurfaceHover)) using (var hp = Theme.RoundRect(_moreRect, 5)) g.FillPath(hb, hp);
            int md = Px(8);
            using (var db = new SolidBrush(_t.Border)) g.FillEllipse(db, _moreRect.X + Px(8), _moreRect.Y + (_moreRect.Height - md) / 2, md, md);
            var link = _showFolded ? "Hide" : "Show";
            int lw = TextRenderer.MeasureText(g, link, _fSub, Size.Empty, np).Width;
            var mt = $"{folded.Count} more not connected or virtual: " + string.Join(", ", folded.Select(f => f.Name));
            int mx = _moreRect.X + Px(24);
            TextRenderer.DrawText(g, mt, _fSub, new Rectangle(mx, _moreRect.Y, _moreRect.Right - mx - lw - Px(16), _moreRect.Height), _t.SubText, flags | TextFormatFlags.VerticalCenter);
            TextRenderer.DrawText(g, link, _fSub, new Rectangle(_moreRect.Right - lw - Px(8), _moreRect.Y, lw, _moreRect.Height), _t.Accent, np | TextFormatFlags.VerticalCenter);
        }
        for (int i = 0; i < _c.Entries.Count && i < _entryRects.Count; i++)
        {
            var r = _entryRects[i];
            if (r.IsEmpty) { _gwRects.Remove(i); _ipRects.Remove(i); continue; }
            var entry = _c.Entries[i];
            var (name, line, copy, up, gw, rest) = entry;
            bool clickable = copy != null || entry.Click != null || entry.Actions != null;
            if (i == _hoverEntry && clickable && _hoverIp != i && _hoverGw != i)
                using (var hb = new SolidBrush(_t.SurfaceHover)) using (var hp = Theme.RoundRect(r, 5)) g.FillPath(hb, hp);
            int edot = Px(8);
            var dotRect = new Rectangle(r.X + Px(8), r.Y + Px(_c.Compact ? 4 : 5) + (lh - edot) / 2, edot, edot);
            if (up && _blinkOn.Contains(i))
            {
                var glow = Rectangle.Inflate(dotRect, Px(3), Px(3));
                using (var gb = new SolidBrush(Color.FromArgb(70, _t.Success))) g.FillEllipse(gb, glow);
                using (var lb = new SolidBrush(ControlPaint.Light(_t.Success, 0.6f))) g.FillEllipse(lb, dotRect);
            }
            else
                using (var db = new SolidBrush(up ? _t.Success : _t.Border)) g.FillEllipse(db, dotRect);

            int tx = r.X + Px(24), right = r.Right - Px(8);
            int ty = r.Y + Px(_c.Compact ? 4 : 5);
            // Right side of the name row: live speed, or the entry's badge/hint (profile list).
            string? rightText = null; Color rightColor = _t.SubText;
            if (i == _copiedEntry) { rightText = "Copied"; rightColor = _t.Success; }
            else if (entry.Badge != null) { rightText = entry.Badge; rightColor = _t.Accent; }
            else if (i == _hoverEntry && entry.Click != null) rightText = entry.Hint ?? "Switch";
            else if (up && (_rates.TryGetValue(name, out var rate) || (TestRates?.TryGetValue(name, out rate) ?? false))) rightText = rate;
            int rw = rightText == null ? 0 : TextRenderer.MeasureText(g, rightText, _fSub, Size.Empty, np).Width;
            if (rightText != null)
                TextRenderer.DrawText(g, rightText, _fSub, new Rectangle(right - rw, ty, rw, lh), rightColor, np | TextFormatFlags.VerticalCenter);
            int tagW = entry.Tag == null ? 0 : TextRenderer.MeasureText(g, entry.Tag, _fSub, Size.Empty, np).Width + Px(10);
            int nameMax = _c.Compact ? Px(118) : right - rw - Px(10) - tx - (tagW > 0 ? tagW + Px(8) : 0);
            int nw = Math.Min(nameMax, TextRenderer.MeasureText(g, name, _fHeading, Size.Empty, np).Width);
            TextRenderer.DrawText(g, name, _fHeading, new Rectangle(tx, ty, nw, lh), _t.Text, flags);
            int cx = tx + nw + Px(8);
            if (_c.Compact)
            {
                // IP in a column, then the tag.
                cx = tx + Px(126);
                if (copy != null || !up)
                {
                    var ipText = copy ?? "not connected";
                    int iw = Math.Min(TextRenderer.MeasureText(g, ipText, _fSub, Size.Empty, np).Width, Math.Max(0, right - rw - Px(10) - cx));
                    var ir = new Rectangle(cx - Px(3), ty + (lh - sh) / 2 - Px(1), iw + Px(6), sh + Px(2));
                    if (copy != null) { _ipRects[i] = ir; if (i == _hoverIp) using (var hb = new SolidBrush(_t.Border)) using (var hp = Theme.RoundRect(ir, Px(4))) g.FillPath(hb, hp); }
                    TextRenderer.DrawText(g, ipText, _fSub, new Rectangle(cx, ty, iw, lh), copy != null ? _t.Text : _t.SubText, flags | TextFormatFlags.VerticalCenter);
                    cx += iw + Px(8);
                }
            }
            if (entry.Tag != null && cx + tagW + Px(4) <= right - rw)
            {
                int tw2 = tagW;
                var tr = new Rectangle(cx, ty + (lh - sh) / 2 - Px(1), tw2, sh + Px(2));
                var tagColor = entry.Tag == "Internet" ? _t.Accent : _t.SubText;
                using (var tp = new Pen(tagColor)) using (var path = Theme.RoundRect(tr, Px(4))) g.DrawPath(tp, path);
                TextRenderer.DrawText(g, entry.Tag, _fSub, tr, tagColor, np | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }

            int ly = ty + lh;
            if (!_c.Compact)
            {
                var lineRect = new Rectangle(tx, ly, right - tx, sh);
                int px = lineRect.X;
                void Part(string text, Color color, bool last = false)
                {
                    int pw = last ? lineRect.Right - px : TextRenderer.MeasureText(g, text, _fSub, Size.Empty, np).Width;
                    TextRenderer.DrawText(g, text, _fSub, new Rectangle(px, lineRect.Y, Math.Max(0, Math.Min(pw, lineRect.Right - px)), lineRect.Height), color, last ? flags : np);
                    px += pw;
                }
                // The address: its own click target (copies), drawn brighter than the rest.
                if (copy != null)
                {
                    int iw = TextRenderer.MeasureText(g, line, _fSub, Size.Empty, np).Width;
                    var ir = new Rectangle(px - Px(4), ly - Px(1), iw + Px(8), sh + Px(2));
                    _ipRects[i] = ir;
                    if (i == _hoverIp)
                        using (var hb = new SolidBrush(_t.Border)) using (var hp = Theme.RoundRect(ir, Px(4))) g.FillPath(hb, hp);
                    Part(line, _t.Text);
                }
                else { _ipRects.Remove(i); Part(line, _t.SubText, last: gw == null && rest.Length == 0); }
                if (gw != null)
                {
                    Part("  ·  ", _t.SubText);
                    var gwText = "gw " + gw;
                    int gwW = TextRenderer.MeasureText(g, gwText, _fSub, Size.Empty, np).Width;
                    var gr = new Rectangle(px - Px(4), ly - Px(1), gwW + Px(8), sh + Px(2));
                    _gwRects[i] = gr;
                    if (i == _hoverGw)
                        using (var hb = new SolidBrush(_t.Border)) using (var hp = Theme.RoundRect(gr, Px(4))) g.FillPath(hb, hp);
                    Part(gwText, _t.Accent);
                }
                else _gwRects.Remove(i);
                if (rest.Length > 0) Part("  ·  " + rest, _t.SubText, last: true);
                ly += sh;
                if (entry.Line2 != null)
                {
                    int bx = tx;
                    if (entry.Signal >= 0)
                    {
                        int bw = Px(3), gap = Px(1.5f), bh = Px(10), by = ly + (sh - bh) / 2;
                        for (int b = 0; b < 4; b++)
                        {
                            int h2 = bh * (b + 1) / 4;
                            using var bb = new SolidBrush(b < entry.Signal ? _t.Accent : _t.Border);
                            g.FillRectangle(bb, bx + b * (bw + gap), by + bh - h2, bw, h2);
                        }
                        bx += 4 * (bw + gap) + Px(6);
                    }
                    TextRenderer.DrawText(g, entry.Line2, _fSub, new Rectangle(bx, ly, right - bx, sh), _t.SubText, flags);
                    ly += sh;
                }
            }
            if (entry.Warning != null)
                TextRenderer.DrawText(g, entry.Warning, _fSub, new Rectangle(tx, ly, right - tx, sh), Color.FromArgb(0xE0, 0x9B, 0x1A), flags);
        }
    }

    /// <summary>Push-pin icon: outlined when off, filled in the accent colour when pinned.</summary>
    void DrawPin(Graphics g, Rectangle r)
    {
        if (_hoverPin || Pinned)
            using (var hb = new SolidBrush(_hoverPin ? _t.SurfaceHover : Color.Transparent)) using (var hp = Theme.RoundRect(r, 4)) g.FillPath(hb, hp);
        var color = Pinned ? _t.Accent : _t.SubText;
        var state = g.Save();
        g.TranslateTransform(r.X + r.Width / 2f, r.Y + r.Height / 2f);
        g.RotateTransform(45);
        float u = S;
        using var pen = new Pen(color, Math.Max(1.2f, 1.3f * u)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        using var fill = new SolidBrush(color);
        // head, collar, needle (drawn upright, rotated 45 degrees)
        var head = new RectangleF(-3.2f * u, -7f * u, 6.4f * u, 6f * u);
        var collar = new RectangleF(-5f * u, -1.5f * u, 10f * u, 2f * u);
        if (Pinned) { g.FillRectangle(fill, head); g.FillRectangle(fill, collar); }
        else { g.DrawRectangle(pen, head.X, head.Y, head.Width, head.Height); g.DrawRectangle(pen, collar.X, collar.Y, collar.Width, collar.Height); }
        g.DrawLine(pen, 0, 0.5f * u, 0, 7f * u);
        g.Restore(state);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        bool h = _closeRect.Contains(e.Location);
        if (h != _closeHover) { _closeHover = h; Invalidate(_closeRect); }
        int he = _entryRects.FindIndex(r => r.Contains(e.Location));
        int hg = GatewayAt(e.Location);
        bool hp = _pub != null && _pubRect.Contains(e.Location);
        int hi = IpAt(e.Location);
        bool hm = _moreRect.Contains(e.Location);
        bool hpin = _pinRect.Contains(e.Location);
        if (he != _hoverEntry || hg != _hoverGw || hp != _hoverPub || hi != _hoverIp || hm != _hoverMore || hpin != _hoverPin)
        { _hoverEntry = he; _hoverGw = hg; _hoverPub = hp; _hoverIp = hi; _hoverMore = hm; _hoverPin = hpin; Invalidate(); }
        Cursor = h || hg >= 0 || hp || hi >= 0 || hm || hpin || (he >= 0 && (_c.Entries[he].Copy != null || _c.Entries[he].Click != null || _c.Entries[he].Actions != null)) ? Cursors.Hand : Cursors.Default;
    }

    protected override void OnMouseLeave(EventArgs e) { _closeHover = false; _hoverEntry = -1; _hoverGw = -1; _hoverIp = -1; _hoverMore = false; _hoverPin = false; Invalidate(); }

    int IpAt(Point p)
    {
        foreach (var (i, r) in _ipRects) if (!_entryRects[i].IsEmpty && r.Contains(p)) return i;
        return -1;
    }

    int GatewayAt(Point p)
    {
        foreach (var (i, r) in _gwRects) if (r.Contains(p)) return i;
        return -1;
    }

    // Which menu was open and when it closed: the click that closes a menu also reaches us,
    // and that click must not open the same menu again.
    string? _menuKey;
    DateTime _menuClosedAt;

    bool ToggleOff(string key)
    {
        bool open = _toolsMenu is { Visible: true };
        bool justClosed = (DateTime.UtcNow - _menuClosedAt).TotalMilliseconds < 400;
        if (_menuKey == key && (open || justClosed))
        {
            _toolsMenu?.Close();
            _menuKey = null;
            return true;
        }
        return false;
    }

    ContextMenuStrip NewMenu(string key)
    {
        _toolsMenu?.Dispose();
        var m = new ContextMenuStrip { ShowImageMargin = false, Font = _fBody, Renderer = new MenuRenderer(_t), BackColor = _t.Surface, Padding = new Padding(0, 4, 0, 4) };
        m.Closed += (_, _) => { _menuClosedAt = DateTime.UtcNow; };
        _menuKey = key;
        _toolsMenu = m;
        return m;
    }

    void ShowActions(string header, List<PopupAction> actions, Point at)
    {
        var m = NewMenu("entry:" + header);
        m.Items.Add(new ToolStripMenuItem(header) { Enabled = false, Tag = "header", Padding = new Padding(4, 4, 4, 2) });
        foreach (var a in actions)
        {
            if (a.Text == null) { m.Items.Add(new ToolStripSeparator()); continue; }
            var run = a.Run;
            // Run after the menu has closed and returned: the action may close this pop-up (and its menu) or open a dialog.
            var ctx = SynchronizationContext.Current;
            m.Items.Add(new ToolStripMenuItem(a.Text, null, (_, _) => { if (run != null) { if (ctx != null) ctx.Post(_ => run(), null); else run(); } })
            { Padding = new Padding(4, 5, 4, 5), ShortcutKeyDisplayString = a.Detail, ShowShortcutKeys = a.Detail.Length > 0, Enabled = run != null });
        }
        m.Show(this, at);
    }

    void ShowGatewayTools(int i)
    {
        var gw = _c.Entries[i].Gateway!;
        var m = NewMenu("gw:" + i);
        ToolStripMenuItem Item(string text, string detail, string tool)
        {
            var it = new ToolStripMenuItem(text, null, (_, _) => { ToolRequested?.Invoke(tool, gw); if (tool == "copy") { _copiedEntry = -1; } })
            { Padding = new Padding(4, 5, 4, 5), ShortcutKeyDisplayString = detail, ShowShortcutKeys = detail.Length > 0 };
            return it;
        }
        var header = new ToolStripMenuItem($"Gateway {gw}") { Enabled = false, Tag = "header", Padding = new Padding(4, 4, 4, 2) };
        m.Items.Add(header);
        m.Items.Add(Item("Ping continuously", "ping -t", "ping-t"));
        m.Items.Add(Item("Ping 4 times", "ping", "ping"));
        m.Items.Add(Item("Trace route", "tracert -d", "tracert"));
        m.Items.Add(Item("Path ping", "pathping -n", "pathping"));
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add(Item("Open in browser", "http://", "browser"));
        m.Items.Add(Item("Copy address", "", "copy"));
        var r = _gwRects[i];
        m.Show(this, new Point(r.Left, r.Bottom + Px(2)));
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        if (_closeRect.Contains(e.Location)) { BeginClose(); return; }
        if (_pinRect.Contains(e.Location)) { Pinned = !Pinned; Invalidate(_pinRect); return; }
        if (_moreRect.Contains(e.Location))
        {
            _showFolded = !_showFolded;
            LayoutContent();
            Restack();
            Invalidate();
            return;
        }
        int ipHit = IpAt(e.Location);
        if (ipHit >= 0 && _c.Entries[ipHit].Copy is { } ipc)
        {
            try { Clipboard.SetText(ipc); _copiedEntry = ipHit; Invalidate(); } catch { }
            return;
        }
        int g = GatewayAt(e.Location);
        if (g >= 0) { if (!ToggleOff("gw:" + g)) ShowGatewayTools(g); return; }
        if (_pub != null && _pubRect.Contains(e.Location))
        {
            try { Clipboard.SetText(_pub.Ip); _pubCopied = true; Invalidate(_netRect); } catch { }
            return;
        }
        int i = _entryRects.FindIndex(r => r.Contains(e.Location));
        if (i >= 0 && _c.Entries[i].Click is { } click) { BeginClose(); click(); return; }
        if (i >= 0 && _c.Entries[i].Actions is { Count: > 0 } actions)
        {
            if (!ToggleOff("entry:" + _c.Entries[i].Name)) ShowActions(_c.Entries[i].Name, actions, new Point(e.X, e.Y + Px(4)));
            return;
        }
        if (i >= 0 && _c.Entries[i].Copy is { } ip)
        {
            try { Clipboard.SetText(ip); _copiedEntry = i; Invalidate(); } catch { }
        }
    }
}
