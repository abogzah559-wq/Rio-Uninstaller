using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using RioUninstaller.Models;

namespace RioUninstaller.Services;

public enum LeftoverKind { Folder, File, RegKey, RegValue, Service, Task }

public sealed class LeftoverItem
{
    public LeftoverKind Kind { get; init; }
    public string Path { get; init; }                 // file/folder path, registry key path, service name, task name
    public RegistryHive Hive { get; init; }
    public RegistryView View { get; init; }
    public string ValueName { get; init; }
    public bool IsUserData { get; init; }             // settings / data — only removed in complete wipe
    public bool IsStartup { get; init; }              // Run entry / startup shortcut

    public string Display => Kind switch
    {
        LeftoverKind.RegKey => $"{HiveName}\\{Path}",
        LeftoverKind.RegValue => $"{HiveName}\\{Path} → {ValueName}",
        LeftoverKind.Service => $"Service: {Path}",
        LeftoverKind.Task => $"Task: {Path}",
        _ => Path,
    };
    string HiveName => Hive == RegistryHive.CurrentUser ? "HKCU" : "HKLM";

    public string Key => $"{Kind}|{Hive}|{View}|{Path}|{ValueName}".ToLowerInvariant();
}

/// <summary>Finds everything that belongs to a program. Matching is strict: exact (normalized) folder/key names
/// or references into the program's own install folder. Everything passes through <see cref="SafetyGuard"/>.</summary>
public sealed class LeftoverFinder
{
    readonly InstalledApp _app;
    readonly List<InstalledApp> _all;
    readonly HashSet<string> _kws = new();
    readonly HashSet<string> _pubs = new();
    readonly string _refDir;                                 // the program's own folder (safe to match references against)
    readonly List<string> _ownFolders = new();               // folders confirmed as belonging to the program

    public string RefDir => _refDir;
    public IReadOnlyCollection<string> Keywords => _kws;

    public LeftoverFinder(InstalledApp app, IEnumerable<InstalledApp> all)
    {
        _app = app;
        _all = all.ToList();

        var loc = app.InstallLocation;
        if (!string.IsNullOrEmpty(loc) && Directory.Exists(loc) && SafetyGuard.CanDeleteFolder(loc, app, _all))
            _refDir = SafetyGuard.Normalize(loc);

        BuildKeywords();
    }

    // ---------------- keywords ----------------

    static readonly HashSet<string> Generic = new(StringComparer.OrdinalIgnoreCase)
    {
        "microsoft","windows","common","commonfiles","program","programs","programfiles","app","apps","application","data","temp","tmp",
        "cache","update","updater","updates","setup","install","installer","launcher","uninstall","uninstaller","system","tools",
        "tool","driver","drivers","games","game","software","client","service","services","helper","runtime","plugin","plugins",
        "config","settings","user","users","local","locallow","roaming","default","packages","crashpad","crashreports","logs","log",
        "bin","lib","x64","x86","shared","support","sdk","net","framework","desktop","documents","downloads","music","pictures",
        "videos","player","current","main","core","engine","files","resources","assets","package","appdata","home","new","test",
        "inc","llc","ltd","corp","corporation","company","studio","studios","team","group","the","pro","free","lite","beta",
        "intel","amd","nvidia","nvidiacorporation","realtek","google","apple","adobe","mozilla","oracle","java","python","node","nodejs",
        "electron","squirrel","steam","steamapps","epicgames","riotuninstaller","riouninstaller","rio","vc","redist","directx","dotnet",
    };

    public static string Norm(string s) =>
        string.IsNullOrEmpty(s) ? "" : new string(s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    public static string CleanName(string n)
    {
        if (string.IsNullOrWhiteSpace(n)) return "";
        var s = Regex.Replace(n, @"\(.*?\)|\[.*?\]", " ");
        s = Regex.Replace(s, @"(?<![\w])(version|ver\.|x64|x86|64-bit|32-bit|64 bit|32 bit|amd64|win64|win32|for windows)(?![\w])", " ", RegexOptions.IgnoreCase);
        var m = Regex.Match(s, @"\s[vV]?\d+(\.\d+)+.*$");
        if (m.Success) s = s[..m.Index];
        return Regex.Replace(s, @"\s+", " ").Trim(' ', '-', '–', '_', ',', '.');
    }

    static string CleanPublisher(string p)
    {
        if (string.IsNullOrWhiteSpace(p)) return "";
        var s = Regex.Replace(p, @"[,.]?\s*(inc\.?|llc|ltd\.?|limited|corp\.?|corporation|co\.?|gmbh|s\.?a\.?|b\.?v\.?|ab|oy|pty|plc|contributors|team|foundation|software|technologies|technology|games|entertainment|studios?)\s*$", "", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"[,.]?\s*(inc\.?|llc|ltd\.?|corp\.?|corporation|gmbh)\s*$", "", RegexOptions.IgnoreCase);
        return s.Trim(' ', ',', '.');
    }

    public static bool IsGenericExe(string name) =>
        Regex.IsMatch(name ?? "", @"^(unins\d*|uninst.*|uninstall.*|update.*|setup.*|crash.*|.*helper.*|elevat.*|squirrel|createdump|vc_?redist.*|dxsetup|install.*|launcher|ffmpeg|7za?|python\w*|pythonw|node|java\w*|cefsharp.*|qtwebengineprocess|unitycrashhandler\d*|dotnet|wow_helper|notification_helper|reporter|bugreport.*|errorreport.*|cleanup|repair|maintenance.*|vcruntime.*|msedgewebview2|chrome_proxy|.*service)$",
            RegexOptions.IgnoreCase);

    static void AddKw(HashSet<string> set, string raw, int min)
    {
        var n = Norm(raw);
        if (n.Length < min || Generic.Contains(n)) return;
        if (n.All(char.IsDigit)) return;
        set.Add(n);
    }

    static IEnumerable<string> PrimaryKeywords(InstalledApp a)
    {
        var name = CleanName(a.Name);
        yield return Norm(name);
        var pub = CleanPublisher(a.Publisher);
        var firstPub = pub.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (!string.IsNullOrEmpty(firstPub) && name.StartsWith(firstPub + " ", StringComparison.OrdinalIgnoreCase))
            yield return Norm(name[(firstPub.Length + 1)..]);
        if (!string.IsNullOrEmpty(a.InstallLocation))
        {
            var leaf = Path.GetFileName(a.InstallLocation.TrimEnd('\\'));
            yield return Norm(leaf);
        }
    }

    void BuildKeywords()
    {
        var name = CleanName(_app.Name);
        AddKw(_kws, name, 3);
        AddKw(_kws, name + "updater", 6);
        var pub = CleanPublisher(_app.Publisher);
        var firstPub = pub.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (!string.IsNullOrEmpty(firstPub) && name.StartsWith(firstPub + " ", StringComparison.OrdinalIgnoreCase))
            AddKw(_kws, name[(firstPub.Length + 1)..], 4);

        if (!string.IsNullOrEmpty(pub)) { var np = Norm(pub); if (np.Length >= 2) _pubs.Add(np); }
        if (!string.IsNullOrEmpty(firstPub)) { var nf = Norm(firstPub); if (nf.Length >= 3) _pubs.Add(nf); }

        if (_refDir != null)
        {
            var dir = _refDir;
            for (int i = 0; i < 3 && dir != null; i++)               // walk up past generic leaf names ("Application", "bin")
            {
                var leaf = Path.GetFileName(dir);
                if (!Generic.Contains(Norm(leaf))) { AddKw(_kws, leaf, 3); break; }
                dir = Path.GetDirectoryName(dir);
            }
            try
            {
                foreach (var exe in Directory.EnumerateFiles(_refDir, "*.exe", SearchOption.TopDirectoryOnly).Take(20))
                {
                    var n = Path.GetFileNameWithoutExtension(exe);
                    if (!IsGenericExe(n)) AddKw(_kws, n, 4);
                }
            }
            catch { }
        }
        var iconExe = AppScanner.PathFromCommand(_app.DisplayIcon);
        if (!string.IsNullOrEmpty(iconExe) && iconExe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            var n = Path.GetFileNameWithoutExtension(iconExe);
            if (!IsGenericExe(n)) AddKw(_kws, n, 4);
        }

        // Drop any keyword that is also another program's identity, or a publisher of other programs.
        var others = new HashSet<string>();
        foreach (var o in _all)
        {
            if (o.Id == _app.Id) continue;
            if (Norm(CleanName(o.Name)) == Norm(name)) continue;            // same product, other arch/entry
            foreach (var k in PrimaryKeywords(o)) if (!string.IsNullOrEmpty(k)) others.Add(k);
            var op = Norm(CleanPublisher(o.Publisher));
            if (op.Length > 0) others.Add(op);
        }
        _kws.RemoveWhere(k => others.Contains(k));
        _kws.RemoveWhere(k => _pubs.Contains(k) && k != Norm(name));         // never treat the publisher folder itself as ours
    }

    bool MatchesKw(string name) => _kws.Contains(Norm(name));
    bool MatchesPub(string name) { var n = Norm(name); return n.Length > 0 && _pubs.Contains(n); }

    bool PointsInto(string s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        string ex;
        try { ex = Environment.ExpandEnvironmentVariables(s); } catch { ex = s; }
        ex = ex.Replace('/', '\\');
        foreach (var dir in OwnDirs())
        {
            int i = ex.IndexOf(dir, StringComparison.OrdinalIgnoreCase);
            if (i < 0) continue;
            int end = i + dir.Length;
            if (end == ex.Length || ex[end] == '\\' || ex[end] == '"' || ex[end] == '|') return true;
        }
        return false;
    }

    IEnumerable<string> OwnDirs()
    {
        if (_refDir != null) yield return _refDir;
        foreach (var f in _ownFolders) yield return f;
    }

    // ---------------- main scan ----------------

    public List<LeftoverItem> Find(bool includeData)
    {
        var items = new List<LeftoverItem>();
        var seen = new HashSet<string>();
        void Add(LeftoverItem it) { if (seen.Add(it.Key)) items.Add(it); }

        // 1) Program folders
        if (_refDir != null && Directory.Exists(_refDir))
            Add(new LeftoverItem { Kind = LeftoverKind.Folder, Path = _refDir });

        var pf = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"),
        };
        foreach (var root in pf.Distinct(StringComparer.OrdinalIgnoreCase))
            ScanRoot(root, false, Add);

        // 2) Data / settings folders (complete wipe only)
        var dataRoots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            SafetyGuard.LocalLow,
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            Path.GetTempPath(),
        };
        // Some apps install straight into %LocalAppData%\<Name> (Discord, etc.) — that's the program folder, not data.
        foreach (var root in dataRoots.Distinct(StringComparer.OrdinalIgnoreCase))
            ScanRoot(root, true, it => { if (includeData || (it.Kind == LeftoverKind.Folder && _refDir != null && SafetyGuard.IsInside(it.Path, _refDir))) Add(it); });

        foreach (var it in items.Where(i => i.Kind == LeftoverKind.Folder))
            if (!_ownFolders.Contains(it.Path, StringComparer.OrdinalIgnoreCase)) _ownFolders.Add(it.Path);

        // 3) Shortcuts (desktop, start menu, taskbar pins)
        FindShortcuts(Add);

        // 4) Registry
        Add(new LeftoverItem { Kind = LeftoverKind.RegKey, Hive = _app.Hive, View = _app.View, Path = _app.KeyPath });
        FindRunEntries(Add);
        FindRegistryTraces(Add);
        FindAppPaths(Add);
        FindProgIds(Add);
        FindFirewallRules(Add);
        if (includeData) FindSoftwareKeys(Add);

        // 5) Services & tasks
        foreach (var s in FindServices()) Add(new LeftoverItem { Kind = LeftoverKind.Service, Path = s });
        foreach (var t in FindTasks()) Add(new LeftoverItem { Kind = LeftoverKind.Task, Path = t });

        return items;
    }

    void ScanRoot(string root, bool isData, Action<LeftoverItem> add)
    {
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return;
        string[] dirs;
        try { dirs = Directory.GetDirectories(root); } catch { return; }
        foreach (var d in dirs)
        {
            var leaf = Path.GetFileName(d);
            if (MatchesKw(leaf))
            {
                if (SafetyGuard.CanDeleteFolder(d, _app, _all))
                    add(new LeftoverItem { Kind = LeftoverKind.Folder, Path = SafetyGuard.Normalize(d), IsUserData = isData });
            }
            else if (MatchesPub(leaf))
            {
                string[] subs;
                try { subs = Directory.GetDirectories(d); } catch { continue; }
                foreach (var s in subs)
                    if (MatchesKw(Path.GetFileName(s)) && SafetyGuard.CanDeleteFolder(s, _app, _all))
                        add(new LeftoverItem { Kind = LeftoverKind.Folder, Path = SafetyGuard.Normalize(s), IsUserData = isData });
            }
        }
    }

    // ---------------- shortcuts ----------------

    public IEnumerable<string> ShortcutRoots()
    {
        Func<Environment.SpecialFolder, string> env = Environment.GetFolderPath;
        yield return env(Environment.SpecialFolder.Desktop);
        yield return env(Environment.SpecialFolder.CommonDesktopDirectory);
        yield return env(Environment.SpecialFolder.Programs);
        yield return env(Environment.SpecialFolder.CommonPrograms);
        yield return Path.Combine(env(Environment.SpecialFolder.ApplicationData), @"Microsoft\Internet Explorer\Quick Launch");
    }

    static object _shell;
    public static string ResolveLnk(string lnk)
    {
        try
        {
            _shell ??= Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
            dynamic sh = _shell;
            dynamic sc = sh.CreateShortcut(lnk);
            string t = sc.TargetPath;
            string a = sc.Arguments;
            return (t ?? "") + " " + (a ?? "");
        }
        catch { return ""; }
    }

    void FindShortcuts(Action<LeftoverItem> add)
    {
        string startup = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
        string cstartup = Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup);
        foreach (var root in ShortcutRoots().Where(Directory.Exists))
        {
            bool recursive = !root.EndsWith("Desktop", StringComparison.OrdinalIgnoreCase);
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(root, "*.*", recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly).ToList(); }
            catch { continue; }
            foreach (var f in files)
            {
                var ext = Path.GetExtension(f).ToLowerInvariant();
                if (ext != ".lnk" && ext != ".url") continue;
                string target = ext == ".lnk" ? ResolveLnk(f) : SafeRead(f);
                bool ours = PointsInto(target);
                if (!ours && ext == ".lnk" && string.IsNullOrWhiteSpace(target.Trim()))
                    ours = MatchesKw(Path.GetFileNameWithoutExtension(f));              // broken shortcut with our name
                if (!ours) continue;
                bool isStartup = SafetyGuard.IsInside(f, startup) || SafetyGuard.IsInside(f, cstartup);
                add(new LeftoverItem { Kind = LeftoverKind.File, Path = f, IsStartup = isStartup });
            }
            if (recursive && !root.Contains("Quick Launch"))
            {
                try
                {
                    foreach (var d in Directory.GetDirectories(root))
                        if (MatchesKw(Path.GetFileName(d)) && !SafetyGuard.IsProtectedPath(d))
                            add(new LeftoverItem { Kind = LeftoverKind.Folder, Path = SafetyGuard.Normalize(d) });
                }
                catch { }
            }
        }
    }

    static string SafeRead(string f) { try { return File.ReadAllText(f); } catch { return ""; } }

    // ---------------- registry ----------------

    static readonly (RegistryHive, RegistryView, string)[] RunKeys =
    {
        (RegistryHive.CurrentUser, RegistryView.Registry64, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run"),
        (RegistryHive.CurrentUser, RegistryView.Registry64, @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce"),
        (RegistryHive.LocalMachine, RegistryView.Registry64, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run"),
        (RegistryHive.LocalMachine, RegistryView.Registry64, @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce"),
        (RegistryHive.LocalMachine, RegistryView.Registry32, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run"),
        (RegistryHive.LocalMachine, RegistryView.Registry32, @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce"),
    };

    public IEnumerable<LeftoverItem> RunEntries()
    {
        var list = new List<LeftoverItem>();
        foreach (var (hive, view, path) in RunKeys)
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var k = root.OpenSubKey(path);
                if (k == null) continue;
                foreach (var v in k.GetValueNames())
                {
                    var data = k.GetValue(v, "", RegistryValueOptions.DoNotExpandEnvironmentNames)?.ToString();
                    if (PointsInto(data))
                        list.Add(new LeftoverItem { Kind = LeftoverKind.RegValue, Hive = hive, View = view, Path = path, ValueName = v, IsStartup = true });
                }
            }
            catch { }
        }
        return list;
    }

    void FindRunEntries(Action<LeftoverItem> add)
    {
        foreach (var r in RunEntries())
        {
            add(r);
            // matching StartupApproved flag
            var approved = r.Hive == RegistryHive.CurrentUser || r.View == RegistryView.Registry64
                ? @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run"
                : @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run32";
            if (ValueExists(r.Hive, RegistryView.Registry64, approved, r.ValueName))
                add(new LeftoverItem { Kind = LeftoverKind.RegValue, Hive = r.Hive, View = RegistryView.Registry64, Path = approved, ValueName = r.ValueName });
        }
    }

    static bool ValueExists(RegistryHive hive, RegistryView view, string path, string name)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            using var k = root.OpenSubKey(path);
            return k?.GetValueNames().Contains(name, StringComparer.OrdinalIgnoreCase) == true;
        }
        catch { return false; }
    }

    /// <summary>MuiCache, Compatibility Assistant, compatibility layers — values named after files in the program folder.</summary>
    void FindRegistryTraces(Action<LeftoverItem> add)
    {
        var keys = new (RegistryHive, string)[]
        {
            (RegistryHive.CurrentUser, @"SOFTWARE\Classes\Local Settings\Software\Microsoft\Windows\Shell\MuiCache"),
            (RegistryHive.CurrentUser, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Compatibility Assistant\Store"),
            (RegistryHive.CurrentUser, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers"),
            (RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers"),
            (RegistryHive.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\FeatureUsage\AppSwitched"),
            (RegistryHive.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\FeatureUsage\ShowJumpView"),
            (RegistryHive.CurrentUser, @"SOFTWARE\Microsoft\DirectX\UserGpuPreferences"),
        };
        foreach (var (hive, path) in keys)
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
                using var k = root.OpenSubKey(path);
                if (k == null) continue;
                foreach (var v in k.GetValueNames())
                    if (PointsInto(v))
                        add(new LeftoverItem { Kind = LeftoverKind.RegValue, Hive = hive, View = RegistryView.Registry64, Path = path, ValueName = v });
            }
            catch { }
        }
    }

    void FindAppPaths(Action<LeftoverItem> add)
    {
        foreach (var (hive, view) in new[] { (RegistryHive.LocalMachine, RegistryView.Registry64), (RegistryHive.LocalMachine, RegistryView.Registry32), (RegistryHive.CurrentUser, RegistryView.Registry64) })
        {
            const string ap = @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths";
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var k = root.OpenSubKey(ap);
                if (k == null) continue;
                foreach (var s in k.GetSubKeyNames())
                {
                    using var sk = k.OpenSubKey(s);
                    var def = sk?.GetValue("")?.ToString();
                    if (PointsInto(def))
                        add(new LeftoverItem { Kind = LeftoverKind.RegKey, Hive = hive, View = view, Path = ap + "\\" + s });
                }
            }
            catch { }
        }
    }

    /// <summary>ProgIDs / URL protocols whose name contains our keyword and whose command runs from our folder.</summary>
    void FindProgIds(Action<LeftoverItem> add)
    {
        if (_kws.Count == 0) return;
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
                using var classes = root.OpenSubKey(@"SOFTWARE\Classes");
                if (classes == null) continue;
                foreach (var name in classes.GetSubKeyNames())
                {
                    if (name.StartsWith(".") || name == "*") continue;
                    var n = Norm(name);
                    if (!_kws.Any(k => k.Length >= 3 && n.Contains(k))) continue;
                    using var cmd = classes.OpenSubKey(name + @"\shell\open\command");
                    using var ico = classes.OpenSubKey(name + @"\DefaultIcon");
                    var c = cmd?.GetValue("")?.ToString();
                    var i = ico?.GetValue("")?.ToString();
                    if (PointsInto(c) || (c == null && PointsInto(i)))
                        add(new LeftoverItem { Kind = LeftoverKind.RegKey, Hive = hive, View = RegistryView.Registry64, Path = @"SOFTWARE\Classes\" + name });
                }
            }
            catch { }
        }
    }

    void FindFirewallRules(Action<LeftoverItem> add)
    {
        const string fw = @"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\FirewallRules";
        try
        {
            using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var k = root.OpenSubKey(fw);
            if (k == null) return;
            foreach (var v in k.GetValueNames())
            {
                var data = k.GetValue(v)?.ToString();
                if (data == null) continue;
                var m = Regex.Match(data, @"\|App=([^|]+)", RegexOptions.IgnoreCase);
                if (m.Success && PointsInto(m.Groups[1].Value))
                    add(new LeftoverItem { Kind = LeftoverKind.RegValue, Hive = RegistryHive.LocalMachine, View = RegistryView.Registry64, Path = fw, ValueName = v });
            }
        }
        catch { }
    }

    /// <summary>HKCU/HKLM\Software\[Publisher\]Product — settings (complete wipe only).</summary>
    void FindSoftwareKeys(Action<LeftoverItem> add)
    {
        foreach (var (hive, view) in new[] { (RegistryHive.CurrentUser, RegistryView.Registry64), (RegistryHive.LocalMachine, RegistryView.Registry64), (RegistryHive.LocalMachine, RegistryView.Registry32) })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var sw = root.OpenSubKey("SOFTWARE");
                if (sw == null) continue;
                foreach (var name in sw.GetSubKeyNames())
                {
                    if (MatchesKw(name))
                    {
                        var p = @"SOFTWARE\" + name;
                        if (SafetyGuard.CanDeleteRegKey(p))
                            add(new LeftoverItem { Kind = LeftoverKind.RegKey, Hive = hive, View = view, Path = p, IsUserData = true });
                    }
                    else if (MatchesPub(name))
                    {
                        using var pk = sw.OpenSubKey(name);
                        if (pk == null) continue;
                        foreach (var sub in pk.GetSubKeyNames())
                        {
                            if (!MatchesKw(sub)) continue;
                            var p = $@"SOFTWARE\{name}\{sub}";
                            if (SafetyGuard.CanDeleteRegKey(p))
                                add(new LeftoverItem { Kind = LeftoverKind.RegKey, Hive = hive, View = view, Path = p, IsUserData = true });
                        }
                    }
                }
            }
            catch { }
        }
    }

    // ---------------- services / tasks / processes ----------------

    public List<string> FindServices()
    {
        var list = new List<string>();
        try
        {
            using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var svcs = root.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
            if (svcs == null) return list;
            foreach (var name in svcs.GetSubKeyNames())
            {
                try
                {
                    using var s = svcs.OpenSubKey(name);
                    var img = s?.GetValue("ImagePath", null, RegistryValueOptions.DoNotExpandEnvironmentNames)?.ToString();
                    if (string.IsNullOrEmpty(img)) continue;
                    if (img.Contains(@"\Windows\", StringComparison.OrdinalIgnoreCase) || img.StartsWith("system32", StringComparison.OrdinalIgnoreCase)) continue;
                    if (PointsInto(img)) list.Add(name);
                }
                catch { }
            }
        }
        catch { }
        return list;
    }

    public List<string> FindTasks()
    {
        var list = new List<string>();
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), @"System32\Tasks");
        try
        {
            foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                try
                {
                    var rel = f.Substring(root.Length).Replace('/', '\\');
                    if (rel.StartsWith(@"\Microsoft\", StringComparison.OrdinalIgnoreCase)) continue;
                    var xml = File.ReadAllText(f, Encoding.Unicode);
                    if (!xml.Contains("<Task", StringComparison.OrdinalIgnoreCase)) xml = File.ReadAllText(f);
                    foreach (Match m in Regex.Matches(xml, @"<(Command|WorkingDirectory|Arguments)>([^<]+)</", RegexOptions.IgnoreCase))
                    {
                        if (PointsInto(System.Net.WebUtility.HtmlDecode(m.Groups[2].Value))) { list.Add(rel); break; }
                    }
                }
                catch { }
            }
        }
        catch { }
        return list;
    }

    public List<Process> FindProcesses()
    {
        var list = new List<Process>();
        int self = Environment.ProcessId;
        foreach (var p in Process.GetProcesses())
        {
            if (p.Id == self || p.Id <= 4) continue;
            var path = Native.GetProcessPath(p);
            if (path != null && OwnDirs().Any(d => SafetyGuard.IsInside(path, d))) list.Add(p);
        }
        return list;
    }

    /// <summary>Quick pre-scan of own folders (install dir + data dirs) — used before killing processes.</summary>
    public void PrimeFolders(bool includeData)
    {
        var items = new List<LeftoverItem>();
        foreach (var root in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"),
                     Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                     Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                 })
            ScanRoot(root, false, items.Add);
        foreach (var i in items)
            if (!_ownFolders.Contains(i.Path, StringComparer.OrdinalIgnoreCase)) _ownFolders.Add(i.Path);
    }
}
