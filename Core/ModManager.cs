using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;

namespace RimTogether;

public sealed class ModManager(HttpClient http, string dataRoot, Action<string> log, string? guardFolder = null)
{
    public static void RequireIdle()
    {
        if (Process.GetProcessesByName("RimWorldWin64").Length != 0)
            throw new InvalidOperationException("Сначала закрой RimWorld. Моды работающей игры не обновляются.");
    }

    public async Task<string> GetArchive(CancellationToken ct = default)
    {
        var cache = Path.Combine(dataRoot, "cache");
        Directory.CreateDirectory(cache);
        var path = Path.Combine(cache, "Multiplayer-" + Contract.Tag + ".zip");
        var bundled = Path.Combine(AppContext.BaseDirectory, "packages", "Multiplayer-" + Contract.Tag + ".zip");
        const string knownHash = "FB34AC6EFA79A64746AF8F53FAE9D8959796A26262951F653CDC38C2DA2196F5";
        if (File.Exists(path) && Hash(path) == knownHash) return path;
        if (File.Exists(bundled) && Hash(bundled) == knownHash)
        {
            File.Copy(bundled, path, true);
            log("Multiplayer " + Contract.Version + ": проверен комплектный архив SHA256.");
            return path;
        }
        log("Скачивание официального Multiplayer " + Contract.Version + "…");
        using var response = await http.GetAsync("https://github.com/rwmt/Multiplayer/releases/download/" + Contract.Tag + "/Multiplayer-" + Contract.Tag + ".zip", HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        var temp = path + ".download";
        try
        {
            await using (var output = File.Create(temp)) await response.Content.CopyToAsync(output, ct);
            if (Hash(temp) != knownHash) throw new InvalidDataException("SHA256 архива Multiplayer не совпал. Установка отменена.");
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
        return path;
    }

    public async Task CheckNewReleases(CancellationToken ct = default)
    {
        try
        {
            // /latest points at the upstream continuous build; select only stable tags.
            using var doc = JsonDocument.Parse(await http.GetStringAsync("https://api.github.com/repos/rwmt/Multiplayer/releases?per_page=30", ct));
            var newer = doc.RootElement.EnumerateArray().Where(r => !r.GetProperty("prerelease").GetBoolean() && !r.GetProperty("draft").GetBoolean())
                .Select(r => r.GetProperty("tag_name").GetString() ?? "")
                .Where(t => t.StartsWith('v') && System.Version.TryParse(t[1..], out _))
                .OrderByDescending(t => System.Version.Parse(t[1..])).FirstOrDefault();
            if (newer != null && System.Version.Parse(newer[1..]) > System.Version.Parse(Contract.Version))
                log("Есть " + newer + ". Для перехода обоим нужен комплект с совместимым сервером. Текущая пара закреплена на " + Contract.Tag + ".");
            else log("Канал проверен: совместимый комплект " + Contract.Tag + ".");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        { log("Проверка GitHub недоступна; используем проверенный комплект. " + e.Message); }
    }

    public static string Hash(string file)
    {
        using var stream = File.OpenRead(file);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    public static void ExtractSafe(string archive, string target)
    {
        var root = Path.GetFullPath(target) + Path.DirectorySeparatorChar;
        using var zip = ZipFile.OpenRead(archive);
        long total = 0;
        foreach (var entry in zip.Entries)
        {
            total += entry.Length;
            if (total > 300_000_000) throw new InvalidDataException("Слишком большой архив.");
            var destination = Path.GetFullPath(Path.Combine(root, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
            if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Небезопасный путь в архиве.");
            if (entry.FullName.EndsWith('/')) { Directory.CreateDirectory(destination); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination, true);
        }
    }

    public static string? PackageId(string folder)
    {
        var about = Path.Combine(folder, "About", "About.xml");
        return File.Exists(about) ? XDocument.Load(about).Root?.Element("packageId")?.Value.ToLowerInvariant() : null;
    }

    public static void ValidateGame(string game)
    {
        if (!File.Exists(Path.Combine(game, "RimWorldWin64.exe"))) throw new InvalidOperationException("Выбери папку с RimWorldWin64.exe.");
        var versionFile = Path.Combine(game, "Version.txt");
        if (!File.Exists(versionFile) || !File.ReadAllText(versionFile).Trim().StartsWith("1.6.4633"))
            throw new InvalidOperationException("Этот комплект проверен для RimWorld 1.6.4633. Версия игры отличается.");
    }

    public void Install(string game, string archive, string configPath)
    {
        RequireIdle(); ValidateGame(game);
        var payloadRoot = guardFolder ?? Path.Combine(AppContext.BaseDirectory, "packages", "guard");
        var guard = GuardPayload.Load(payloadRoot);
        var mods = Path.Combine(game, "Mods");
        Directory.CreateDirectory(mods);
        var existing = Directory.GetDirectories(mods).Where(p => PackageId(p) == "rwmt.multiplayer").ToArray();
        if (!Directory.GetDirectories(mods).Any(p => PackageId(p) == "zetrith.prepatcher"))
            throw new InvalidOperationException("Не найден Prepatcher в Mods. Установи его перед Multiplayer.");
        var destination = Path.Combine(mods, "RimTogether-Multiplayer");
        if (Directory.Exists(destination) && !existing.Contains(destination, StringComparer.OrdinalIgnoreCase))
            throw new IOException("Папка RimTogether-Multiplayer занята другим модом.");
        var state = Path.Combine(dataRoot, "installed-" + Contract.GuardRevision + "-" + Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(game).ToUpperInvariant())))[..12] + ".json");
        var backup = Path.Combine(dataRoot, "backups", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-ffff"));
        // Content hashes detect changes and incomplete installations, not just version text.
        var healthy = false;
        if (existing.Length == 1 && existing[0].Equals(destination, StringComparison.OrdinalIgnoreCase) && File.Exists(state))
        {
            try
            {
                var hashes = Storage.Read<Dictionary<string, string>>(state);
                healthy = hashes.Count > 0 && hashes.All(kv => File.Exists(Path.Combine(destination, kv.Key)) && Hash(Path.Combine(destination, kv.Key)) == kv.Value)
                    && Directory.GetFiles(destination, "*", SearchOption.AllDirectories).Length == hashes.Count;
            }
            catch (JsonException) { log("Метаданные установки повреждены; восстанавливаем из проверенного комплекта."); }
        }
        healthy = healthy && guard.Installed(destination);
        if (healthy)
        {
            PrepareConfig(game, configPath, backup);
            log("Multiplayer " + Contract.Version + ": все файлы проверены, профиль Radmin готов."); return;
        }
        var staging = Path.Combine(mods, ".rimtogether-stage-" + Guid.NewGuid().ToString("N"));
        var moved = new List<(string Original, string Backup)>();
        bool promoted = false;
        bool retainRollback = false;
        try
        {
            ExtractSafe(archive, staging);
            var incoming = Path.Combine(staging, "Multiplayer");
            if (PackageId(incoming) != "rwmt.multiplayer" || !File.Exists(Path.Combine(incoming, "1.6", "AssembliesCustom", "Multiplayer.dll")))
                throw new InvalidDataException("Структура архива Multiplayer не распознана.");
            guard.Apply(payloadRoot, incoming);
            Directory.CreateDirectory(backup);
            foreach (var old in existing)
            {
                var saved = Path.Combine(backup, Path.GetFileName(old));
                CopyVerifiedDirectory(old, saved);
                // Rename only within Mods, on the game's volume. The durable copy may live on another drive.
                var rollback = Path.Combine(staging, "rollback", Path.GetFileName(old));
                Directory.CreateDirectory(Path.GetDirectoryName(rollback)!);
                Directory.Move(old, rollback); moved.Add((old, rollback));
            }
            Directory.Move(incoming, destination); promoted = true;
            PrepareConfig(game, configPath, backup);
            var hashes = Directory.GetFiles(destination, "*", SearchOption.AllDirectories).ToDictionary(f => Path.GetRelativePath(destination, f), Hash);
            Storage.Write(state, hashes);
            log("Установлен Multiplayer " + Contract.Version + ". Резервная копия: " + backup);
        }
        catch (Exception installError)
        {
            try
            {
                if (promoted) Directory.Delete(destination, true);
                foreach (var pair in moved) Directory.Move(pair.Backup, pair.Original);
                if (File.Exists(Path.Combine(backup, "ModsConfig.xml"))) File.Copy(Path.Combine(backup, "ModsConfig.xml"), configPath, true);
            }
            catch (Exception restoreError)
            {
                retainRollback = true;
                throw new AggregateException("Откат требует восстановления; оригиналы оставлены в " + staging + ", резервная копия: " + backup, installError, restoreError);
            }
            throw;
        }
        finally { if (!retainRollback && Directory.Exists(staging)) Directory.Delete(staging, true); }
    }

    public static void CopyVerifiedDirectory(string source, string destination)
    {
        var root = Path.GetFullPath(source).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if ((Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar).StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Резервная копия не может находиться внутри исходного мода.");
        if (Directory.Exists(destination)) throw new IOException("Папка резервной копии уже существует: " + destination);
        var temp = destination + ".copy-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(temp);
        try
        {
            var pending = new Stack<(string Source, string Target)>(); pending.Push((source, temp));
            while (pending.Count != 0)
            {
                var pair = pending.Pop();
                foreach (var directory in Directory.GetDirectories(pair.Source))
                {
                    if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                        throw new IOException("Внутри мода найдена ссылка на папку; исходный мод сохранён: " + directory);
                    var target = Path.Combine(pair.Target, Path.GetFileName(directory));
                    Directory.CreateDirectory(target); pending.Push((directory, target));
                }
                foreach (var file in Directory.GetFiles(pair.Source))
                {
                    var target = Path.Combine(pair.Target, Path.GetFileName(file));
                    var expected = Hash(file); File.Copy(file, target, false);
                    if (!Hash(target).Equals(expected, StringComparison.Ordinal))
                        throw new IOException("Не прошла проверка резервной копии: " + file);
                }
            }
            Directory.Move(temp, destination); // Both paths share the backup directory and volume.
        }
        finally { if (Directory.Exists(temp)) Directory.Delete(temp, true); }
    }

    private static void PrepareConfig(string game, string path, string backup)
    {
        if (!File.Exists(path)) throw new IOException("Запусти RimWorld один раз и закрой: нужен Config/ModsConfig.xml.");
        var doc = XDocument.Load(path);
        var active = doc.Root?.Element("activeMods") ?? throw new InvalidDataException("Неверный ModsConfig.xml.");
        var before = doc.ToString();
        foreach (var li in active.Elements("li").Where(e => e.Value.Equals("hiteke.steam", StringComparison.OrdinalIgnoreCase) || e.Value.Equals("rwmt.multiplayer", StringComparison.OrdinalIgnoreCase) || e.Value.Equals("zetrith.prepatcher", StringComparison.OrdinalIgnoreCase)).ToArray()) li.Remove();
        active.AddFirst(new XElement("li", "zetrith.prepatcher"));
        var core = active.Elements("li").LastOrDefault(e => e.Value.StartsWith("ludeon.rimworld", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException("Core отсутствует в активных модах.");
        core.AddAfterSelf(new XElement("li", "rwmt.multiplayer"));
        if (before == doc.ToString()) return;
        Directory.CreateDirectory(backup);
        File.Copy(path, Path.Combine(backup, "ModsConfig.xml"), false);
        var temp = path + ".rimtogether.tmp";
        doc.Save(temp); File.Move(temp, path, true);
    }
}
