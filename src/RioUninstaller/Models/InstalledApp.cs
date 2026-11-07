using System.ComponentModel;
using System.Windows.Media;
using Microsoft.Win32;

namespace RioUninstaller.Models;

public sealed class InstalledApp : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler PropertyChanged;
    void Raise(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));

    // Where the uninstall entry lives
    public RegistryHive Hive { get; init; }
    public RegistryView View { get; init; }
    public string KeyName { get; init; }            // subkey name under ...\Uninstall
    public string KeyPath => $@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{KeyName}";

    public string Name { get; init; }
    public string Publisher { get; init; }
    public string Version { get; init; }
    public string InstallLocation { get; set; }      // may be derived
    public string UninstallString { get; init; }
    public string QuietUninstallString { get; init; }
    public string DisplayIcon { get; init; }
    public bool IsMsi { get; init; }
    public bool Is64 { get; init; }
    public long SizeKb { get; init; }
    public DateTime? InstallDate { get; init; }

    ImageSource _icon;
    public ImageSource Icon { get => _icon; set { _icon = value; Raise(nameof(Icon)); } }

    bool _bgDisabled;
    public bool BackgroundDisabled { get => _bgDisabled; set { _bgDisabled = value; Raise(nameof(BackgroundDisabled)); } }

    public string SizeText => SizeKb <= 0 ? "" : FormatSize(SizeKb * 1024L);
    public string DateText => InstallDate?.ToString("yyyy/MM/dd", System.Globalization.CultureInfo.InvariantCulture) ?? "";
    public string PublisherText => string.IsNullOrWhiteSpace(Publisher) ? "" : Publisher;
    public string Initial => string.IsNullOrEmpty(Name) ? "?" : Name.Substring(0, 1).ToUpperInvariant();

    public static string FormatSize(long bytes)
    {
        string[] u = { "B", "KB", "MB", "GB", "TB" };
        double v = bytes; int i = 0;
        while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return i == 0 ? $"{v:0} {u[i]}" : $"{v:0.#} {u[i]}";
    }

    public string Id => $"{Hive}|{View}|{KeyName}";
}
