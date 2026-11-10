using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace RimTogether;

internal static class UpdateStorage
{
    public static readonly JsonSerializerOptions Json = new() { WriteIndented = true, IncludeFields = true };
    public static T Read<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json) ?? throw new InvalidDataException("Пустой манифест.");
    public static void Write<T>(string path, T value, bool keepRawBackup = true)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(stream, value, Json); stream.Flush(true); }
            if (keepRawBackup && File.Exists(path)) File.Copy(path,path+".bak",true);
            File.Move(temporary,path,true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

internal static class UpdateFiles
{
    public static string Hash(string path) { using var stream=File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
    public static void ExtractSafe(string archive,string directory)
    {
        using var zip=ZipFile.OpenRead(archive); long total=0;
        var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var entry in zip.Entries)
        {
            var target=BundleUpdate.SafeFile(directory,entry.FullName);
            if (!seen.Add(target)) throw new InvalidDataException("Повторяющийся путь в архиве.");
            total+=entry.Length;
            if (entry.Length>200_000_000 || total>1_000_000_000 || seen.Count>5000) throw new InvalidDataException("Архив обновления слишком большой.");
            if(entry.FullName.EndsWith('/')) { Directory.CreateDirectory(target);continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!); entry.ExtractToFile(target);
        }
    }
}
