using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using Microsoft.Win32;

namespace RimTogether;

public partial class MainWindow : Window
{
    private readonly string root = Contract.DataRoot;
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(90) };
    private readonly SemaphoreSlim gate = new(1, 1);
    private Settings settings = new();
    private Process? server;
    private StreamWriter? serverLog;
    private bool closing;
    private bool serverReady;
    private string SettingsPath => Path.Combine(root, "settings.json");

    public MainWindow()
    {
        InitializeComponent();
        Directory.CreateDirectory(root);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("RimTogether/1.0");
        if (File.Exists(SettingsPath))
        {
            try { settings = Storage.Read<Settings>(SettingsPath); }
            catch (Exception e) { Log("Настройки повреждены: " + e.Message + ". Оригинал сохранён; проверь поля."); }
        }
        else
        {
            var address = NetworkInterface.GetAllNetworkInterfaces().Where(n => n.Name.Contains("Radmin", StringComparison.OrdinalIgnoreCase))
                .SelectMany(n => n.GetIPProperties().UnicastAddresses).FirstOrDefault(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)?.Address;
            if (address != null) settings.HostAddress = address.ToString();
            var replayDir = Path.Combine(Contract.GameData, "MpReplays");
            if (Directory.Exists(replayDir)) settings.SeedReplay = Directory.GetFiles(replayDir, "*.zip").OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault() ?? "";
        }
        Role.SelectedIndex = settings.IsHost ? 0 : 1;
        Username.Text = settings.Username; HostAddress.Text = settings.HostAddress; GamePath.Text = settings.GamePath;
        ServerPassword.Password = settings.Password; ReplayPath.Text = settings.SeedReplay; AutoUpdate.IsChecked = settings.AutoUpdate; FastRecovery.IsChecked = settings.FastRecovery;
        Log("RimTogether " + typeof(MainWindow).Assembly.GetName().Version?.ToString(3) + " • " + Contract.GuardRevision + " / protocol " + Contract.Protocol + ". Сервер работает, пока включён ПК хоста.");
        Log("Другу: https://github.com/" + BundleUpdate.Repository + "/releases/latest — скачать RimTogether.Start.exe один раз. Дальше обновления при запуске.");
        Log("Сервер хранит контрольную точку каждые 30 секунд. Закрытие лаунчера останавливает сервер.");
        Log("Подготовка профиля отключает hiteke.steam, ставит Prepatcher и Multiplayer в нужный порядок. Исходный конфиг сохраняется.");
        RefreshButtons();
    }
    private void Log(string text)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => Log(text)); return; }
        var line = DateTime.Now.ToString("HH:mm:ss") + "  " + text;
        LogBox.AppendText(line + Environment.NewLine); LogBox.ScrollToEnd();
        try { File.AppendAllText(Path.Combine(root, "launcher.log"), line + Environment.NewLine); } catch (IOException) { }
    }
    private Settings ReadForm()
    {
        if (!Regex.IsMatch(Username.Text.Trim(), "^[a-zA-Z0-9_]{3,15}$")) throw new InvalidOperationException("Ник: 3–15 латинских букв, цифр или _. У игроков разные ники.");
        if (!IPAddress.TryParse(HostAddress.Text.Trim(), out var ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork || ip.Equals(IPAddress.Any))
            throw new InvalidOperationException("Введи IPv4 хоста из Radmin VPN.");
        if (ServerPassword.Password.Length < 6) throw new InvalidOperationException("Пароль должен содержать минимум 6 символов.");
        var result = new Settings { IsHost = Role.SelectedIndex == 0, GamePath = GamePath.Text.Trim(), Username = Username.Text.Trim(),
            HostAddress = ip.ToString(), Password = ServerPassword.Password, SeedReplay = ReplayPath.Text.Trim(), AutoUpdate = AutoUpdate.IsChecked == true,
            LastFingerprint = settings.LastFingerprint, ExpectedFingerprint = settings.ExpectedFingerprint, FastRecovery = FastRecovery.IsChecked == true };
        Storage.Write(SettingsPath, result); settings = result; return result;
    }
    private async Task Run(Func<Task> action)
    {
        if (!await gate.WaitAsync(0)) { Log("Дождись завершения текущей операции."); return; }
        try { Status.Text = "Выполняется…"; PlayButton.IsEnabled = StartButton.IsEnabled = false; await action(); Status.Text = "Готово"; }
        catch (Exception e) { Log("Ошибка: " + e.Message); Status.Text = "Нужно действие: " + e.Message; }
        finally { gate.Release(); RefreshButtons(); }
    }
    private void RefreshButtons()
    {
        var running = server != null && !server.HasExited;
        PlayButton.IsEnabled = true; StartButton.IsEnabled = Role.SelectedIndex == 0 && !running;
        StopButton.IsEnabled = running;
        FastRecovery.Visibility = Role.SelectedIndex == 0 ? Visibility.Collapsed : Visibility.Visible;
        RecoveryModeHint.Visibility = Role.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
        Role.IsEnabled = Username.IsEnabled = HostAddress.IsEnabled = ServerPassword.IsEnabled = GamePath.IsEnabled = ReplayPath.IsEnabled = !running;
        if (Summary != null) Summary.Text = Contract.GuardRevision + " • Multiplayer " + Contract.Version + " • UDP 30502 • " + (running ? (serverReady ? "сервер работает" : "сервер запускается") : "сервер остановлен");
    }
    private async Task Prepare(Settings config, bool force = false)
    {
        ModManager.RequireIdle(); ModManager.ValidateGame(config.GamePath);
        if (!config.IsHost && config.ExpectedFingerprint.Length == 0)
            throw new InvalidOperationException("Сначала вставь приглашение хоста: нужна сверка файлов и версии Guard.");
        if (server != null && !server.HasExited) return;
        var manager = new ModManager(http, root, Log);
        var configPath = Path.Combine(Contract.GameData, "Config", "ModsConfig.xml");
        if (config.AutoUpdate || force)
        {
            var archive = await manager.GetArchive();
            await Task.Run(() => manager.Install(config.GamePath, archive, configPath));
            await manager.CheckNewReleases();
        }
        else
        {
            var installed = Path.Combine(config.GamePath, "Mods", "RimTogether-Multiplayer", "About", "About.xml");
            if (!File.Exists(installed) || !GuardPayload.Load(Path.Combine(AppContext.BaseDirectory, "packages", "guard")).Installed(Path.GetDirectoryName(Path.GetDirectoryName(installed)!)!))
                throw new InvalidOperationException("Сначала нажми «Проверить обновления» для установки совместимого мода.");
        }
        var fingerprint = await Task.Run(() => GameFingerprint.Compute(config.GamePath, configPath));
        if (!config.IsHost && config.ExpectedFingerprint.Length > 0 && config.ExpectedFingerprint != fingerprint)
            throw new InvalidOperationException("Файлы игры или активных модов отличаются от хоста. Нужны одинаковые DLC, Prepatcher и прочие моды. Попроси новое приглашение после обновления обоих.");
        config.LastFingerprint = fingerprint; settings = config; Storage.Write(SettingsPath, config);
        Log("Файлы игры и модов: " + fingerprint[..16] + (config.ExpectedFingerprint == fingerprint ? " — совпадают с хостом." : "."));
    }
    private async Task StartServer(Settings config)
    {
        if (server != null && !server.HasExited) return;
        if (!config.IsHost) throw new InvalidOperationException("Сервер запускает только хост.");
        var serverExe = Path.Combine(AppContext.BaseDirectory, "server", "RimTogether.Server.exe");
        if (!File.Exists(serverExe)) throw new FileNotFoundException("Не найдена папка server. Распакуй весь архив лаунчера.");
        var localAddresses = NetworkInterface.GetAllNetworkInterfaces().SelectMany(n => n.GetIPProperties().UnicastAddresses).Select(a => a.Address.ToString());
        if (!localAddresses.Contains(config.HostAddress)) throw new InvalidOperationException("Этот Radmin IP не принадлежит твоему ПК. Включи Radmin или исправь адрес.");
        var serverDir = Path.Combine(root, "server"); Directory.CreateDirectory(serverDir);
        var configPath = Path.Combine(serverDir, "config.json");
        if (!File.Exists(Path.Combine(serverDir, "world", "checkpoint.json")) && !File.Exists(Path.Combine(serverDir, "world", "checkpoint.json.bak")) && !File.Exists(config.SeedReplay))
            throw new InvalidOperationException("Выбери MP-сохранение ZIP. Если колонии ещё нет: создай её в RimWorld, начни MP и сохрани.");
        Storage.Write(configPath, config);
        var start = new ProcessStartInfo(serverExe) { WorkingDirectory = serverDir, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(configPath);
        serverReady = false;
        serverLog = new StreamWriter(Path.Combine(serverDir, "server-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".log"), true) { AutoFlush = true };
        server = new Process { StartInfo = start, EnableRaisingEvents = true };
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handle(string? line)
        {
            if (line == null) return;
            lock (this) serverLog?.WriteLine(line);
            if (line.StartsWith("READY ")) { serverReady = true; ready.TrySetResult(); }
            Log("Сервер: " + line);
        }
        server.OutputDataReceived += (_, e) => Handle(e.Data); server.ErrorDataReceived += (_, e) => Handle(e.Data);
        server.Exited += (_, _) => { ready.TrySetException(new IOException("Сервер завершился. Причина — в журнале ниже.")); Dispatcher.BeginInvoke(() => { serverReady = false; RefreshButtons(); }); };
        server.Start(); server.BeginOutputReadLine(); server.BeginErrorReadLine();
        try { await ready.Task.WaitAsync(TimeSpan.FromSeconds(30)); }
        catch { if (!server.HasExited) await StopServer(); throw; }
        Log("Друг подключается по " + config.HostAddress + ":30502. Если Windows спросит о доступе к сети, разреши сервер на сети Radmin.");
    }
    private async Task StopServer()
    {
        if (server == null || server.HasExited) return;
        await server.StandardInput.WriteLineAsync("stop"); await server.StandardInput.FlushAsync();
        await server.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        lock (this) { serverLog?.Dispose(); serverLog = null; }
        serverReady = false; Log("Сервер остановлен. Контрольная точка и резервная копия остаются в папке данных.");
    }
    private async void Play_Click(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        var config = ReadForm(); await Prepare(config);
        if (config.IsHost) await StartServer(config);
        var endpoint = (config.IsHost ? "127.0.0.1" : config.HostAddress) + ":30502";
        var start = new ProcessStartInfo(Path.Combine(config.GamePath, "RimWorldWin64.exe")) { WorkingDirectory = config.GamePath, UseShellExecute = false };
        start.ArgumentList.Add("-connect=" + endpoint); start.ArgumentList.Add("-username=" + config.Username);
        start.Environment["RIMTOGETHER_ROLE"] = config.IsHost ? "host" : "friend";
        start.Environment["RIMTOGETHER_FAST_RESYNC"] = config.FastRecovery ? "1" : "0";
        Process.Start(start); Log("Игра запущена. Пароль сервера вводится в RimWorld; адрес " + endpoint + ".");
    });
    private async void StartServer_Click(object sender, RoutedEventArgs e) => await Run(async () => { var config = ReadForm(); await Prepare(config); await StartServer(config); });
    private async void StopServer_Click(object sender, RoutedEventArgs e) => await Run(StopServer);
    private async void Update_Click(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        if (server != null && !server.HasExited) throw new InvalidOperationException("Перед обновлением останови сервер и закрой игру у обоих.");
        await Prepare(ReadForm(), true);
    });
    private void BrowseGame_Click(object sender, RoutedEventArgs e) { var dialog = new OpenFileDialog { Filter = "RimWorld|RimWorldWin64.exe" }; if (dialog.ShowDialog() == true) GamePath.Text = Path.GetDirectoryName(dialog.FileName)!; }
    private void BrowseReplay_Click(object sender, RoutedEventArgs e) { var dialog = new OpenFileDialog { Filter = "Multiplayer save|*.zip", InitialDirectory = Path.Combine(Contract.GameData, "MpReplays") }; if (dialog.ShowDialog() == true) ReplayPath.Text = dialog.FileName; }
    private async void Invite_Click(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        var config = ReadForm();
        if (!config.IsHost) throw new InvalidOperationException("Приглашение создаёт хост.");
        if (server == null || server.HasExited || config.LastFingerprint.Length == 0) await Prepare(config, true);
        Clipboard.SetText(JsonSerializer.Serialize(new Invitation(1, Contract.Tag, config.HostAddress, config.Password, config.LastFingerprint, Contract.GuardRevision)));
        Log("Приглашение скопировано с контрольной суммой файлов. Оно содержит пароль: передай только другу. Друг нажимает «Вставить приглашение».");
    });
    private void Import_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var invite = JsonSerializer.Deserialize<Invitation>(Clipboard.GetText()) ?? throw new InvalidDataException("Пустое приглашение.");
            if (invite.Schema != 1 || invite.Tag != Contract.Tag || invite.GuardRevision != Contract.GuardRevision || !IPAddress.TryParse(invite.Address, out _) || invite.Password.Length < 6 || !Regex.IsMatch(invite.Fingerprint ?? "", "^[A-F0-9]{64}$"))
                throw new InvalidDataException("Приглашение несовместимо. Используйте один комплект лаунчера.");
            HostAddress.Text = invite.Address; ServerPassword.Password = invite.Password; Role.SelectedIndex = 1;
            settings.ExpectedFingerprint = invite.Fingerprint!;
            Log("Приглашение принято. Введи свой ник и выбери локальную папку RimWorld.");
        }
        catch (Exception ex) { Log("Не удалось прочитать приглашение: " + ex.Message); }
    }
    private void OpenData_Click(object sender, RoutedEventArgs e) => Process.Start(new ProcessStartInfo(root) { UseShellExecute = true });
    private void Role_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e) { if (StartButton != null) RefreshButtons(); }
    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (closing) return;
        if (gate.CurrentCount == 0) { e.Cancel = true; Log("Дождись окончания установки или остановки сервера."); return; }
        try { ReadForm(); } catch { }
        if (server == null || server.HasExited) return;
        e.Cancel = true;
        try { await StopServer(); closing = true; Close(); }
        catch (Exception ex) { Log("Не удалось остановить сервер: " + ex.Message + ". Лаунчер остаётся открыт."); }
    }
    private record Invitation(int Schema, string Tag, string Address, string Password, string Fingerprint, string? GuardRevision = null);
}
