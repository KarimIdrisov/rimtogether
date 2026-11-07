using Multiplayer.Common;

namespace RimTogether;

public sealed class RecoveryCoordinator(MultiplayerServer server, bool enabled, Action<string> log)
{
    public const int RefreshIntervalTicks = MultiplayerServer.NetTicksPerSecond * 90;
    private int requestedAt = server.NetTimer;
    private int lastWorkTicks = server.workTicks;
    public void Tick() => Tick(server.NetTimer);
    public void Tick(int now)
    {
        if (!enabled || server.worldData.CreatingJoinPoint || now - Math.Max(requestedAt, server.worldData.LastSnapshotAtNetTick) < RefreshIntervalTicks || server.workTicks == lastWorkTicks) return;
        if (server.worldData.TryStartJoinPointCreation())
        {
            requestedAt = now; lastWorkTicks = server.workTicks;
            log("RECOVERY: refreshing host snapshot; bounded replay window.");
        }
    }
}
