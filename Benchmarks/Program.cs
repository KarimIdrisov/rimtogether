using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using LiteNetLib;
using Multiplayer.Common;
using RimTogether;

if (args.Length < 1) { Console.WriteLine("Usage: Benchmarks <MP-save.zip> [output.json]"); return 2; }
var load = Stopwatch.StartNew(); var checkpoint = Checkpoint.FromReplay(args[0]);
var saveMs = load.Elapsed.TotalMilliseconds;
var writer = new ByteWriter(); writer.WriteInt32(checkpoint.HostFaction); writer.WriteInt32(checkpoint.Tick); writer.WriteInt32(0); writer.WriteBool(false);
writer.WritePrefixedBytes(checkpoint.World); writer.WritePrefixedBytes(checkpoint.Session); writer.WriteInt32(0); writer.WriteInt32(checkpoint.Maps.Count);
foreach (var map in checkpoint.Maps) { writer.WriteInt32(map.Key); writer.WritePrefixedBytes(map.Value); }
writer.WriteInt32(0); var payload = writer.ToArray();
var results = new List<object>();
foreach (var rtt in new[] { 0, 50, 100 })
foreach (var chunk in new[] { 1024, 1020, 1400 })
{
    var result = await Transfer.Run(payload, rtt, 15, chunk);
    results.Add(result); Console.WriteLine(JsonSerializer.Serialize(result));
}
using (var tcp = new BulkDownloadServer([new IPEndPoint(IPAddress.Loopback, 0)], _ => { }))
{
    for (var i = 0; i < 3; i++)
    {
        var offer = tcp.Offer(IPAddress.Loopback, payload); var watch = Stopwatch.StartNew();
        var bytes = BulkDownloadServer.Download(new IPEndPoint(IPAddress.Loopback, tcp.Port), offer, _ => { }); watch.Stop();
        if (!bytes.SequenceEqual(payload)) throw new Exception("TCP benchmark integrity failed");
        var result = new { Transport = "TCP loopback (not WAN)", Bytes = bytes.Length, ElapsedMs = watch.Elapsed.TotalMilliseconds,
            MBps = bytes.Length / watch.Elapsed.TotalSeconds / 1_000_000 };
        results.Add(result); Console.WriteLine(JsonSerializer.Serialize(result));
    }
}
var report = new { PayloadBytes = payload.Length, ReadAndCompressMs = saveMs, Results = results };
if (args.Length > 1) Storage.Write(args[1], report);
return 0;

static class Transfer
{
    public static async Task<object> Run(byte[] payload, int rtt, int update, int chunk)
    {
        var serverListener = new EventBasedNetListener(); var clientListener = new EventBasedNetListener();
        var server = new NetManager(serverListener) { IPv6Enabled = false, EnableStatistics = true, UpdateTime = update };
        var client = new NetManager(clientListener) { IPv6Enabled = false, EnableStatistics = true, UpdateTime = update };
        if (chunk > 1024) server.MtuOverride = client.MtuOverride = 1404; // Controlled stand-in for successfully discovered MTU.
        var done = false; byte[]? received = null; var sent = false; var watch = new Stopwatch(); NetPeer? connected = null;
        var chunks = new List<byte[]>();
        serverListener.ConnectionRequestEvent += req => req.AcceptIfKey("local-benchmark");
        clientListener.PeerConnectedEvent += _ => { };
        serverListener.PeerConnectedEvent += peer => connected = peer;
        clientListener.NetworkReceiveEvent += (_, reader, _, _) =>
        {
            var data = reader.GetRemainingBytes();
            // Reproduce the application's ordered 1024-byte framing; verify complete payload at the end.
            var header = data[0]; var state = header & 0xc0;
            if (state == 0x40 && !sent) throw new InvalidOperationException("Unexpected data");
            var offset = received == null ? 8 : 2;
            chunks.Add(data.AsSpan(offset).ToArray());
            if (received == null) received = [];
            if (state == 0x80) { received = chunks.SelectMany(c => c).ToArray(); done = true; }
        };
        server.Start(IPAddress.Loopback, IPAddress.IPv6Any, 0); client.Start(IPAddress.Loopback, IPAddress.IPv6Any, 0);
        await using var proxy = new DelayProxy(new IPEndPoint(IPAddress.Loopback, server.LocalPort), rtt / 2);
        client.Connect("127.0.0.1", proxy.Port, "local-benchmark");
        var deadline = Stopwatch.StartNew();
        try
        {
            while (!done && deadline.Elapsed < TimeSpan.FromSeconds(90))
            {
                server.PollEvents(); client.PollEvents();
                if (connected != null && !sent)
                {
                    sent = true; watch.Start();
                    var max = chunk - 2; var parts = (payload.Length + 6 + max - 1) / max; var read = 0;
                    while (read < payload.Length)
                    {
                        var len = Math.Min(read == 0 ? max - 6 : max, payload.Length - read);
                        var bytes = new ByteWriter(); bytes.WriteByte((byte)((read + len == payload.Length ? 0x80 : 0x40) | 1)); bytes.WriteByte(0);
                        if (read == 0) { bytes.WriteUShort((ushort)parts); bytes.WriteUInt32((uint)payload.Length); }
                        bytes.WriteRaw(payload.AsSpan(read, len).ToArray());
                        connected.Send(bytes.ToArray(), DeliveryMethod.ReliableOrdered); read += len;
                    }
                }
                await Task.Delay(1);
            }
            watch.Stop();
            if (!done || received == null || !SHA256.HashData(payload).SequenceEqual(SHA256.HashData(received))) throw new Exception("Transfer failed integrity/deadline");
            return new { RttMs = rtt, UpdateMs = update, ChunkBytes = chunk, ActualPingMs = connected!.Ping, Bytes = payload.Length, ElapsedMs = Math.Round(watch.Elapsed.TotalMilliseconds, 1),
                MBps = Math.Round(payload.Length / watch.Elapsed.TotalSeconds / 1_000_000, 2),
                SentDatagrams = server.Statistics.PacketsSent, Lost = server.Statistics.PacketLoss };
        }
        finally { server.Stop(); client.Stop(); }
    }
}

sealed class DelayProxy : IAsyncDisposable
{
    private readonly UdpClient socket = new(new IPEndPoint(IPAddress.Loopback, 0));
    private readonly CancellationTokenSource stop = new();
    private readonly Task pump;
    private readonly List<Task> pending = [];
    public int Port => ((IPEndPoint)socket.Client.LocalEndPoint!).Port;
    public DelayProxy(IPEndPoint server, int delay)
    {
        socket.Client.ReceiveBufferSize = 1024 * 1024;
        socket.Client.SendBufferSize = 1024 * 1024;
        if (OperatingSystem.IsWindows()) socket.Client.IOControl((IOControlCode)(-1744830452), new byte[4], null);
        pump = Task.Run(async () =>
        {
            IPEndPoint? client = null;
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    var packet = await socket.ReceiveAsync(stop.Token);
                    var fromServer = packet.RemoteEndPoint.Port == server.Port;
                    if (!fromServer) client = packet.RemoteEndPoint;
                    var target = fromServer ? client : server;
                    if (target == null) continue;
                    pending.Add(Forward(packet.Buffer, target));
                }
            }
            catch (OperationCanceledException) { }
            catch (SocketException) when (stop.IsCancellationRequested) { }
            async Task Forward(byte[] buffer, IPEndPoint target)
            {
                try { if (delay != 0) await Task.Delay(delay, stop.Token); await socket.SendAsync(buffer, target, stop.Token); }
                catch (OperationCanceledException) { }
                catch (SocketException) when (stop.IsCancellationRequested) { }
            }
        });
    }
    public async ValueTask DisposeAsync() { stop.Cancel(); await pump; await Task.WhenAll(pending); socket.Dispose(); stop.Dispose(); }
}
