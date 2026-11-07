using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using RioUninstaller.Models;

namespace RioUninstaller.Services;

public static class IconLoader
{
    /// <summary>Tries every source Windows itself uses, so almost every program gets its real icon.</summary>
    public static ImageSource Load(InstalledApp app)
    {
        foreach (var (file, index) in Candidates(app))
        {
            var img = Extract(file, index);
            if (img != null) return img;
        }
        foreach (var (file, _) in Candidates(app))
        {
            var img = Associated(file);
            if (img != null) return img;
        }
        return null;
    }

    static IEnumerable<(string, int)> Candidates(InstalledApp app)
    {
        // 1) DisplayIcon
        var di = ParseIcon(app.DisplayIcon);
        if (Usable(di.file)) yield return di;

        // 2) MSI product icon (Installer\Products\<packed guid>\ProductIcon)
        var msi = MsiProductIcon(app);
        if (Usable(msi.file)) yield return msi;

        // 3) The main exe in the install folder (top level, then one level deeper)
        var loc = app.InstallLocation;
        if (!string.IsNullOrEmpty(loc) && Directory.Exists(loc))
        {
            foreach (var exe in MainExes(loc, SearchOption.TopDirectoryOnly)) yield return (exe, 0);
            foreach (var exe in MainExes(loc, SearchOption.AllDirectories).Take(3)) yield return (exe, 0);
        }

        // 4) The uninstaller itself
        var un = AppScanner.PathFromCommand(app.UninstallString);
        if (Usable(un) && !un.EndsWith("msiexec.exe", StringComparison.OrdinalIgnoreCase)) yield return (un, 0);
    }

    static bool Usable(string f) =>
        !string.IsNullOrEmpty(f) && File.Exists(f) && !f.EndsWith(".msi", StringComparison.OrdinalIgnoreCase);

    static IEnumerable<string> MainExes(string dir, SearchOption opt)
    {
        List<string> files = null;
        try
        {
            var en = new EnumerationOptions { RecurseSubdirectories = opt == SearchOption.AllDirectories, MaxRecursionDepth = 2, IgnoreInaccessible = true };
            files = Directory.EnumerateFiles(dir, "*.exe", en).Take(60).ToList();
        }
        catch { }
        if (files == null) yield break;
        foreach (var f in files
                     .Where(f => !LeftoverFinder.IsGenericExe(Path.GetFileNameWithoutExtension(f)))
                     .OrderByDescending(f => { try { return new FileInfo(f).Length; } catch { return 0L; } })
                     .Take(3))
            yield return f;
    }

    static (string file, int index) ParseIcon(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return (null, 0);
        s = Environment.ExpandEnvironmentVariables(s.Trim());
        int idx = 0;
        int comma = s.LastIndexOf(',');
        if (comma > 0 && int.TryParse(s[(comma + 1)..].Trim(), out var n)) { idx = n; s = s[..comma]; }
        s = s.Trim().Trim('"');
        return (s, idx);
    }

    static (string file, int index) MsiProductIcon(InstalledApp app)
    {
        var guid = app.KeyName;
        if (string.IsNullOrEmpty(guid) || guid.Length != 38 || guid[0] != '{') return (null, 0);
        var packed = PackGuid(guid);
        if (packed == null) return (null, 0);
        foreach (var path in new[] { @"SOFTWARE\Classes\Installer\Products\" + packed })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                using var k = root.OpenSubKey(path);
                var v = k?.GetValue("ProductIcon") as string;
                if (!string.IsNullOrEmpty(v)) return ParseIcon(v);
            }
            catch { }
        }
        try
        {
            using var root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
            using var k = root.OpenSubKey(@"SOFTWARE\Microsoft\Installer\Products\" + packed);
            var v = k?.GetValue("ProductIcon") as string;
            if (!string.IsNullOrEmpty(v)) return ParseIcon(v);
        }
        catch { }
        return (null, 0);
    }

    /// <summary>{12345678-ABCD-EF01-2345-6789ABCDEF01} → MSI "packed" form.</summary>
    static string PackGuid(string g)
    {
        var s = g.Trim('{', '}').Replace("-", "");
        if (s.Length != 32) return null;
        string Rev(string x) => new string(x.Reverse().ToArray());
        string Swap(string x) { var c = new char[x.Length]; for (int i = 0; i < x.Length; i += 2) { c[i] = x[i + 1]; c[i + 1] = x[i]; } return new string(c); }
        return (Rev(s[..8]) + Rev(s[8..12]) + Rev(s[12..16]) + Swap(s[16..])).ToUpperInvariant();
    }

    static ImageSource Extract(string file, int index)
    {
        var large = new IntPtr[1];
        var small = new IntPtr[1];
        try
        {
            uint got = Native.ExtractIconEx(file, index, large, small, 1);
            if ((got == 0 || large[0] == IntPtr.Zero) && index != 0)
                got = Native.ExtractIconEx(file, 0, large, small, 1);
            if (got == 0 || large[0] == IntPtr.Zero) return null;
            return ToImage(large[0]);
        }
        catch { return null; }
        finally
        {
            if (large[0] != IntPtr.Zero) Native.DestroyIcon(large[0]);
            if (small[0] != IntPtr.Zero) Native.DestroyIcon(small[0]);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SHFILEINFO
    {
        public IntPtr hIcon; public int iIcon; public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr SHGetFileInfo(string path, uint attrs, ref SHFILEINFO psfi, uint cb, uint flags);

    static ImageSource Associated(string file)
    {
        if (string.IsNullOrEmpty(file) || !File.Exists(file)) return null;
        var info = new SHFILEINFO();
        try
        {
            const uint SHGFI_ICON = 0x100, SHGFI_LARGEICON = 0x0;
            SHGetFileInfo(file, 0, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI_ICON | SHGFI_LARGEICON);
            return info.hIcon == IntPtr.Zero ? null : ToImage(info.hIcon);
        }
        catch { return null; }
        finally { if (info.hIcon != IntPtr.Zero) Native.DestroyIcon(info.hIcon); }
    }

    static ImageSource ToImage(IntPtr hIcon)
    {
        var src = Imaging.CreateBitmapSourceFromHIcon(hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
        src.Freeze();
        return src;
    }
}
