using System.Net;
using LiteNetLib;
using Multiplayer.Common;
using Multiplayer.Common.Networking.Packet;

namespace RimTogether;

public static class NetworkChecks
{
    public static void Run(Checkpoint checkpoint, Action<bool, string> check)
    {
        var server = MultiplayerServer.instance = new MultiplayerServer(new ServerSettings
        { maxPlayers = 2, hasPassword = true, password = "test-password", autoJoinPoint = AutoJoinPointFlags.Join | AutoJoinPointFlags.Desync })
        { running = true, IsStandaloneServer = true, hostUsername = "Alpha" };
        checkpoint.Apply(server);
        if (!LiteNetManager.Create(server, [new IPEndPoint(IPAddress.Loopback, 0)], out var manager)) throw new Exception("Test bind failed");
        server.netManagers.Add(manager);
        var port = manager.netManagers[0].manager.LocalPort;
        MpConnectionState.SetImplementation(ConnectionStateEnum.ClientJoining, typeof(ProbeState));
        var thread = new Thread(server.Run); thread.Start();
        var clients = new List<Probe>();
        Probe Connect(string name, string password)
        {
            var probe = new Probe(name, password); clients.Add(probe); probe.Manager.Start(); probe.Manager.Connect("127.0.0.1", port, ""); return probe;
        }
        void Until(Func<bool> condition)
        {
            var until = DateTime.UtcNow.AddSeconds(8);
            while (!condition() && DateTime.UtcNow < until)
            { foreach (var p in clients) p.Manager.PollEvents(); Thread.Sleep(5); }
            if (!condition()) throw new Exception("UDP integration test timed out");
        }
        try
        {
            var wrong = Connect("Wrong", "incorrect"); Until(() => wrong.Disconnected);
            check(!wrong.Authenticated, "UDP server rejects incorrect password");
            var first = Connect("Alpha", "test-password"); Until(() => first.Authenticated);
            var second = Connect("Bravo", "test-password"); Until(() => second.Authenticated);
            check(first.Authenticated && second.Authenticated, "two clients authenticate and receive full world data over UDP");
            var third = Connect("Third", "test-password"); Until(() => third.Disconnected);
            check(!third.Authenticated && third.Reason == DisconnectReason.ConnectionRejected, "third client explicitly rejected by two-player limit");
        }
        finally
        {
            foreach (var p in clients) p.Manager.Stop();
            server.running = false; thread.Join(TimeSpan.FromSeconds(5));
        }
    }
}

public class Probe
{
    public readonly NetManager Manager;
    public bool Authenticated;
    public bool Disconnected;
    public DisconnectReason Reason;
    public Probe(string name, string password)
    {
        var listener = new EventBasedNetListener();
        LiteNetConnection? connection = null;
        listener.PeerConnectedEvent += peer =>
        {
            connection = new LiteNetConnection(peer) { username = name, Lenient = true };
            connection.ChangeState(new ProbeState(connection, password, () => Authenticated = true));
        };
        listener.PeerDisconnectedEvent += (_, info) => { Reason = info.Reason; Disconnected = true; };
        listener.NetworkReceiveEvent += (_, reader, _, method) =>
        { connection!.HandleReceiveRaw(new ByteReader(reader.GetRemainingBytes()), method == DeliveryMethod.ReliableOrdered); };
        Manager = new NetManager(listener) { IPv6Enabled = false };
    }
}

public class ProbeState(ConnectionBase connection, string password, Action authenticated) : AsyncConnectionState(connection)
{
    [PacketHandler(Packets.Server_PlayerList)]
    public void Players(ByteReader packet) => packet.Seek(packet.Length);
    [PacketHandler(Packets.Server_TimeControl)]
    public void Time(ByteReader packet) => packet.Seek(packet.Length);
    [PacketHandler(Packets.Server_KeepAlive)]
    public void KeepAlive(ByteReader packet) => packet.Seek(packet.Length);
    [PacketHandler(Packets.Server_Command)]
    public void Command(ByteReader packet) => packet.Seek(packet.Length);
    [PacketHandler(Packets.Server_Chat)]
    public void Chat(ByteReader packet) => packet.Seek(packet.Length);
    protected override async Task RunState()
    {
        connection.Send(ClientProtocolPacket.Current()); await TypedPacket<ServerProtocolOkPacket>();
        connection.Send(new ClientUsernamePacket(connection.username!, password));
        if (connection.username == "Alpha")
        {
            await TypedPacket<ServerInitDataRequestPacket>();
            connection.Send(new ClientInitDataPacket { rwVersion = "1.6.4633", debugOnlySyncCmds = [], hostOnlySyncCmds = [],
                modCtorRoundMode = RoundModeEnum.ToNearest, staticCtorRoundMode = RoundModeEnum.ToNearest, defInfos = [], rawData = [] });
        }
        var packet = await Packet(Packets.Server_UsernameOk); packet.Seek(packet.Length);
        connection.Send(new ClientJoinDataPacket { modCtorRoundMode = RoundModeEnum.ToNearest,
            staticCtorRoundMode = RoundModeEnum.ToNearest, defInfos = [] });
        await TypedPacket<ServerJoinDataPacket>();
        connection.Send(Packets.Client_WorldRequest);
        packet = await Packet(Packets.Server_WorldDataStart); packet.Seek(packet.Length);
        packet = await Packet(Packets.Server_WorldData).Fragmented(); packet.Seek(packet.Length);
        authenticated();
    }
}
