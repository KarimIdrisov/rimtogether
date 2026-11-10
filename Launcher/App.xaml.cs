using System.Windows;
using System.Diagnostics;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
namespace RimTogether;
public partial class App : Application
{
    private Mutex? mutex;
    public static bool IsPreview { get; private set; }
    protected override async void OnStartup(StartupEventArgs e)
    {
        if (e.Args.Contains("--render-preview"))
        {
            IsPreview = true;
            Environment.SetEnvironmentVariable("RIMTOGETHER_DATA_ROOT", Path.Combine(Environment.CurrentDirectory,".build","design-preview"));
            var output = e.Args[Array.IndexOf(e.Args,"--render-preview")+1];
            var preview = new MainWindow();
            var content = (FrameworkElement)preview.Content;
            content.Measure(new Size(1080,860)); content.Arrange(new Rect(0,0,1080,860)); content.UpdateLayout();
            var bitmap = new RenderTargetBitmap(1080,860,96,96,PixelFormats.Pbgra32); bitmap.Render(content);
            var encoder = new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
            using(var file=File.Create(output))encoder.Save(file);
            Shutdown(); return;
        }
        if (e.Args.FirstOrDefault(a=>a.StartsWith("--wait-pid=")) is {} wait && int.TryParse(wait[11..],out var parent))
        { try { await Process.GetProcessById(parent).WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)); } catch(ArgumentException){} catch(TimeoutException){Shutdown();return;} }
        if (!e.Args.Contains("--managed"))
        {
            var bootstrap = Path.Combine(AppContext.BaseDirectory,"RimTogether.Start.exe");
            if(!File.Exists(bootstrap))bootstrap=Environment.GetEnvironmentVariable("RIMTOGETHER_BOOTSTRAP")??bootstrap;
            if(File.Exists(bootstrap)) { var start=new ProcessStartInfo(bootstrap){UseShellExecute=false};start.ArgumentList.Add("--wait-pid="+Environment.ProcessId);Process.Start(start);Shutdown();return; }
        }
        mutex=new Mutex(true,"Local\\RimTogether-Launcher",out var created);
        if(!created){MessageBox.Show("RimTogether уже открыт.");Shutdown();return;}
        DispatcherUnhandledException+=(_,error)=>{MessageBox.Show(error.Exception.Message,"RimTogether");error.Handled=true;};
        base.OnStartup(e);
        var window=new MainWindow();MainWindow=window;window.Show();
        var bundled=Path.Combine(AppContext.BaseDirectory,"RimTogether.Start.exe");
        if(File.Exists(bundled))_ = Task.Run(()=>BootstrapEntry.RefreshExternalAsync(bundled,Environment.GetEnvironmentVariable("RIMTOGETHER_BOOTSTRAP"),Path.Combine(Contract.DataRoot,"app")));
    }
    protected override void OnExit(ExitEventArgs e){mutex?.Dispose();base.OnExit(e);}
}
