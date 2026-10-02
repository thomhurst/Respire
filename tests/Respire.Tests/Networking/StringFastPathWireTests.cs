using System.Text;
using System.Security.Cryptography;
using Respire.Commands;
using Respire.Internal;
using Respire.Networking;
using Respire.Protocol;
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
    public async Task GetStream_LargeBulkReplyStreamsAndPreservesBytes()
    {
        var payload = Enumerable.Range(0, 128 * 1024).Select(static index => (byte)(index % 251)).ToArray();
        var header = Encoding.ASCII.GetBytes($"${payload.Length}\r\n");
        var reply = new byte[header.Length + payload.Length + 2];
        header.CopyTo(reply, 0);
        payload.CopyTo(reply, header.Length);
        reply[^2] = (byte)'\r';
        reply[^1] = (byte)'\n';
        await using var server = new FakeRespServer(reply);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);

        await using var stream = await client.Strings.GetStreamAsync("large");
        await Assert.That(stream).IsNotNull();
        using var received = new MemoryStream();
        await stream!.CopyToAsync(received);

        await Assert.That(received.ToArray().SequenceEqual(payload)).IsTrue();
        await Assert.That(server.ReceivedCommands[0]).IsEqualTo("GET large");
    }

    [Test]
    public async Task GetStream_FiftyMegabytesArriveIncrementally()
    {
        const int totalBytes = 50 * 1024 * 1024;
        const int chunkSize = 512 * 1024;
        const int chunkCount = totalBytes / chunkSize;
        var chunk = new byte[chunkSize];
        for (var index = 0; index < chunk.Length; index++)
        {
            chunk[index] = (byte)(index % 251);
        }

        await using var server = new FakeRespServer("$-1\r\n"u8.ToArray())
        {
            SuppressReply = static command => command == "GET large"
        };
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var pending = client.Strings.GetStreamAsync("large").AsTask();
        using var commandTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (server.CommandsSeen == 0)
        {
            await Task.Delay(10, commandTimeout.Token);
        }

        await server.SendRawAsync(Encoding.ASCII.GetBytes($"${totalBytes}\r\n"));
        await using var stream = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(stream).IsNotNull();

        var readTask = Task.Run(async () =>
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[64 * 1024];
            var readTotal = 0;
            while (true)
            {
                var read = await stream!.ReadAsync(buffer);
                if (read == 0) break;
                readTotal += read;
                hash.AppendData(buffer, 0, read);
            }

            if (readTotal != totalBytes) throw new InvalidDataException($"Read {readTotal} of {totalBytes} bytes.");
            return hash.GetHashAndReset();
        });

        using var expectedHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        for (var index = 0; index < chunkCount; index++)
        {
            expectedHash.AppendData(chunk);
            await server.SendRawAsync(chunk);
        }
        await server.SendRawAsync("\r\n"u8.ToArray());

        var actualHash = await readTask.WaitAsync(TimeSpan.FromSeconds(30));
        await Assert.That(actualHash.SequenceEqual(expectedHash.GetHashAndReset())).IsTrue();
    }

    [Test]
    public async Task GetStream_MissingKeyReturnsNull()
    {
        await using var server = new FakeRespServer("$-1\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);

        await Assert.That(await client.Strings.GetStreamAsync("missing")).IsNull();
    }

    [Test]
    public async Task GetStream_Resp3NullReplyReturnsNull()
    {
        await using var server = new FakeRespServer("_\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);

        await Assert.That(await client.Strings.GetStreamAsync("missing")).IsNull();
    }

    [Test]
    public async Task GetStream_ErrorReplyThrowsServerException()
    {
        await using var server = new FakeRespServer("-ERR broken\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);

        var exception = await Assert.That(async () => await client.Strings.GetStreamAsync("key"))
            .ThrowsExactly<RespireServerException>();
        await Assert.That(exception!.CommandName).IsEqualTo("GET");
    }

    [Test]
    public async Task GetStream_LifetimeCancellationUnblocksPendingRead()
    {
        await using var server = new FakeRespServer("$-1\r\n"u8.ToArray())
        {
            SuppressReply = static command => command is "GET key" or "PING"
        };
        await using var connection = await RespireConnection.ConnectAsync("127.0.0.1", server.Port);
        using var lifetime = new CancellationTokenSource();
        var command = new Cmd1(Verbs.Get, "key");
        var pending = connection.SendBulkStreamAsync(in command, lifetime.Token, "GET");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (server.CommandsSeen == 0) await Task.Delay(10, timeout.Token);
        await server.SendRawAsync("$10\r\n"u8.ToArray());
        await using var stream = await pending.AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        var read = stream!.ReadAsync(new byte[16]).AsTask();
        await Assert.That(read.IsCompleted).IsFalse();

        lifetime.Cancel();
        await Assert.That(async () => await read.WaitAsync(TimeSpan.FromSeconds(5)))
            .Throws<OperationCanceledException>();

        await server.SendRawAsync("abcdefghij\r\n"u8.ToArray());
        var ping = connection.SendCheckedAsync(new Cmd(new Verb("PING")), commandName: "PING").AsTask();
        while (server.CommandsSeen < 2) await Task.Delay(10, timeout.Token);
        await server.SendRawAsync("+PONG\r\n"u8.ToArray());
        using var pong = await ping.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(pong.AsString()).IsEqualTo("PONG");
    }

    [Test]
    public async Task GetStream_CancellationOnRetiredConnectionPreservesOtherAcceptedReplies()
    {
        await using var server = new FakeRespServer(FakeRespServer.OkReply)
        {
            SuppressReply = static command => command is "GET key" or "PING",
        };
        await using var connection = await RespireConnection.ConnectAsync("127.0.0.1", server.Port);
        using var lifetime = new CancellationTokenSource();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var command = new Cmd1(Verbs.Get, "key");
        var pending = connection.SendBulkStreamAsync(in command, lifetime.Token, "GET");
        while (server.CommandsSeen == 0) await Task.Delay(10, timeout.Token);
        var connectionId = server.ReceivedConnectionIds[0];
        await server.SendRawAsync("$8\r\na"u8.ToArray(), connectionId);
        await using var stream = await pending.AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        var firstByte = new byte[1];
        await Assert.That(await stream!.ReadAsync(firstByte)).IsEqualTo(1);

        var ping = connection.SendCheckedAsync(new Cmd(new Verb("PING")), commandName: "PING").AsTask();
        while (server.CommandsSeen < 2) await Task.Delay(10, timeout.Token);
        var retirement = connection.RetireAsync();
        var pendingRead = stream.ReadAsync(new byte[8]).AsTask();
        lifetime.Cancel();
        await Assert.That(async () => await pendingRead.WaitAsync(TimeSpan.FromSeconds(5)))
            .Throws<OperationCanceledException>();
        await Task.Delay(50, timeout.Token);
        await Assert.That(server.PeerClosed.IsCompleted).IsFalse();

        await server.SendRawAsync("bcdefgh\r\n+PONG\r\n"u8.ToArray(), connectionId);
        using var pong = await ping.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(pong.AsString()).IsEqualTo("PONG");
        await retirement.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task GetStream_CancelledStreamDoesNotBlockLaterRetirement()
    {
        await using var server = new FakeRespServer(FakeRespServer.OkReply)
        {
            SuppressReply = static command => command == "GET key",
        };
        await using var connection = await RespireConnection.ConnectAsync("127.0.0.1", server.Port);
        using var lifetime = new CancellationTokenSource();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var command = new Cmd1(Verbs.Get, "key");
        var pending = connection.SendBulkStreamAsync(in command, lifetime.Token, "GET");
        while (server.CommandsSeen == 0) await Task.Delay(10, timeout.Token);
        var connectionId = server.ReceivedConnectionIds[0];
        await server.SendRawAsync("$8\r\na"u8.ToArray(), connectionId);
        await using var stream = await pending.AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        var firstByte = new byte[1];
        await Assert.That(await stream!.ReadAsync(firstByte)).IsEqualTo(1);
        var pendingRead = stream.ReadAsync(new byte[8]).AsTask();

        lifetime.Cancel();
        await Assert.That(async () => await pendingRead.WaitAsync(TimeSpan.FromSeconds(5)))
            .Throws<OperationCanceledException>();
        await Task.Delay(50, timeout.Token);
        await Assert.That(server.PeerClosed.IsCompleted).IsFalse();

        await connection.RetireAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await server.PeerClosed.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task GetStream_DisposingEarlyDrainsFrameAndPreservesNextReply()
    {
        var payload = new byte[256 * 1024];
        var header = Encoding.ASCII.GetBytes($"${payload.Length}\r\n");
        var reply = new byte[header.Length + payload.Length + 2];
        header.CopyTo(reply, 0);
        payload.CopyTo(reply, header.Length);
        reply[^2] = (byte)'\r';
        reply[^1] = (byte)'\n';
        await using var server = new FakeRespServer(reply, FakeRespServer.PongReply);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);

        var stream = await client.Strings.GetStreamAsync("large");
        await Assert.That(stream).IsNotNull();
        stream!.Dispose();

        await client.PingAsync();
    }

    [Test]
    public async Task GetStream_UnreadPayloadAppliesBackpressureToLaterReplies()
    {
        var payload = new byte[256 * 1024];
        var header = Encoding.ASCII.GetBytes($"${payload.Length}\r\n");
        var reply = new byte[header.Length + payload.Length + 2];
        header.CopyTo(reply, 0);
        payload.CopyTo(reply, header.Length);
        reply[^2] = (byte)'\r';
        reply[^1] = (byte)'\n';
        await using var server = new FakeRespServer(reply, FakeRespServer.PongReply)
        {
            MinimumCommandsBeforeReply = 2
        };
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);

        var get = client.Strings.GetStreamAsync("large").AsTask();
        var ping = client.PingAsync().AsTask();
        await using var stream = await get.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(50);
        await Assert.That(ping.IsCompleted).IsFalse();

        await stream!.CopyToAsync(Stream.Null);
        await ping.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task GetStream_AskPrefixAndBulkReplyInSameRead()
    {
        await using var server = new FakeRespServer("$-1\r\n"u8.ToArray())
        {
            SuppressReply = static command => command == "GET key",
            MinimumCommandsBeforeReply = 2
        };
        await using var connection = await RespireConnection.ConnectAsync(
            "127.0.0.1", server.Port,
            new RespireConnectionOptions { ResponseTimeout = TimeSpan.FromMilliseconds(100) });
        var command = new Cmd1(Verbs.Get, "key");
        var pending = ClusterRouter.SendAskingBulkStreamAsync(connection, in command, default, "GET");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (server.CommandsSeen < 2) await Task.Delay(10, timeout.Token);
        await server.SendRawAsync("+OK\r\n$5\r\nhello\r\n"u8.ToArray());

        await using var stream = await pending.AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        using var reader = new StreamReader(stream!);
        await Assert.That(await reader.ReadToEndAsync()).IsEqualTo("hello");
        await Task.Delay(250);
        await Assert.That(connection.IsConnected).IsTrue();
    }

    [Test]
    public async Task GetStream_PartialPayloadKeepsResponseWatchdogArmed()
    {
        await using var server = new FakeRespServer("$-1\r\n"u8.ToArray())
        {
            SuppressReply = static command => command == "GET key"
        };
        await using var connection = await RespireConnection.ConnectAsync(
            "127.0.0.1", server.Port,
            new RespireConnectionOptions { ResponseTimeout = TimeSpan.FromMilliseconds(100) });
        var command = new Cmd1(Verbs.Get, "key");
        var pending = connection.SendBulkStreamAsync(in command, commandName: "GET");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (server.CommandsSeen == 0) await Task.Delay(10, timeout.Token);
        await server.SendRawAsync("$10\r\n"u8.ToArray());

        await using var stream = await pending.AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await connection.Closed.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(connection.IsConnected).IsFalse();
        await Assert.That(stream).IsNotNull();
        await Assert.That(async () => await stream!.CopyToAsync(Stream.Null))
            .Throws<RespireConnectionException>();
    }

    [Test]
    public async Task GetStream_SlowConsumerDoesNotTripResponseWatchdog()
    {
        const int payloadLength = 256 * 1024;
        await using var server = new FakeRespServer("$-1\r\n"u8.ToArray())
        {
            SuppressReply = static command => command == "GET key"
        };
        await using var connection = await RespireConnection.ConnectAsync(
            "127.0.0.1", server.Port,
            new RespireConnectionOptions { ResponseTimeout = TimeSpan.FromMilliseconds(100) });
        var command = new Cmd1(Verbs.Get, "key");
        var pending = connection.SendBulkStreamAsync(in command, commandName: "GET");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (server.CommandsSeen == 0) await Task.Delay(10, timeout.Token);
        await server.SendRawAsync(Encoding.ASCII.GetBytes($"${payloadLength}\r\n"));
        await server.SendRawAsync(new byte[payloadLength]);
        await server.SendRawAsync("\r\n"u8.ToArray());
        await using var stream = await pending.AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        // The receive loop now waits on the full 64 KiB pipe, not on the server.
        await Task.Delay(500);
        await Assert.That(connection.IsConnected).IsTrue();

        var copy = new MemoryStream();
        await stream!.CopyToAsync(copy).WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(copy.Length).IsEqualTo(payloadLength);
        await Assert.That(connection.IsConnected).IsTrue();
    }

    [Test]
    public async Task GetStream_RetirementWaitsForActivePayload()
    {
        await using var server = new FakeRespServer("$-1\r\n"u8.ToArray())
        {
            SuppressReply = static command => command == "GET key"
        };
        await using var connection = await RespireConnection.ConnectAsync("127.0.0.1", server.Port);
        var command = new Cmd1(Verbs.Get, "key");
        var pending = connection.SendBulkStreamAsync(in command, commandName: "GET");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (server.CommandsSeen == 0) await Task.Delay(10, timeout.Token);
        await server.SendRawAsync("$10\r\nhello"u8.ToArray());
        await using var stream = await pending.AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        var retirement = connection.RetireAsync();
        await Task.Delay(100);
        await Assert.That(retirement.IsCompleted).IsFalse();

        await server.SendRawAsync("world\r\n"u8.ToArray());
        using var reader = new StreamReader(stream!);
        await Assert.That(await reader.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(5)))
            .IsEqualTo("helloworld");
        await retirement.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task GetStream_DisposeConnectionUnblocksUnreadPayloadBackpressure()
    {
        await using var server = new FakeRespServer("$-1\r\n"u8.ToArray())
        {
            SuppressReply = static command => command == "GET key"
        };
        await using var connection = await RespireConnection.ConnectAsync("127.0.0.1", server.Port);
        var command = new Cmd1(Verbs.Get, "key");
        var pending = connection.SendBulkStreamAsync(in command, commandName: "GET");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (server.CommandsSeen == 0) await Task.Delay(10, timeout.Token);
        await server.SendRawAsync("$262144\r\n"u8.ToArray());
        await server.SendRawAsync(new byte[256 * 1024]);
        await using var stream = await pending.AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(100);

        await connection.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        stream!.Dispose();
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
        // FIFO pairing for the next command. The health probe deliberately does not inherit
        // the first command's 100 ms deadline: under CI load that would test scheduler latency,
        // not whether the connection recovered. A generous cancellation guard still bounds it.
        await server.SendRawAsync("$5\r\nfirst\r\n"u8.ToArray());
        var connection = client.Core.Multiplexer.GetConnection();
        var command = new Cmd1(Verbs.Get, "second");
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var reply = await connection.SendAsync(
            in command, guard.Token, armCommandDeadline: false, commandName: "GET");
        await Assert.That(reply.AsString()).IsEqualTo("second");
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
            Protocol = RespProtocol.Resp2,
            Endpoints = { new RespireEndpoint("127.0.0.1", server.Port) },
            Connections = 1,
            CommandTimeout = commandTimeout
        });
}
