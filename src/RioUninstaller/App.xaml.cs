using System.Windows;
using System.Windows.Threading;
using RioUninstaller.Services;

namespace RioUninstaller;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        System.Globalization.CultureInfo.DefaultThreadCurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
        System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
        var args = e.Args.Select(a => a.ToLowerInvariant()).ToArray();

        if (args.Contains("--register")) { Elevation.RegisterTask(); Shutdown(); return; }
        if (args.Contains("--unregister")) { Elevation.UnregisterTask(); Shutdown(); return; }

        if (!Elevation.IsAdmin)
        {
            // Launch the elevated copy through our scheduled task (no UAC). Only falls back to UAC the first time.
            if (!Elevation.TryRunTask()) Elevation.RelaunchAsAdmin();
            Shutdown();
            return;
        }

        // Elevated: make sure the task exists so next launches skip UAC.
        Task.Run(() => Elevation.EnsureTask());

        DispatcherUnhandledException += OnUnhandled;
        Loc.I.SetLanguage(Settings.Current.Arabic);
        base.OnStartup(e);
        var w = new MainWindow();
        MainWindow = w;
        w.Show();
    }

    void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(e.Exception.Message, "Rio Uninstaller", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }
}
