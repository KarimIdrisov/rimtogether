using System.Text.RegularExpressions;

namespace RimTogether;

public sealed class ReleaseFile
{
    public string Path { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public long Size { get; set; }
    public long DownloadSize { get; set; }
    public string Asset => "part-"+Sha256.ToLowerInvariant()+".gz";
}

public sealed class FileIndex
{
    public int Schema { get; set; } = 1;
    public string Tag { get; set; } = "";
    public string Commit { get; set; } = "";
    public string ArchiveSha256 { get; set; } = "";
    public List<ReleaseFile> Files { get; set; } = [];
    public void Validate(BundleManifest manifest)
    {
        if(Schema!=1 || Tag!=manifest.Tag || Commit!=manifest.Commit || !ArchiveSha256.Equals(manifest.Sha256,StringComparison.OrdinalIgnoreCase) || Files.Count is < 3 or > 5000)
            throw new InvalidDataException("Индекс файлов не соответствует выпуску.");
        var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase); long size=0;
        foreach(var file in Files)
        {
            if(string.IsNullOrWhiteSpace(file.Path) || file.Path.Contains('\\') || file.Path.Contains(':') || System.IO.Path.IsPathRooted(file.Path) || file.Path.Split('/').Any(p=>p is "" or "." or "..") || !names.Add(file.Path) ||
                !Regex.IsMatch(file.Sha256,"^[A-Fa-f0-9]{64}$") || file.Size is < 0 or > 200_000_000 || file.DownloadSize is < 1 or > 200_000_000)
                throw new InvalidDataException("Некорректный файл в индексе обновления.");
            size+=file.Size;
        }
        if(size>1_000_000_000 || !names.Contains("RimTogether.exe") || !names.Contains("RimTogether.Start.exe") || !(names.Contains("RimTogether.Server.exe") || names.Contains("server/RimTogether.Server.exe")) || !names.Contains("packages/guard/manifest.json"))
            throw new InvalidDataException("В выпуске отсутствуют обязательные компоненты.");
    }
}

public record UpdateProgress(string Title,string Detail,long Downloaded=0,long Total=0,int FilesDone=0,int FilesTotal=0,long Reused=0);
public record UpdateResult(InstalledBundle Bundle,string Directory,bool Changed,long Downloaded,long Reused);
