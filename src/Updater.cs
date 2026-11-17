using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;

namespace NetSwitch;

/// <summary>Checks GitHub releases for a newer NetSwitch.exe and swaps it in place.</summary>
public static class Updater
{
    const string Repo = "JetLaggedJackal/netswitch";
    const string AssetName = "NetSwitch.exe";

    public sealed record Release(Version Version, string Tag, string Notes, string AssetUrl, long Size, string PageUrl);

    public static Version Current
    {
        get
        {
            var v = typeof(Updater).Assembly.GetName().Version ?? new Version(1, 0, 0);
            return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
        }
    }

    public static string CurrentText => $"{Current.Major}.{Current.Minor}.{Current.Build}";

    static HttpClient NewClient(TimeSpan timeout)
    {
        var http = new HttpClient { Timeout = timeout };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("NetSwitch-updater", CurrentText));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return http;
    }

    /// <summary>The latest published release, or null if there is none yet.</summary>
    public static async Task<Release?> GetLatestAsync()
    {
        using var http = NewClient(TimeSpan.FromSeconds(20));
        using var resp = await http.GetAsync($"https://api.github.com/repos/{Repo}/releases/latest");
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version)) return null;
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            if (!string.Equals(asset.GetProperty("name").GetString(), AssetName, StringComparison.OrdinalIgnoreCase)) continue;
            return new Release(
                new Version(version.Major, version.Minor, Math.Max(0, version.Build)),
                tag,
                root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "",
                asset.GetProperty("browser_download_url").GetString() ?? "",
                asset.GetProperty("size").GetInt64(),
                root.GetProperty("html_url").GetString() ?? "");
        }
        return null;
    }

    public static bool IsNewer(Release r) => r.Version > Current;

    /// <summary>
    /// Downloads the release next to the running exe, swaps the files (Windows allows renaming a running exe)
    /// and starts the new version, which waits for this process to exit. The caller should then exit the app.
    /// </summary>
    static int _running;
    /// <summary>True while an update is downloading or swapping.</summary>
    public static bool IsUpdating => Volatile.Read(ref _running) != 0;

    /// <summary>Returns false (and does nothing) if another update is already running.</summary>
    public static async Task<bool> DownloadAndSwapAsync(Release r, IProgress<int>? progress)
    {
        if (Interlocked.Exchange(ref _running, 1) != 0) return false;
        try { await DownloadAndSwapCoreAsync(r, progress); return true; }
        catch { Volatile.Write(ref _running, 0); throw; }
        // On success _running stays set: the app is about to exit and restart.
    }

    static async Task DownloadAndSwapCoreAsync(Release r, IProgress<int>? progress)
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Can't find the running exe.");
        var dir = Path.GetDirectoryName(exe)!;
        var stamp = DateTime.Now.ToString("yyyyMMddHHmmss");
        var newFile = $"{exe}.new-{stamp}";
        var oldFile = $"{exe}.old-{stamp}"; // unique, so a leftover that is still locked can't block us
        CleanupLeftovers();

        try
        {
            using (var http = NewClient(TimeSpan.FromMinutes(10)))
            using (var resp = await http.GetAsync(r.AssetUrl, HttpCompletionOption.ResponseHeadersRead))
            {
                resp.EnsureSuccessStatusCode();
                var total = resp.Content.Headers.ContentLength ?? r.Size;
                await using var src = await resp.Content.ReadAsStreamAsync();
                await using var dst = File.Create(newFile);
                var buffer = new byte[81920];
                long done = 0; int read, last = -1;
                while ((read = await src.ReadAsync(buffer)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, read));
                    done += read;
                    var pct = total > 0 ? (int)(done * 100 / total) : 0;
                    if (pct != last) { progress?.Report(pct); last = pct; }
                }
                if (r.Size > 0 && done != r.Size)
                    throw new IOException($"Download was incomplete ({done:N0} of {r.Size:N0} bytes).");
            }
        }
        catch { TryDelete(newFile); throw; }

        // Antivirus often holds a freshly downloaded exe open for a few seconds, so retry the renames.
        try
        {
            await RetryAsync(() => File.Move(exe, oldFile), "move the current version aside");
            try { await RetryAsync(() => File.Move(newFile, exe), "put the new version in place"); }
            catch { try { File.Move(oldFile, exe); } catch { } throw; }
        }
        catch (UnauthorizedAccessException)
        {
            TryDelete(newFile);
            throw new UnauthorizedAccessException(
                $"NetSwitch can't replace itself in {dir}. Move it to a folder you can write to, such as %LOCALAPPDATA%\\Programs\\NetSwitch.");
        }
        catch { TryDelete(newFile); throw; }

        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false, ArgumentList = { "--after-update", Environment.ProcessId.ToString() } });
    }

    static async Task RetryAsync(Action action, string what)
    {
        for (int attempt = 1; ; attempt++)
        {
            try { action(); return; }
            catch (IOException ex) when (attempt < 20) { _ = ex; await Task.Delay(500); }
            catch (IOException ex) { throw new IOException($"Couldn't {what}: {ex.Message}", ex); }
        }
    }

    /// <summary>Called at startup of the new version.</summary>
    public static void AfterUpdate(string[] args)
    {
        if (args.Length >= 2 && int.TryParse(args[1], out var pid))
        {
            try { using var p = Process.GetProcessById(pid); p.WaitForExit(15_000); } catch { }
        }
        // The old process may need a moment to let go of its file.
        for (int i = 0; i < 10 && Leftovers().Any(); i++) { CleanupLeftovers(); if (Leftovers().Any()) Thread.Sleep(500); }
    }

    static IEnumerable<string> Leftovers()
    {
        var exe = Environment.ProcessPath;
        if (exe == null) return Array.Empty<string>();
        try
        {
            var name = Path.GetFileName(exe);
            return Directory.EnumerateFiles(Path.GetDirectoryName(exe)!, name + ".*")
                .Where(f => { var ext = f[(exe.Length + 1)..]; return ext.StartsWith("old") || ext.StartsWith("new"); })
                .ToList();
        }
        catch { return Array.Empty<string>(); }
    }

    /// <summary>Deletes NetSwitch.exe.old*/.new* left by earlier updates; locked ones are skipped.</summary>
    public static void CleanupLeftovers()
    {
        foreach (var f in Leftovers()) TryDelete(f);
    }

    static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
}
