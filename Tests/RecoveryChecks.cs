using System.Text.Json;
using Multiplayer.Common;
using Multiplayer.Common.Networking.Packet;

namespace RimTogether;

public static class RecoveryChecks
{
    public static void Run(Checkpoint source, Action<bool, string> check, Action<Action, string> throws)
    {
        check(MpVersion.Protocol == Contract.Protocol && Contract.Protocol == 5403, "guard client/server require dedicated-host verification protocol");
        check(!RecoveryPolicy.CanTickSimulation(false, ConnectionStateEnum.ClientLoading) &&
            !RecoveryPolicy.CanTickSimulation(false, ConnectionStateEnum.ClientJoining) &&
            RecoveryPolicy.CanTickSimulation(false, ConnectionStateEnum.ClientPlaying) &&
            RecoveryPolicy.CanTickSimulation(true, ConnectionStateEnum.ClientLoading), "rejoin cannot tick old world while connection is loading; active play and replay remain supported");
        var verification = new SyncVerificationWatchdog();
        for (var i = 0; i < SyncVerificationWatchdog.MaxUnverifiedOpinions - 1; i++)
            if (verification.ObserveLocal(true, false)) throw new Exception("Verification alarm before backlog limit");
        check(true, "verification tolerates bounded network delay without a false alarm");
        check(verification.ObserveLocal(true, false), "missing comparisons cannot silently discard an unlimited opinion backlog");
        verification.Matched();
        check(!verification.ObserveLocal(true, false), "successful comparison resets missing-verification budget");
        for (var i = 0; i < 100; i++)
            if (verification.ObserveLocal(false, false)) throw new Exception("Authority triggered watchdog");
        for (var i = 0; i < 100; i++)
            if (verification.ObserveLocal(true, true)) throw new Exception("Catch-up triggered watchdog");
        check(true, "authority without arbiter and replay catch-up do not trigger missing-verification alarm");
        check(RecoveryPolicy.ShouldUpload(false, false, false, false, true), "dedicated host uploads fresh world snapshots");
        check(!RecoveryPolicy.ShouldUpload(false, false, false, false, false) && !RecoveryPolicy.ShouldUpload(true, false, false, false, true), "friend and replay simulation cannot publish authoritative world");
        var budget = new AutoRecoveryBudget();
        check(budget.TryAcquire(10) && !budget.TryAcquire(20) && budget.CircuitOpen && !budget.TryAcquire(400), "repeated desync trips auto-resync circuit breaker instead of looping");
        var sporadic = new AutoRecoveryBudget(); check(sporadic.TryAcquire(0) && sporadic.TryAcquire(181), "isolated recovery remains available after a stable interval");
        MultiplayerServer MakeServer()
        {
            var server = MultiplayerServer.instance = new MultiplayerServer(new ServerSettings { pauseOnDesync = true, autoJoinPoint = AutoJoinPointFlags.Desync })
            { IsStandaloneServer = true, running = true, hostUsername = "Host" };
            JsonSerializer.Deserialize<Checkpoint>(JsonSerializer.Serialize(source, Storage.Json), Storage.Json)!.Apply(server);
            return server;
        }
        ServerPlayer Add(MultiplayerServer server, string name)
        {
            var conn = new MemoryConn { username = name };
            var player = server.playerManager.OnConnected(conn); player.FactionId = 14; player.hasJoined = true;
            conn.ChangeState(ConnectionStateEnum.ServerPlaying); player.status = PlayerStatus.Playing;
            return player;
        }
        var active = MakeServer(); var host = Add(active, "Host");
        check(active.worldData.TryStartJoinPointCreation(true), "healthy host can produce a requested joinpoint");
        var firstId = active.worldData.PendingJoinPointId;
        var waiting = active.worldData.WaitJoinPoint(); var oldWorld = active.worldData.savedGame;
        throws(() => active.worldData.CommitSnapshot(firstId, [], [], []), "incomplete upload rejected before replacing world");
        check(ReferenceEquals(oldWorld, active.worldData.savedGame), "failed upload preserves authoritative snapshot");
        active.worldData.TickJoinPointTimeout(WorldData.JoinPointTimeoutTicks);
        check(!active.worldData.CreatingJoinPoint && waiting.IsCompleted && ReferenceEquals(oldWorld, active.worldData.savedGame), "joinpoint timeout releases rejoin and retains cached snapshot");
        check(active.worldData.TryStartJoinPointCreation(true), "new joinpoint can start after timeout");
        var secondId = active.worldData.PendingJoinPointId;
        check(secondId != firstId && !active.worldData.CommitSnapshot(firstId, new() { [0] = [9] }, [9], []), "late upload from previous generation cannot replace newer snapshot request");
        var bad = new ByteWriter(); bad.WriteInt32(secondId); bad.WriteInt32(1); bad.WriteInt32(0); bad.WritePrefixedBytes([8]);
        throws(() => ((ServerPlayingState)host.conn.StateObj!).HandleWorldDataUpload(new ByteReader(bad.ToArray())), "truncated wire upload cannot partially publish map data");
        check(ReferenceEquals(oldWorld, active.worldData.savedGame), "wire parse failure leaves old world intact");
        throws(() => active.worldData.CommitSnapshot(secondId, source.Maps, [1, 2, 3], source.Session), "corrupt compressed snapshot rejected before cache promotion");
        var suffix = active.worldData.tmpMapCmds;
        check(active.worldData.CommitSnapshot(secondId, source.Maps, source.World, source.Session), "matching complete world upload commits atomically");
        check(active.worldData.SnapshotGeneration == 1 && ReferenceEquals(active.worldData.mapCmds, suffix), "snapshot commit cuts replay history only after complete upload");
        check(!active.worldData.CommitSnapshot(secondId, [], [99], []), "duplicate completed upload is ignored");
        active.worldData.TryStartJoinPointCreation(true);
        active.playerManager.OnDesync(host, active.gameTimer, 0);
        check(!active.worldData.CreatingJoinPoint && active.worldData.savedGame!.SequenceEqual(source.World), "desynced host cannot publish a fresh authority snapshot");
        var mapless = MakeServer(); mapless.worldData.mapData = []; Checkpoint.Capture(mapless).Validate();
        check(true, "world-only checkpoint remains valid when all pawns are travelling");
        var broken = Checkpoint.Capture(MakeServer()); broken.World = [1, 2, 3];
        throws(broken.Validate, "valid JSON with corrupted gzip world is rejected during checkpoint recovery");
        var absent = MakeServer(); var friend = Add(absent, "Friend");
        check(!absent.worldData.TryStartJoinPointCreation(true), "host absence cannot start an impossible joinpoint");
        absent.playerManager.OnDesync(friend, absent.gameTimer, 0);
        check(!absent.worldData.CreatingJoinPoint, "desync with absent host retains cache without throwing");
        check(RecoveryCoordinator.AutomaticTriggers.HasFlag(AutoJoinPointFlags.Desync), "on-demand fresh snapshot remains enabled for desync recovery");
        check(RecoveryCoordinator.AutomaticTriggers == (AutoJoinPointFlags.Join | AutoJoinPointFlags.Desync), "normal autosave and periodic timers do not request extra world reloads");
        var demanded = MakeServer(); Add(demanded, "Host"); var guest = Add(demanded, "Guest");
        demanded.settings.autoJoinPoint = RecoveryCoordinator.AutomaticTriggers;
        demanded.playerManager.OnDesync(guest, demanded.gameTimer, 0);
        check(demanded.worldData.CreatingJoinPoint, "actual guest desync still requests a fresh snapshot from healthy host");
        demanded.worldData.AbortJoinPointCreation();
        var restarted = MakeServer(); Add(restarted, "Host"); restarted.workTicks = 10_000; restarted.worldData.lastJoinPointAtWorkTicks = 10_000;
        restarted.UpdateHostWorkTicks(0); restarted.UpdateHostWorkTicks(40);
        check(restarted.worldData.TryStartJoinPointCreation(), "restarted host counter cannot block fresh snapshots behind prior-session work ticks");
        restarted.worldData.AbortJoinPointCreation();
        CheckAffinity(MakeServer(), check);
        CheckInitDisconnect(check);
    }

    private static void CheckInitDisconnect(Action<bool, string> check)
    {
        var server = MultiplayerServer.instance = new MultiplayerServer(new ServerSettings()) { IsStandaloneServer = true, hostUsername = "Host", running = true };
        MemoryConn Connect()
        {
            var conn = new MemoryConn(); server.playerManager.OnConnected(conn); conn.ChangeState(ConnectionStateEnum.ServerJoining);
            void Deliver(SerializedPacket packet)
            {
                var wire = new ByteWriter(); wire.WriteByte((byte)packet.id); wire.WriteRaw(packet.data);
                conn.HandleReceiveRaw(new ByteReader(wire.ToArray()), true);
            }
            Deliver(ClientProtocolPacket.Current().Serialize()); Deliver(new ClientUsernamePacket("Host").Serialize()); return conn;
        }
        var first = Connect();
        check(server.InitDataState == InitDataState.Requested, "host handshake requests initial config before receiving large payload");
        server.playerManager.SetDisconnected(first, MpDisconnectReason.ClientLeft);
        check(server.InitDataState == InitDataState.Waiting, "disconnect during init upload releases pending server config instead of requesting again on dead connection");
        var second = Connect();
        check(server.InitDataState == InitDataState.Requested, "new host connection can supply config after failed fragmented handshake");
        server.playerManager.SetDisconnected(second, MpDisconnectReason.ClientLeft);
    }

    private static void CheckAffinity(MultiplayerServer server, Action<bool, string> check)
    {
        var previous = SynchronizationContext.Current;
        var context = new ServerLoopContext(server);
        var mainThread = Environment.CurrentManagedThreadId;
        try
        {
            // ClientSteam has no implementation in this server-only test process.
            MpConnectionState.SetImplementation(ConnectionStateEnum.ClientSteam, typeof(AffinityState));
            SynchronizationContext.SetSynchronizationContext(context);
            var conn = new MemoryConn { username = "Host" }; server.playerManager.OnConnected(conn);
            var state = new AffinityState(conn); conn.ChangeState(state);
            SynchronizationContext.SetSynchronizationContext(previous);
            var wire = new ByteWriter(); wire.WriteByte((byte)Packets.Client_KeepAlive);
            new ClientKeepAlivePacket(0, 0, false, 0).Bind(new PacketWriter(wire));
            Task.Run(() => conn.HandleReceiveRaw(new ByteReader(wire.ToArray()), true)).GetAwaiter().GetResult();
            check(state.After == 0, "off-thread packet completion queues protocol transitions instead of mutating server immediately");
            SynchronizationContext.SetSynchronizationContext(context);
            server.queue.RunQueue(Console.WriteLine);
            check(state.After == mainThread, "async protocol continuation executes on owning server loop");
            var closed = new MemoryConn { username = "Closed" }; server.playerManager.OnConnected(closed);
            var abandoned = new AffinityState(closed); closed.ChangeState(abandoned);
            SynchronizationContext.SetSynchronizationContext(previous);
            Task.Run(() => closed.HandleReceiveRaw(new ByteReader(wire.ToArray()), true)).GetAwaiter().GetResult();
            closed.ChangeState(ConnectionStateEnum.Disconnected);
            SynchronizationContext.SetSynchronizationContext(context); server.queue.RunQueue(Console.WriteLine);
            check(abandoned.After == 0, "queued continuation from a disconnected protocol state cannot mutate server");
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }
}

public class MemoryConn : ConnectionBase
{
    protected override void SendRaw(byte[] raw, bool reliable = true) { }
    protected override void OnClose(ServerDisconnectPacket? goodbye) { }
}

public class AffinityState(ConnectionBase connection) : AsyncConnectionState(connection)
{
    public int After;
    protected override async Task RunState() { await TypedPacket<ClientKeepAlivePacket>(); After = Environment.CurrentManagedThreadId; }
}
