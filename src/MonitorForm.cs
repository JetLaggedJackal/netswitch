using System.Drawing.Drawing2D;
using System.Globalization;
using System.Text;

namespace NetSwitch;

/// <summary>Pings one target every second and graphs latency, jitter and loss over the last 10 minutes.</summary>
public sealed class MonitorForm : Form
{
    readonly Theme _t = Theme.Current;
    readonly string? _gateway;
    readonly string[] _targets;
    readonly SegmentedControl _seg;
    readonly ModernTextBox _custom;
    readonly ModernButton _pause, _csv;
    readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
    readonly List<(DateTime At, long? Ms)> _samples = new();
    readonly Font _fTitle, _fBody, _fSmall, _fStat;
    string _target;
    bool _paused, _busy;
    const int Window = 600; // samples shown (10 min at 1/s)

    float S => DeviceDpi / 96f;
    int Px(float v) => (int)Math.Round(v * S);

    public MonitorForm(string? gateway, string? target)
    {
        _gateway = gateway;
        var opts = new List<string> { "1.1.1.1", "8.8.8.8" };
        if (gateway != null) opts.Add("Gateway");
        opts.Add("Custom");
        _targets = opts.ToArray();
        _target = target ?? "1.1.1.1";

        AutoScaleMode = AutoScaleMode.None;
        Text = "NetSwitch · Latency monitor";
        BackColor = _t.Window;
        ForeColor = _t.Text;
        DoubleBuffered = true;
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = true;
        using (var s = typeof(MonitorForm).Assembly.GetManifestResourceStream("NetSwitch.app.ico")) if (s != null) Icon = new Icon(s);
        _fTitle = Theme.UiFont(12f, FontStyle.Bold);
        _fBody = Theme.UiFont(9.25f);
        _fSmall = Theme.UiFont(8.5f);
        _fStat = Theme.UiFont(15f, FontStyle.Bold);
        Font = _fBody;

        _seg = new SegmentedControl(_t, _targets) { Font = _fBody };
        _custom = new ModernTextBox(_t) { PlaceholderText = "Host or IP, then Enter", Font = _fBody, Enabled = false };
        _pause = new ModernButton(_t, "Pause") { Font = _fBody };
        _csv = new ModernButton(_t, "Save CSV…") { Font = _fBody };
        _seg.SelectedIndexChanged += (_, _) => OnTargetPicked();
        _custom.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; SetTarget(_custom.Text.Trim()); } };
        _pause.Click += (_, _) => { _paused = !_paused; _pause.Text = _paused ? "Resume" : "Pause"; Invalidate(); };
        _csv.Click += (_, _) => SaveCsv();
        Controls.AddRange(new Control[] { _seg, _custom, _pause, _csv });
        _timer.Tick += async (_, _) => await Tick();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        _t.ApplyWindowChrome(this);
        MinimumSize = SizeFromClientSize(new Size(Px(560), Px(380)));
        var wa = Screen.FromControl(this).WorkingArea;
        var size = SizeFromClientSize(new Size(Px(720), Px(460)));
        Size = new Size(Math.Min(size.Width, wa.Width), Math.Min(size.Height, wa.Height));
        Location = new Point(wa.X + (wa.Width - Width) / 2, wa.Y + (wa.Height - Height) / 2);
        _seg.Height = Px(30);
        LayoutControls();
        int i = Array.IndexOf(_targets, _target == _gateway ? "Gateway" : _target);
        if (i < 0) { i = _targets.Length - 1; _custom.Text = _target; }
        _seg.SelectedIndex = i;
        OnTargetPicked();
        _timer.Start();
        _ = Tick();
    }

    protected override void OnFormClosed(FormClosedEventArgs e) { _timer.Dispose(); base.OnFormClosed(e); }
    protected override void OnResize(EventArgs e) { base.OnResize(e); LayoutControls(); Invalidate(); }

    void OnTargetPicked()
    {
        var pick = _targets[Math.Clamp(_seg.SelectedIndex, 0, _targets.Length - 1)];
        _custom.Enabled = pick == "Custom";
        if (pick == "Custom") { _custom.Focus(); if (_custom.Text.Trim().Length > 0) SetTarget(_custom.Text.Trim()); return; }
        SetTarget(pick == "Gateway" ? _gateway! : pick);
    }

    void SetTarget(string t)
    {
        if (t.Length == 0 || t.Length > 253 || t.Any(c => !(char.IsLetterOrDigit(c) || c is '.' or '-' or ':'))) return;
        if (t == _target && _samples.Count > 0) return;
        _target = t;
        _samples.Clear();
        Invalidate();
    }

    async Task Tick()
    {
        if (_paused || _busy || IsDisposed) return;
        _busy = true;
        var target = _target;
        try
        {
            var ms = await NetCheck.PingAsync(target, 2000);
            if (IsDisposed || target != _target) return;
            _samples.Add((DateTime.Now, ms));
            if (_samples.Count > 3600) _samples.RemoveAt(0);
            Invalidate();
        }
        finally { _busy = false; }
    }

    void SaveCsv()
    {
        using var d = new SaveFileDialog { Filter = "CSV file|*.csv", FileName = $"latency-{_target.Replace(':', '_')}-{DateTime.Now:yyyyMMdd-HHmm}.csv" };
        if (d.ShowDialog(this) != DialogResult.OK) return;
        var sb = new StringBuilder("time,target,ms\r\n");
        foreach (var (at, ms) in _samples)
            sb.Append(at.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append(',').Append(_target).Append(',')
              .Append(ms?.ToString(CultureInfo.InvariantCulture) ?? "lost").Append("\r\n");
        try { File.WriteAllText(d.FileName, sb.ToString()); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Could not save", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }

    int Pad => Px(20);

    void LayoutControls()
    {
        if (!IsHandleCreated) return;
        int bh = Px(30);
        var segW = _seg.GetPreferredSize(Size.Empty).Width;
        _seg.SetBounds(Pad, Pad + Px(34), segW, bh);
        _custom.SetBounds(_seg.Right + Px(10), _seg.Top, Math.Max(Px(120), ClientSize.Width - Pad - _seg.Right - Px(10)), bh);
        int bw = Px(104);
        _csv.SetBounds(ClientSize.Width - Pad - bw, ClientSize.Height - Pad - bh, bw, bh);
        _pause.SetBounds(_csv.Left - Px(8) - bw, _csv.Top, bw, bh);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(_t.Window);
        TextRenderer.DrawText(g, "Latency monitor", _fTitle, new Point(Pad - Px(2), Pad), _t.Text);

        var view = _samples.Skip(Math.Max(0, _samples.Count - Window)).ToList();
        var ok = view.Where(s => s.Ms != null).Select(s => s.Ms!.Value).ToList();
        string Ms(double v) => v < 10 ? $"{v:0.#} ms" : $"{v:0} ms";
        double jitter = 0;
        for (int i = 1; i < view.Count; i++)
            if (view[i].Ms is { } a && view[i - 1].Ms is { } b) jitter += Math.Abs(a - b);
        int pairs = Enumerable.Range(1, Math.Max(0, view.Count - 1)).Count(i => view[i].Ms != null && view[i - 1].Ms != null);
        var stats = new (string, string)[]
        {
            ("Now", view.Count == 0 ? "…" : view[^1].Ms is { } now ? Ms(now) : "lost"),
            ("Average", ok.Count == 0 ? "–" : Ms(ok.Average())),
            ("Jitter", pairs == 0 ? "–" : Ms(jitter / pairs)),
            ("Loss", view.Count == 0 ? "–" : $"{100.0 * (view.Count - ok.Count) / view.Count:0.#}%"),
            ("Worst", ok.Count == 0 ? "–" : Ms(ok.Max())),
        };
        int sy = _seg.Bottom + Px(16), sx = Pad;
        foreach (var (label, value) in stats)
        {
            TextRenderer.DrawText(g, label, _fSmall, new Point(sx, sy), _t.SubText);
            var col = label == "Loss" && view.Count > ok.Count ? _t.Danger : _t.Text;
            TextRenderer.DrawText(g, value, _fStat, new Point(sx - Px(2), sy + Px(16)), col);
            sx += Math.Max(Px(110), TextRenderer.MeasureText(value, _fStat).Width + Px(24));
        }

        // Graph
        var gr = new Rectangle(Pad, sy + Px(58), ClientSize.Width - Pad * 2, _pause.Top - Px(36) - (sy + Px(58)));
        if (gr.Height < Px(40)) return;
        using (var b = new SolidBrush(_t.Surface)) using (var p = new Pen(_t.Border)) using (var path = Theme.RoundRect(gr, Px(6)))
        { g.FillPath(b, path); g.DrawPath(p, path); }
        var plot = Rectangle.Inflate(gr, -Px(10), -Px(10));
        double max = Math.Max(20, ok.Count == 0 ? 20 : ok.Max() * 1.15);
        double stepMs = max <= 40 ? 10 : max <= 100 ? 25 : max <= 250 ? 50 : max <= 600 ? 100 : 250;
        using (var grid = new Pen(_t.Border))
            for (double v = stepMs; v < max; v += stepMs)
            {
                int y = plot.Bottom - (int)(plot.Height * v / max);
                g.DrawLine(grid, plot.X, y, plot.Right, y);
                var lbl = $"{v:0} ms";
                var sz = TextRenderer.MeasureText(lbl, _fSmall);
                TextRenderer.DrawText(g, lbl, _fSmall, new Point(plot.Right - sz.Width, y - sz.Height), _t.SubText);
            }
        float step = plot.Width / (float)(Window - 1);
        float x0 = plot.Right - step * (view.Count - 1);
        using var line = new Pen(_t.Accent, Math.Max(1.4f, S * 1.4f)) { LineJoin = LineJoin.Round };
        using var lost = new SolidBrush(Color.FromArgb(140, _t.Danger));
        var run = new List<PointF>();
        for (int i = 0; i < view.Count; i++)
        {
            float x = x0 + i * step;
            if (view[i].Ms is not { } v)
            {
                if (run.Count > 1) g.DrawLines(line, run.ToArray());
                run.Clear();
                g.FillRectangle(lost, x - Math.Max(1, step / 2), plot.Y, Math.Max(Px(2), step), plot.Height);
                continue;
            }
            run.Add(new PointF(x, plot.Bottom - (float)(plot.Height * Math.Min(1, v / max))));
        }
        if (run.Count > 1) g.DrawLines(line, run.ToArray());

        var note = _paused ? $"Paused · {_target}" : $"{_target} · last 10 minutes · 1 ping per second · red = lost";
        TextRenderer.DrawText(g, note, _fSmall, new Rectangle(Pad, _pause.Top, _pause.Left - Pad - Px(8), _pause.Height), _t.SubText,
            TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
    }
}
