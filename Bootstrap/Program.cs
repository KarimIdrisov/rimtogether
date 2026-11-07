using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using RimTogether;

Console.OutputEncoding = System.Text.Encoding.UTF8;
Console.WriteLine("RimTogether • синхронизация через GitHub");
if (args.FirstOrDefault(a => a.StartsWith("--wait-pid=")) is { } wait && int.TryParse(wait[11..], out var parentId))
{
    try { await Process.GetProcessById(parentId).WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)); }
    catch (ArgumentException) { }
}
var root = Path.Combine(Contract.DataRoot, "app");
Directory.CreateDirectory(root);
FileStream exclusive;
try { exclusive = new FileStream(Path.Combine(root, "update.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
catch (IOException) { Console.WriteLine("Обновление уже запущено. Дождись завершения."); return 1; }
using (exclusive)
{
    var updater = new BundleUpdate(root);
    var current = updater.Current();
    var busy = new[] { "RimTogether", "RimTogether.Server", "RimWorldWin64" }.Any(name => Process.GetProcessesByName(name).Length > 0);
    if (!busy)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("RimTogether-Updater/1.1");
        try
        {
            Console.WriteLine("Проверяем общий комплект…");
            using var response = await http.GetAsync("https://api.github.com/repos/" + BundleUpdate.Repository + "/releases/latest");
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > 1_000_000) throw new InvalidDataException("Слишком большой ответ GitHub.");
            using var release = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var tag = release.RootElement.GetProperty("tag_name").GetString() ?? "";
            if (tag.Length > 40 || !System.Text.RegularExpressions.Regex.IsMatch(tag, @"^v\d+\.\d+\.\d+$") || !Version.TryParse(tag[1..], out _) || release.RootElement.GetProperty("prerelease").GetBoolean())
                throw new InvalidDataException("Некорректный стабильный релиз.");
            if (current != null && Version.Parse(current.Manifest.Tag[1..]) > Version.Parse(tag[1..]))
                Console.WriteLine("Локальная версия новее канала. Автоматический откат не выполняется.");
            else
            {
                var baseUrl = "https://github.com/" + BundleUpdate.Repository + "/releases/download/" + tag + "/";
                using var manifestResponse = await http.GetAsync(baseUrl + BundleUpdate.ManifestName);
                manifestResponse.EnsureSuccessStatusCode();
                var manifestBytes = await manifestResponse.Content.ReadAsByteArrayAsync();
                if (manifestBytes.Length > 100_000) throw new InvalidDataException("Слишком большой манифест.");
                var manifest = JsonSerializer.Deserialize<BundleManifest>(manifestBytes, Storage.Json) ?? throw new InvalidDataException("Пустой манифест.");
                manifest.Validate(tag);
                if (current == null || current.Manifest.Tag != tag || !current.Manifest.Sha256.Equals(manifest.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    var download = Path.Combine(root, "download-" + Guid.NewGuid().ToString("N") + ".zip");
                    try
                    {
                        Console.WriteLine("Скачиваем " + tag + ". Лаунчер + сервер + Multiplayer…");
                        using var archiveResponse = await http.GetAsync(baseUrl + BundleUpdate.ArchiveName, HttpCompletionOption.ResponseHeadersRead);
                        archiveResponse.EnsureSuccessStatusCode();
                        await using (var input = await archiveResponse.Content.ReadAsStreamAsync())
                        await using (var output = File.Create(download))
                        {
                            var buffer = new byte[81920]; long total = 0; int size;
                            while ((size = await input.ReadAsync(buffer)) > 0)
                            {
                                total += size;
                                if (total > manifest.Size) throw new InvalidDataException("Архив превышает заявленный размер.");
                                await output.WriteAsync(buffer.AsMemory(0, size));
                            }
                        }
                        current = updater.Install(download, manifest);
                        Console.WriteLine("SHA256 проверен. Установлена общая версия " + tag + ".");
                    }
                    finally { if (File.Exists(download)) File.Delete(download); }
                }
                else Console.WriteLine("Уже установлена версия " + tag + ". Все файлы проверены.");
            }
        }
        catch (Exception e) when (e is HttpRequestException or IOException or JsonException or TaskCanceledException or ArgumentException)
        { Console.WriteLine("Обновление недоступно: " + e.Message + "\nПробуем ранее проверенную версию."); }
    }
    else Console.WriteLine("Игра, лаунчер или сервер работают. Обновление отложено до их закрытия.");
    var exe = current != null ? Path.Combine(updater.DirectoryFor(current.Manifest), "RimTogether.exe") : Path.Combine(AppContext.BaseDirectory, "RimTogether.exe");
    if (!File.Exists(exe))
    {
        Console.WriteLine("Нет установленного комплекта. Нужен доступ к GitHub для первой загрузки.\n" + "https://github.com/" + BundleUpdate.Repository + "/releases/latest");
        if (!Console.IsInputRedirected) { Console.WriteLine("Enter — закрыть"); Console.ReadLine(); }
        return 1;
    }
    var start = new ProcessStartInfo(exe) { WorkingDirectory = Path.GetDirectoryName(exe)!, UseShellExecute = false };
    start.ArgumentList.Add("--managed");
    start.Environment["RIMTOGETHER_BOOTSTRAP"] = Path.Combine(AppContext.BaseDirectory, "RimTogether.Start.exe");
    Process.Start(start); return 0;
}
