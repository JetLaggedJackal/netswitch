using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

namespace NetSwitch;

/// <summary>Applies a profile with netsh. Must run elevated.</summary>
public static class NetApply
{
    [DllImport("kernel32.dll")] static extern uint GetOEMCP();

    public static (bool ok, string message) Apply(Profile p)
    {
        var problem = IpUtil.Validate(p);
        if (problem != null) return (false, problem);

        var snap = NetInfo.Snapshot();
        if (!snap.TryGetValue(p.Adapter, out var adapter))
            return (false, $"Adapter \"{p.Adapter}\" was not found.");

        var name = $"name=\"{adapter.Name}\"";
        var cmds = new List<string>();
        if (p.Dhcp)
        {
            // netsh errors with "DHCP is already enabled" if we set it twice, so skip when already on.
            if (!adapter.Dhcp) cmds.Add($"interface ipv4 set address {name} source=dhcp");
            if (p.UsesAutoDns) cmds.Add($"interface ipv4 set dnsservers {name} source=dhcp");
            else AddStaticDns(cmds, name, p.Dns);
        }
        else
        {
            var (ip, prefMask) = IpUtil.SplitIp(p.Ip);
            var mask = prefMask ?? IpUtil.NormalizeMask(p.Mask)!;
            var gw = string.IsNullOrWhiteSpace(p.Gateway) ? "none" : p.Gateway.Trim();
            cmds.Add($"interface ipv4 set address {name} source=static address={ip} mask={mask} gateway={gw}");

            AddStaticDns(cmds, name, p.Dns);
        }

        foreach (var c in cmds)
        {
            var (code, output) = RunNetsh(c);
            if (code != 0)
                return (false, $"netsh failed: {output.Trim()}\n({c})");
        }
        return (true, p.Dhcp
            ? $"{adapter.Name} switched to DHCP (\"{p.Name}\")."
            : $"{adapter.Name} set to {IpUtil.SplitIp(p.Ip).ip} (\"{p.Name}\").");
    }

    static void AddStaticDns(List<string> cmds, string name, List<string> servers)
    {
        var dns = servers.Where(d => !string.IsNullOrWhiteSpace(d)).Select(d => d.Trim()).ToList();
        if (dns.Count == 0)
        {
            cmds.Add($"interface ipv4 set dnsservers {name} source=static address=none");
            return;
        }
        cmds.Add($"interface ipv4 set dnsservers {name} source=static address={dns[0]} register=primary validate=no");
        for (int i = 1; i < dns.Count; i++)
            cmds.Add($"interface ipv4 add dnsservers {name} address={dns[i]} index={i + 1} validate=no");
    }

    static (int code, string output) RunNetsh(string args)
    {
        Encoding enc;
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            enc = Encoding.GetEncoding((int)GetOEMCP());
        }
        catch { enc = Encoding.Default; }

        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "netsh.exe"), args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = enc,
            StandardErrorEncoding = enc,
        };
        using var proc = Process.Start(psi)!;
        var errTask = proc.StandardError.ReadToEndAsync();
        var output = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit(30_000);
        return (proc.HasExited ? proc.ExitCode : -1, output + errTask.Result);
    }
}

public static class Elevation
{
    public static bool IsElevated
    {
        get
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    public static string ExePath => Environment.ProcessPath ?? Application.ExecutablePath;

    public static string ResultFile(string configPath) =>
        Path.Combine(Path.GetDirectoryName(configPath)!, "last-result.txt");

    /// <summary>Runs this exe elevated with the given arguments and waits. Returns null if the user cancelled UAC.</summary>
    public static async Task<int?> RunElevatedAsync(params string[] args)
    {
        var psi = new ProcessStartInfo(ExePath) { UseShellExecute = true, Verb = "runas" };
        foreach (var a in args) psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi);
            if (p == null) return -1;
            await p.WaitForExitAsync();
            return p.ExitCode;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) // ERROR_CANCELLED
        {
            return null;
        }
    }
}

/// <summary>"Start with Windows" via a scheduled task with highest privileges, so switching never prompts for UAC.</summary>
public static class Startup
{
    const string TaskName = "NetSwitch";

    public static bool IsEnabled()
    {
        try
        {
            var psi = new ProcessStartInfo("schtasks.exe", $"/Query /TN \"{TaskName}\"")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            using var p = Process.Start(psi)!;
            p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd();
            p.WaitForExit(10_000);
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    /// <summary>Must run elevated.</summary>
    public static (bool ok, string message) Install(string user, string exePath)
    {
        string esc(string s) => System.Security.SecurityElement.Escape(s)!;
        var xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo><Description>NetSwitch tray app (network profile switcher)</Description></RegistrationInfo>
              <Triggers>
                <LogonTrigger><Enabled>true</Enabled><UserId>{esc(user)}</UserId><Delay>PT5S</Delay></LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author"><UserId>{esc(user)}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>7</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec><Command>{esc(exePath)}</Command></Exec>
              </Actions>
            </Task>
            """;
        var tmp = Path.Combine(Path.GetTempPath(), "netswitch-task.xml");
        File.WriteAllText(tmp, xml, Encoding.Unicode);
        try { return Run($"/Create /F /TN \"{TaskName}\" /XML \"{tmp}\""); }
        finally { try { File.Delete(tmp); } catch { } }
    }

    public static (bool ok, string message) Remove() => Run($"/Delete /F /TN \"{TaskName}\"");

    static (bool ok, string message) Run(string args)
    {
        var psi = new ProcessStartInfo("schtasks.exe", args)
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEndAsync();
        var outp = p.StandardOutput.ReadToEnd();
        p.WaitForExit(15_000);
        return (p.ExitCode == 0, (outp + err.Result).Trim());
    }
}
