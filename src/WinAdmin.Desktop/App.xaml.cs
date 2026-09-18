using System.Windows;
using WinAdmin.Core.Services;

namespace WinAdmin.Desktop;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        var reset = MachineSetup.PrepareThisPc();
        var libraries = SetupWindow.Probe();
        var missingRequired = libraries.Any(l => l.Required && !l.Installed);
        if (reset.IsNewPc || missingRequired)
        {
            var setup = new SetupWindow(reset, libraries);
            var ok = setup.ShowDialog();
            if (ok != true)
            {
                Shutdown();
                return;
            }
        }

        var main = new MainWindow();
        MainWindow = main;
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        main.WindowState = WindowState.Maximized;
        main.Show();
        main.WindowState = WindowState.Maximized;
        main.Activate();
    }
}
