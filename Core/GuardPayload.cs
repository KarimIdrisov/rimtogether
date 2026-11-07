namespace RimTogether;

public sealed class GuardPayload
{
    public string Revision { get; set; } = Contract.GuardRevision;
    public int Protocol { get; set; } = Contract.Protocol;
    public Dictionary<string, string> Files { get; set; } = [];
    public static readonly string[] Paths = ["1.6/AssembliesCustom/Multiplayer.dll", "1.6/AssembliesCustom/MultiplayerCommon.dll", "1.6/Assemblies/MultiplayerLoader.dll"];
    public const string Marker = "About/RimTogetherGuard.json";

    public static GuardPayload Load(string folder)
    {
        var manifest = Storage.Read<GuardPayload>(Path.Combine(folder, "manifest.json"));
        if (manifest.Revision != Contract.GuardRevision || manifest.Protocol != Contract.Protocol ||
            manifest.Files.Count != Paths.Length || !Paths.All(manifest.Files.ContainsKey) || !manifest.Matches(folder))
            throw new InvalidDataException("Пакет защиты от десинков повреждён или несовместим. Перезапусти RimTogether.Start.exe.");
        return manifest;
    }
    public bool Matches(string folder) => Files.All(kv =>
        File.Exists(BundleUpdate.SafeFile(folder, kv.Key)) && ModManager.Hash(BundleUpdate.SafeFile(folder, kv.Key)).Equals(kv.Value, StringComparison.OrdinalIgnoreCase));
    public bool Installed(string folder)
    {
        var marker = Path.Combine(folder, Marker);
        if (!File.Exists(marker)) return false;
        try
        {
            var local = Storage.Read<GuardPayload>(marker);
            return local.Revision == Revision && local.Protocol == Protocol && Matches(folder);
        }
        catch (System.Text.Json.JsonException) { return false; }
    }
    public void Apply(string source, string destination)
    {
        foreach (var path in Paths)
        {
            var target = BundleUpdate.SafeFile(destination, path);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(BundleUpdate.SafeFile(source, path), target, true);
        }
        Storage.Write(Path.Combine(destination, Marker), this);
    }
}
