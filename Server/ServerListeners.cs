using System.Net;
using Multiplayer.Common;

namespace RimTogether;

public static class ServerListeners
{
    public static IReadOnlyList<LiteNetManager> Start(MultiplayerServer server, IEnumerable<IPEndPoint> endpoints, Action<string> log)
    {
        var started = new List<LiteNetManager>();
        try
        {
            foreach (var endpoint in endpoints.Distinct())
            {
                // Upstream groups endpoints by port and keeps only one address per family.
                // Separate managers preserve both the Radmin and local IPv4 listeners.
                var success = LiteNetManager.Create(server, [endpoint], out var manager);
                started.Add(manager);
                if (!success) throw new IOException("Не удалось занять UDP " + endpoint + ". Проверь IP Radmin и другой запущенный сервер.");
                log("UDP listening " + endpoint.Address + ":" + manager.netManagers[0].manager.LocalPort);
            }
            server.netManagers.AddRange(started);
            return started;
        }
        catch
        {
            foreach (var manager in started) manager.Stop();
            throw;
        }
    }
}
