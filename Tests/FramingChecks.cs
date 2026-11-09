using Multiplayer.Common;
using Multiplayer.Common.Networking.Packet;

namespace RimTogether;

public static class FramingChecks
{
    public static void Run(Action<bool, string> check)
    {
        MpConnectionState.SetImplementation(ConnectionStateEnum.ServerSteam, typeof(FramingState));
        foreach (var limit in new[] { 1020, 1024, 1400 })
        {
            foreach (var size in new[] { 1019, 1020, 1023, 1024, 1163, 1393, 1399, 1400, 1401, 2796, 3000 })
            {
                var sender = new FramingConnection(limit); sender.ChangeState(ConnectionStateEnum.ServerSteam);
                var receiver = new FramingConnection(1400); receiver.ChangeState(ConnectionStateEnum.ServerSteam);
                var bytes = Enumerable.Range(0, size).Select(i => (byte)i).ToArray();
                sender.SendFragmented(Packets.Server_SyncInfo, bytes);
                if (sender.Frames.Any(frame => frame.Length > limit)) throw new Exception("Frame exceeds negotiated MTU");
                if (size + 1 <= limit && (sender.Frames.Count != 1 || (sender.Frames[0][0] & 0xc0) != 0))
                    throw new Exception($"Payload {size} fitting MTU {limit} unnecessarily carries fragmented framing");
                foreach (var frame in sender.Frames) receiver.HandleReceiveRaw(new ByteReader(frame), true);
                if (!((FramingState)receiver.StateObj!).Received!.SequenceEqual(bytes)) throw new Exception("Framing altered payload");
            }
            check(true, "sync packets cross MTU boundary without false single-fragment framing or payload changes: " + limit);
        }
    }
}

public class FramingConnection(int limit) : ConnectionBase
{
    public List<byte[]> Frames = [];
    protected override int FragmentPacketSize => limit;
    protected override int MaxReceivedFragmentSize => limit;
    protected override void SendRaw(byte[] raw, bool reliable = true) => Frames.Add(raw);
    protected override void OnClose(ServerDisconnectPacket? goodbye) { }
}

public class FramingState(ConnectionBase connection) : MpConnectionState(connection)
{
    public byte[]? Received;
    [PacketHandler(Packets.Server_SyncInfo, allowFragmented: true)]
    public void Sync(ByteReader packet) => Received = packet.ReadRaw(packet.Left);
}
