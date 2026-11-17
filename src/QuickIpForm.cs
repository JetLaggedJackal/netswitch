namespace NetSwitch;

/// <summary>Small dialog to put a static IP on an adapter right now, without making a profile.</summary>
public sealed class QuickIpForm : Form
{
    readonly Theme _t = Theme.Current;
    readonly AdapterInfo _a;
    readonly ModernTextBox _ip, _mask, _gw, _dns1, _dns2;
    readonly Label _error;
    public Profile? Result { get; private set; }

    public QuickIpForm(AdapterInfo a)
    {
        _a = a;
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = $"Static IP · {a.Name}";
        Font = Theme.UiFont(9f);
        BackColor = _t.Window;
        ForeColor = _t.Text;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = true;
        TopMost = true;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(520, 300);
        Padding = new Padding(20, 16, 20, 16);

        var cur = a.Addresses.Count > 0 ? a.Addresses[0] : (Ip: "", Mask: "");
        TextBox(out _ip, cur.Ip, "192.168.1.50 or 192.168.1.50/24");
        TextBox(out _mask, cur.Mask.Length > 0 ? cur.Mask : "255.255.255.0", "255.255.255.0 or 24");
        TextBox(out _gw, a.Gateways.FirstOrDefault() ?? "", "optional");
        TextBox(out _dns1, a.Dns.ElementAtOrDefault(0) ?? "", "optional");
        TextBox(out _dns2, a.Dns.ElementAtOrDefault(1) ?? "", "optional");

        _ip.Leave += (_, _) =>
        {
            var (ip, mask) = IpUtil.SplitIp(_ip.Text);
            if (mask == null || !IpUtil.IsIPv4(ip)) return;
            _ip.Text = ip; _mask.Text = mask;
        };
        _mask.Leave += (_, _) =>
        {
            var t = _mask.Text.Trim();
            if (t.Length > 0 && !IpUtil.IsIPv4(t) && IpUtil.NormalizeMask(t) is { } dotted) _mask.Text = dotted;
        };

        var title = Ui.Label(_t, $"Static IP on {a.Name}", 12f, FontStyle.Bold);
        var current = a.Addresses.Count == 0 ? "No IPv4 address right now"
            : $"Now: {cur.Ip}/{NetInfo.PrefixLength(cur.Mask)} ({(a.Dhcp ? "DHCP" : "static")})";
        var sub = Ui.Label(_t, current + ". This applies once and doesn't create a profile.", 8.75f, sub: true);
        sub.MaximumSize = new Size(440, 0);
        sub.Margin = new Padding(0, 2, 0, 12);

        var grid = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Top, BackColor = _t.Window, Margin = new Padding(0) };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        grid.Controls.Add(Field("IP address", _ip));
        grid.Controls.Add(Field("Subnet mask", _mask));
        grid.Controls.Add(Field("Gateway", _gw));
        grid.Controls.Add(new Label { Width = 1, Height = 1, Margin = new Padding(0) });
        grid.Controls.Add(Field("DNS 1", _dns1));
        grid.Controls.Add(Field("DNS 2", _dns2));

        _error = Ui.Label(_t, "", 8.75f);
        _error.ForeColor = _t.Danger;
        _error.MaximumSize = new Size(440, 0);
        _error.Margin = new Padding(0, 0, 0, 6);

        var apply = new ModernButton(_t, "Apply", primary: true) { Width = 100, Margin = new Padding(8, 0, 0, 0) };
        var cancel = new ModernButton(_t, "Cancel") { Width = 100, Margin = new Padding(0) };
        apply.Click += (_, _) => TryApply();
        cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill, BackColor = _t.Window, Margin = new Padding(0, 6, 0, 0), WrapContents = false };
        buttons.Controls.Add(apply);
        buttons.Controls.Add(cancel);
        AcceptButton = apply;

        var stack = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Dock = DockStyle.Fill, BackColor = _t.Window };
        stack.Controls.Add(title);
        stack.Controls.Add(sub);
        stack.Controls.Add(grid);
        stack.Controls.Add(_error);
        stack.Controls.Add(buttons);
        Controls.Add(stack);
        ResumeLayout(true);
        Shown += (_, _) => { Activate(); _ip.Focus(); _ip.SelectAll(); };
    }

    void TextBox(out ModernTextBox box, string text, string placeholder)
    {
        box = new ModernTextBox(_t) { Text = text, PlaceholderText = placeholder };
    }

    Control Field(string caption, Control input)
    {
        var f = Ui.Field(_t, caption, input);
        f.BackColor = _t.Window;
        foreach (Control c in f.Controls) if (c is Label) c.BackColor = _t.Window;
        return f;
    }

    void TryApply()
    {
        // Same shortcuts as the editor, in case Enter was pressed straight from a box.
        var (ip, prefixMask) = IpUtil.SplitIp(_ip.Text.Trim());
        var mask = prefixMask ?? (IpUtil.IsIPv4(_mask.Text.Trim()) ? _mask.Text.Trim() : IpUtil.NormalizeMask(_mask.Text) ?? _mask.Text.Trim());
        var dns = new[] { _dns1.Text.Trim(), _dns2.Text.Trim() }.Where(d => d.Length > 0).ToList();
        var p = new Profile
        {
            Name = $"Static {ip} on {_a.Name}",
            Adapter = _a.Name,
            Dhcp = false,
            Ip = ip,
            Mask = mask,
            Gateway = _gw.Text.Trim(),
            Dns = dns,
            DnsAuto = false,
        };
        if (IpUtil.Validate(p) is { } problem) { _error.Text = problem; return; }
        Result = p;
        DialogResult = DialogResult.OK;
        Close();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        _t.ApplyWindowChrome(this);
    }
}
