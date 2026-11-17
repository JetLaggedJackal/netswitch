using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace NetSwitch;

public enum PopupKind { Info, Success, Error, Change }

public sealed class PopupContent
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
    /// <summary>Optional main button (replaces "Copy IP").</summary>
    public string? ActionText { get; init; }
    public Action? Action { get; init; }
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
    ContextMenuStrip? _toolsMenu;
    public static Action<string, string>? ToolRequested; // (tool, ip)

    readonly PopupContent _c;
    readonly Theme _t = Theme.Current;
    readonly System.Windows.Forms.Timer _life = new();
    readonly System.Windows.Forms.Timer _fade = new() { Interval = 15 };
    readonly Font _fTitle, _fHeading, _fSub, _fBig, _fBody, _fLabel, _fSuffix;
    readonly ModernButton? _copy;
    readonly ModernButton _open;
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
                if (down && !wasDown && !inMenu && !Bounds.Contains(Cursor.Position)) BeginClose();
                wasDown = down;
            };
        }
        _open = new ModernButton(_t, "Settings…") { Font = _fBody, TabStop = false };
        _open.Click += (_, _) => { OpenProfilesRequested?.Invoke(); BeginClose(); };
        Controls.Add(_open);

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
        _life.Dispose(); _fade.Dispose(); _outside.Dispose(); _toolsMenu?.Dispose();
        base.OnFormClosed(e);
    }

    // ---------- layout & paint ----------

    int Pad => Px(18);

    void LayoutContent()
    {
        int width = Px(380);
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
        _entryRects.Clear();
        if (_c.Entries.Count > 0)
        {
            y += Px(10);
            int eh = TextRenderer.MeasureText("Ag", _fHeading).Height + TextRenderer.MeasureText("Ag", _fSub).Height + Px(10);
            foreach (var _ in _c.Entries)
            {
                _entryRects.Add(new Rectangle(Pad - Px(8), y, width - Pad * 2 + Px(16), eh));
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
        foreach (var b in new[] { _open, _copy })
        {
            if (b == null) continue;
            var w = Math.Max(Px(88), TextRenderer.MeasureText(b.Text, b.Font).Width + Px(28));
            x -= w;
            b.SetBounds(x, y, w, btnH);
            x -= Px(8);
        }
        y += btnH + Pad;
        Size = new Size(width, y);
        _closeRect = new Rectangle(width - Pad - Px(20) + Px(4), Pad - Px(3), Px(24), Px(24));
    }

    int LabelWidth() => Px(92);

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

        // Title row: app icon dot, title, time, close.
        int dot = Px(10);
        using (var b = new SolidBrush(accent)) g.FillEllipse(b, x, y + Px(5), dot, dot);
        var titleRect = new Rectangle(x + dot + Px(8), y, w - Px(90), Px(20));
        TextRenderer.DrawText(g, _c.Title, _fTitle, titleRect, _t.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        var time = DateTime.Now.ToString("HH:mm");
        var tw = TextRenderer.MeasureText(time, _fSub).Width;
        TextRenderer.DrawText(g, time, _fSub, new Rectangle(_closeRect.Left - tw - Px(6), y, tw, Px(20)), _t.SubText, TextFormatFlags.VerticalCenter);
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
        for (int i = 0; i < _c.Entries.Count && i < _entryRects.Count; i++)
        {
            var (name, line, copy, up, gw, rest) = _c.Entries[i];
            var r = _entryRects[i];
            var entry = _c.Entries[i];
            bool clickable = copy != null || entry.Click != null;
            if (i == _hoverEntry && clickable)
                using (var hb = new SolidBrush(_t.SurfaceHover)) using (var hp = Theme.RoundRect(r, 5)) g.FillPath(hb, hp);
            int lh = TextRenderer.MeasureText("Ag", _fHeading).Height;
            int edot = Px(8);
            using (var db = new SolidBrush(up ? _t.Success : _t.Border))
                g.FillEllipse(db, r.X + Px(8), r.Y + Px(5) + (lh - edot) / 2, edot, edot);
            int tx = r.X + Px(24), etw = r.Width - Px(32);
            var flags = TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
            TextRenderer.DrawText(g, name, _fHeading, new Rectangle(tx, r.Y + Px(5), etw - Px(60), lh), _t.Text, flags);
            if (i == _copiedEntry || (i == _hoverEntry && clickable) || entry.Badge != null)
            {
                var hint = i == _copiedEntry ? "Copied" : entry.Badge ?? entry.Hint ?? "Copy IP";
                var hc = i == _copiedEntry ? _t.Success : entry.Badge != null ? _t.Accent : _t.SubText;
                TextRenderer.DrawText(g, hint, _fSub, new Rectangle(tx, r.Y + Px(5), etw, lh), hc,
                    TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
            var lineRect = new Rectangle(tx, r.Y + Px(5) + lh, etw, r.Height - lh - Px(5));
            if (gw == null)
            {
                _gwRects.Remove(i);
                var full = rest.Length > 0 ? $"{line}  ·  {rest}" : line;
                TextRenderer.DrawText(g, full, _fSub, lineRect, _t.SubText, flags);
            }
            else
            {
                // "<ip>  ·  " then the gateway as a link, then the rest.
                var np = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
                int cx = lineRect.X;
                void Part(string text, Color color, bool last = false)
                {
                    int pw = last ? lineRect.Right - cx : TextRenderer.MeasureText(g, text, _fSub, Size.Empty, np).Width;
                    TextRenderer.DrawText(g, text, _fSub, new Rectangle(cx, lineRect.Y, Math.Max(0, Math.Min(pw, lineRect.Right - cx)), lineRect.Height), color, last ? flags : np);
                    cx += pw;
                }
                Part(line + "  ·  ", _t.SubText);
                var gwText = "gw " + gw;
                int gwW = TextRenderer.MeasureText(g, gwText, _fSub, Size.Empty, np).Width;
                int sh = TextRenderer.MeasureText("Ag", _fSub).Height;
                var gr = new Rectangle(cx - Px(4), lineRect.Y - Px(1), gwW + Px(8), sh + Px(2));
                _gwRects[i] = gr;
                if (i == _hoverGw)
                    using (var hb = new SolidBrush(_t.Border)) using (var hp = Theme.RoundRect(gr, Px(4))) g.FillPath(hb, hp);
                Part(gwText, _t.Accent);
                if (rest.Length > 0) Part("  ·  " + rest, _t.SubText, last: true);
            }
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        bool h = _closeRect.Contains(e.Location);
        if (h != _closeHover) { _closeHover = h; Invalidate(_closeRect); }
        int he = _entryRects.FindIndex(r => r.Contains(e.Location));
        int hg = GatewayAt(e.Location);
        if (he != _hoverEntry || hg != _hoverGw) { _hoverEntry = he; _hoverGw = hg; Invalidate(); }
        Cursor = h || hg >= 0 || (he >= 0 && (_c.Entries[he].Copy != null || _c.Entries[he].Click != null)) ? Cursors.Hand : Cursors.Default;
    }

    protected override void OnMouseLeave(EventArgs e) { _closeHover = false; _hoverEntry = -1; _hoverGw = -1; Invalidate(); }

    int GatewayAt(Point p)
    {
        foreach (var (i, r) in _gwRects) if (r.Contains(p)) return i;
        return -1;
    }

    void ShowGatewayTools(int i)
    {
        var gw = _c.Entries[i].Gateway!;
        _toolsMenu?.Dispose();
        var m = _toolsMenu = new ContextMenuStrip { ShowImageMargin = false, Font = _fBody, Renderer = new MenuRenderer(_t), BackColor = _t.Surface, Padding = new Padding(0, 4, 0, 4) };
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
        int g = GatewayAt(e.Location);
        if (g >= 0) { ShowGatewayTools(g); return; }
        int i = _entryRects.FindIndex(r => r.Contains(e.Location));
        if (i >= 0 && _c.Entries[i].Click is { } click) { BeginClose(); click(); return; }
        if (i >= 0 && _c.Entries[i].Copy is { } ip)
        {
            try { Clipboard.SetText(ip); _copiedEntry = i; Invalidate(); } catch { }
        }
    }
}
