using System.Diagnostics;

namespace NetSwitch;

/// <summary>First-run install: copies the exe to %LOCALAPPDATA%\Programs\NetSwitch and adds a Start menu shortcut.</summary>
public static class Installer
{
    public static string InstallDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "NetSwitch");
    public static string InstalledExe => Path.Combine(InstallDir, "NetSwitch.exe");
    static string ShortcutPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Programs), "NetSwitch.lnk");

    static string? Self => Environment.ProcessPath;

    public static bool IsInstalledCopy => Self != null &&
        string.Equals(Path.GetFullPath(Self), Path.GetFullPath(InstalledExe), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Runs before the tray app starts when NetSwitch was launched from somewhere else (e.g. Downloads).
    /// Returns true when this process should exit because the installed copy takes over.
    /// </summary>
    public static bool HandleFirstRun(bool otherInstanceRunning)
    {
        if (Self == null || IsInstalledCopy) return false;
        // A portable setup: an empty "portable" file next to the exe skips all of this.
        if (File.Exists(Path.Combine(Path.GetDirectoryName(Self)!, "portable"))) return false;

        var mine = Version(Self);
        var installed = File.Exists(InstalledExe) ? Version(InstalledExe) : null;

        if (installed != null && installed >= mine)
        {
            if (otherInstanceRunning)
            {
                Info("NetSwitch is already running", "Look for its icon in the system tray (you may need to click the ^ arrow next to the clock).");
                return true;
            }
            EnsureShortcut();
            Start(InstalledExe);
            return true;
        }

        if (otherInstanceRunning)
        {
            Info("NetSwitch is already running",
                installed != null
                    ? $"An older NetSwitch ({Text(installed)}) is running. Update it from Settings > Updates, or quit it (Settings > Startup > Quit NetSwitch) and open this file again."
                    : "Quit the running NetSwitch first (Settings > Startup > Quit NetSwitch), then open this file again.");
            return true;
        }

        var page = new TaskDialogPage
        {
            Caption = "NetSwitch",
            Heading = installed == null ? "Install NetSwitch?" : $"Update NetSwitch {Text(installed)} to {Text(mine)}?",
            Text = $"NetSwitch will be copied to {InstallDir} and added to the Start menu. Your profiles and settings are kept. No admin rights are needed.",
            Icon = TaskDialogIcon.Information,
            Buttons = { new TaskDialogButton("Install") { Tag = "install" }, new TaskDialogButton("Run from here") { Tag = "run" } },
            AllowCancel = true,
        };
        page.DefaultButton = page.Buttons[0];
        var choice = TaskDialog.ShowDialog(page);
        if (choice == TaskDialogButton.Cancel) return true;
        if (choice.Tag as string != "install")
        {
            // Remember the choice so this folder isn't asked about again (e.g. at every sign-in).
            try { File.WriteAllText(Path.Combine(Path.GetDirectoryName(Self)!, "portable"), ""); } catch { }
            return false;
        }

        try
        {
            Directory.CreateDirectory(InstallDir);
            File.Copy(Self, InstalledExe, overwrite: true);
            EnsureShortcut();
        }
        catch (Exception ex)
        {
            Info("Couldn't install NetSwitch", ex.Message + "\n\nNetSwitch will run from where it is instead.");
            return false;
        }
        Start(InstalledExe);
        return true;
    }

    /// <summary>
    /// Removes the Start menu shortcut and, after this process exits, the program folder (and optionally the settings).
    /// The startup task must already be removed by the caller (it needs admin rights).
    /// </summary>
    public static void ScheduleUninstall(bool deleteSettings)
    {
        try { if (File.Exists(ShortcutPath)) File.Delete(ShortcutPath); } catch { }
        var targets = new List<string>();
        if (IsInstalledCopy) targets.Add($"rmdir /s /q \"{InstallDir}\"");
        else if (Self != null)
        {
            targets.Add($"del /f /q \"{Self}\"");
            targets.Add($"del /f /q \"{Path.Combine(Path.GetDirectoryName(Self)!, "portable")}\"");
        }
        if (deleteSettings) targets.Add($"rmdir /s /q \"{Path.GetDirectoryName(AppConfig.DefaultPath)}\"");
        // Wait for NetSwitch to exit (ping as a sleep), then delete.
        var cmd = $"/c ping 127.0.0.1 -n 4 > nul & {string.Join(" & ", targets)}";
        Process.Start(new ProcessStartInfo("cmd.exe", cmd) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetTempPath() });
    }

    static void EnsureShortcut()
    {
        try
        {
            if (File.Exists(ShortcutPath)) return;
            var type = Type.GetTypeFromProgID("WScript.Shell");
            if (type == null) return;
            dynamic shell = Activator.CreateInstance(type)!;
            dynamic lnk = shell.CreateShortcut(ShortcutPath);
            lnk.TargetPath = InstalledExe;
            lnk.WorkingDirectory = InstallDir;
            lnk.Description = "Switch network profiles from the tray";
            lnk.Save();
        }
        catch { }
    }

    static void Start(string exe)
    {
        try { Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! }); } catch { }
    }

    static Version? Version(string path)
    {
        try
        {
            var v = FileVersionInfo.GetVersionInfo(path);
            return new Version(v.FileMajorPart, v.FileMinorPart, v.FileBuildPart);
        }
        catch { return null; }
    }

    static string Text(Version? v) => v == null ? "?" : $"{v.Major}.{v.Minor}.{v.Build}";

    static void Info(string heading, string text) =>
        TaskDialog.ShowDialog(new TaskDialogPage { Caption = "NetSwitch", Heading = heading, Text = text, Icon = TaskDialogIcon.Information });
}
