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
    public static async Task DownloadAndSwapAsync(Release r, IProgress<int>? progress)
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Can't find the running exe.");
        var newFile = exe + ".new";
        var oldFile = exe + ".old";

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

        try
        {
            if (File.Exists(oldFile)) File.Delete(oldFile);
            File.Move(exe, oldFile);
            File.Move(newFile, exe);
        }
        catch (UnauthorizedAccessException)
        {
            TryDelete(newFile);
            throw new UnauthorizedAccessException(
                $"NetSwitch can't replace itself in {Path.GetDirectoryName(exe)}. Move it to a folder you can write to, such as %LOCALAPPDATA%\\Programs\\NetSwitch.");
        }

        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false, ArgumentList = { "--after-update", Environment.ProcessId.ToString() } });
    }

    /// <summary>Called at startup of the new version.</summary>
    public static void AfterUpdate(string[] args)
    {
        if (args.Length >= 2 && int.TryParse(args[1], out var pid))
        {
            try { using var p = Process.GetProcessById(pid); p.WaitForExit(15_000); } catch { }
        }
        var exe = Environment.ProcessPath;
        if (exe != null) TryDelete(exe + ".old");
    }

    public static void CleanupLeftovers()
    {
        var exe = Environment.ProcessPath;
        if (exe == null) return;
        TryDelete(exe + ".old");
        TryDelete(exe + ".new");
    }

    static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
}
