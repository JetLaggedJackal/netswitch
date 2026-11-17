namespace NetSwitch;

/// <summary>Turns network state into pop-up content.</summary>
public static class PopupBuilder
{
    public static PopupContent Change(string name, AdapterInfo? before, AdapterInfo? after, IEnumerable<Profile> profiles, bool sticky)
    {
        bool nowUp = after is { IsUp: true };
        bool wasUp = before is { IsUp: true };
        string title = !nowUp ? "Adapter disconnected"
            : !wasUp ? "Adapter connected"
            : after!.Addresses.Count == 0 ? "IP address lost"
            : "IP address changed";

        var first = after?.Addresses.FirstOrDefault();
        string big = !nowUp ? "Disconnected" : first?.Ip ?? "No IPv4 address";
        string? suffix = nowUp && first is { Mask: { Length: > 0 } m } ? "/" + NetInfo.PrefixLength(m) : null;
        string? previous = before == null ? null
            : !wasUp ? "was disconnected"
            : before.Addresses.Count == 0 ? "had no IPv4 address"
            : "was " + string.Join(", ", before.Addresses.Select(a => a.Ip));

        var rows = new List<(string, string)>();
        if (after != null && nowUp)
        {
            if (first is { Mask: { Length: > 0 } mask }) rows.Add(("Subnet mask", mask));
            if (after.Addresses.Count > 1) rows.Add(("Also", string.Join(", ", after.Addresses.Skip(1).Select(a => a.Ip))));
            rows.Add(("Gateway", after.Gateways.Count > 0 ? string.Join(", ", after.Gateways) : "none"));
            rows.Add(("DNS", after.Dns.Count > 0 ? string.Join(", ", after.Dns) : "none"));
            rows.Add(("Assigned by", Addressing(after)));
            if (after.IsWireless && NetInfo.WifiSsid(after.Name) is { } ssid) rows.Add(("Wi-Fi", ssid));
            var match = profiles.FirstOrDefault(p => string.Equals(p.Adapter, after.Name, StringComparison.OrdinalIgnoreCase) && NetInfo.Matches(p, after));
            if (match != null) rows.Add(("Profile", match.Name));
            if (NetInfo.FormatSpeed(after.SpeedBps) is { Length: > 0 } speed) rows.Add(("Link speed", speed));
            if (after.Mac.Length > 0) rows.Add(("MAC", after.Mac));
        }

        return new PopupContent
        {
            Kind = nowUp ? PopupKind.Change : PopupKind.Info,
            Title = title,
            Heading = name,
            SubHeading = after?.Description ?? before?.Description,
            Big = big,
            BigSuffix = suffix,
            Previous = previous,
            Rows = rows,
            CopyText = nowUp ? first?.Ip : null,
            Sticky = sticky,
        };
    }

    static string Addressing(AdapterInfo a)
    {
        if (!a.Dhcp) return "Static (set by hand)";
        var parts = new List<string> { "DHCP" };
        if (!string.IsNullOrEmpty(a.DhcpServer)) parts.Add("server " + a.DhcpServer);
        if (NetInfo.FormatDuration(a.LeaseSeconds) is { Length: > 0 } lease) parts.Add("lease " + lease + " left");
        if (a.Addresses.FirstOrDefault().Ip?.StartsWith("169.254.") == true) parts.Add("no DHCP answer (APIPA)");
        return string.Join(" · ", parts);
    }

    public static PopupContent Applied(Profile p, AdapterInfo? a, bool sticky)
    {
        var rows = new List<(string, string)>();
        if (!p.Dhcp)
        {
            var (ip, prefMask) = IpUtil.SplitIp(p.Ip);
            var mask = prefMask ?? IpUtil.NormalizeMask(p.Mask) ?? p.Mask;
            rows.Add(("Subnet mask", mask));
            rows.Add(("Gateway", string.IsNullOrWhiteSpace(p.Gateway) ? "none" : p.Gateway));
        }
        rows.Add(("DNS", p.UsesAutoDns ? "from DHCP" : p.Dns.Count > 0 ? string.Join(", ", p.Dns) : "none"));
        if (p.Dhcp) rows.Add(("Note", "The new address can take a few seconds to arrive."));
        var ipOnly = IpUtil.SplitIp(p.Ip).ip;
        return new PopupContent
        {
            Kind = PopupKind.Success,
            Title = $"Switched to \"{p.Name}\"",
            Heading = p.Adapter,
            SubHeading = a?.Description,
            Big = p.Dhcp ? "Automatic (DHCP)" : ipOnly,
            BigSuffix = p.Dhcp ? null : "/" + NetInfo.PrefixLength(IpUtil.SplitIp(p.Ip).prefixMask ?? IpUtil.NormalizeMask(p.Mask) ?? ""),
            Previous = a is { IsUp: true } ? "was " + a.AddressText + $" ({a.ModeText})" : null,
            Rows = rows,
            CopyText = p.Dhcp ? null : ipOnly,
            Sticky = sticky,
        };
    }

    /// <summary>Right-click overview: every visible adapter with its address.</summary>
    public static PopupContent Overview(IEnumerable<AdapterInfo> adapters, IEnumerable<Profile> profiles,
        Func<AdapterInfo, List<PopupAction>>? actions = null, Func<AdapterInfo, WifiInfo?>? wifi = null,
        List<(string, Action)>? buttons = null, bool publicIp = false)
    {
        var entries = new List<PopupEntry>();
        foreach (var a in adapters)
        {
            if (!a.IsUp) { entries.Add(new PopupEntry(a.Name, "Disconnected", null, false) { Actions = actions?.Invoke(a) }); continue; }
            var ips = a.Addresses.Count == 0 ? "No IPv4 address" : string.Join(", ", a.Addresses.Select(x => x.Mask.Length > 0 ? $"{x.Ip}/{NetInfo.PrefixLength(x.Mask)}" : x.Ip));
            var rest = new List<string> { a.ModeText };
            var speed = NetInfo.FormatSpeed(a.SpeedBps);
            if (speed.Length > 0) rest.Add(speed);
            var match = profiles.FirstOrDefault(p => string.Equals(p.Adapter, a.Name, StringComparison.OrdinalIgnoreCase) && NetInfo.Matches(p, a));
            if (match != null) rest.Add(match.Name);
            var gw = a.Gateways.FirstOrDefault(g => System.Net.IPAddress.TryParse(g, out _));
            var w = a.IsWireless ? wifi?.Invoke(a) : null;
            string? line2 = w == null ? null : string.Join("  ·  ", new[]
            {
                w.Ssid, $"{w.SignalPercent}%", w.Channel > 0 ? $"ch {w.Channel} ({w.Band})" : "", NetDetails.ShortStandard(w.Standard),
            }.Where(x => x.Length > 0));
            entries.Add(new PopupEntry(a.Name, ips, a.Addresses.FirstOrDefault().Ip, true, gw, string.Join("  ·  ", rest))
            {
                Actions = actions?.Invoke(a), Line2 = line2, Signal = w?.Bars ?? -1,
            });
        }
        return new PopupContent
        {
            Kind = PopupKind.Info,
            Title = "Network adapters",
            LiveInternet = true,
            LiveActivity = true,
            PublicIp = publicIp,
            Buttons = buttons ?? new(),
            Entries = entries,
            Message = entries.Count == 0 ? "No adapters to show. Hidden adapters can be turned back on in Settings." : null,
            CloseOnOutsideClick = true,
            Sticky = true,
        };
    }

    /// <summary>Left-click picker: one entry per profile, click to switch. Same look and place as the overview.</summary>
    public static PopupContent Profiles(IReadOnlyList<Profile> profiles, IReadOnlyDictionary<string, AdapterInfo> snap, bool busy, Action<Profile> apply,
        (string Adapter, PreviousSettings Prev) previous = default, Action<string>? restore = null, bool hotkeys = false)
    {
        int index = 0;
        var entries = new List<PopupEntry>();
        foreach (var p in profiles)
        {
            snap.TryGetValue(p.Adapter, out var a);
            bool active = NetInfo.Matches(p, a);
            var ip = IpUtil.SplitIp(p.Ip).ip;
            var what = p.Dhcp ? "DHCP" : string.Equals(ip, p.Name.Trim(), StringComparison.OrdinalIgnoreCase) ? "Fixed IP" : $"Fixed {ip}";
            var dnsList = p.Dns.Where(d => !string.IsNullOrWhiteSpace(d)).ToList();
            var dns = p.UsesAutoDns || dnsList.Count == 0 ? null : "DNS " + string.Join(", ", dnsList);
            var rest = string.Join("  ·  ", new[] { p.Adapter, dns }.Where(x => !string.IsNullOrEmpty(x)));
            var profile = p;
            index = entries.Count;
            entries.Add(new PopupEntry(p.Name, what, null, active, null, rest)
            {
                Click = busy ? null : () => apply(profile),
                Hint = hotkeys && index < 9 ? $"Switch  ·  Ctrl+Alt+{index + 1}" : "Switch",
                Badge = active ? "Active" : null,
            });
        }
        var buttons = new List<(string, Action)>();
        if (previous.Prev?.Settings is { } ps && restore != null && !busy)
        {
            snap.TryGetValue(previous.Adapter, out var cur);
            // Only offer it while it would actually change something.
            if (!NetInfo.Matches(ps, cur))
            {
                var adapter = previous.Adapter;
                buttons.Add(("Previous", () => restore(adapter)));
            }
        }
        return new PopupContent
        {
            Kind = PopupKind.Info,
            Title = busy ? "Switching…" : "Switch profile",
            Entries = entries,
            Buttons = buttons,
            Message = entries.Count == 0 ? "No profiles yet. Create one in Settings." : null,
            CloseOnOutsideClick = true,
            Sticky = true,
        };
    }

    public static PopupContent Error(string title, string message) => new()
    {
        Kind = PopupKind.Error, Title = title, Message = message, Sticky = true,
    };

    public static PopupContent Info(string title, string message, bool sticky = false) => new()
    {
        Kind = PopupKind.Info, Title = title, Message = message, Sticky = sticky,
    };
}
