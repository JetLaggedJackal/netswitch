using System.Runtime.InteropServices;

namespace NetSwitch;

/// <summary>System-wide Ctrl+Alt+0..9 hotkeys. Keys another app already owns are skipped.</summary>
public sealed class Hotkeys : NativeWindow, IDisposable
{
    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    const uint ModAlt = 0x1, ModControl = 0x2, ModNoRepeat = 0x4000;
    const int WmHotkey = 0x0312;

    readonly List<int> _ids = new();
    /// <summary>Digit pressed with Ctrl+Alt (0-9).</summary>
    public event Action<int>? Pressed;
    public IReadOnlyList<int> Failed => _failed;
    readonly List<int> _failed = new();

    public Hotkeys() => CreateHandle(new CreateParams());

    public void Enable(bool on)
    {
        foreach (var id in _ids) UnregisterHotKey(Handle, id);
        _ids.Clear(); _failed.Clear();
        if (!on) return;
        for (int d = 0; d <= 9; d++)
        {
            int id = 0x4E53 + d;
            if (RegisterHotKey(Handle, id, ModControl | ModAlt | ModNoRepeat, (uint)(0x30 + d))) _ids.Add(id);
            else _failed.Add(d);
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmHotkey)
        {
            int d = m.WParam.ToInt32() - 0x4E53;
            if (d is >= 0 and <= 9) Pressed?.Invoke(d);
        }
        base.WndProc(ref m);
    }

    public void Dispose() { Enable(false); DestroyHandle(); }
}
