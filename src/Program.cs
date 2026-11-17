namespace NetSwitch;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--preview")
            return Preview.Run(args.Length > 1 ? args[1] : "editor");

        bool afterUpdate = args.Length > 0 && args[0] == "--after-update";
        if (afterUpdate) { Updater.AfterUpdate(args); args = Array.Empty<string>(); }
        else if (args.Length == 0) Updater.CleanupLeftovers();

        // Elevated helper modes: started by the tray app through UAC, do one job and exit.
        if (args.Length > 0 && args[0].StartsWith("--"))
            return RunHelper(args);

        using var mutex = new Mutex(true, @"Local\NetSwitch.Tray", out bool first);
        if (!first) return 0; // already running

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new TrayApp());
        return 0;
    }

    static int RunHelper(string[] args)
    {
        string? Arg(string name)
        {
            var i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        var configPath = Arg("--config") ?? AppConfig.DefaultPath;
        var resultFile = Elevation.ResultFile(configPath);
        (bool ok, string msg) result;
        try
        {
            switch (args[0])
            {
                case "--apply":
                    var cfg = AppConfig.Load(configPath);
                    var name = Arg("--apply") ?? "";
                    var profile = cfg.Profiles.FirstOrDefault(p => p.Name == name);
                    result = profile == null ? (false, $"Profile \"{name}\" not found.") : NetApply.Apply(profile);
                    break;
                case "--startup-on":
                    result = Startup.Install(Arg("--startup-on") ?? Environment.UserName, Elevation.ExePath);
                    break;
                case "--startup-off":
                    result = Startup.Remove();
                    break;
                default:
                    result = (false, "Unknown option " + args[0]);
                    break;
            }
        }
        catch (Exception ex) { result = (false, ex.Message); }

        try { File.WriteAllText(resultFile, result.msg); } catch { }
        return result.ok ? 0 : 1;
    }
}
