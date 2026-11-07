using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using RioUninstaller.Models;

namespace RioUninstaller.Services;

public static class AppScanner
{
    const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    public static List<InstalledApp> Scan(bool includeSystem = false)
    {
        var list = new List<InstalledApp>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (hive, view) in new[]
        {
            (RegistryHive.LocalMachine, RegistryView.Registry64),
            (RegistryHive.LocalMachine, RegistryView.Registry32),
            (RegistryHive.CurrentUser, RegistryView.Registry64),
        })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var key = root.OpenSubKey(UninstallKey);
                if (key == null) continue;
                foreach (var sub in key.GetSubKeyNames())
                {
                    try
                    {
                        using var k = key.OpenSubKey(sub);
                        var app = Read(k, sub, hive, view, includeSystem);
                        if (app == null) continue;
                        // de-dup identical entries (same name + version) seen in both views
                        string dedup = app.Name + "|" + app.Version;
                        if (!seen.Add(dedup)) continue;
                        list.Add(app);
                    }
                    catch { }
                }
            }
            catch { }
        }

        // Fill derived install locations after all are known
        foreach (var a in list)
            if (string.IsNullOrWhiteSpace(a.InstallLocation))
                a.InstallLocation = DeriveLocation(a);

        return list.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    static InstalledApp Read(RegistryKey k, string sub, RegistryHive hive, RegistryView view, bool includeSystem)
    {
        if (k == null) return null;
        string name = (k.GetValue("DisplayName") as string)?.Trim();
        if (string.IsNullOrEmpty(name)) return null;
        string uninst = (k.GetValue("UninstallString") as string)?.Trim();
        string quiet = (k.GetValue("QuietUninstallString") as string)?.Trim();

        if (!includeSystem)
        {
            if (k.GetValue("SystemComponent") is int sc && sc == 1) return null;
            if (k.GetValue("ParentKeyName") != null) return null;               // patches / sub components
            if (k.GetValue("ReleaseType") is string rt &&
                (rt.Contains("Update", StringComparison.OrdinalIgnoreCase) || rt.Contains("Hotfix", StringComparison.OrdinalIgnoreCase)))
                return null;
            if (Regex.IsMatch(name, @"\(KB\d+\)")) return null;
            if (string.IsNullOrEmpty(uninst) && string.IsNullOrEmpty(quiet)) return null;
        }

        long size = 0;
        try { size = Convert.ToInt64(k.GetValue("EstimatedSize") ?? 0); } catch { }

        DateTime? date = null;
        if (k.GetValue("InstallDate") is string ds &&
            DateTime.TryParseExact(ds.Trim(), "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
            date = d;

        bool isMsi = (k.GetValue("WindowsInstaller") is int wi && wi == 1) ||
                     (uninst?.Contains("msiexec", StringComparison.OrdinalIgnoreCase) ?? false);

        string loc = (k.GetValue("InstallLocation") as string)?.Trim().Trim('"');
        if (!string.IsNullOrEmpty(loc))
        {
            loc = Environment.ExpandEnvironmentVariables(loc).TrimEnd('\\');
            if (!Directory.Exists(loc)) loc = null;
        }

        return new InstalledApp
        {
            Hive = hive,
            View = view,
            KeyName = sub,
            Name = name,
            Publisher = (k.GetValue("Publisher") as string)?.Trim(),
            Version = (k.GetValue("DisplayVersion") as string)?.Trim(),
            UninstallString = uninst,
            QuietUninstallString = quiet,
            DisplayIcon = (k.GetValue("DisplayIcon") as string)?.Trim(),
            InstallLocation = loc,
            IsMsi = isMsi,
            Is64 = view == RegistryView.Registry64,
            SizeKb = size,
            InstallDate = date,
        };
    }

    /// <summary>Install folder from DisplayIcon / UninstallString when InstallLocation is missing. Never returns a protected folder.</summary>
    static string DeriveLocation(InstalledApp a)
    {
        foreach (var candidate in new[] { PathFromCommand(a.DisplayIcon), PathFromCommand(a.UninstallString) })
        {
            if (string.IsNullOrEmpty(candidate)) continue;
            if (candidate.Contains("msiexec", StringComparison.OrdinalIgnoreCase)) continue;
            string dir;
            try { dir = Path.GetDirectoryName(candidate); } catch { continue; }
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;
            // Uninstallers often live in a sub folder like "uninstall" or "_uninst"
            string leaf = Path.GetFileName(dir).ToLowerInvariant();
            if (leaf is "uninstall" or "uninstaller" or "_uninst" or "uninst" or "bin" or "app")
            {
                var parent = Path.GetDirectoryName(dir);
                if (!string.IsNullOrEmpty(parent) && !SafetyGuard.IsProtectedPath(parent)) dir = parent;
            }
            if (SafetyGuard.IsProtectedPath(dir)) continue;
            if (dir.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.Windows), StringComparison.OrdinalIgnoreCase)) continue;
            return dir;
        }
        return null;
    }

    /// <summary>Extracts the executable path from a command line like "C:\x\y.exe" /arg or C:\x\y.exe,0</summary>
    public static string PathFromCommand(string cmd)
    {
        if (string.IsNullOrWhiteSpace(cmd)) return null;
        cmd = Environment.ExpandEnvironmentVariables(cmd.Trim());
        if (cmd.StartsWith("\""))
        {
            int end = cmd.IndexOf('"', 1);
            return end > 1 ? cmd.Substring(1, end - 1) : cmd.Trim('"');
        }
        int comma = cmd.LastIndexOf(',');
        if (comma > 0 && int.TryParse(cmd[(comma + 1)..].Trim(), out _)) cmd = cmd[..comma];
        int exe = cmd.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exe > 0) return cmd[..(exe + 4)];
        return File.Exists(cmd) ? cmd : null;
    }

    public static (string file, string args) SplitCommand(string cmd)
    {
        cmd = Environment.ExpandEnvironmentVariables(cmd.Trim());
        if (cmd.StartsWith("\""))
        {
            int end = cmd.IndexOf('"', 1);
            if (end > 1) return (cmd.Substring(1, end - 1), cmd[(end + 1)..].Trim());
        }
        int exe = cmd.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exe > 0) return (cmd[..(exe + 4)], cmd[(exe + 4)..].Trim());
        int sp = cmd.IndexOf(' ');
        return sp > 0 ? (cmd[..sp], cmd[(sp + 1)..]) : (cmd, "");
    }
}
