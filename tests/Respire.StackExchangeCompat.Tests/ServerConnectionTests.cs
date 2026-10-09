using Respire.Tests.Networking;
using TUnit.Core;
using Assert = Xunit.Assert;

namespace Respire.StackExchangeCompat.Tests;

public class ServerConnectionTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task FailedServerConnectionReportsFalseAndClosedAdapterStillThrows(bool tls)
    {
        await using var server = new FakeRespServer(FakeRespServer.OkReply);
        if (!tls) await server.DisposeAsync();
        await using var connection = RespireConnectionMultiplexer.Create(new RespireOptions
        {
            Endpoints = [new("127.0.0.1", server.Port)],
            Protocol = RespProtocol.Resp2,
            Connections = 1,
            UseTls = tls,
        });
        var handle = connection.GetServer("127.0.0.1", server.Port);
        var connected = Task.Run(() => handle.IsConnected);
        if (tls)
        {
            await server.ConnectionAccepted.WaitAsync(TimeSpan.FromSeconds(10));
            await server.SendRawAsync("not a TLS record\r\n"u8.ToArray());
        }
        Assert.False(await connected.WaitAsync(TimeSpan.FromSeconds(10)));

        await connection.CloseAsync();
        Assert.Throws<ObjectDisposedException>(() => { _ = handle.IsConnected; });
    }
}
