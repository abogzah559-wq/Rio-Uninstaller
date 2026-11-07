using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace RioUninstaller.Services;

/// <summary>Checks GitHub Releases (only) for a newer Rio Uninstaller and installs it.</summary>
public static class Updater
{
    const string LatestApi = "https://api.github.com/repos/abogzah559-wq/Rio-Uninstaller/releases/latest";
    const string AssetName = "RioUninstaller-Setup.exe";
    const string AllowedHost = "github.com";

    public sealed class Release { public Version Version; public string Tag; public string Url; }

    public static Version Current
    {
        get
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
            return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
        }
    }

    static HttpClient NewClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("RioUninstaller/" + Current);
        c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return c;
    }

    public static async Task<Release> CheckAsync()
    {
        try
        {
            using var c = NewClient();
            c.Timeout = TimeSpan.FromSeconds(15);
            using var doc = JsonDocument.Parse(await c.GetStringAsync(LatestApi));
            var root = doc.RootElement;
            var tag = root.GetProperty("tag_name").GetString() ?? "";
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out var v)) return null;
            v = new Version(v.Major, v.Minor, Math.Max(0, v.Build));
            if (v <= Current) return null;
            foreach (var a in root.GetProperty("assets").EnumerateArray())
            {
                if (!string.Equals(a.GetProperty("name").GetString(), AssetName, StringComparison.OrdinalIgnoreCase)) continue;
                var url = a.GetProperty("browser_download_url").GetString();
                if (url == null || !new Uri(url).Host.Equals(AllowedHost, StringComparison.OrdinalIgnoreCase)) return null;
                return new Release { Version = v, Tag = tag, Url = url };
            }
        }
        catch { }
        return null;
    }

    /// <summary>Downloads the setup and runs it silently; the setup restarts Rio when done.</summary>
    public static async Task<bool> InstallAsync(Release r, IProgress<int> progress)
    {
        var path = Path.Combine(Path.GetTempPath(), $"RioUninstaller-Setup-{r.Version}.exe");
        try
        {
            using var c = NewClient();
            c.DefaultRequestHeaders.Accept.Clear();
            using var resp = await c.GetAsync(r.Url, HttpCompletionOption.ResponseHeadersRead);
            resp.EnsureSuccessStatusCode();
            long total = resp.Content.Headers.ContentLength ?? 0;
            await using (var src = await resp.Content.ReadAsStreamAsync())
            await using (var dst = File.Create(path))
            {
                var buf = new byte[81920];
                long read = 0; int n, last = -1;
                while ((n = await src.ReadAsync(buf)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n));
                    read += n;
                    if (total > 0)
                    {
                        int pct = (int)(read * 100 / total);
                        if (pct != last) { last = pct; progress?.Report(pct); }
                    }
                }
            }
            Process.Start(new ProcessStartInfo(path, "/SILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS")
            { UseShellExecute = true });
            return true;
        }
        catch { return false; }
    }
}
