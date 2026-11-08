using Multiplayer.Common;

namespace RimTogether;

public static class CheckpointWriterChecks
{
    public static void Run(Checkpoint source, Action<bool, string> check, Action<Action, string> throws)
    {
        var server = new MultiplayerServer(new ServerSettings());
        System.Text.Json.JsonSerializer.Deserialize<Checkpoint>(System.Text.Json.JsonSerializer.Serialize(source, Storage.Json), Storage.Json)!.Apply(server);
        var immutable = Checkpoint.CaptureImmutable(server);
        var before = immutable.Commands.Values.Sum(v => v.Count);
        server.worldData.mapCmds.GetOrAddNew(-1).Add(ScheduledCommand.Serialize(new ScheduledCommand(CommandType.PauseAll, server.gameTimer, -1, -1, -1, [])));
        server.worldData.mapData.Clear();
        check(immutable.Maps.Count == source.Maps.Count && immutable.Commands.Values.Sum(v => v.Count) == before, "background checkpoint owns container copies unaffected by later server commands");
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var writes = new List<int>();
        using (var writer = new CheckpointWriter(snapshot =>
        {
            writes.Add(snapshot.Tick);
            if (snapshot.Tick == 1) { entered.Set(); if (!release.Wait(TimeSpan.FromSeconds(10))) throw new Exception("Writer test timed out"); }
        }, _ => { }))
        {
            writer.Submit(new Checkpoint { Tick = 1 });
            if (!entered.Wait(TimeSpan.FromSeconds(5))) throw new Exception("Background worker did not start");
            try
            {
                writer.Submit(new Checkpoint { Tick = 2 }); writer.Submit(new Checkpoint { Tick = 3 });
                check(writes.Count == 1, "server can submit checkpoints while disk writer is blocked without waiting on I/O");
            }
            finally { release.Set(); }
            writer.Complete();
            check(writes.SequenceEqual(new[] { 1, 3 }), "checkpoint queue coalesces stale pending work and flushes latest on shutdown");
        }
        var failed = new CheckpointWriter(_ => throw new IOException("Injected disk failure"), _ => { });
        failed.Submit(new Checkpoint()); throws(failed.Dispose, "final checkpoint write failure is reported instead of claiming a successful save");
    }
}
