using System.Text;
using TUnit.Core;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Respire.Tests.Networking;

/// <summary>
/// Wire tests for the direct bulk-string response path (GET-family commands returning
/// <c>string?</c>). Small fully buffered bulk replies decode straight from the receive buffer;
/// every other reply shape must fall back to the general RespValue conversion path with
/// identical results.
/// </summary>
public class StringFastPathWireTests
{
    [Test]
    public async Task Get_SmallBulkReply_ReturnsValue()
    {
        await using var server = new FakeRespServer("$5\r\nhello\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);

        var result = await client.GetStringAsync("key");

        await Assert.That(server.ReceivedCommands[0]).IsEqualTo("GET key");
        await Assert.That(result).IsEqualTo("hello");
    }

    [Test]
    public async Task Get_NullBulkReply_ReturnsNull()
    {
        await using var server = new FakeRespServer("$-1\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);

        var result = await client.GetStringAsync("missing");

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task Get_EmptyBulkReply_ReturnsEmptyString()
    {
        await using var server = new FakeRespServer("$0\r\n\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);

        var result = await client.GetStringAsync("empty");

        await Assert.That(result).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task Get_NonAsciiBulkReply_RoundTrips()
    {
        await using var server = new FakeRespServer("$12\r\ncaf\u00e9 \u20ac ok\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);

        var result = await client.GetStringAsync("key");

        await Assert.That(result).IsEqualTo("caf\u00e9 \u20ac ok");
    }

    [Test]
    public async Task Get_ErrorReply_ThrowsServerExceptionWithCommandName()
    {
        await using var server = new FakeRespServer("-ERR broken\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);

        var exception = await Assert.That(async () => await client.GetStringAsync("key"))
            .ThrowsExactly<RespireServerException>();
        await Assert.That(exception!.Message).Contains("ERR broken");
        await Assert.That(exception.CommandName).IsEqualTo("GET");
    }

    [Test]
    public async Task Get_SimpleStringReply_FallsBackToConversion()
    {
        await using var server = new FakeRespServer("+status\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);

        var result = await client.GetStringAsync("key");

        await Assert.That(result).IsEqualTo("status");
    }

    [Test]
    public async Task Get_VerbatimStringReply_FallsBackAndStripsPrefix()
    {
        await using var server = new FakeRespServer("=9\r\ntxt:hello\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);

        var result = await client.GetStringAsync("key");

        await Assert.That(result).IsEqualTo("hello");
    }

    [Test]
    public async Task Get_LargeBulkReply_DirectFillFallbackRoundTrips()
    {
        // Above the 4 KB direct-fill threshold: must take the pooled RespValue path.
        var payload = new string('y', 64 * 1024);
        var reply = Encoding.UTF8.GetBytes($"${payload.Length}\r\n{payload}\r\n");
        await using var server = new FakeRespServer(reply);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);

        var result = await client.GetStringAsync("big");

        await Assert.That(result).IsEqualTo(payload);
    }

    [Test]
    public async Task Get_FragmentedBulkReply_FallsBackAndRoundTrips()
    {
        await using var server = new FakeRespServer("$-1\r\n"u8.ToArray());
        server.SuppressReply = command => command.StartsWith("GET");
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);

        var pending = client.GetStringAsync("key");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (server.CommandsSeen == 0)
        {
            await Task.Delay(10, timeout.Token);
        }

        // Split mid-payload so the client's first receive holds an incomplete frame.
        await server.SendRawAsync("$10\r\nhellow"u8.ToArray());
        await Task.Delay(50);
        await server.SendRawAsync("orld\r\n"u8.ToArray());

        await Assert.That(await pending).IsEqualTo("helloworld");
    }

    [Test]
    public async Task Get_PipelinedReplies_PairInOrder()
    {
        await using var server = new FakeRespServer(
            "$5\r\nfirst\r\n"u8.ToArray(),
            "$-1\r\n"u8.ToArray(),
            "$5\r\nthird\r\n"u8.ToArray())
        {
            MinimumCommandsBeforeReply = 3,
        };
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);

        var first = client.GetStringAsync("a");
        var second = client.GetStringAsync("b");
        var third = client.GetStringAsync("c");

        await Assert.That(await first).IsEqualTo("first");
        await Assert.That(await second).IsNull();
        await Assert.That(await third).IsEqualTo("third");
    }

    [Test]
    public async Task Get_CancelledCommand_ReplyConsumedAndConnectionStaysUsable()
    {
        // Scripted replies serve only unsuppressed commands: the cancelled GET's late reply is
        // injected raw, so "GET second" consumes the first scripted slot.
        await using var server = new FakeRespServer("$6\r\nsecond\r\n"u8.ToArray());
        server.SuppressReply = command => command == "GET first";
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        using var cancellation = new CancellationTokenSource();
        var first = client.GetStringAsync("first", cancellation.Token);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (server.CommandsSeen == 0)
        {
            await Task.Delay(10, timeout.Token);
        }

        cancellation.Cancel();
        await Assert.That(async () => await first)
            .ThrowsExactly<OperationCanceledException>();

        // The cancelled command's reply arrives late and must be drained via the fast path
        // without corrupting FIFO pairing for the next command.
        await server.SendRawAsync("$5\r\nfirst\r\n"u8.ToArray());
        await Assert.That(await client.GetStringAsync("second")).IsEqualTo("second");
    }

    [Test]
    public async Task Get_CommandTimeout_ThrowsRespireTimeoutException()
    {
        await using var server = new FakeRespServer("$5\r\nhello\r\n"u8.ToArray())
        {
            SuppressReply = static command => command == "GET key"
        };
        await using var client = await ConnectClientAsync(server, TimeSpan.FromMilliseconds(100));

        await Assert.That(async () => await client.GetStringAsync("key"))
            .ThrowsExactly<RespireTimeoutException>();
    }

    [Test]
    public async Task Ping_CommandTimeout_ThrowsRespireTimeoutException()
    {
        await using var server = new FakeRespServer(FakeRespServer.PongReply)
        {
            SuppressReply = static command => command == "PING"
        };
        await using var client = await ConnectClientAsync(server, TimeSpan.FromMilliseconds(100));

        await Assert.That(async () => await client.PingAsync())
            .ThrowsExactly<RespireTimeoutException>();
    }

    [Test]
    public async Task Get_TimedOutCommand_ReplyConsumedAndConnectionStaysUsable()
    {
        // Scripted replies serve only unsuppressed commands: the timed-out GET's late reply is
        // injected raw, so "GET second" consumes the first scripted slot.
        await using var server = new FakeRespServer("$6\r\nsecond\r\n"u8.ToArray());
        server.SuppressReply = command => command == "GET first";
        await using var client = await ConnectClientAsync(server, TimeSpan.FromMilliseconds(100));

        var exception = await Assert.That(async () => await client.GetStringAsync("first"))
            .ThrowsExactly<RespireTimeoutException>();
        await Assert.That(exception!.CommandName).IsEqualTo("GET");

        // The timed-out command's reply arrives late and must be drained without corrupting
        // FIFO pairing for the next command.
        await server.SendRawAsync("$5\r\nfirst\r\n"u8.ToArray());
        await Assert.That(await client.GetStringAsync("second")).IsEqualTo("second");
    }

    [Test]
    public async Task BlockingPop_IsExemptFromCommandTimeout()
    {
        // BLPOP travels over the dedicated blocking pool (the server's second connection) and
        // must never be expired by the command deadline sweep, no matter how long it blocks.
        await using var server = new FakeRespServer(2, "$-1\r\n"u8.ToArray());
        server.SuppressReply = command => command.StartsWith("BLPOP", StringComparison.Ordinal);
        var client = await ConnectClientAsync(server, TimeSpan.FromMilliseconds(100));

        var pop = client.Lists.LeftPopAsync("key", waitFor: TimeSpan.FromSeconds(5));

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!server.ReceivedCommands.Any(static c => c.StartsWith("BLPOP", StringComparison.Ordinal)))
        {
            await Task.Delay(10, timeout.Token);
        }

        await Task.Delay(400);
        await Assert.That(pop.IsCompleted).IsFalse();

        // Tearing the client down fails the still-blocked wait with a connection error, never
        // a timeout.
        await client.DisposeAsync();
        await Assert.That(async () => await pop).Throws<RespireConnectionException>();
    }

    [Test]
    public async Task Get_CallerCancellation_RemainsOperationCanceledException()
    {
        await using var server = new FakeRespServer("$5\r\nhello\r\n"u8.ToArray())
        {
            SuppressReply = static command => command == "GET key"
        };
        await using var client = await ConnectClientAsync(server, TimeSpan.FromSeconds(10));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.That(async () => await client.GetStringAsync("key", cancellation.Token))
            .ThrowsExactly<OperationCanceledException>();
    }

    [Test]
    public async Task HashGet_SmallBulkReply_UsesSamePath()
    {
        await using var server = new FakeRespServer("$5\r\nvalue\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);

        var result = await client.Hashes.GetStringAsync("key", "field");

        await Assert.That(server.ReceivedCommands[0]).IsEqualTo("HGET key field");
        await Assert.That(result).IsEqualTo("value");
    }

    private static ValueTask<RespireClient> ConnectClientAsync(
        FakeRespServer server,
        TimeSpan commandTimeout)
        => RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = { new RespireEndpoint("127.0.0.1", server.Port) },
            Connections = 1,
            CommandTimeout = commandTimeout
        });
}
