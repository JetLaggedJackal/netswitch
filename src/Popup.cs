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
}

/// <summary>Custom notification window: bottom-right, no focus stealing, stacks, rich content.</summary>
public sealed class PopupForm : Form
{
    static readonly List<PopupForm> Open = new();
    public static Action? OpenProfilesRequested;

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

    public static void Show(PopupContent c, int seconds)
    {
        var f = new PopupForm(c, seconds);
        f.ShowPopup();
    }

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

        if (!string.IsNullOrEmpty(c.CopyText))
        {
            _copy = new ModernButton(_t, "Copy IP", primary: true) { Font = _fBody, TabStop = false };
            _copy.Click += (_, _) =>
            {
                try { Clipboard.SetText(c.CopyText!); _copy.Text = "Copied"; } catch { }
            };
            Controls.Add(_copy);
        }
        _open = new ModernButton(_t, "Profiles…") { Font = _fBody, TabStop = false };
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
        _life.Dispose(); _fade.Dispose();
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
        _contentHeight = y;
        int btnH = Px(32);
        y += Px(12);
        int x = width - Pad;
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
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        bool h = _closeRect.Contains(e.Location);
        if (h != _closeHover) { _closeHover = h; Invalidate(_closeRect); }
        Cursor = h ? Cursors.Hand : Cursors.Default;
    }

    protected override void OnMouseLeave(EventArgs e) { _closeHover = false; Invalidate(); }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        if (_closeRect.Contains(e.Location)) BeginClose();
    }
}
