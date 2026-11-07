using System.IO;
using System.Text.Json;
using Microsoft.Win32;
using RioUninstaller.Models;

namespace RioUninstaller.Services;

/// <summary>Stops a program from running in the background without deleting anything — and can undo it.</summary>
public static class BackgroundControl
{
    public sealed class SavedService { public string Name { get; set; } public int Start { get; set; } }
    public sealed class SavedValue
    {
        public RegistryHive Hive { get; set; }
        public RegistryView View { get; set; }
        public string Path { get; set; }
        public string Name { get; set; }
        public string Data { get; set; }
        public RegistryValueKind Kind { get; set; }
    }
    public sealed class SavedShortcut { public string Original { get; set; } public string Backup { get; set; } }
    public sealed class Record
    {
        public string AppName { get; set; }
        public List<SavedService> Services { get; set; } = new();
        public List<SavedValue> RunValues { get; set; } = new();
        public List<SavedShortcut> Shortcuts { get; set; } = new();
        public List<string> Tasks { get; set; } = new();
    }

    static string FilePath => Path.Combine(Settings.DataDir, "background.json");
    static readonly object Lock = new();

    static Dictionary<string, Record> Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Dictionary<string, Record>>(File.ReadAllText(FilePath)) ?? new();
        }
        catch { }
        return new();
    }

    static void Save(Dictionary<string, Record> d)
    {
        try
        {
            Directory.CreateDirectory(Settings.DataDir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(d, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    public static HashSet<string> DisabledIds()
    {
        lock (Lock) return Load().Keys.ToHashSet();
    }

    public static void Forget(InstalledApp app)
    {
        lock (Lock) { var d = Load(); if (d.Remove(app.Id)) Save(d); }
    }

    public sealed class Status { public int Processes; public int Services; public int Startup; public int Tasks; }

    public static Status Inspect(InstalledApp app, List<InstalledApp> all)
    {
        var f = new LeftoverFinder(app, all);
        f.PrimeFolders(false);
        var st = new Status
        {
            Processes = f.FindProcesses().Count,
            Services = f.FindServices().Count,
            Tasks = f.FindTasks().Count,
            Startup = f.RunEntries().Count() + StartupShortcuts(f).Count,
        };
        return st;
    }

    static List<string> StartupShortcuts(LeftoverFinder f)
    {
        var list = new List<string>();
        foreach (var dir in new[] { Environment.GetFolderPath(Environment.SpecialFolder.Startup), Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup) })
        {
            try
            {
                foreach (var lnk in Directory.EnumerateFiles(dir, "*.lnk"))
                {
                    var t = LeftoverFinder.ResolveLnk(lnk);
                    if (!string.IsNullOrWhiteSpace(t) && f.RefDir != null && t.Contains(f.RefDir, StringComparison.OrdinalIgnoreCase))
                        list.Add(lnk);
                }
            }
            catch { }
        }
        return list;
    }

    public static EngineResult Disable(InstalledApp app, List<InstalledApp> all)
    {
        var r = new EngineResult();
        var f = new LeftoverFinder(app, all);
        f.PrimeFolders(false);
        Record rec;
        lock (Lock) { var d = Load(); rec = d.TryGetValue(app.Id, out var ex) ? ex : new Record { AppName = app.Name }; }

        // 1) services: stop + disable
        foreach (var s in f.FindServices())
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                using var k = root.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + s, true);
                if (k == null) continue;
                int start = k.GetValue("Start") is int i ? i : 3;
                UninstallEngine.RunHiddenSync("sc.exe", $"stop \"{s}\"", 15000);
                if (start != 4)
                {
                    if (!rec.Services.Any(x => x.Name == s)) rec.Services.Add(new SavedService { Name = s, Start = start });
                    k.SetValue("Start", 4, RegistryValueKind.DWord);
                }
                r.Services++; r.Details.Add("Service: " + s);
            }
            catch { }
        }

        // 2) processes
        r.Processes = UninstallEngine.KillAll(f.FindProcesses(), r);

        // 3) startup registry entries → backed up, then removed
        foreach (var v in f.RunEntries())
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(v.Hive, v.View);
                using var k = root.OpenSubKey(v.Path, true);
                if (k == null) continue;
                var data = k.GetValue(v.ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames)?.ToString();
                if (data == null) continue;
                rec.RunValues.Add(new SavedValue { Hive = v.Hive, View = v.View, Path = v.Path, Name = v.ValueName, Data = data, Kind = k.GetValueKind(v.ValueName) });
                k.DeleteValue(v.ValueName, false);
                r.StartupEntries++; r.Details.Add(v.Display);
            }
            catch { }
        }

        // 4) startup-folder shortcuts → moved to backup
        var backupDir = Path.Combine(Settings.DataDir, "startup-backup");
        foreach (var lnk in StartupShortcuts(f))
        {
            try
            {
                Directory.CreateDirectory(backupDir);
                var dest = Path.Combine(backupDir, Guid.NewGuid().ToString("N") + ".lnk");
                File.Move(lnk, dest);
                rec.Shortcuts.Add(new SavedShortcut { Original = lnk, Backup = dest });
                r.StartupEntries++; r.Details.Add(lnk);
            }
            catch { }
        }

        // 5) scheduled tasks → disabled
        foreach (var t in f.FindTasks())
        {
            if (UninstallEngine.RunHiddenSync("schtasks.exe", $"/Change /TN \"{t}\" /DISABLE", 15000) == 0)
            {
                if (!rec.Tasks.Contains(t)) rec.Tasks.Add(t);
                r.Tasks++; r.Details.Add("Task: " + t);
            }
        }

        lock (Lock) { var d = Load(); d[app.Id] = rec; Save(d); }
        return r;
    }

    public static EngineResult Enable(InstalledApp app)
    {
        var r = new EngineResult();
        Record rec;
        lock (Lock) { var d = Load(); if (!d.TryGetValue(app.Id, out rec)) return r; }

        foreach (var s in rec.Services)
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                using var k = root.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + s.Name, true);
                if (k == null) continue;
                k.SetValue("Start", s.Start, RegistryValueKind.DWord);
                if (s.Start == 2) UninstallEngine.RunHiddenSync("sc.exe", $"start \"{s.Name}\"", 15000);
                r.Services++; r.Details.Add("Service: " + s.Name);
            }
            catch { }
        }
        foreach (var v in rec.RunValues)
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(v.Hive, v.View);
                using var k = root.CreateSubKey(v.Path, true);
                k.SetValue(v.Name, v.Data, v.Kind == RegistryValueKind.ExpandString ? RegistryValueKind.ExpandString : RegistryValueKind.String);
                r.StartupEntries++; r.Details.Add(v.Name);
            }
            catch { }
        }
        foreach (var s in rec.Shortcuts)
        {
            try
            {
                if (File.Exists(s.Backup) && !File.Exists(s.Original)) File.Move(s.Backup, s.Original);
                r.StartupEntries++; r.Details.Add(s.Original);
            }
            catch { }
        }
        foreach (var t in rec.Tasks)
        {
            UninstallEngine.RunHiddenSync("schtasks.exe", $"/Change /TN \"{t}\" /ENABLE", 15000);
            r.Tasks++; r.Details.Add("Task: " + t);
        }
        lock (Lock) { var d = Load(); d.Remove(app.Id); Save(d); }
        return r;
    }
}
