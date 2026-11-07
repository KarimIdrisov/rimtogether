using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace RimTogether;

public static class GameFingerprint
{
    public static string Compute(string game, string configPath)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Add(string value) => hash.AppendData(Encoding.UTF8.GetBytes(value + "\n"));
        void AddTree(string folder, Func<string, bool> include, string label)
        {
            foreach (var file in Directory.GetFiles(folder, "*", SearchOption.AllDirectories)
                .Where(include).OrderBy(f => Path.GetRelativePath(folder, f), StringComparer.OrdinalIgnoreCase))
            {
                Add(label + "/" + Path.GetRelativePath(folder, file).Replace('\\', '/').ToLowerInvariant());
                Add(ModManager.Hash(file));
            }
        }
        var managed = Path.Combine(game, "RimWorldWin64_Data", "Managed");
        if (!Directory.Exists(managed)) throw new DirectoryNotFoundException("Не найдена папка Managed игры.");
        AddTree(managed, f => f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase), "managed");
        Add("native/RimWorldWin64.exe"); Add(ModManager.Hash(Path.Combine(game, "RimWorldWin64.exe")));
        foreach (var file in Directory.GetFiles(game, "*.dll").OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
        { Add("native/" + Path.GetFileName(file).ToLowerInvariant()); Add(ModManager.Hash(file)); }
        var mono = Path.Combine(game, "MonoBleedingEdge", "EmbedRuntime");
        if (Directory.Exists(mono)) AddTree(mono, f => f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase), "mono");
        var folders = new[] { Path.Combine(game, "Data"), Path.Combine(game, "Mods") }.Where(Directory.Exists).SelectMany(Directory.GetDirectories).ToArray();
        var ids = XDocument.Load(configPath).Root?.Element("activeMods")?.Elements("li").Select(e => e.Value.ToLowerInvariant()).ToArray()
            ?? throw new InvalidDataException("Нет списка активных модов.");
        foreach (var id in ids)
        {
            Add("active:" + id);
            var matches = folders.Where(f => ModManager.PackageId(f) == id).ToArray();
            if (matches.Length != 1) throw new InvalidDataException("Для " + id + " найдено папок: " + matches.Length + ". Уберите дубликаты или установите мод локально в Mods.");
            AddTree(matches[0], f =>
            {
                var relative = Path.GetRelativePath(matches[0], f).Replace('\\', '/');
                return new[] { ".dll", ".xml" }.Contains(Path.GetExtension(f).ToLowerInvariant()) &&
                    !relative.StartsWith("Languages/", StringComparison.OrdinalIgnoreCase) && !relative.Contains("/Languages/", StringComparison.OrdinalIgnoreCase);
            }, id);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
}
