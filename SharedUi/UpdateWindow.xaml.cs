using System.ComponentModel;
using System.IO;
using System.Windows;
namespace RimTogether;
public partial class UpdateWindow:Window
{
    private readonly string root; private readonly int ignorePid;
    private CancellationTokenSource cancellation=new();private bool running;
    public UpdateResult? Result{get;private set;}
    public UpdateWindow(string root,int ignorePid=0){InitializeComponent();this.root=root;this.ignorePid=ignorePid;}
    private async void Window_Loaded(object sender,RoutedEventArgs e)=>await Update();
    private async Task Update()
    {
        cancellation.Dispose();cancellation=new();running=true;CancelButton.Content="Отмена";
        Retry.Visibility=OpenCurrent.Visibility=Visibility.Collapsed;
        var progress=new Progress<UpdateProgress>(state=>{
            Heading.Text=state.Title;Detail.Text=state.Detail;Progress.IsIndeterminate=state.Total==0&&state.FilesTotal==0;
            Progress.Value=state.Total>0?Math.Min(100,100d*state.Downloaded/state.Total):state.FilesTotal>0?100d*state.FilesDone/state.FilesTotal:0;
            Numbers.Text=state.Total>0?$"{Size(state.Downloaded)} из {Size(state.Total)}":state.FilesTotal>0?$"Готово {state.FilesDone} из {state.FilesTotal} файлов":"";
            Reuse.Text=state.Reused>0?$"{Size(state.Reused)} уже есть на компьютере":"Скачиваем только необходимые компоненты";
        });
        try{using var service=new UpdateService(root);Result=await service.RunAsync(progress,cancellation.Token,ignorePid);running=false;Close();}
        catch(OperationCanceledException){running=false;Close();}
        catch(Exception error){running=false;Heading.Text="Обновление не завершено";Detail.Text=error.Message;Progress.IsIndeterminate=false;Progress.Value=0;Numbers.Text="Сохранения и прежняя версия не изменены";Reuse.Text="";Retry.Visibility=Visibility.Visible;CancelButton.Content="Закрыть";try{if(new BundleUpdate(root).Current()!=null)OpenCurrent.Visibility=Visibility.Visible;}catch(IOException){}}
    }
    private static string Size(long bytes)=>bytes>=1048576?$"{bytes/1048576d:0.0} МБ":$"{bytes/1024d:0} КБ";
    private async void Retry_Click(object sender,RoutedEventArgs e)=>await Update();
    private void Cancel_Click(object sender,RoutedEventArgs e){if(running)cancellation.Cancel();else Close();}
    private void OpenCurrent_Click(object sender,RoutedEventArgs e){var updater=new BundleUpdate(root);var current=updater.Current();if(current==null)return;Result=new(current,updater.DirectoryFor(current.Manifest),false,0,0);Close();}
    private void Window_Closing(object? sender,CancelEventArgs e){if(running){e.Cancel=true;cancellation.Cancel();CancelButton.Content="Отменяем…";}}
}
