namespace RimTogether;
public static class BootstrapEntry
{
    public static async Task RefreshExternalAsync(string source,string? target,string appRoot)
    {
        if(string.IsNullOrWhiteSpace(target)||!File.Exists(target)||!Path.GetFileName(target).Equals("RimTogether.Start.exe",StringComparison.OrdinalIgnoreCase))return;
        source=Path.GetFullPath(source);target=Path.GetFullPath(target);
        var versions=Path.GetFullPath(Path.Combine(appRoot,"versions"))+Path.DirectorySeparatorChar;
        if(source.Equals(target,StringComparison.OrdinalIgnoreCase)||target.StartsWith(versions,StringComparison.OrdinalIgnoreCase))return;
        var temporary=target+".new-"+Guid.NewGuid().ToString("N");
        try{File.Copy(source,temporary);for(var attempt=0;attempt<20;attempt++){try{File.Move(temporary,target,true);return;}catch(IOException)when(attempt<19){await Task.Delay(500);}}}
        catch(Exception error)when(error is IOException or UnauthorizedAccessException){}
        finally{if(File.Exists(temporary))try{File.Delete(temporary);}catch(IOException){}}
    }
}
