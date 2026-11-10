using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

namespace RimTogether;

public sealed class UpdateService(string root):IDisposable
{
    private readonly HttpClient http = CreateClient();
    public void Dispose()=>http.Dispose();
    private static HttpClient CreateClient()
    {
        var client=new HttpClient { Timeout=TimeSpan.FromSeconds(60) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("RimTogether-Updater/2.0"); return client;
    }
    public async Task<UpdateResult> RunAsync(IProgress<UpdateProgress> progress,CancellationToken cancellation,int ignoreLauncherPid=0)
    {
        Directory.CreateDirectory(root);
        using var exclusive=new FileStream(Path.Combine(root,"update.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
        var updater=new BundleUpdate(root);
        progress.Report(new("Проверяем обновления","Сверяем установленный комплект"));
        var current=await Task.Run(updater.Current,cancellation);
        var busy=new[]{"RimTogether","RimTogether.Server","RimWorldWin64"}.SelectMany(Process.GetProcessesByName).Any(p=>p.Id!=ignoreLauncherPid);
        if(busy)
        {
            throw new IOException("Закрой игру, сервер и второй лаунчер, затем повтори обновление. Установленная версия сохранена.");
        }
        var releaseBytes=await GetBytes("https://api.github.com/repos/"+BundleUpdate.Repository+"/releases/latest",5_000_000,cancellation);
        using var release=JsonDocument.Parse(releaseBytes);
        var tag=release.RootElement.GetProperty("tag_name").GetString()??"";
        if(!System.Text.RegularExpressions.Regex.IsMatch(tag,@"^v\d+\.\d+\.\d+$") || release.RootElement.GetProperty("prerelease").GetBoolean()) throw new InvalidDataException("Некорректный выпуск.");
        if(current!=null && Version.Parse(current.Manifest.Tag[1..])>Version.Parse(tag[1..])) return new(current,updater.DirectoryFor(current.Manifest),false,0,0);
        var baseUrl="https://github.com/"+BundleUpdate.Repository+"/releases/download/"+tag+"/";
        var manifest=JsonSerializer.Deserialize<BundleManifest>(await GetBytes(baseUrl+BundleUpdate.ManifestName,100_000,cancellation),UpdateStorage.Json)??throw new InvalidDataException("Пустой манифест.");
        manifest.Validate(tag);
        if(current!=null && current.Manifest.Tag==tag && current.Manifest.Sha256.Equals(manifest.Sha256,StringComparison.OrdinalIgnoreCase))
        { progress.Report(new("Всё актуально",tag+" · файлы проверены"));return new(current,updater.DirectoryFor(manifest),false,0,0); }
        if(manifest.FilesIndexHash.Length!=64) return await FullArchive(updater,manifest,baseUrl,progress,cancellation);
        var indexBytes=await GetBytes(baseUrl+BundleUpdate.FilesIndexName,2_000_000,cancellation);
        if(!Convert.ToHexString(SHA256.HashData(indexBytes)).Equals(manifest.FilesIndexHash,StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("SHA256 индекса файлов не совпал.");
        var index=JsonSerializer.Deserialize<FileIndex>(indexBytes,UpdateStorage.Json)??throw new InvalidDataException("Пустой индекс.");index.Validate(manifest);
        return await Task.Run(()=>InstallFiles(updater,current,manifest,index,baseUrl,progress,cancellation),cancellation);
    }
    private async Task<UpdateResult> InstallFiles(BundleUpdate updater,InstalledBundle? previous,BundleManifest manifest,FileIndex index,string baseUrl,IProgress<UpdateProgress> progress,CancellationToken cancellation)
    {
        var old=previous==null?null:updater.DirectoryFor(previous.Manifest);
        var cache=Path.Combine(root,"files");Directory.CreateDirectory(cache);
        var reusable=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        foreach(var file in index.Files)
        {
            var cached=Path.Combine(cache,file.Sha256.ToLowerInvariant());
            if(File.Exists(cached) && Matches(cached,file)) reusable[file.Path]=cached;
            else if(old!=null && previous!.Files.TryGetValue(file.Path,out var hash) && hash.Equals(file.Sha256,StringComparison.OrdinalIgnoreCase)) reusable[file.Path]=BundleUpdate.SafeFile(old,file.Path);
        }
        var known=index.Files.Where(f=>reusable.ContainsKey(f.Path)).GroupBy(f=>f.Sha256,StringComparer.OrdinalIgnoreCase).ToDictionary(g=>g.Key,g=>reusable[g.First().Path],StringComparer.OrdinalIgnoreCase);
        foreach(var file in index.Files)if(!reusable.ContainsKey(file.Path)&&known.TryGetValue(file.Sha256,out var source))reusable[file.Path]=source;
        long total=index.Files.Where(f=>!reusable.ContainsKey(f.Path)).GroupBy(f=>f.Sha256,StringComparer.OrdinalIgnoreCase).Sum(g=>g.First().DownloadSize);
        long downloaded=0,reused=0;int done=0;
        var stage=Path.Combine(root,"stage-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(stage);
        try
        {
            foreach(var file in index.Files)
            {
                cancellation.ThrowIfCancellationRequested();
                var target=BundleUpdate.SafeFile(stage,file.Path);Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if(reusable.TryGetValue(file.Path,out var source)) { File.Copy(source,target);reused+=file.Size; }
                else
                {
                    var cached=Path.Combine(cache,file.Sha256.ToLowerInvariant());
                    if(!File.Exists(cached) || !Matches(cached,file))
                    {
                        var temporary=cached+"."+Guid.NewGuid().ToString("N")+".tmp";
                        try
                        {
                            using var response=await GetResponse(baseUrl+file.Asset,cancellation);
                            await using var input=await response.Content.ReadAsStreamAsync(cancellation);
                            using var counted=new CountingStream(input,bytes=>progress.Report(new("Обновляем "+manifest.Tag,file.Path,downloaded+bytes,total,done,index.Files.Count,reused)),file.DownloadSize);
                            await using(var gzip=new GZipStream(counted,CompressionMode.Decompress,leaveOpen:true))
                            await using(var output=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None))
                            {
                                var buffer=new byte[81920];long size=0;
                                while(true){var read=await gzip.ReadAsync(buffer,cancellation);if(read==0)break;size+=read;if(size>file.Size)throw new InvalidDataException("Файл превышает размер из манифеста.");await output.WriteAsync(buffer.AsMemory(0,read),cancellation);}
                            }
                            if(counted.Count!=file.DownloadSize || !Matches(temporary,file))throw new InvalidDataException("Размер или SHA256 компонента не совпал: "+file.Path);
                            downloaded+=file.DownloadSize;File.Move(temporary,cached,true);
                        }
                        finally{if(File.Exists(temporary))File.Delete(temporary);}
                    }
                    File.Copy(cached,target);
                }
                if(!Matches(target,file))throw new InvalidDataException("Проверка установленного файла не пройдена: "+file.Path);
                done++;progress.Report(new("Обновляем "+manifest.Tag,file.Path,downloaded,total,done,index.Files.Count,reused));
            }
            var installed=new InstalledBundle{Manifest=manifest,Files=index.Files.ToDictionary(f=>f.Path,f=>f.Sha256)};
            cancellation.ThrowIfCancellationRequested();
            var destination=updater.DirectoryFor(manifest);Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            if(Directory.Exists(destination))Directory.Move(destination,destination+".damaged-"+Guid.NewGuid().ToString("N"));
            Directory.Move(stage,destination);
            if(!updater.IsHealthy(installed))throw new InvalidDataException("Новый комплект не прошёл проверку целостности.");
            cancellation.ThrowIfCancellationRequested();
            if(previous!=null)UpdateStorage.Write(updater.Pointer+".bak",previous,keepRawBackup:false);
            UpdateStorage.Write(updater.Pointer,installed,keepRawBackup:false);
            return new(installed,destination,true,downloaded,reused);
        }
        finally{if(Directory.Exists(stage))Directory.Delete(stage,true);}
    }
    private static bool Matches(string path,ReleaseFile file)=>new FileInfo(path).Length==file.Size && UpdateFiles.Hash(path).Equals(file.Sha256,StringComparison.OrdinalIgnoreCase);
    private async Task<UpdateResult> FullArchive(BundleUpdate updater,BundleManifest manifest,string baseUrl,IProgress<UpdateProgress> progress,CancellationToken cancellation)
    {
        var temporary=Path.Combine(root,"download-"+Guid.NewGuid().ToString("N")+".zip");
        try
        {
            using var response=await GetResponse(baseUrl+manifest.Archive,cancellation);
            await using var input=await response.Content.ReadAsStreamAsync(cancellation);
            await using(var output=File.Create(temporary))
            {
                var buffer=new byte[81920];long total=0;
                while(true){var size=await ReadWithTimeout(input,buffer,cancellation);if(size==0)break;total+=size;if(total>manifest.Size)throw new InvalidDataException("Архив больше манифеста.");await output.WriteAsync(buffer.AsMemory(0,size),cancellation);progress.Report(new("Устанавливаем комплект",manifest.Tag,total,manifest.Size));}
            }
            var installed=await Task.Run(()=>updater.Install(temporary,manifest),cancellation);
            return new(installed,updater.DirectoryFor(manifest),true,manifest.Size,0);
        }
        finally{if(File.Exists(temporary))File.Delete(temporary);}
    }
    private async Task<HttpResponseMessage> GetResponse(string url,CancellationToken cancellation)
    {
        for(var attempt=0;;attempt++)
        {
            try
            {
                var response=await http.GetAsync(url,HttpCompletionOption.ResponseHeadersRead,cancellation);
                if((int)response.StatusCode is 408 or 429 or >=500 && attempt<2){response.Dispose();await Task.Delay(1000*(attempt+1),cancellation);continue;}
                if(!response.IsSuccessStatusCode){var code=response.StatusCode;response.Dispose();throw new HttpRequestException("GitHub: "+code);}return response;
            }
            catch(HttpRequestException) when(attempt<2){await Task.Delay(1000*(attempt+1),cancellation);}
        }
    }
    private async Task<byte[]> GetBytes(string url,int limit,CancellationToken cancellation)
    {
        using var response=await GetResponse(url,cancellation);await using var input=await response.Content.ReadAsStreamAsync(cancellation);using var output=new MemoryStream();var buffer=new byte[16384];
        while(true){var read=await ReadWithTimeout(input,buffer,cancellation);if(read==0)break;if(output.Length+read>limit)throw new InvalidDataException("Манифест превышает допустимый размер.");output.Write(buffer,0,read);}return output.ToArray();
    }
    private static async ValueTask<int> ReadWithTimeout(Stream input,Memory<byte> buffer,CancellationToken cancellation)
    {
        using var idle=CancellationTokenSource.CreateLinkedTokenSource(cancellation);idle.CancelAfter(TimeSpan.FromSeconds(30));
        try{return await input.ReadAsync(buffer,idle.Token);}catch(OperationCanceledException)when(!cancellation.IsCancellationRequested){throw new IOException("GitHub не передаёт данные 30 секунд. Повтори обновление.");}
    }
    private sealed class CountingStream(Stream inner,Action<long> progress,long maximum):Stream
    {
        public long Count{get;private set;}
        public override bool CanRead=>true;public override bool CanSeek=>false;public override bool CanWrite=>false;
        public override long Length=>throw new NotSupportedException();public override long Position{get=>Count;set=>throw new NotSupportedException();}
        public override int Read(byte[] buffer,int offset,int count){var read=inner.Read(buffer,offset,count);Add(read);return read;}
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken cancellationToken=default){var read=await ReadWithTimeout(inner,buffer,cancellationToken);Add(read);return read;}
        private void Add(int size){Count+=size;if(Count>maximum)throw new InvalidDataException("Компонент больше манифеста.");progress(Count);}
        public override void Flush()=>throw new NotSupportedException();public override long Seek(long offset,SeekOrigin origin)=>throw new NotSupportedException();public override void SetLength(long value)=>throw new NotSupportedException();public override void Write(byte[] buffer,int offset,int count)=>throw new NotSupportedException();
    }
}
