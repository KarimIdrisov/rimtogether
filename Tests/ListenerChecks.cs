using System.Net;
using System.Net.Sockets;
using LiteNetLib;
using Multiplayer.Common;

namespace RimTogether;

public static class ListenerChecks
{
    public static void Run(Action<bool, string> check)
    {
        var remoteAddress = IPAddress.Parse("127.0.0.2");
        int port;
        using (var reservation = new UdpClient(new IPEndPoint(remoteAddress, 0)))
            port = ((IPEndPoint)reservation.Client.LocalEndPoint!).Port;
        var server = MultiplayerServer.instance = new MultiplayerServer(new ServerSettings { maxPlayers = 2 }) { BootstrapMode = true };
        var endpoints = new[] { new IPEndPoint(remoteAddress, port), new IPEndPoint(IPAddress.Loopback, port) };
        var managers = ServerListeners.Start(server, endpoints, _ => { });
        var clients = new List<NetManager>(); int connected = 0;
        try
        {
            check(managers.Count == 2 && managers.All(m => m.netManagers.Single().manager.LocalPort == port), "Radmin-style and local IPv4 addresses retain separate UDP listeners on the same port");
            foreach (var endpoint in endpoints)
            {
                var listener = new EventBasedNetListener();
                listener.PeerConnectedEvent += _ => connected++;
                var client = new NetManager(listener) { IPv6Enabled = false };
                clients.Add(client);
                if (!client.Start()) throw new Exception("Probe bind failed");
                client.Connect(endpoint.Address.ToString(), port, "");
            }
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (connected < 2 && DateTime.UtcNow < deadline)
            {
                foreach (var manager in managers) manager.Tick();
                foreach (var client in clients) client.PollEvents();
                Thread.Sleep(5);
            }
            check(connected == 2, "UDP clients connect through both IPv4 addresses rather than only the last address");
        }
        finally
        {
            foreach (var client in clients) client.Stop();
            foreach (var manager in managers) manager.Stop();
        }
    }
}
