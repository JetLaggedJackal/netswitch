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

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        bool running = Mutex.TryOpenExisting(@"Local\NetSwitch.Tray", out var existing);
        existing?.Dispose();
        try { if (!afterUpdate && Installer.HandleFirstRun(running)) return 0; }
        catch { /* never let the install offer stop the app from starting */ }

        using var mutex = new Mutex(true, @"Local\NetSwitch.Tray", out bool first);
        if (!first) return 0; // already running

        // An unexpected error should show a pop-up and keep the tray icon alive, not kill the app.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => ReportCrash(e.Exception, fatal: false);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => { if (e.ExceptionObject is Exception ex) ReportCrash(ex, fatal: true); };
        TaskScheduler.UnobservedTaskException += (_, e) => { Log(e.Exception); e.SetObserved(); };

        Application.Run(new TrayApp());
        return 0;
    }

    public static string LogPath => Path.Combine(Path.GetDirectoryName(AppConfig.DefaultPath)!, "error.log");

    static void Log(Exception ex)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 512_000) File.Delete(LogPath);
            File.AppendAllText(LogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] NetSwitch {Updater.CurrentText}\n{ex}\n\n");
        }
        catch { }
    }

    static void ReportCrash(Exception ex, bool fatal)
    {
        Log(ex);
        if (fatal) return;
        try
        {
            PopupForm.Show(PopupBuilder.Error("Something went wrong",
                $"{ex.Message}\n\nNetSwitch is still running. Details were saved to {LogPath}."), 20);
        }
        catch { }
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
                case "--fix":
                    result = QuickFix.Run(Arg("--fix") ?? "", Arg("--adapter") ?? "");
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
