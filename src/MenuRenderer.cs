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
        var r = new Rectangle(4, 1, e.Item.Width - 8, e.Item.Height - 2);
        using var path = Theme.RoundRect(r, 4);
        using var b = new SolidBrush(_t.SurfaceHover);
        g.FillPath(b, path);
    }

    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = e.ImageRectangle;
        float d = Math.Min(r.Width, r.Height) * 0.5f;
        using var b = new SolidBrush(_t.Accent);
        g.FillEllipse(b, r.X + (r.Width - d) / 2f, r.Y + (r.Height - d) / 2f, d, d);
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        if (e.Item.Tag as string == "header")
        {
            e.TextColor = _t.SubText;
        }
        else
        {
            bool shortcut = e.Text == (e.Item as ToolStripMenuItem)?.ShortcutKeyDisplayString;
            e.TextColor = !e.Item.Enabled ? _t.SubText : shortcut ? _t.SubText : _t.Text;
        }
        base.OnRenderItemText(e);
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        using var p = new Pen(_t.Border);
        int y = e.Item.Height / 2;
        e.Graphics.DrawLine(p, 8, y, e.Item.Width - 8, y);
    }

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// <summary>Round the menu's corners on Windows 11.</summary>
    public static void RoundCorners(ToolStripDropDown menu)
    {
        try { int round = 2; DwmSetWindowAttribute(menu.Handle, 33, ref round, 4); } catch { }
    }
}
