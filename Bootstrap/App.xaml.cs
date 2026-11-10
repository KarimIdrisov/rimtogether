using System.Diagnostics;
using System.IO;
using System.Windows;
namespace RimTogether;
public partial class BootstrapApp:Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if(e.Args.FirstOrDefault(a=>a.StartsWith("--wait-pid=")) is {} wait&&int.TryParse(wait[11..],out var pid))
        {try{await Process.GetProcessById(pid).WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));}catch(ArgumentException){}catch(TimeoutException){Shutdown(1);return;}}
        var root=Environment.GetEnvironmentVariable("RIMTOGETHER_UPDATER_ROOT")??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"RimTogether","app");
        var window=new UpdateWindow(root);window.ShowDialog();
        if(window.Result is not {} result){Shutdown(1);return;}
        if(!e.Args.Contains("--check-only")){
            var start=new ProcessStartInfo(Path.Combine(result.Directory,"RimTogether.exe")){UseShellExecute=false,WorkingDirectory=result.Directory};start.ArgumentList.Add("--managed");
            start.Environment["RIMTOGETHER_BOOTSTRAP"]=Environment.ProcessPath??Path.Combine(AppContext.BaseDirectory,"RimTogether.Start.exe");Process.Start(start);
        }
        Shutdown(0);
    }
}
