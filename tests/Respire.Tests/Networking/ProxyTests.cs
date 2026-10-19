using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class ProxyTests
{
    [Test]
    [Arguments("redis.internal", 3, false)]
    [Arguments("127.0.0.1", 1, false)]
    [Arguments("::1", 4, false)]
    [Arguments("redis.internal", 3, true)]
    [Arguments("bücher.example", 3, false)]
    public async Task SocksSendsDestinationAndPreservesTunnelBytes(string host, int addressType, bool authenticate)
    {
        await WithPeer(async (proxy, peer, token) =>
        {
            var connecting = RespireProxy.Socks5(proxy, authenticate ? "user" : null, authenticate ? "secret" : null)(new(host, 6380), token).AsTask();
            using var socket = await peer.AcceptSocketAsync(token);
            await using var stream = new NetworkStream(socket);
            await Assert.That(await Read(stream, 3, token)).IsEquivalentTo(new byte[] { 5, 1, authenticate ? (byte)2 : (byte)0 });
            await Fragment(stream, new byte[] { 5, authenticate ? (byte)2 : (byte)0 }, token);
            if (authenticate)
            {
                await Assert.That(await Read(stream, 13, token)).IsEquivalentTo(new byte[] { 1, 4, 117, 115, 101, 114, 6, 115, 101, 99, 114, 101, 116 });
                await Fragment(stream, new byte[] { 1, 0 }, token);
            }
            await Assert.That(await Read(stream, 4, token)).IsEquivalentTo(new byte[] { 5, 1, 0, (byte)addressType });
            var length = addressType == 3 ? (await Read(stream, 1, token))[0] : addressType == 1 ? 4 : 16;
            var address = await Read(stream, length, token);
            if (addressType == 3)
                await Assert.That(Encoding.ASCII.GetString(address)).IsEqualTo(host == "bücher.example" ? "xn--bcher-kva.example" : host);
            else
                await Assert.That(new IPAddress(address)).IsEqualTo(IPAddress.Parse(host));
            await Assert.That(await Read(stream, 2, token)).IsEquivalentTo(new byte[] { 24, 236 });
            // Exercise all three bound-address encodings, and coalesce the first tunnel data.
            byte[] bound = addressType == 3 ? [3, 1, (byte)'x'] : [(byte)addressType, .. new byte[length]];
            await Fragment(stream, new byte[] { 5, 0, 0 }, token);
            byte[] boundReply = [.. bound, 0, 0, 42];
            await stream.WriteAsync(boundReply, token);
            await using var tunnel = await connecting;
            await Assert.That(await Read(tunnel, 1, token)).IsEquivalentTo(new byte[] { 42 });
            await tunnel.WriteAsync(new byte[] { 43 }, token);
            await Assert.That(await Read(stream, 1, token)).IsEquivalentTo(new byte[] { 43 });
            await tunnel.DisposeAsync();
            await Assert.That(await stream.ReadAsync(new byte[1], token)).IsEqualTo(0);
        });
    }

    [Test]
    [Arguments("redis.internal", "redis.internal:6380", false)]
    [Arguments("127.0.0.1", "127.0.0.1:6380", false)]
    [Arguments("::1", "[::1]:6380", true)]
    public async Task HttpSendsAuthorityAndPreservesTunnelBytes(string host, string authority, bool authenticate)
    {
        await WithPeer(async (proxy, peer, token) =>
        {
            var connecting = RespireProxy.HttpConnect(proxy, authenticate ? "user" : null, authenticate ? "secret" : null)(new(host, 6380), token).AsTask();
            using var socket = await peer.AcceptSocketAsync(token);
            await using var stream = new NetworkStream(socket);
            var request = await ReadHeaders(stream, token);
            await Assert.That(request).IsEqualTo($"CONNECT {authority} HTTP/1.1\r\nHost: {authority}\r\n" +
                (authenticate ? "Proxy-Authorization: Basic dXNlcjpzZWNyZXQ=\r\n" : "") + "\r\n");
            await Fragment(stream, "HTTP/1.1 100 Continue\r\n\r\n"u8.ToArray(), token);
            await stream.WriteAsync("HTTP/1.0 201 Tunnel\r\nContent-Length: 100\r\n\r\n+PONG\r\n"u8.ToArray(), token);
            await using var tunnel = await connecting;
            await Assert.That(Encoding.ASCII.GetString(await Read(tunnel, 7, token))).IsEqualTo("+PONG\r\n");
            await tunnel.DisposeAsync();
            await Assert.That(await stream.ReadAsync(new byte[1], token)).IsEqualTo(0);
        });
    }

    [Test]
    [Arguments("HTTP/1.1 407 Authentication required\r\n\r\n", "407")]
    [Arguments("HTTP/1.1 403 Forbidden\r\n\r\n", "403")]
    [Arguments("HTTP/1.1 101 Switching Protocols\r\n\r\n", "101")]
    [Arguments("HTTP/1.1 2000 Bad\r\n\r\n", "status line")]
    [Arguments("HTTP/2 200 OK\r\n\r\n", "status line")]
    [Arguments("HTTP/1.1 200 OK\r\nBad Header: x\r\n\r\n", "header name")]
    [Arguments("HTTP/1.1 200 OK\r\nNoColon\r\n\r\n", "header")]
    [Arguments("HTTP/1.1 200 OK\r\nX: bad\nvalue\r\n\r\n", "field value")]
    [Arguments("oversized", "16 KiB")]
    [Arguments("informational", "16 KiB")]
    [Arguments("eof", "closed")]
    public async Task HttpRejectsInvalidResponsesAndClosesSocket(string response, string message)
    {
        await WithPeer(async (proxy, peer, token) =>
        {
            var connecting = RespireProxy.HttpConnect(proxy)(new("redis.internal"), token).AsTask();
            using var socket = await peer.AcceptSocketAsync(token);
            await using var stream = new NetworkStream(socket);
            await ReadHeaders(stream, token);
            if (response == "eof") socket.Shutdown(SocketShutdown.Send);
            else
            {
                var data = response switch
                {
                    "oversized" => "HTTP/1.1 200 OK\r\nX: " + new string('x', 16384),
                    "informational" => string.Concat(Enumerable.Repeat("HTTP/1.1 100 Continue\r\n\r\n", 800)),
                    _ => response,
                };
                await stream.WriteAsync(Encoding.ASCII.GetBytes(data), token);
            }
            var error = await Assert.That(async () => await connecting).Throws<IOException>();
            await Assert.That(error!.Message).Contains(message);
            await AssertClosed(stream, token);
        });
    }

    [Test]
    [Arguments("version")]
    [Arguments("method")]
    [Arguments("downgrade")]
    [Arguments("auth")]
    [Arguments("reply-version")]
    [Arguments("reserved")]
    [Arguments("rejected")]
    [Arguments("address-type")]
    [Arguments("empty-host")]
    [Arguments("truncated")]
    public async Task SocksRejectsInvalidResponsesAndClosesSocket(string failure)
    {
        await WithPeer(async (proxy, peer, token) =>
        {
            var auth = failure is "auth" or "downgrade";
            var connecting = RespireProxy.Socks5(proxy, auth ? "u" : null, auth ? "p" : null)(new("127.0.0.1"), token).AsTask();
            using var socket = await peer.AcceptSocketAsync(token);
            await using var stream = new NetworkStream(socket);
            await Read(stream, 3, token);
            await stream.WriteAsync(new byte[] { failure == "version" ? (byte)4 : (byte)5,
                failure == "method" ? (byte)255 : auth && failure != "downgrade" ? (byte)2 : (byte)0 }, token);
            if (failure == "auth")
            {
                await Read(stream, 5, token);
                await stream.WriteAsync(new byte[] { 1, 1 }, token);
            }
            else if (failure is not ("version" or "method" or "downgrade"))
            {
                await Read(stream, 10, token);
                byte[] response = failure switch
                {
                    "reply-version" => [4, 0, 0, 1], "reserved" => [5, 0, 1, 1],
                    "rejected" => [5, 5, 0, 1], "address-type" => [5, 0, 0, 2],
                    "empty-host" => [5, 0, 0, 3, 0], _ => [5, 0, 0, 4, 1],
                };
                await stream.WriteAsync(response, token);
                if (failure == "truncated") socket.Shutdown(SocketShutdown.Send);
            }
            await Assert.That(async () => await connecting).Throws<IOException>();
            await AssertClosed(stream, token);
        });
    }

    [Test]
    [Arguments(false, "method")]
    [Arguments(false, "auth")]
    [Arguments(false, "connect")]
    [Arguments(true, "headers")]
    public async Task CancellationClosesSocketDuringNegotiation(bool http, string stage)
    {
        await WithPeer(async (proxy, peer, token) =>
        {
            using var cancel = CancellationTokenSource.CreateLinkedTokenSource(token);
            var factory = http ? RespireProxy.HttpConnect(proxy) : RespireProxy.Socks5(proxy, "u", "p");
            var connecting = factory(new("127.0.0.1"), cancel.Token).AsTask();
            using var socket = await peer.AcceptSocketAsync(token);
            await using var stream = new NetworkStream(socket);
            if (http) await ReadHeaders(stream, token);
            else
            {
                await Read(stream, 3, token);
                if (stage != "method")
                {
                    await stream.WriteAsync(new byte[] { 5, 2 }, token);
                    await Read(stream, 5, token);
                    if (stage == "connect")
                    {
                        await stream.WriteAsync(new byte[] { 1, 0 }, token);
                        await Read(stream, 10, token);
                    }
                }
            }
            cancel.Cancel();
            await Assert.That(async () => await connecting).Throws<OperationCanceledException>();
            await AssertClosed(stream, token);
        });
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RespireConnectTimeoutCoversProxyNegotiation(bool http)
    {
        await WithPeer(async (proxy, peer, token) =>
        {
            var options = new RespireOptions
            {
                ConnectionFactory = http ? RespireProxy.HttpConnect(proxy) : RespireProxy.Socks5(proxy),
                ConnectTimeout = TimeSpan.FromSeconds(1),
            };
            var connecting = Respire.Networking.RespireConnection.ConnectAsync("redis.internal", 6379,
                options.ToConnectionOptions(), cancellationToken: token);
            using var socket = await peer.AcceptSocketAsync(token);
            await using var stream = new NetworkStream(socket);
            if (http) await ReadHeaders(stream, token);
            else await Read(stream, 3, token);
            await Assert.That(async () => await connecting).Throws<RespireTimeoutException>();
            await AssertClosed(stream, token);
        });
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RedisTlsStartsAfterProxyNegotiation(bool http)
    {
        using var certificate = TestTlsCertificate.Create("redis.internal");
        await WithPeer(async (proxy, peer, token) =>
        {
            var options = new RespireOptions
            {
                ConnectionFactory = http ? RespireProxy.HttpConnect(proxy) : RespireProxy.Socks5(proxy),
                UseTls = true,
                Protocol = RespireProtocol.Resp2, LibraryName = null,
                TlsOptions = new() { RemoteCertificateValidationCallback = (_, received, _, _) => received?.GetCertHashString() == certificate.GetCertHashString() },
            };
            var connecting = Respire.Networking.RespireConnection.ConnectAsync("redis.internal", 6379,
                options.ToConnectionOptions(), cancellationToken: token);
            using var socket = await peer.AcceptSocketAsync(token);
            await using var stream = new NetworkStream(socket);
            if (http)
            {
                await ReadHeaders(stream, token);
                await stream.WriteAsync("HTTP/1.1 200 OK\r\n\r\n"u8.ToArray(), token);
            }
            else
            {
                await Read(stream, 3, token);
                await stream.WriteAsync(new byte[] { 5, 0 }, token);
                await Read(stream, 4, token);
                var length = (await Read(stream, 1, token))[0];
                await Read(stream, length + 2, token);
                await stream.WriteAsync(new byte[] { 5, 0, 0, 1, 0, 0, 0, 0, 0, 0 }, token);
            }
            string? targetHost = null;
            await using var tls = new SslStream(stream, leaveInnerStreamOpen: true);
            await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificateSelectionCallback = (_, name) => { targetHost = name; return certificate; },
            }, token);
            await using var connection = await connecting;
            await Assert.That(targetHost).IsEqualTo("redis.internal");
        });
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RejectsInvalidDestinationsAndPreCanceledAttempts(bool http)
    {
        var factory = http ? RespireProxy.HttpConnect(new("localhost", 1)) : RespireProxy.Socks5(new("localhost", 1));
        foreach (var host in new[] { "bad\r\nInjected: x", "/tmp/redis.sock", "fe80::1%3" })
            await Assert.That(async () => await factory(new(host), default)).Throws<ArgumentException>();
        await Assert.That(async () => await factory(new("redis", 0), default)).Throws<ArgumentOutOfRangeException>();
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.That(async () => await factory(new("redis"), canceled.Token)).Throws<OperationCanceledException>();
    }

    [Test]
    public async Task RejectsInvalidCredentialsBeforeConnecting()
    {
        var proxy = new RespireEndpoint("localhost", 1080);
        await Assert.That(() => RespireProxy.Socks5(proxy, "user")).Throws<ArgumentException>();
        await Assert.That(() => RespireProxy.Socks5(proxy, "", "p")).Throws<ArgumentException>();
        await Assert.That(() => RespireProxy.Socks5(proxy, new string('é', 128), "p")).Throws<ArgumentException>();
        await Assert.That(() => RespireProxy.HttpConnect(proxy, password: "p")).Throws<ArgumentException>();
        await Assert.That(() => RespireProxy.HttpConnect(proxy, "u:s", "p")).Throws<ArgumentException>();
        await Assert.That(() => RespireProxy.HttpConnect(proxy, "u", "p\r\nx")).Throws<ArgumentException>();
        await Assert.That(() => RespireProxy.HttpConnect(proxy, "u", "é")).Throws<ArgumentException>();
    }

    private static async Task WithPeer(Func<RespireEndpoint, TcpListener, CancellationToken, Task> action)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        await action(new("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port), listener, deadline.Token);
    }

    private static async Task<byte[]> Read(Stream stream, int count, CancellationToken token)
    {
        var bytes = new byte[count];
        await stream.ReadExactlyAsync(bytes, token);
        return bytes;
    }

    private static async Task Fragment(Stream stream, byte[] bytes, CancellationToken token)
    {
        foreach (var b in bytes) await stream.WriteAsync(new byte[] { b }, token);
    }

    private static async Task<string> ReadHeaders(Stream stream, CancellationToken token)
    {
        var bytes = new List<byte>();
        while (bytes.Count < 16384)
        {
            bytes.Add((await Read(stream, 1, token))[0]);
            if (bytes.Count >= 4 && bytes.TakeLast(4).SequenceEqual("\r\n\r\n"u8.ToArray()))
                return Encoding.ASCII.GetString(bytes.ToArray());
        }
        throw new IOException("Test request exceeded header limit.");
    }

    private static async Task AssertClosed(Stream stream, CancellationToken token)
    {
        try { await Assert.That(await stream.ReadAsync(new byte[1], token)).IsEqualTo(0); }
        catch (IOException) { /* A close with unread inbound bytes can send TCP RST. */ }
    }
}
