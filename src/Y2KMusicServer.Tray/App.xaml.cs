using System.Windows;

// Type alias: with <UseWindowsForms>true</UseWindowsForms> enabled,
// the SDK implicitly imports System.Windows.Forms, which collides
// with System.Windows on common type names (Application, MessageBox).
// Aliasing the WPF type explicitly resolves the ambiguity without
// turning off implicit usings or fully qualifying every reference.
using Application = System.Windows.Application;

namespace Y2KMusicServer.Tray;

public partial class App : Application
{
    private TrayApp? _tray;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Single-instance guard: only one tray at a time.
        if (!SingleInstance.AcquireOrFocusExisting("Y2KMusicServer.Tray"))
        {
            Shutdown();
            return;
        }

        _tray = new TrayApp();
        _tray.Initialise();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        base.OnExit(e);
    }
}
