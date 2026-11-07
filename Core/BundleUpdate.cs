using System.Text.RegularExpressions;

namespace RimTogether;

public sealed class BundleManifest
{
    public int Schema { get; set; } = 1;
    public string Tag { get; set; } = "";
    public string Commit { get; set; } = "";
    public string Archive { get; set; } = BundleUpdate.ArchiveName;
    public string Sha256 { get; set; } = "";
    public long Size { get; set; }
    public string MultiplayerTag { get; set; } = Contract.Tag;
    public int CheckpointSchema { get; set; } = 1;
    public void Validate(string releaseTag)
    {
        if (Schema != 1 || Tag != releaseTag || Tag == null || Tag.Length > 40 || !Regex.IsMatch(Tag, @"^v\d+\.\d+\.\d+$") || !Version.TryParse(Tag[1..], out _) ||
            Archive != BundleUpdate.ArchiveName || !Regex.IsMatch(Sha256, "^[A-Fa-f0-9]{64}$") ||
            !Regex.IsMatch(Commit, "^[A-Fa-f0-9]{40}$") || !Regex.IsMatch(MultiplayerTag, @"^v\d+\.\d+\.\d+$") || Size < 1 || Size > 256_000_000 || CheckpointSchema != 1)
            throw new InvalidDataException("Несовместимый манифест обновления. Текущая версия сохранена.");
    }
}

public sealed class InstalledBundle
{
    public BundleManifest Manifest { get; set; } = new();
    public Dictionary<string, string> Files { get; set; } = [];
}

public sealed class BundleUpdate(string appRoot)
{
    public const string Repository = "KarimIdrisov/rimtogether";
    public const string ArchiveName = "RimTogether-win-x64.zip";
    public const string ManifestName = "RimTogether-manifest.json";
    public string Pointer => Path.Combine(appRoot, "current.json");

    public string DirectoryFor(BundleManifest manifest)
    {
        manifest.Validate(manifest.Tag);
        return Path.Combine(appRoot, "versions", manifest.Tag + "-" + manifest.Sha256[..12].ToLowerInvariant());
    }
    public InstalledBundle? Current()
    {
        foreach (var candidate in new[] { Pointer, Pointer + ".bak" })
        {
            if (!File.Exists(candidate)) continue;
            try
            {
                var installed = Storage.Read<InstalledBundle>(candidate);
                if (IsHealthy(installed)) return installed;
            }
            catch (Exception e) when (e is IOException or System.Text.Json.JsonException or ArgumentException) { }
        }
        return null;
    }
    public bool IsHealthy(InstalledBundle installed)
    {
        if (installed.Manifest == null || installed.Files == null) return false;
        var directory = DirectoryFor(installed.Manifest);
        if (installed.Files.Count < 3 || !installed.Files.ContainsKey("RimTogether.exe") ||
            !installed.Files.ContainsKey("server/RimTogether.Server.exe") ||
            !installed.Files.ContainsKey("packages/Multiplayer-" + installed.Manifest.MultiplayerTag + ".zip")) return false;
        if (!Directory.Exists(directory) || Directory.GetFiles(directory, "*", SearchOption.AllDirectories).Length != installed.Files.Count) return false;
        return installed.Files.All(kv =>
        {
            var file = SafeFile(directory, kv.Key);
            return File.Exists(file) && ModManager.Hash(file).Equals(kv.Value, StringComparison.OrdinalIgnoreCase);
        });
    }
    public static string SafeFile(string root, string relative)
    {
        var fullRoot = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        var file = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!file.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Путь за пределами папки обновления.");
        return file;
    }
    public InstalledBundle Install(string archive, BundleManifest manifest)
    {
        manifest.Validate(manifest.Tag);
        if (new FileInfo(archive).Length != manifest.Size || !ModManager.Hash(archive).Equals(manifest.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Размер или SHA256 обновления не совпал. Установка отменена.");
        var destination = DirectoryFor(manifest);
        Directory.CreateDirectory(appRoot);
        var stage = Path.Combine(appRoot, "stage-" + Guid.NewGuid().ToString("N"));
        try
        {
            ModManager.ExtractSafe(archive, stage);
            var installed = new InstalledBundle { Manifest = manifest, Files = Directory.GetFiles(stage, "*", SearchOption.AllDirectories)
                .ToDictionary(f => Path.GetRelativePath(stage, f).Replace('\\', '/'), ModManager.Hash) };
            if (!installed.Files.ContainsKey("RimTogether.exe") || !installed.Files.ContainsKey("server/RimTogether.Server.exe") ||
                !installed.Files.ContainsKey("packages/Multiplayer-" + manifest.MultiplayerTag + ".zip"))
                throw new InvalidDataException("В обновлении нет полного комплекта лаунчера, сервера и мода.");
            // Each release gets its own immutable directory. The old running version is never overwritten.
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            if (Directory.Exists(destination))
            {
                var damaged = destination + ".damaged-" + Guid.NewGuid().ToString("N");
                Directory.Move(destination, damaged);
                try { Directory.Move(stage, destination); }
                catch { Directory.Move(damaged, destination); throw; }
            }
            else Directory.Move(stage, destination);
            Storage.Write(Pointer, installed);
            return installed;
        }
        finally { if (Directory.Exists(stage)) Directory.Delete(stage, true); }
    }
}
