using System.Windows;
using System.Diagnostics;
using System.IO;
namespace RimTogether;
public partial class App : Application
{
    private Mutex? mutex;
    protected override void OnStartup(StartupEventArgs e)
    {
        if (!e.Args.Contains("--managed"))
        {
            var bootstrap = Environment.GetEnvironmentVariable("RIMTOGETHER_BOOTSTRAP") ?? Path.Combine(AppContext.BaseDirectory, "RimTogether.Start.exe");
            if (File.Exists(bootstrap))
            {
                var start = new ProcessStartInfo(bootstrap) { UseShellExecute = false };
                start.ArgumentList.Add("--wait-pid=" + Environment.ProcessId);
                Process.Start(start); Shutdown(); return;
            }
        }
        mutex = new Mutex(true, "Local\\RimTogether-Launcher", out var created);
        if (!created) { MessageBox.Show("RimTogether уже открыт."); Shutdown(); return; }
        DispatcherUnhandledException += (_, error) => { MessageBox.Show(error.Exception.Message, "RimTogether"); error.Handled = true; };
        base.OnStartup(e);
    }
    protected override void OnExit(ExitEventArgs e) { mutex?.Dispose(); base.OnExit(e); }
}
