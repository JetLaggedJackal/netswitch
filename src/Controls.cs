using System.Diagnostics.CodeAnalysis;
using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace NetSwitch;

/// <summary>A section of the page: flat, no box, just spacing (minimal layout).</summary>
public class Card : Panel
{
    readonly Theme _t;
    public Card(Theme t)
    {
        _t = t;
        BackColor = t.Panel;
        Padding = new Padding(0);
    }
    protected override void OnControlAdded(ControlEventArgs e)
    {
        base.OnControlAdded(e);
        if (e.Control is Label or CheckBox or FlowLayoutPanel or TableLayoutPanel) e.Control.BackColor = _t.Panel;
    }
}

/// <summary>Text box with rounded border and an accent underline when focused (Windows 11 style).</summary>
[DefaultEvent(nameof(TextChanged))]
public class ModernTextBox : Control
{
    readonly Theme _t;
    readonly TextBox _box;
    public ModernTextBox(Theme t)
    {
        _t = t;
        DoubleBuffered = true;
        SetStyle(ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        _box = new TextBox { BorderStyle = BorderStyle.None, BackColor = t.Surface, ForeColor = t.Text };
        _box.TextChanged += (_, _) => OnTextChanged(EventArgs.Empty);
        _box.GotFocus += (_, _) => Invalidate();
        _box.LostFocus += (_, _) => Invalidate();
        Controls.Add(_box);
        BackColor = t.Window;
        Height = 30;
        Cursor = Cursors.IBeam;
    }
    [AllowNull]
    public override string Text { get => _box.Text; set => _box.Text = value ?? ""; }
    public string PlaceholderText { get => _box.PlaceholderText; set => _box.PlaceholderText = value; }
    public new bool Focused => _box.Focused;
    public new void Focus() => _box.Focus();
    public void SelectAll() => _box.SelectAll();
    public override Font Font { get => base.Font; set { base.Font = value; _box.Font = value; LayoutBox(); } }
    protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); _box.Font = Font; LayoutBox(); }
    protected override void OnResize(EventArgs e) { base.OnResize(e); LayoutBox(); }
    // DPI scaling moves the inner box after our own layout, so re-centre it on every layout pass.
    protected override void OnLayout(LayoutEventArgs e) { base.OnLayout(e); LayoutBox(); }
    protected override void OnClick(EventArgs e) { base.OnClick(e); _box.Focus(); }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); _box.Focus(); }
    protected override void OnEnabledChanged(EventArgs e)
    {
        base.OnEnabledChanged(e);
        _box.Enabled = Enabled;
        _box.BackColor = Enabled ? _t.Surface : _t.Window;
        Invalidate();
    }
    void LayoutBox()
    {
        if (_box == null) return;
        int pad = LogicalToDeviceUnits(10);
        _box.Width = Math.Max(10, Width - pad * 2);
        _box.Location = new Point(pad, Math.Max(2, (Height - _box.Height) / 2));
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? _t.Window);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        using (var path = Theme.RoundRect(r, 5))
        using (var b = new SolidBrush(Enabled ? _t.Surface : _t.Window))
        using (var p = new Pen(_t.Border))
        {
            g.FillPath(b, path);
            g.DrawPath(p, path);
        }
        if (_box.Focused)
        {
            g.SmoothingMode = SmoothingMode.None;
            using var acc = new Pen(_t.Accent, 2);
            g.DrawLine(acc, 4, Height - 2, Width - 5, Height - 2);
        }
    }
}

/// <summary>Flat rounded button. Primary = filled accent; otherwise subtle.</summary>
public class ModernButton : Button
{
    readonly Theme _t;
    bool _hover, _down;
    public bool Primary { get; set; }
    public bool Ghost { get; set; } // no border, used for icon-ish/text buttons
    public ModernButton(Theme t, string text, bool primary = false)
    {
        _t = t;
        Text = text;
        Primary = primary;
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        Height = 30;
        MinimumSize = new Size(76, 30);
        Cursor = Cursors.Hand;
        AutoSize = false;
        Padding = new Padding(12, 0, 12, 0);
    }
    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; _down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { _down = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }
    public override Size GetPreferredSize(Size proposed)
    {
        var s = TextRenderer.MeasureText(Text, Font);
        return new Size(Math.Max(MinimumSize.Width, s.Width + Padding.Horizontal + 8), Math.Max(MinimumSize.Height, s.Height + 10));
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? _t.Window);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Color fill, border, text;
        if (Primary)
        {
            fill = _down ? Theme.Blend(_t.Accent, _t.Window, 0.8f) : _hover ? Theme.Blend(_t.Accent, _t.Window, 0.9f) : _t.Accent;
            border = fill; text = _t.AccentText;
        }
        else if (Ghost)
        {
            var parent = Parent?.BackColor ?? _t.Window;
            fill = _down ? _t.Border : _hover ? _t.SurfaceHover : parent;
            border = fill; text = Enabled ? _t.Accent : _t.SubText;
        }
        else
        {
            fill = _down ? _t.Window : _hover ? _t.SurfaceHover : _t.Surface;
            border = _t.Border; text = _t.Text;
        }
        if (!Enabled) { text = _t.SubText; if (Primary) fill = border = _t.Border; }
        using (var path = Theme.RoundRect(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), 5))
        using (var b = new SolidBrush(fill))
        using (var p = new Pen(border))
        {
            g.FillPath(b, path);
            g.DrawPath(p, path);
        }
        TextRenderer.DrawText(g, Text, Font, ClientRectangle, text,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        if (Focused && ShowFocusCues)
        {
            using var fp = new Pen(_t.Text) { DashStyle = DashStyle.Dot };
            g.SmoothingMode = SmoothingMode.None;
            g.DrawRectangle(fp, 3, 3, Width - 7, Height - 7);
        }
    }
}

/// <summary>Two-option pill selector, e.g. Automatic | Manual.</summary>
public class SegmentedControl : Control
{
    readonly Theme _t;
    readonly string[] _options;
    int _selected, _hover = -1;
    public event EventHandler? SelectedIndexChanged;
    public SegmentedControl(Theme t, params string[] options)
    {
        _t = t; _options = options;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        Height = 30;
        Cursor = Cursors.Hand;
        TabStop = true;
        AutoSize = true;
    }
    public override Size GetPreferredSize(Size proposed)
    {
        int seg = _options.Max(o => TextRenderer.MeasureText(o, Font).Width) + LogicalToDeviceUnits(28);
        return new Size(seg * _options.Length + 7, Height);
    }
    protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); if (AutoSize) Size = GetPreferredSize(Size.Empty); }
    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); if (AutoSize) Size = GetPreferredSize(Size.Empty); }
    public int SelectedIndex
    {
        get => _selected;
        set { if (value == _selected) return; _selected = value; Invalidate(); SelectedIndexChanged?.Invoke(this, EventArgs.Empty); }
    }
    RectangleF Seg(int i)
    {
        float w = (Width - 7f) / _options.Length;
        return new RectangleF(3 + i * w, 3, w, Height - 7);
    }
    int HitTest(Point p) { for (int i = 0; i < _options.Length; i++) if (Seg(i).Contains(p)) return i; return -1; }
    protected override void OnMouseMove(MouseEventArgs e) { var h = HitTest(e.Location); if (h != _hover) { _hover = h; Invalidate(); } }
    protected override void OnMouseLeave(EventArgs e) { _hover = -1; Invalidate(); }
    protected override void OnMouseDown(MouseEventArgs e) { Focus(); var h = HitTest(e.Location); if (h >= 0 && Enabled) SelectedIndex = h; }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
    protected override bool IsInputKey(Keys k) => k is Keys.Left or Keys.Right || base.IsInputKey(k);
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Left && _selected > 0) SelectedIndex--;
        if (e.KeyCode == Keys.Right && _selected < _options.Length - 1) SelectedIndex++;
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? _t.Window);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var path = Theme.RoundRect(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), 6))
        using (var b = new SolidBrush(_t.Window))
        using (var p = new Pen(Focused ? _t.Accent : _t.Border))
        {
            g.FillPath(b, path);
            g.DrawPath(p, path);
        }
        for (int i = 0; i < _options.Length; i++)
        {
            var r = Seg(i);
            bool sel = i == _selected;
            if (sel || i == _hover)
            {
                using var path = Theme.RoundRect(r, 4);
                using var b = new SolidBrush(sel ? _t.Accent : _t.SurfaceHover);
                g.FillPath(b, path);
            }
            TextRenderer.DrawText(g, _options[i], Font, Rectangle.Round(r),
                sel ? _t.AccentText : (Enabled ? _t.Text : _t.SubText),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }
    }
}

/// <summary>On/off switch with a label to its right.</summary>
public class ToggleSwitch : Control
{
    readonly Theme _t;
    bool _on;
    public event EventHandler? CheckedChanged;
    public ToggleSwitch(Theme t, string text)
    {
        _t = t; Text = text;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        Height = 28; Cursor = Cursors.Hand; TabStop = true;
    }
    public bool Checked { get => _on; set { if (_on == value) return; _on = value; Invalidate(); CheckedChanged?.Invoke(this, EventArgs.Empty); } }
    protected override void OnClick(EventArgs e) { base.OnClick(e); Focus(); Checked = !Checked; }
    protected override void OnKeyDown(KeyEventArgs e) { if (e.KeyCode == Keys.Space) Checked = !Checked; }
    protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Invalidate(); }

    // Never narrower than the switch plus its label, whatever the DPI.
    int MinWidth => string.IsNullOrEmpty(Text) ? 0
        : LogicalToDeviceUnits(56) + TextRenderer.MeasureText(Text, Font).Width + LogicalToDeviceUnits(8);
    protected override void SetBoundsCore(int x, int y, int width, int height, BoundsSpecified specified)
        => base.SetBoundsCore(x, y, Math.Max(width, MinWidth), height, specified);
    protected override void OnDpiChangedAfterParent(EventArgs e) { base.OnDpiChangedAfterParent(e); Width = Width; }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? _t.Window);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float s = DeviceDpi / 96f;
        float h = 20 * s, w = 40 * s, y = (Height - h) / 2;
        var track = new RectangleF(1, y, w, h);
        using (var path = Theme.RoundRect(track, h / 2))
        {
            if (_on) { using var b = new SolidBrush(_t.Accent); g.FillPath(b, path); }
            else { using var p = new Pen(_t.SubText, 1.2f * s); g.DrawPath(p, path); }
        }
        float d = (_on ? 12 : 10) * s;
        float x = _on ? track.Right - d - 5 * s : track.X + 5 * s;
        using (var knob = new SolidBrush(_on ? _t.AccentText : _t.SubText))
            g.FillEllipse(knob, x, y + (h - d) / 2, d, d);
        int tx = (int)(w + 12 * s);
        var textRect = new Rectangle(tx, 0, Width - tx, Height);
        TextRenderer.DrawText(g, Text, Font, textRect, _t.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
    }
}

/// <summary>Themed drop-down: looks like the text boxes, opens a styled menu.</summary>
public class ModernCombo : Control
{
    readonly Theme _t;
    readonly ContextMenuStrip _menu;
    object? _selected;
    bool _hover, _open;
    public List<object> Items { get; } = new();
    public Func<object, (string main, string? sub)>? Describe { get; set; }
    public event EventHandler? SelectedIndexChanged;

    public ModernCombo(Theme t)
    {
        _t = t;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        Height = 30;
        Cursor = Cursors.Hand;
        TabStop = true;
        _menu = new ContextMenuStrip { ShowCheckMargin = true, ShowImageMargin = false, Renderer = new MenuRenderer(t), BackColor = t.Surface };
        _menu.HandleCreated += (_, _) => MenuRenderer.RoundCorners(_menu);
        _menu.Closed += (_, _) => { _open = false; Invalidate(); };
    }

    public object? SelectedItem
    {
        get => _selected;
        set
        {
            if (Equals(_selected, value)) return;
            _selected = value;
            Invalidate();
            SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    (string main, string? sub) Text2(object o) => Describe?.Invoke(o) ?? (o.ToString() ?? "", null);

    void OpenMenu()
    {
        if (!Enabled || Items.Count == 0) return;
        _menu.Items.Clear();
        _menu.Font = Font;
        foreach (var it in Items)
        {
            var (main, sub) = Text2(it);
            var item = new ToolStripMenuItem(main)
            {
                Checked = Equals(it, _selected),
                ShortcutKeyDisplayString = sub,
                ShowShortcutKeys = sub != null,
                Padding = new Padding(4, 4, 4, 4),
            };
            var value = it;
            item.Click += (_, _) => SelectedItem = value;
            _menu.Items.Add(item);
        }
        _menu.MinimumSize = new Size(Width, 0);
        _open = true;
        Invalidate();
        _menu.Show(this, new Point(0, Height + 2));
    }

    protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); Focus(); if (e.Button == MouseButtons.Left) OpenMenu(); }
    protected override bool IsInputKey(Keys k) => k is Keys.Down or Keys.Up || base.IsInputKey(k);
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        int i = _selected == null ? -1 : Items.IndexOf(_selected);
        if (e.KeyCode == Keys.Down && i < Items.Count - 1) SelectedItem = Items[i + 1];
        else if (e.KeyCode == Keys.Up && i > 0) SelectedItem = Items[i - 1];
        else if (e.KeyCode is Keys.Space or Keys.F4 || (e.Alt && e.KeyCode == Keys.Down)) OpenMenu();
    }
    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? _t.Window);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        using (var path = Theme.RoundRect(r, 5))
        using (var b = new SolidBrush(_hover && !_open ? _t.SurfaceHover : _t.Surface))
        using (var p = new Pen(_t.Border))
        {
            g.FillPath(b, path);
            g.DrawPath(p, path);
        }
        if (Focused || _open)
        {
            g.SmoothingMode = SmoothingMode.None;
            using var acc = new Pen(_t.Accent, 2);
            g.DrawLine(acc, 4, Height - 2, Width - 5, Height - 2);
            g.SmoothingMode = SmoothingMode.AntiAlias;
        }
        int pad = LogicalToDeviceUnits(10), chevron = LogicalToDeviceUnits(24);
        var textRect = new Rectangle(pad, 0, Width - pad - chevron, Height);
        if (_selected != null)
        {
            var (main, sub) = Text2(_selected);
            var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
            var mw = Math.Min(textRect.Width, TextRenderer.MeasureText(g, main, Font, textRect.Size, flags).Width);
            TextRenderer.DrawText(g, main, Font, new Rectangle(textRect.X, 0, mw, Height), _t.Text, flags);
            if (!string.IsNullOrEmpty(sub) && textRect.Width - mw > 30)
                TextRenderer.DrawText(g, sub, Font, new Rectangle(textRect.X + mw + LogicalToDeviceUnits(8), 0, textRect.Width - mw - LogicalToDeviceUnits(8), Height), _t.SubText, flags);
        }
        // chevron
        float cx = Width - chevron / 2f - 2, cy = Height / 2f, s = LogicalToDeviceUnits(4);
        using var cp = new Pen(_t.SubText, Math.Max(1.2f, DeviceDpi / 96f * 1.2f));
        g.DrawLines(cp, new[] { new PointF(cx - s, cy - s / 2), new PointF(cx, cy + s / 2), new PointF(cx + s, cy - s / 2) });
    }
}

public static class Ui
{
    public static Label Label(Theme t, string text, float size = 9.75f, FontStyle style = FontStyle.Regular, bool sub = false) => new()
    {
        Text = text, AutoSize = true, Font = Theme.UiFont(size, style),
        ForeColor = sub ? t.SubText : t.Text, BackColor = Color.Transparent,
        UseCompatibleTextRendering = false,
    };

    /// <summary>A small caption above a field.</summary>
    public static Control Field(Theme t, string caption, Control input)
    {
        var p = new TableLayoutPanel { ColumnCount = 1, RowCount = 2, Dock = DockStyle.Fill, AutoSize = true, Margin = new Padding(0, 0, 12, 8), BackColor = t.Panel };
        p.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        p.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var l = Label(t, caption, 8.5f, FontStyle.Regular, sub: true);
        l.Margin = new Padding(1, 0, 0, 3);
        p.Controls.Add(l, 0, 0);
        input.Dock = DockStyle.Top;
        input.Margin = new Padding(0);
        p.Controls.Add(input, 0, 1);
        return p;
    }
}
