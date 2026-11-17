using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace NetSwitch;

/// <summary>Colors and helpers that follow the Windows light/dark setting and accent color.</summary>
public sealed class Theme
{
    public bool Dark { get; private init; }
    public Color Window { get; private init; }      // page background
    public Color Surface { get; private init; }     // inputs, menus, pop-ups
    public Color Panel => Window;                  // section background (flat layout)
    public Color SurfaceHover { get; private init; }
    public Color Sidebar { get; private init; }
    public Color Border { get; private init; }
    public Color Text { get; private init; }
    public Color SubText { get; private init; }
    public Color Accent { get; private init; }
    public Color AccentText { get; private init; }  // text on accent
    public Color AccentSoft { get; private init; }  // selection background
    public Color Success { get; private init; }
    public Color Danger { get; private init; }

    /// <summary>Null = follow Windows; true/false = always dark/light (Settings > Pop-ups > Colours).</summary>
    public static bool? ForceDark;

    public static void Apply(string mode) => ForceDark = mode switch { "dark" => true, "light" => false, _ => null };

    public static Font UiFont(float size = 9.75f, FontStyle style = FontStyle.Regular)
    {
        foreach (var name in new[] { "Segoe UI Variable Text", "Segoe UI" })
        {
            using var probe = new Font(name, size, style);
            if (probe.Name == name) return new Font(name, size, style);
        }
        return new Font(FontFamily.GenericSansSerif, size, style);
    }

    public static Theme Current
    {
        get
        {
            bool dark = false;
            Color accent = Color.FromArgb(0, 103, 192);
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                dark = k?.GetValue("AppsUseLightTheme") is int v && v == 0;
            }
            catch { }
            if (ForceDark is { } forced) dark = forced;
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
                if (k?.GetValue("AccentColor") is int abgr)
                    accent = Color.FromArgb(abgr & 0xFF, (abgr >> 8) & 0xFF, (abgr >> 16) & 0xFF);
            }
            catch { }

            // A grey or near-black Windows accent would leave NetSwitch colourless: use its own blue then.
            if (accent.GetSaturation() < 0.3f || accent.GetBrightness() < 0.15f || accent.GetBrightness() > 0.9f)
                accent = dark ? Color.FromArgb(0x4C, 0xC2, 0xFF) : Color.FromArgb(0x00, 0x67, 0xC0);
            // Keep accent readable on the chosen background.
            if (dark) accent = Lighten(accent, 0.35f);
            else if (accent.GetBrightness() > 0.6f) accent = Darken(accent, 0.3f);

            return dark
                ? new Theme
                {
                    Dark = true,
                    Window = Color.FromArgb(32, 32, 32),
                    Sidebar = Color.FromArgb(28, 28, 28),
                    Surface = Color.FromArgb(45, 45, 45),
                    SurfaceHover = Color.FromArgb(55, 55, 55),
                    Border = Color.FromArgb(70, 70, 70),
                    Text = Color.FromArgb(240, 240, 240),
                    SubText = Color.FromArgb(160, 160, 160),
                    Accent = accent,
                    AccentText = accent.GetBrightness() > 0.55f ? Color.Black : Color.White,
                    AccentSoft = Blend(accent, Color.FromArgb(45, 45, 45), 0.25f),
                    Success = Color.FromArgb(108, 203, 95),
                    Danger = Color.FromArgb(255, 153, 164),
                }
                : new Theme
                {
                    Dark = false,
                    Window = Color.FromArgb(249, 249, 249),
                    Sidebar = Color.FromArgb(240, 240, 240),
                    Surface = Color.White,
                    SurfaceHover = Color.FromArgb(245, 245, 245),
                    Border = Color.FromArgb(222, 222, 222),
                    Text = Color.FromArgb(26, 26, 26),
                    SubText = Color.FromArgb(96, 96, 96),
                    Accent = accent,
                    AccentText = Color.White,
                    AccentSoft = Blend(accent, Color.White, 0.14f),
                    Success = Color.FromArgb(15, 123, 15),
                    Danger = Color.FromArgb(196, 43, 28),
                };
        }
    }

    public static Color Blend(Color a, Color b, float amountOfA) => Color.FromArgb(
        (int)(a.R * amountOfA + b.R * (1 - amountOfA)),
        (int)(a.G * amountOfA + b.G * (1 - amountOfA)),
        (int)(a.B * amountOfA + b.B * (1 - amountOfA)));

    static Color Lighten(Color c, float f) => Blend(Color.White, c, f);
    static Color Darken(Color c, float f) => Blend(Color.Black, c, f);

    // ---------- native window chrome ----------

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] static extern int SetWindowTheme(IntPtr hwnd, string? app, string? idList);

    /// <summary>Dark/light title bar, rounded corners and caption color on Windows 10/11 (ignored elsewhere).</summary>
    public void ApplyWindowChrome(Form f, bool small = false)
    {
        try
        {
            int dark = Dark ? 1 : 0;
            DwmSetWindowAttribute(f.Handle, 20, ref dark, 4);              // DWMWA_USE_IMMERSIVE_DARK_MODE
            int corner = small ? 3 : 2;                                     // ROUNDSMALL / ROUND
            DwmSetWindowAttribute(f.Handle, 33, ref corner, 4);            // DWMWA_WINDOW_CORNER_PREFERENCE
            int caption = ColorTranslator.ToWin32(Window);
            DwmSetWindowAttribute(f.Handle, 35, ref caption, 4);           // DWMWA_CAPTION_COLOR
            int border = ColorTranslator.ToWin32(Border);
            DwmSetWindowAttribute(f.Handle, 34, ref border, 4);            // DWMWA_BORDER_COLOR
        }
        catch { }
    }

    /// <summary>Dark scrollbars etc. for a native control.</summary>
    public void ApplyControlTheme(Control c)
    {
        try { SetWindowTheme(c.Handle, Dark ? "DarkMode_Explorer" : "Explorer", null); } catch { }
    }

    public static GraphicsPath RoundRect(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float d = radius * 2;
        if (d <= 0) { p.AddRectangle(r); return p; }
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}
