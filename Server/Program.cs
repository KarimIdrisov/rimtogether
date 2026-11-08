using System.Net;
using Multiplayer.Common;
using RimTogether;

// The server uses pinned Common v0.11.5 plus the paired RimTogether Guard patches.
// This host adds durable checkpoints, a bounded two-player configuration and clean shutdown.
if (args.Length != 1) { Console.Error.WriteLine("Usage: RimTogether.Server.exe <server-config.json>"); return 2; }
try
{
    var configPath = Path.GetFullPath(args[0]);
    var config = Storage.Read<Settings>(configPath);
    var directory = Path.Combine(Path.GetDirectoryName(configPath)!, "world");
    Directory.CreateDirectory(directory);
    using var exclusive = new FileStream(Path.Combine(directory, "server.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    var stateFile = Path.Combine(directory, "checkpoint.json");
    Checkpoint? checkpoint = null;
    if (File.Exists(stateFile) || File.Exists(stateFile + ".bak"))
    {
        foreach (var candidate in new[] { stateFile, stateFile + ".bak" })
        {
            try { checkpoint = Storage.Read<Checkpoint>(candidate); checkpoint.Validate(); break; }
            catch (Exception e) { Console.WriteLine("CHECKPOINT WARNING: " + e.Message); checkpoint = null; }
        }
        if (checkpoint == null) throw new InvalidDataException("Контрольные точки не удалось безопасно загрузить. Причина указана выше; несовместимые или повреждённые данные не перезаписываются.");
    }
    else checkpoint = Checkpoint.FromReplay(config.SeedReplay);
    if (checkpoint.ClientProtocol != Contract.Protocol)
    {
        var backup = Path.Combine(directory, "checkpoint.before-protocol-" + checkpoint.ClientProtocol + ".json");
        if (!File.Exists(backup)) Storage.Write(backup, checkpoint, keepRawBackup: false);
        checkpoint.ClientProtocol = Contract.Protocol; checkpoint.Tag = Checkpoint.GuardTag;
        Console.WriteLine("MIGRATION: compatible snapshot imported; protocol backup retained.");
    }
    if (!IPAddress.TryParse(config.HostAddress, out var bind) || bind.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        throw new InvalidDataException("Нужен IPv4 адаптера Radmin.");
    if (config.Password.Length < 6) throw new InvalidDataException("Пароль должен быть от 6 символов.");
    var settings = new ServerSettings { direct = true, lan = false, steam = false, maxPlayers = 2,
        directAddress = config.HostAddress + ":30502", gameName = "RimTogether — двое", hasPassword = true,
        password = config.Password, asyncTime = checkpoint.AsyncTime, multifaction = checkpoint.Multifaction,
        syncConfigs = true, desyncTraces = true, pauseOnJoin = true, pauseOnDesync = true,
        autoJoinPoint = RecoveryCoordinator.AutomaticTriggers };
    var server = MultiplayerServer.instance = new MultiplayerServer(settings) { running = true, IsStandaloneServer = true,
        BootstrapMode = false, hostUsername = config.Username };
    checkpoint.Apply(server);
    var endpoints = new List<IPEndPoint> { new(bind, Contract.Port) };
    if (!IPAddress.IsLoopback(bind)) endpoints.Add(new IPEndPoint(IPAddress.Loopback, Contract.Port));
    ServerListeners.Start(server, endpoints, Console.WriteLine);
    try { server.BulkDownloads = new BulkDownloadServer(endpoints, Console.WriteLine); Console.WriteLine("BULK TCP enabled on port " + Contract.Port); }
    catch (Exception error) { Console.WriteLine("BULK TCP unavailable; UDP remains active: " + error.Message); }
    // Preserve a healthy recovered backup instead of backing up corrupt primary bytes.
    Storage.Write(stateFile, checkpoint, keepRawBackup: false);
    var thread = new Thread(server.Run) { Name = "RimTogether server" };
    thread.Start();
    using var writer = new CheckpointWriter(state =>
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        state.Validate(); Storage.Write(stateFile, state);
        Console.WriteLine("CHECKPOINT OK tick=" + state.Tick + " background-ms=" + watch.ElapsedMilliseconds);
    }, Console.WriteLine);
    int lastPersistedGeneration = server.worldData.SnapshotGeneration;
    int lastPersistAt = server.NetTimer;
    void Persist()
    {
        if (server.worldData.CreatingJoinPoint) { Console.WriteLine("CHECKPOINT WAIT: joinpoint in progress; previous checkpoint retained"); return; }
        writer.Submit(Checkpoint.CaptureImmutable(server));
    }
    using var timer = new Timer(_ => server.Enqueue(() =>
    {
        try
        {
            if (server.worldData.SnapshotGeneration != lastPersistedGeneration || server.NetTimer - lastPersistAt >= MultiplayerServer.NetTicksPerSecond * 30)
            {
                Persist(); lastPersistAt = server.NetTimer;
                lastPersistedGeneration = server.worldData.SnapshotGeneration;
            }
        }
        catch (Exception e) { Console.WriteLine("CHECKPOINT ERROR: " + e.Message); }
    }), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    Console.WriteLine("READY " + config.HostAddress + ":30502 version=" + Contract.Version + " guard=" + Contract.GuardRevision + " protocol=" + Contract.Protocol + " tick=" + server.gameTimer);
    // A disconnected launcher pipe is a clean-stop request, not an infinite busy loop.
    var serverDone = Task.Run(() => thread.Join());
    while (server.running)
    {
        var input = Task.Run(Console.ReadLine);
        if (await Task.WhenAny(input, serverDone) == serverDone) break;
        var line = await input;
        if (line == null || line.Equals("stop", StringComparison.OrdinalIgnoreCase))
        {
            server.Enqueue(() => { try { server.worldData.AbortJoinPointCreation(); Persist(); } finally { server.running = false; } });
            break;
        }
        if (line == "save") server.Enqueue(Persist);
    }
    thread.Join();
    // Includes /stop requested by the in-game host; the networking thread is stopped now.
    server.worldData.AbortJoinPointCreation(); Persist();
    writer.Complete();
    Console.WriteLine("STOPPED"); return 0;
}
catch (Exception e) { Console.Error.WriteLine("SERVER ERROR: " + e); return 1; }
