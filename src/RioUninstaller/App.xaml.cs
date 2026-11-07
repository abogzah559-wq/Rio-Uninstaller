using System.Windows;
using System.Windows.Threading;

namespace RioUninstaller;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnUnhandled;
        Loc.I.SetLanguage(Services.Settings.Current.Arabic);
        base.OnStartup(e);
    }

    void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(e.Exception.Message, "Rio Uninstaller", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }
}
