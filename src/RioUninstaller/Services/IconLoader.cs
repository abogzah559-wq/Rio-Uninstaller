using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RioUninstaller.Models;

namespace RioUninstaller.Services;

public static class IconLoader
{
    public static ImageSource Load(InstalledApp app)
    {
        var (file, index) = ParseIcon(app.DisplayIcon);
        if (file == null || !File.Exists(file) || file.EndsWith(".msi", StringComparison.OrdinalIgnoreCase))
        {
            file = null;
            if (!string.IsNullOrEmpty(app.InstallLocation) && Directory.Exists(app.InstallLocation))
            {
                try
                {
                    file = Directory.EnumerateFiles(app.InstallLocation, "*.exe", SearchOption.TopDirectoryOnly)
                        .Where(f => !LeftoverFinder.IsGenericExe(Path.GetFileNameWithoutExtension(f)))
                        .OrderByDescending(f => new FileInfo(f).Length)
                        .FirstOrDefault();
                }
                catch { }
            }
            index = 0;
        }
        if (file == null) return null;
        return Extract(file, index);
    }

    static (string, int) ParseIcon(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return (null, 0);
        s = Environment.ExpandEnvironmentVariables(s.Trim());
        int idx = 0;
        int comma = s.LastIndexOf(',');
        if (comma > 0 && int.TryParse(s[(comma + 1)..].Trim(), out var n)) { idx = n; s = s[..comma]; }
        return (s.Trim().Trim('"'), idx);
    }

    static ImageSource Extract(string file, int index)
    {
        var large = new IntPtr[1];
        var small = new IntPtr[1];
        try
        {
            uint got = Native.ExtractIconEx(file, index, large, small, 1);
            if (got == 0 || large[0] == IntPtr.Zero)
            {
                if (index != 0) { got = Native.ExtractIconEx(file, 0, large, small, 1); }
                if (got == 0 || large[0] == IntPtr.Zero) return null;
            }
            var src = Imaging.CreateBitmapSourceFromHIcon(large[0], Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }
        catch { return null; }
        finally
        {
            if (large[0] != IntPtr.Zero) Native.DestroyIcon(large[0]);
            if (small[0] != IntPtr.Zero) Native.DestroyIcon(small[0]);
        }
    }
}
