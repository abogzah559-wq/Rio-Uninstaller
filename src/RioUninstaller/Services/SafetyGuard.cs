using System.IO;
using RioUninstaller.Models;

namespace RioUninstaller.Services;

/// <summary>
/// Last line of defence: nothing is deleted unless it passes these checks.
/// Protected system / user folders, their parents, and folders that hold other installed programs are never touched.
/// </summary>
public static class SafetyGuard
{
    static readonly Lazy<List<string>> _roots = new(BuildRoots);

    static readonly HashSet<string> ProtectedLeafNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft", "Windows", "Common Files", "Packages", "Programs", "Temp", "Users", "System32", "SysWOW64",
        "steamapps", "common", "WindowsApps", "Microsoft.NET", "dotnet", "Package Cache", "Installer",
        "Windows Defender", "Windows NT", "Windows Mail", "Windows Media Player", "Windows Photo Viewer",
        "Internet Explorer", "Reference Assemblies", "MSBuild", "Desktop", "Documents", "Downloads",
        "Start Menu", "Startup", "Fonts", "Default", "Public", "All Users", "Application Data",
        "RioUninstaller", "Rio Uninstaller", "NVIDIA Corporation", "Intel", "AMD", "Realtek",
    };

    static List<string> BuildRoots()
    {
        Func<Environment.SpecialFolder, string> env = Environment.GetFolderPath;
        var list = new List<string>
        {
            env(Environment.SpecialFolder.Windows),
            env(Environment.SpecialFolder.System),
            env(Environment.SpecialFolder.SystemX86),
            env(Environment.SpecialFolder.ProgramFiles),
            env(Environment.SpecialFolder.ProgramFilesX86),
            env(Environment.SpecialFolder.CommonProgramFiles),
            env(Environment.SpecialFolder.CommonProgramFilesX86),
            env(Environment.SpecialFolder.CommonApplicationData),
            env(Environment.SpecialFolder.ApplicationData),
            env(Environment.SpecialFolder.LocalApplicationData),
            env(Environment.SpecialFolder.UserProfile),
            env(Environment.SpecialFolder.Desktop),
            env(Environment.SpecialFolder.CommonDesktopDirectory),
            env(Environment.SpecialFolder.MyDocuments),
            env(Environment.SpecialFolder.CommonDocuments),
            env(Environment.SpecialFolder.MyMusic),
            env(Environment.SpecialFolder.MyPictures),
            env(Environment.SpecialFolder.MyVideos),
            env(Environment.SpecialFolder.StartMenu),
            env(Environment.SpecialFolder.Programs),
            env(Environment.SpecialFolder.CommonStartMenu),
            env(Environment.SpecialFolder.CommonPrograms),
            env(Environment.SpecialFolder.Startup),
            env(Environment.SpecialFolder.CommonStartup),
            env(Environment.SpecialFolder.Templates),
            env(Environment.SpecialFolder.Fonts),
            Path.GetTempPath(),
            Path.Combine(env(Environment.SpecialFolder.UserProfile), "Downloads"),
            Path.Combine(env(Environment.SpecialFolder.UserProfile), "Saved Games"),
            Path.Combine(env(Environment.SpecialFolder.UserProfile), "OneDrive"),
            Path.Combine(LocalLow, ""),
            Path.Combine(env(Environment.SpecialFolder.LocalApplicationData), "Programs"),
            Path.Combine(env(Environment.SpecialFolder.LocalApplicationData), "Packages"),
            Path.Combine(env(Environment.SpecialFolder.LocalApplicationData), "Microsoft"),
            Path.Combine(env(Environment.SpecialFolder.ApplicationData), "Microsoft"),
            Path.Combine(env(Environment.SpecialFolder.CommonApplicationData), "Microsoft"),
            Path.Combine(env(Environment.SpecialFolder.ProgramFiles), "WindowsApps"),
            Path.Combine(env(Environment.SpecialFolder.CommonApplicationData), "Package Cache"),
            AppContext.BaseDirectory,
            Settings.DataDir,
        };
        try
        {
            var users = Directory.GetParent(env(Environment.SpecialFolder.UserProfile))?.FullName;
            if (users != null) list.Add(users);
        }
        catch { }
        return list.Where(s => !string.IsNullOrWhiteSpace(s)).Select(Normalize).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static string LocalLow =>
        Path.Combine(Directory.GetParent(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData))?.FullName ?? "", "LocalLow");

    public static string Normalize(string p)
    {
        if (string.IsNullOrWhiteSpace(p)) return "";
        try { p = Path.GetFullPath(Environment.ExpandEnvironmentVariables(p.Trim().Trim('"'))); } catch { }
        return p.TrimEnd('\\', '/');
    }

    /// <summary>True if path is a protected root, the parent of one, a drive root, or too shallow.</summary>
    public static bool IsProtectedPath(string path)
    {
        var p = Normalize(path);
        if (p.Length <= 3) return true;                                   // C:\ or empty
        if (Path.GetPathRoot(p)?.TrimEnd('\\').Equals(p, StringComparison.OrdinalIgnoreCase) == true) return true;
        foreach (var r in _roots.Value)
        {
            if (p.Equals(r, StringComparison.OrdinalIgnoreCase)) return true;
            if (r.StartsWith(p + "\\", StringComparison.OrdinalIgnoreCase)) return true; // p is a parent of a root
        }
        if (ProtectedLeafNames.Contains(Path.GetFileName(p))) return true;
        return false;
    }

    public static bool IsInside(string child, string parent)
    {
        if (string.IsNullOrEmpty(child) || string.IsNullOrEmpty(parent)) return false;
        var c = Normalize(child); var pa = Normalize(parent);
        return c.Equals(pa, StringComparison.OrdinalIgnoreCase) || c.StartsWith(pa + "\\", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Folder can be deleted for <paramref name="target"/> only if it doesn't host any other installed program.</summary>
    public static bool CanDeleteFolder(string folder, InstalledApp target, IEnumerable<InstalledApp> all)
    {
        if (IsProtectedPath(folder)) return false;
        var f = Normalize(folder);
        if (IsInside(AppContext.BaseDirectory, f)) return false;           // never delete ourselves
        foreach (var other in all)
        {
            if (ReferenceEquals(other, target) || other.Id == target.Id) continue;
            var loc = other.InstallLocation;
            if (string.IsNullOrEmpty(loc)) continue;
            // Same folder or the folder contains another program → refuse
            if (IsInside(loc, f)) return false;
        }
        return true;
    }

    /// <summary>File can be deleted if it doesn't live in a folder that belongs to another program (unless inside target's own folder).</summary>
    public static bool CanDeleteFile(string file, InstalledApp target, IEnumerable<InstalledApp> all)
    {
        var f = Normalize(file);
        if (IsInside(f, Environment.GetFolderPath(Environment.SpecialFolder.Windows))) return false;
        if (IsInside(f, AppContext.BaseDirectory)) return false;
        foreach (var other in all)
        {
            if (other.Id == target.Id || string.IsNullOrEmpty(other.InstallLocation)) continue;
            if (IsInside(f, other.InstallLocation) && !IsInside(f, target.InstallLocation ?? "\0")) return false;
        }
        return true;
    }

    static readonly string[] ProtectedRegPrefixes =
    {
        @"SOFTWARE\Microsoft", @"SOFTWARE\Classes", @"SOFTWARE\Policies", @"SOFTWARE\WOW6432Node\Microsoft",
        @"SOFTWARE\WOW6432Node\Classes", @"SOFTWARE\WOW6432Node\Policies", @"SYSTEM", @"SOFTWARE\Wow6432Node",
        @"SOFTWARE\RegisteredApplications", @"SOFTWARE\Clients", @"SOFTWARE\RioUninstaller",
    };

    /// <summary>Registry keys that may be deleted as a whole tree. Uninstall entries are allowed explicitly.</summary>
    public static bool CanDeleteRegKey(string path)
    {
        var p = path.Trim('\\');
        if (p.StartsWith(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\", StringComparison.OrdinalIgnoreCase)
            && p.Count(c => c == '\\') == 5) return true;
        if (p.StartsWith(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\", StringComparison.OrdinalIgnoreCase)
            && p.Count(c => c == '\\') == 5) return true;
        if (p.StartsWith(@"SOFTWARE\Classes\", StringComparison.OrdinalIgnoreCase) && p.Count(c => c == '\\') == 2
            && !p.EndsWith(@"\*") && !p.Contains(@"\.") && !p.EndsWith(@"\CLSID", StringComparison.OrdinalIgnoreCase))
            return true;                                               // a ProgID verified by its command path
        foreach (var pre in ProtectedRegPrefixes)
            if (p.Equals(pre, StringComparison.OrdinalIgnoreCase) || p.StartsWith(pre + "\\", StringComparison.OrdinalIgnoreCase))
                return false;
        // must be at least SOFTWARE\Something
        return p.StartsWith(@"SOFTWARE\", StringComparison.OrdinalIgnoreCase) && p.Count(c => c == '\\') >= 1 && p.Length > 12;
    }
}
