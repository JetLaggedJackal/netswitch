using System.Diagnostics.CodeAnalysis;
using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace NetSwitch;

/// <summary>Rounded container with a surface color and a thin border.</summary>
public class Card : Panel
{
    readonly Theme _t;
    public int Radius { get; set; } = 8;
    public Card(Theme t)
    {
        _t = t;
        DoubleBuffered = true;
        BackColor = t.Window;
        Padding = new Padding(16, 14, 16, 14);
        SetStyle(ControlStyles.ResizeRedraw, true);
    }
    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent?.BackColor ?? _t.Window);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = Theme.RoundRect(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), Radius);
        using var b = new SolidBrush(_t.Surface);
        using var p = new Pen(_t.Border);
        e.Graphics.FillPath(b, path);
        e.Graphics.DrawPath(p, path);
    }
    protected override void OnControlAdded(ControlEventArgs e)
    {
        base.OnControlAdded(e);
        if (e.Control is Label or CheckBox or FlowLayoutPanel or TableLayoutPanel) e.Control.BackColor = _t.Surface;
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
        Height = 34;
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
        int pad = 10;
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
        Height = 32;
        MinimumSize = new Size(88, 32);
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
        return new Size(Math.Max(MinimumSize.Width, s.Width + Padding.Horizontal + 8), Math.Max(MinimumSize.Height, s.Height + 12));
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
        Height = 34;
        Cursor = Cursors.Hand;
        TabStop = true;
    }
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
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? _t.Window);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float h = 20, w = 40, y = (Height - h) / 2;
        var track = new RectangleF(1, y, w, h);
        using (var path = Theme.RoundRect(track, h / 2))
        {
            if (_on) { using var b = new SolidBrush(_t.Accent); g.FillPath(b, path); }
            else { using var p = new Pen(_t.SubText, 1.2f); g.DrawPath(p, path); }
        }
        float d = _on ? 12 : 10;
        float x = _on ? track.Right - d - 5 : track.X + 5;
        using (var knob = new SolidBrush(_on ? _t.AccentText : _t.SubText))
            g.FillEllipse(knob, x, y + (h - d) / 2, d, d);
        var textRect = new Rectangle((int)w + 12, 0, Width - (int)w - 12, Height);
        TextRenderer.DrawText(g, Text, Font, textRect, _t.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
    }
}

/// <summary>Flat owner-drawn drop-down list.</summary>
public class ModernCombo : ComboBox
{
    readonly Theme _t;
    public Func<object, (string main, string? sub)>? Describe { get; set; }
    public ModernCombo(Theme t)
    {
        _t = t;
        DropDownStyle = ComboBoxStyle.DropDownList;
        DrawMode = DrawMode.OwnerDrawFixed;
        FlatStyle = FlatStyle.Flat;
        BackColor = t.Surface;
        ForeColor = t.Text;
        ItemHeight = 26;
    }
    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); _t.ApplyControlTheme(this); }
    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        var g = e.Graphics;
        bool edit = (e.State & DrawItemState.ComboBoxEdit) != 0;
        bool sel = (e.State & DrawItemState.Selected) != 0 && !edit;
        using (var b = new SolidBrush(sel ? _t.AccentSoft : _t.Surface)) g.FillRectangle(b, e.Bounds);
        if (e.Index < 0) return;
        var item = Items[e.Index];
        var (main, sub) = item == null ? ("", null) : Describe?.Invoke(item) ?? (item.ToString() ?? "", null);
        var r = new Rectangle(e.Bounds.X + 6, e.Bounds.Y, e.Bounds.Width - 12, e.Bounds.Height);
        var mainSize = TextRenderer.MeasureText(g, main, Font, r.Size, TextFormatFlags.NoPadding);
        TextRenderer.DrawText(g, main, Font, r, _t.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        if (!string.IsNullOrEmpty(sub))
        {
            var subRect = new Rectangle(r.X + mainSize.Width + 10, r.Y, Math.Max(0, r.Width - mainSize.Width - 10), r.Height);
            TextRenderer.DrawText(g, sub, Font, subRect, _t.SubText, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        }
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
        var p = new TableLayoutPanel { ColumnCount = 1, RowCount = 2, Dock = DockStyle.Fill, AutoSize = true, Margin = new Padding(0, 0, 12, 10), BackColor = t.Surface };
        p.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        p.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var l = Label(t, caption, 9f, FontStyle.Regular, sub: true);
        l.Margin = new Padding(1, 0, 0, 4);
        p.Controls.Add(l, 0, 0);
        input.Dock = DockStyle.Top;
        input.Margin = new Padding(0);
        p.Controls.Add(input, 0, 1);
        return p;
    }
}
