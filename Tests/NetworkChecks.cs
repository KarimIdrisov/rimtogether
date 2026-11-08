using System.Net;
using LiteNetLib;
using Multiplayer.Common;
using Multiplayer.Common.Networking.Packet;

namespace RimTogether;

public static class NetworkChecks
{
    private static bool registered;
    public static void Run(Checkpoint checkpoint, Action<bool, string> check, bool tcp = false, bool rejectTcp = false, bool lateTcp = false)
    {
        checkpoint = System.Text.Json.JsonSerializer.Deserialize<Checkpoint>(System.Text.Json.JsonSerializer.Serialize(checkpoint, Storage.Json), Storage.Json)!;
        checkpoint.Validate(); checkpoint.ClientProtocol = Contract.Protocol; checkpoint.Tag = Checkpoint.GuardTag;
        var server = MultiplayerServer.instance = new MultiplayerServer(new ServerSettings
        { maxPlayers = 2, hasPassword = true, password = "test-password", autoJoinPoint = AutoJoinPointFlags.Join | AutoJoinPointFlags.Desync })
        { running = true, IsStandaloneServer = true, hostUsername = "Alpha" };
        checkpoint.Apply(server);
        if (!LiteNetManager.Create(server, [new IPEndPoint(IPAddress.Loopback, 0)], out var manager)) throw new Exception("Test bind failed");
        server.netManagers.Add(manager);
        if (tcp) server.BulkDownloads = new BulkDownloadServer([new IPEndPoint(IPAddress.Loopback, 0)], Console.WriteLine);
        if (lateTcp) server.BulkAckTimeoutMilliseconds = 30;
        var port = manager.netManagers[0].manager.LocalPort;
        manager.netManagers[0].manager.MtuOverride = 1404;
        if (!registered) { MpConnectionState.SetImplementation(ConnectionStateEnum.ClientJoining, typeof(ProbeState)); registered = true; }
        var thread = new Thread(server.Run); thread.Start();
        var clients = new List<Probe>();
        Probe Connect(string name, string password)
        {
            Action? during = tcp && !rejectTcp && !lateTcp && name == "Bravo" ? () =>
            { server.Enqueue(() => server.commands.Send(CommandType.PauseAll, -1, -1, [])); Thread.Sleep(100); } : null;
            var probe = new Probe(name, password, checkpoint, rejectTcp, lateTcp, during); clients.Add(probe); probe.Manager.Start(); probe.Manager.Connect("127.0.0.1", port, ""); return probe;
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
            var early = Connect("BeforeHost", "test-password"); Until(() => early.Disconnected);
            check(!early.Authenticated, "friend cannot seed server settings before authoritative host enters");
            var first = Connect("Alpha", "test-password"); Until(() => first.Authenticated);
            var second = Connect("Bravo", "test-password"); Until(() => second.Authenticated);
            check(first.Authenticated && second.Authenticated, "two clients authenticate and receive full world data");
            if (tcp) check(first.TcpOffers > 0 && second.TcpOffers > 0, "TCP snapshot is offered only after authenticated joining");
            if (tcp && rejectTcp) check(first.TcpDownloads == 0 && second.TcpDownloads == 0, "explicit TCP failure falls back to complete UDP world");
            if (tcp && !rejectTcp && !lateTcp) check(first.TcpDownloads > 0 && second.TcpDownloads > 0, "TCP world transfer validates payload and acknowledges before entering play");
            if (lateTcp) check(first.WorldTransfers == 1 && second.WorldTransfers == 1, "late TCP acknowledgement cannot apply a second world after UDP fallback");
            Until(() => server.playerManager.Players.Count(p => p.status == PlayerStatus.Playing) == 2);
            if (tcp && !rejectTcp && !lateTcp)
            { Until(() => second.PauseCommands > 0); check(true, "commands issued while TCP world is in flight are delivered after snapshot confirmation"); }
            check(first.IsHost && !second.IsHost, "server grants authority to actual host rather than a client role flag");
            var generation = server.worldData.SnapshotGeneration;
            server.Enqueue(() => server.worldData.TryStartJoinPointCreation(true));
            Until(() => server.worldData.SnapshotGeneration > generation);
            check(!server.worldData.CreatingJoinPoint, "dedicated snapshot request completes through numbered owner upload over UDP");
            var transfers = second.WorldTransfers;
            second.RequestRejoin(); Until(() => second.WorldTransfers > transfers);
            check(second.WorldTransfers == transfers + 1, "friend reloads newest world over existing UDP connection without reconnecting server");
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
    public bool IsHost;
    public int WorldTransfers;
    public int TcpOffers;
    public int TcpDownloads;
    public int PauseCommands;
    private LiteNetConnection? connection;
    public void RequestRejoin() => connection!.Send(Packets.Client_RequestRejoin);
    public Probe(string name, string password, Checkpoint checkpoint, bool rejectTcp, bool lateTcp, Action? during)
    {
        var listener = new EventBasedNetListener();
        listener.PeerConnectedEvent += peer =>
        {
            connection = new LiteNetConnection(peer) { username = name, Lenient = true };
            connection.ChangeState(new ProbeState(connection, password, host => IsHost = host,
                () => { Authenticated = true; WorldTransfers++; }, checkpoint, rejectTcp, lateTcp,
                accepted => { TcpOffers++; if (accepted) TcpDownloads++; }, during, () => PauseCommands++));
        };
        listener.PeerDisconnectedEvent += (_, info) => { Reason = info.Reason; Disconnected = true; };
        listener.NetworkReceiveEvent += (_, reader, _, method) =>
        { connection!.HandleReceiveRaw(new ByteReader(reader.GetRemainingBytes()), method == DeliveryMethod.ReliableOrdered); };
        Manager = new NetManager(listener) { IPv6Enabled = false };
        Manager.MtuOverride = 1404;
    }
}

public class ProbeState(ConnectionBase connection, string password, Action<bool> authority, Action authenticated, Checkpoint checkpoint, bool rejectTcp, bool lateTcp, Action<bool> tcpResult, Action? during, Action pauseSeen) : AsyncConnectionState(connection)
{
    private bool host;
    private bool injected;
    [PacketHandler(Packets.Server_PlayerList)]
    public void Players(ByteReader packet) => packet.Seek(packet.Length);
    [PacketHandler(Packets.Server_TimeControl)]
    public void Time(ByteReader packet) => packet.Seek(packet.Length);
    [PacketHandler(Packets.Server_KeepAlive)]
    public void KeepAlive(ByteReader packet)
    {
        var id = packet.ReadInt32();
        connection.Send(new ClientKeepAlivePacket(id, 0, false, 100));
    }
    [PacketHandler(Packets.Server_Command)]
    public void Command(ByteReader reader)
    {
        var packet = new ServerCommandPacket(); packet.Bind(new PacketReader(reader));
        if (packet.type == CommandType.PauseAll) pauseSeen();
        if (packet.type != CommandType.CreateJoinPoint || !host) return;
        var id = new ByteReader(packet.data ?? throw new InvalidDataException("Missing joinpoint token")).ReadInt32();
        var writer = new ByteWriter(); writer.WriteInt32(id); writer.WriteInt32(checkpoint.Maps.Count);
        foreach (var map in checkpoint.Maps) { writer.WriteInt32(map.Key); writer.WritePrefixedBytes(map.Value); }
        writer.WritePrefixedBytes(checkpoint.World); writer.WritePrefixedBytes(checkpoint.Session);
        connection.SendFragmented(Packets.Client_WorldDataUpload, writer.ToArray());
    }
    [PacketHandler(Packets.Server_Chat)]
    public void Chat(ByteReader packet) => packet.Seek(packet.Length);
    [PacketHandler(Packets.Server_Notification)]
    public void Notification(ByteReader packet) => packet.Seek(packet.Length);
    protected override async Task RunState()
    {
        connection.Send(ClientProtocolPacket.Current()); await TypedPacket<ServerProtocolOkPacket>();
        connection.Send(new ClientUsernamePacket(connection.username!, password));
        if (connection.username == "Alpha")
        {
            await TypedPacket<ServerInitDataRequestPacket>();
            connection.SendFragmented(new ClientInitDataPacket { rwVersion = "1.6.4633", debugOnlySyncCmds = [], hostOnlySyncCmds = [],
                modCtorRoundMode = RoundModeEnum.ToNearest, staticCtorRoundMode = RoundModeEnum.ToNearest, defInfos = [], rawData = new byte[200_000] }.Serialize());
        }
        var packet = await Packet(Packets.Server_UsernameOk); packet.Seek(packet.Length);
        connection.Send(new ClientJoinDataPacket { modCtorRoundMode = RoundModeEnum.ToNearest,
            staticCtorRoundMode = RoundModeEnum.ToNearest, defInfos = [] });
        var join = await TypedPacket<ServerJoinDataPacket>(); host = join.isHost; authority(host);
        connection.Send(Packets.Client_WorldRequest);
        while (alive)
        {
            packet = await Packet(Packets.Server_WorldDataStart);
            var mode = packet.ReadByte();
            if (mode == 1)
            {
                var offer = new BulkOffer { Port = packet.ReadInt32(), Token = packet.ReadString(), Length = packet.ReadInt32(), Hash = packet.ReadString() };
                if (rejectTcp)
                { tcpResult(false); connection.Send(new ClientBulkResultPacket { token = offer.Token, accepted = false }); continue; }
                var direct = (LiteNetConnection)connection;
                byte[] bytes;
                try
                {
                    bytes = BulkDownloadServer.Download(new IPEndPoint(direct.peer.Address, offer.Port), offer, _ =>
                    { if (lateTcp) Thread.Sleep(120); if (!injected) { injected = true; during?.Invoke(); } });
                }
                catch (Exception error) when (error is IOException || error is System.Net.Sockets.SocketException || error is AggregateException)
                { tcpResult(false); connection.Send(new ClientBulkResultPacket { token = offer.Token, accepted = false }); continue; }
                if (bytes.Length != offer.Length) throw new Exception("TCP body mismatch");
                tcpResult(true); connection.Send(new ClientBulkResultPacket { token = offer.Token, accepted = true });
                continue;
            }
            if (mode == 2) packet.ReadString();
            else { packet = await Packet(Packets.Server_WorldData).Fragmented(); packet.Seek(packet.Length); }
            connection.Send(Packets.Client_WorldReady); authenticated();
        }
    }
}
