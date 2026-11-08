using System.Net;
using Multiplayer.Common;

namespace RimTogether;

public static class BulkChecks
{
    public static void Run(Action<bool, string> check, Action<Action, string> throws)
    {
        using var server = new BulkDownloadServer([new IPEndPoint(IPAddress.Loopback, 0)], _ => { });
        var data = new byte[2_000_000]; new Random(123).NextBytes(data);
        var endpoint = new IPEndPoint(IPAddress.Loopback, server.Port);
        var offer = server.Offer(IPAddress.Loopback, data);
        var received = BulkDownloadServer.Download(endpoint, offer, _ => { });
        check(received.SequenceEqual(data), "TCP bulk transfer preserves every byte and validates SHA256");
        throws(() => BulkDownloadServer.Download(endpoint, offer, _ => { }), "TCP token cannot be reused");
        var wrongIp = server.Offer(IPAddress.Parse("127.0.0.2"), data);
        throws(() => BulkDownloadServer.Download(endpoint, wrongIp, _ => { }), "TCP snapshot token is bound to authenticated peer IP"); server.Revoke(wrongIp.Token);
        var revoked = server.Offer(IPAddress.Loopback, data); server.Revoke(revoked.Token);
        throws(() => BulkDownloadServer.Download(endpoint, revoked, _ => { }), "revoked TCP snapshot cannot be fetched");
        var badHash = server.Offer(IPAddress.Loopback, data); badHash.Hash = new string('0', 64);
        throws(() => BulkDownloadServer.Download(endpoint, badHash, _ => { }), "TCP body is rejected before use if SHA256 differs");
        var badSize = server.Offer(IPAddress.Loopback, data); badSize.Length++;
        throws(() => BulkDownloadServer.Download(endpoint, badSize, _ => { }), "TCP body with unexpected size is rejected");
    }
}
