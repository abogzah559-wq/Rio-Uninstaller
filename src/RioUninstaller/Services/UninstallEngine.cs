using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using RioUninstaller.Models;

namespace RioUninstaller.Services;

public enum UninstallMode { Normal, Full }
public enum StepState { Pending, Running, Done, Skipped, Failed }

public sealed class EngineResult
{
    public bool Cancelled { get; set; }
    public int Files { get; set; }
    public int Registry { get; set; }
    public int Services { get; set; }
    public int TasksAndStartup { get; set; }
    public int Processes { get; set; }
    public int StartupEntries { get; set; }
    public int Tasks { get; set; }
    public bool RebootNeeded { get; set; }
    public List<string> Details { get; } = new();
}

public static class UninstallEngine
{
    public static async Task<EngineResult> UninstallAsync(
        InstalledApp app, List<InstalledApp> all, UninstallMode mode,
        Action<string, StepState> step, Func<Task<bool>> askForce)
    {
        var r = new EngineResult();
        var finder = new LeftoverFinder(app, all);
        bool full = mode == UninstallMode.Full;

        // 0) restore point
        if (Settings.Current.RestorePoint)
        {
            step("StepRestore", StepState.Running);
            await RunHidden("powershell.exe",
                $"-NoProfile -ExecutionPolicy Bypass -Command \"Checkpoint-Computer -Description 'Rio Uninstaller - {app.Name.Replace("'", "")}' -RestorePointType APPLICATION_UNINSTALL\"",
                TimeSpan.FromSeconds(120));
            step("StepRestore", StepState.Done);
        }

        // 1) close the program
        step("StepKill", StepState.Running);
        await Task.Run(() =>
        {
            finder.PrimeFolders(full);
            r.Processes += KillAll(finder.FindProcesses(), r);
        });
        step("StepKill", StepState.Done);

        // 2) stop its services so files are not locked
        step("StepServices", StepState.Running);
        var services = await Task.Run(() => finder.FindServices());
        foreach (var s in services) await RunHidden("sc.exe", $"stop \"{s}\"", TimeSpan.FromSeconds(20));
        step("StepServices", services.Count > 0 ? StepState.Done : StepState.Skipped);

        // 3) the program's own uninstaller
        step("StepUninstaller", StepState.Running);
        var outcome = await RunUninstallerAsync(app, finder.RefDir);
        if (outcome != UninstallerOutcome.Success)
        {
            step("StepUninstaller", StepState.Failed);
            if (!await askForce()) { r.Cancelled = true; return r; }
        }
        else step("StepUninstaller", StepState.Done);

        // 4) leftovers
        step("StepScan", StepState.Running);
        var items = await Task.Run(() => finder.Find(full));
        if (!full) items = items.Where(i => !i.IsUserData).ToList();
        step("StepScan", StepState.Done);

        step("StepClean", StepState.Running);
        await Task.Run(() =>
        {
            r.Processes += KillAll(finder.FindProcesses(), r);
            DeleteItems(items, app, all, r);
        });
        BackgroundControl.Forget(app);
        step("StepClean", StepState.Done);
        return r;
    }

    // ---------------- uninstaller ----------------

    enum UninstallerOutcome { Success, Failed, Missing }

    static async Task<UninstallerOutcome> RunUninstallerAsync(InstalledApp app, string refDir)
    {
        bool silent = Settings.Current.Silent;
        string file = null, args = "";

        if (silent && !string.IsNullOrWhiteSpace(app.QuietUninstallString))
            (file, args) = AppScanner.SplitCommand(app.QuietUninstallString);
        else if (app.IsMsi)
        {
            var guid = ExtractGuid(app.UninstallString) ?? ExtractGuid(app.KeyName);
            if (guid != null)
            {
                file = "msiexec.exe";
                args = $"/x {guid}" + (silent ? " /qn /norestart" : " /norestart");
            }
        }
        if (file == null && !string.IsNullOrWhiteSpace(app.UninstallString))
        {
            (file, args) = AppScanner.SplitCommand(app.UninstallString);
            if (silent) args = AddSilentArgs(file, args);
        }
        if (file == null) return UninstallerOutcome.Missing;
        if (!file.Equals("msiexec.exe", StringComparison.OrdinalIgnoreCase) &&
            !file.EndsWith("MsiExec.exe", StringComparison.OrdinalIgnoreCase) && !File.Exists(file))
            return UninstallerOutcome.Missing;

        var started = DateTime.Now;
        int exit;
        try
        {
            var psi = new ProcessStartInfo(file, args)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.IsPathRooted(file) ? Path.GetDirectoryName(file) ?? "" : "",
            };
            using var p = Process.Start(psi);
            if (p == null) return UninstallerOutcome.Failed;
            await p.WaitForExitAsync();
            exit = p.ExitCode;
        }
        catch { return UninstallerOutcome.Failed; }

        await WaitForHelpersAsync(started, refDir);

        // judge
        if (!KeyExists(app)) return UninstallerOutcome.Success;
        if (exit == 1605) return UninstallerOutcome.Missing; // MSI: product unknown
        bool exeGone = string.IsNullOrEmpty(refDir) || !Directory.Exists(refDir) ||
                       !SafeAny(() => Directory.EnumerateFiles(refDir, "*.exe", SearchOption.TopDirectoryOnly));
        if (exit == 0 && exeGone) return UninstallerOutcome.Success;
        if (exit == 0)
        {
            // some uninstallers remove their registry entry a moment later
            for (int i = 0; i < 6 && KeyExists(app); i++) await Task.Delay(500);
            if (!KeyExists(app)) return UninstallerOutcome.Success;
        }
        return UninstallerOutcome.Failed;
    }

    static bool SafeAny(Func<IEnumerable<string>> f) { try { return f().Any(); } catch { return false; } }

    static string AddSilentArgs(string file, string args)
    {
        var name = Path.GetFileNameWithoutExtension(file).ToLowerInvariant();
        if (Regex.IsMatch(name, @"^unins\d+$"))
            return (args + " /VERYSILENT /SUPPRESSMSGBOXES /NORESTART").Trim();
        if (name == "update" && args.Contains("--uninstall", StringComparison.OrdinalIgnoreCase))
            return args.Contains(" -s") ? args : (args + " -s").Trim();
        if (IsNsis(file))
        {
            args = Regex.Replace(args, @"\s*_\?=.*$", "");
            if (!Regex.IsMatch(args, @"(^|\s)/S(\s|$)")) args += " /S";
            return (args + " _?=" + Path.GetDirectoryName(file)).Trim();
        }
        return args;
    }

    static bool IsNsis(string file)
    {
        try
        {
            using var fs = File.OpenRead(file);
            int len = (int)Math.Min(fs.Length, 2_000_000);
            var buf = new byte[len];
            int read = fs.Read(buf, 0, len);
            var s = Encoding.ASCII.GetString(buf, 0, read);
            return s.Contains("Nullsoft", StringComparison.Ordinal) || s.Contains("NullsoftInst", StringComparison.Ordinal);
        }
        catch { return false; }
    }

    static string ExtractGuid(string s)
    {
        if (string.IsNullOrEmpty(s)) return null;
        var m = Regex.Match(s, @"\{[0-9A-Fa-f\-]{36}\}");
        return m.Success ? m.Value : null;
    }

    static async Task WaitForHelpersAsync(DateTime since, string refDir)
    {
        string temp = SafetyGuard.Normalize(Path.GetTempPath());
        var deadline = DateTime.Now.AddMinutes(30);
        int self = Environment.ProcessId;
        while (DateTime.Now < deadline)
        {
            bool any = false;
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    if (p.Id == self) continue;
                    var name = p.ProcessName;
                    bool nsisTmp = name.StartsWith("Au_", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Un_", StringComparison.OrdinalIgnoreCase);
                    if (!nsisTmp && p.StartTime < since.AddSeconds(-2)) continue;
                    var path = Native.GetProcessPath(p);
                    if (path == null) continue;
                    if (nsisTmp || SafetyGuard.IsInside(path, temp) ||
                        (!string.IsNullOrEmpty(refDir) && SafetyGuard.IsInside(path, refDir)) ||
                        Regex.IsMatch(Path.GetFileNameWithoutExtension(path), @"^(unins\d*|uninst.*|uninstall.*|_iu\w+)$", RegexOptions.IgnoreCase))
                    { any = true; break; }
                }
                catch { }
            }
            if (!any) return;
            await Task.Delay(700);
        }
    }

    public static bool KeyExists(InstalledApp app)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(app.Hive, app.View);
            using var k = root.OpenSubKey(app.KeyPath);
            return k != null;
        }
        catch { return false; }
    }

    // ---------------- deletion ----------------

    public static int KillAll(List<Process> procs, EngineResult r)
    {
        int n = 0;
        foreach (var p in procs)
        {
            try { p.Kill(true); p.WaitForExit(3000); n++; r?.Details.Add("Process: " + p.ProcessName); } catch { }
        }
        return n;
    }

    static void DeleteItems(List<LeftoverItem> items, InstalledApp app, List<InstalledApp> all, EngineResult r)
    {
        // services
        foreach (var s in items.Where(i => i.Kind == LeftoverKind.Service))
        {
            RunHiddenSync("sc.exe", $"stop \"{s.Path}\"", 15000);
            RunHiddenSync("sc.exe", $"delete \"{s.Path}\"", 15000);
            r.Services++; r.Details.Add(s.Display);
        }
        // tasks
        foreach (var t in items.Where(i => i.Kind == LeftoverKind.Task))
        {
            RunHiddenSync("schtasks.exe", $"/Delete /TN \"{t.Path}\" /F", 15000);
            r.TasksAndStartup++; r.Details.Add(t.Display);
        }
        // registry values
        foreach (var v in items.Where(i => i.Kind == LeftoverKind.RegValue))
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(v.Hive, v.View);
                using var k = root.OpenSubKey(v.Path, true);
                if (k == null || !k.GetValueNames().Contains(v.ValueName, StringComparer.OrdinalIgnoreCase)) continue;
                k.DeleteValue(v.ValueName, false);
                if (v.IsStartup) r.TasksAndStartup++; else r.Registry++;
                r.Details.Add(v.Display);
            }
            catch { }
        }
        // registry keys
        var parentsToCheck = new HashSet<(RegistryHive, RegistryView, string)>();
        foreach (var k in items.Where(i => i.Kind == LeftoverKind.RegKey))
        {
            if (!SafetyGuard.CanDeleteRegKey(k.Path)) continue;
            try
            {
                using var root = RegistryKey.OpenBaseKey(k.Hive, k.View);
                using (var test = root.OpenSubKey(k.Path)) { if (test == null) continue; }
                root.DeleteSubKeyTree(k.Path, false);
                r.Registry++; r.Details.Add(k.Display);
                var parent = k.Path.Contains('\\') ? k.Path[..k.Path.LastIndexOf('\\')] : null;
                if (parent != null && parent.Count(c => c == '\\') == 1) parentsToCheck.Add((k.Hive, k.View, parent));
            }
            catch { }
        }
        foreach (var (hive, view, p) in parentsToCheck) // empty publisher keys under SOFTWARE
        {
            try
            {
                if (!SafetyGuard.CanDeleteRegKey(p)) continue;
                using var root = RegistryKey.OpenBaseKey(hive, view);
                bool empty;
                using (var k = root.OpenSubKey(p)) empty = k != null && k.SubKeyCount == 0 && k.ValueCount == 0;
                if (empty) { root.DeleteSubKey(p, false); r.Registry++; }
            }
            catch { }
        }
        // files (shortcuts)
        foreach (var f in items.Where(i => i.Kind == LeftoverKind.File))
        {
            if (!SafetyGuard.CanDeleteFile(f.Path, app, all)) continue;
            if (DeleteFile(f.Path, r)) { r.Files++; if (f.IsStartup) r.TasksAndStartup++; r.Details.Add(f.Display); }
        }
        // folders — deepest first
        var parents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in items.Where(i => i.Kind == LeftoverKind.Folder).OrderByDescending(i => i.Path.Length))
        {
            if (!SafetyGuard.CanDeleteFolder(d.Path, app, all)) continue;
            if (!Directory.Exists(d.Path)) continue;
            r.Files += DeleteFolder(d.Path, r);
            r.Details.Add(d.Display);
            var parent = Path.GetDirectoryName(d.Path);
            if (parent != null) parents.Add(parent);
        }
        // remove now-empty parent folders (e.g. %AppData%\Publisher)
        foreach (var p in parents)
        {
            try
            {
                if (Directory.Exists(p) && !SafetyGuard.IsProtectedPath(p) && SafetyGuard.CanDeleteFolder(p, app, all)
                    && !Directory.EnumerateFileSystemEntries(p).Any())
                { Directory.Delete(p); r.Files++; }
            }
            catch { }
        }
    }

    static bool DeleteFile(string f, EngineResult r)
    {
        try
        {
            if (!File.Exists(f)) return false;
            File.SetAttributes(f, FileAttributes.Normal);
            File.Delete(f);
            return true;
        }
        catch
        {
            if (Native.MoveFileEx(f, null, Native.MOVEFILE_DELAY_UNTIL_REBOOT)) { r.RebootNeeded = true; return true; }
            return false;
        }
    }

    static int DeleteFolder(string dir, EngineResult r)
    {
        int count = 0;
        try
        {
            // don't follow junctions/symlinks out of the folder
            var di = new DirectoryInfo(dir);
            if (di.Attributes.HasFlag(FileAttributes.ReparsePoint)) { Directory.Delete(dir); return 1; }

            foreach (var sub in Directory.GetDirectories(dir)) count += DeleteFolder(sub, r);
            foreach (var f in Directory.GetFiles(dir)) if (DeleteFile(f, r)) count++;
            try { Directory.Delete(dir, false); count++; }
            catch
            {
                if (Native.MoveFileEx(dir, null, Native.MOVEFILE_DELAY_UNTIL_REBOOT)) r.RebootNeeded = true;
            }
        }
        catch { }
        return count;
    }

    // ---------------- process helpers ----------------

    public static async Task<int> RunHidden(string file, string args, TimeSpan timeout)
    {
        try
        {
            var psi = new ProcessStartInfo(file, args) { CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden };
            using var p = Process.Start(psi);
            if (p == null) return -1;
            using var cts = new CancellationTokenSource(timeout);
            try { await p.WaitForExitAsync(cts.Token); } catch (OperationCanceledException) { return -1; }
            return p.ExitCode;
        }
        catch { return -1; }
    }

    public static int RunHiddenSync(string file, string args, int timeoutMs)
    {
        try
        {
            var psi = new ProcessStartInfo(file, args) { CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden };
            using var p = Process.Start(psi);
            if (p == null) return -1;
            return p.WaitForExit(timeoutMs) ? p.ExitCode : -1;
        }
        catch { return -1; }
    }
}
