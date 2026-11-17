using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace NetSwitch;

/// <summary>Windows 11-style rendering for the tray menu, following light/dark mode.</summary>
public sealed class MenuRenderer : ToolStripProfessionalRenderer
{
    readonly Theme _t;
    public MenuRenderer(Theme t) : base(new Colors(t)) { _t = t; RoundedEdges = false; }

    sealed class Colors : ProfessionalColorTable
    {
        readonly Theme _t;
        public Colors(Theme t) { _t = t; UseSystemColors = false; }
        public override Color ToolStripDropDownBackground => _t.Surface;
        public override Color ImageMarginGradientBegin => _t.Surface;
        public override Color ImageMarginGradientMiddle => _t.Surface;
        public override Color ImageMarginGradientEnd => _t.Surface;
        public override Color MenuBorder => _t.Border;
        public override Color MenuItemBorder => Color.Transparent;
        public override Color MenuItemSelected => _t.SurfaceHover;
        public override Color SeparatorDark => _t.Border;
        public override Color SeparatorLight => _t.Border;
        public override Color CheckBackground => Color.Transparent;
        public override Color CheckSelectedBackground => Color.Transparent;
        public override Color CheckPressedBackground => Color.Transparent;
    }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        if (!e.Item.Selected || !e.Item.Enabled) return;
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var o = e.Item.Owner;
        int m = o?.LogicalToDeviceUnits(4) ?? 4;
        var r = new Rectangle(m, 1, e.Item.Width - m * 2, e.Item.Height - 2);
        using var path = Theme.RoundRect(r, m);
        using var b = new SolidBrush(_t.SurfaceHover);
        g.FillPath(b, path);
    }

    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float k = (e.Item.Owner?.DeviceDpi ?? 96) / 96f;
        // Windows 11-style accent pill at the left edge of the active profile.
        float w = 3 * k, h = 18 * k;
        var pill = new RectangleF(6 * k, (e.Item.Height - h) / 2f, w, h);
        using var path = Theme.RoundRect(pill, w / 2);
        using var b = new SolidBrush(_t.Accent);
        g.FillPath(b, path);
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        if (e.Item is ProfileMenuItem pm)
        {
            // Two lines: profile name, then what it sets (or that it's active) in smaller text.
            var r = e.TextRectangle;
            int right = pm.Width - pm.Owner!.LogicalToDeviceUnits(14);
            using var small = Theme.UiFont(8.25f);
            int h1 = TextRenderer.MeasureText(e.Graphics, "Ag", e.TextFont).Height;
            int h2 = TextRenderer.MeasureText(e.Graphics, "Ag", small).Height;
            int top = (pm.Height - h1 - h2) / 2;
            var flags = TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.Left | TextFormatFlags.SingleLine;
            TextRenderer.DrawText(e.Graphics, pm.Text, e.TextFont, new Rectangle(r.X, top, right - r.X, h1), pm.Enabled ? _t.Text : _t.SubText, flags);
            TextRenderer.DrawText(e.Graphics, pm.Detail, small, new Rectangle(r.X, top + h1, right - r.X, h2), pm.Checked ? _t.Accent : _t.SubText, flags);
            return;
        }
        if (e.Item.Tag as string == "header")
        {
            e.TextColor = _t.SubText;
        }
        else
        {
            bool shortcut = e.Text == (e.Item as ToolStripMenuItem)?.ShortcutKeyDisplayString;
            e.TextColor = !e.Item.Enabled ? _t.SubText : shortcut ? _t.SubText : _t.Text;
        }
        // Draw the text ourselves: the base renderer embosses disabled items, which looks blurry.
        TextRenderer.DrawText(e.Graphics, e.Text, e.TextFont, e.TextRectangle, e.TextColor, e.TextFormat);
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        using var p = new Pen(_t.Border);
        int y = e.Item.Height / 2, m = e.Item.Owner?.LogicalToDeviceUnits(10) ?? 10;
        e.Graphics.DrawLine(p, m, y, e.Item.Width - m, y);
    }

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// <summary>Round the menu's corners on Windows 11.</summary>
    public static void RoundCorners(ToolStripDropDown menu)
    {
        try { int round = 2; DwmSetWindowAttribute(menu.Handle, 33, ref round, 4); } catch { }
    }
}

/// <summary>Tray menu entry for a profile: name on top, what it sets underneath.</summary>
public sealed class ProfileMenuItem : ToolStripMenuItem
{
    public string Detail { get; set; } = "";
    public ProfileMenuItem(string text, EventHandler onClick) : base(text, null, onClick) { }
    public override Size GetPreferredSize(Size constrainingSize)
    {
        var s = base.GetPreferredSize(constrainingSize);
        var o = Owner;
        int L(int v) => o?.LogicalToDeviceUnits(v) ?? v;
        using var small = Theme.UiFont(8.25f);
        int detailW = TextRenderer.MeasureText(Detail, small).Width + L(60);
        return new Size(Math.Max(Math.Max(s.Width, detailW), L(230)), L(48));
    }
}
