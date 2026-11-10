using Multiplayer.Common;

namespace RimTogether;

public static class HostStartupChecks
{
    public static void Run(Checkpoint source, Action<bool,string> check, Action<Action,string> throws)
    {
        var server = MultiplayerServer.instance = new MultiplayerServer(new ServerSettings())
            { IsStandaloneServer = true, hostUsername = "Host" };
        System.Text.Json.JsonSerializer.Deserialize<Checkpoint>(System.Text.Json.JsonSerializer.Serialize(source,Storage.Json),Storage.Json)!.Apply(server);
        ServerPlayer AddHost()
        {
            var connection = new MemoryConn { username = "Host" };
            var player = server.playerManager.OnConnected(connection);
            connection.ChangeState(ConnectionStateEnum.ServerPlaying);
            player.status = PlayerStatus.Simulating; player.simulating = true;
            return player;
        }
        var host = AddHost();
        server.HostStartup.BeginLoading(host);
        var guestWaiting = server.HostStartup.WaitUntilReady();
        server.HostStartup.Tick();
        check(!guestWaiting.IsCompleted && !server.worldData.CreatingJoinPoint, "guest waits for fully loaded host instead of receiving old command history");
        var commands = server.commands.SentCmds;
        server.commands.Send(CommandType.GlobalTimeSpeed, -1, -1, [1], host);
        check(server.commands.SentCmds == commands, "resume command cannot unpause world during host loading");
        server.workTicks = server.worldData.lastJoinPointAtWorkTicks;
        host.MarkWorldReady();
        check(server.worldData.CreatingJoinPoint && !guestWaiting.IsCompleted, "host ready forces one full snapshot even when paused work ticks did not advance");
        var id = server.worldData.PendingJoinPointId; var baseTick = server.gameTimer;
        server.gameTimer += 5;
        check(server.worldData.CommitSnapshot(id, source.Maps,source.World,source.Session), "complete host startup snapshot is accepted");
        guestWaiting.GetAwaiter().GetResult();
        check(server.HostStartup.Ready && server.startingTimer == baseTick, "fresh base releases guest and records snapshot boundary rather than upload completion tick");
        var generation = server.worldData.SnapshotGeneration;
        host.MarkWorldReady();
        for (var i=0;i<2000;i++) server.HostStartup.Tick();
        check(!server.worldData.CreatingJoinPoint && server.worldData.SnapshotGeneration == generation, "duplicate ready and elapsed ticks never create periodic reloads");
        commands = server.commands.SentCmds;
        server.commands.Send(CommandType.GlobalTimeSpeed,-1,-1,[1],host);
        check(server.commands.SentCmds == commands+1, "manual resume is enabled only after fresh full snapshot completes");
        server.HostStartup.BeginLoading(host); host.MarkWorldReady();
        var nextId = server.worldData.PendingJoinPointId;
        check(nextId != id && !server.HostStartup.Ready, "host reload arms exactly one new snapshot and closes guest gate");
        server.worldData.TickJoinPointTimeout(WorldData.JoinPointTimeoutTicks);
        throws(() => server.HostStartup.WaitUntilReady().GetAwaiter().GetResult(), "failed fresh snapshot does not silently admit guest using stale history");
        server.playerManager.SetDisconnected(host.conn, MpDisconnectReason.ClientLeft);
        var replacement = AddHost(); server.HostStartup.BeginLoading(replacement); replacement.MarkWorldReady();
        var retryId = server.worldData.PendingJoinPointId;
        server.worldData.CommitSnapshot(retryId,source.Maps,source.World,source.Session);
        check(server.HostStartup.Ready, "host reconnect recovers startup gate after snapshot timeout");
        var builtin = new MultiplayerServer(new ServerSettings());
        check(!builtin.HostStartup.PauseRequired && builtin.HostStartup.WaitUntilReady().IsCompleted, "built-in non-standalone server keeps its original startup behavior");
    }
}
