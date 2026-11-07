using System.IO;
using System.Text.Json;

namespace RioUninstaller.Services;

public sealed class Settings
{
    public static string DataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RioUninstaller");
    static string FilePath => Path.Combine(DataDir, "settings.json");

    public bool Arabic { get; set; } = true;
    public bool RestorePoint { get; set; } = false;
    public bool Silent { get; set; } = true;
    public bool ShowSystem { get; set; } = false;

    public static Settings Current { get; private set; } = Load();

    static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch { }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}
