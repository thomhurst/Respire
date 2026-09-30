using Respire.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

public class FakeServerTests
{
    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task RealClientPreservesBinaryValuesLargeRepliesAndOwnership(int protocol)
    {
        await using var server = new RespireFakeServer();
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions() with { Protocol = (RespProtocol)protocol });
        byte[] key = [0, 255, 128, 13, 10];
        var value = Enumerable.Range(0, 150_000).Select(index => (byte)index).ToArray();
        using var stored = await client.ExecuteAsync(RespireCommands.String.SET, key, value);
        await Assert.That(stored.AsString()).IsEqualTo("OK");
        using var first = await client.ExecuteAsync(RespireCommands.String.GET, key);
        var owned = first.AsBytes();
        owned[0] = 99;
        using var second = await client.ExecuteAsync(RespireCommands.String.GET, key);
        await Assert.That(second.AsBytes()).IsEquivalentTo(value);
        first.Dispose();
        await Assert.That(() => first.AsBytes()).ThrowsExactly<ObjectDisposedException>();
        await Assert.That(second.AsBytes()).IsEquivalentTo(value);
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task ClientsShareAtomicStateAndSeparateServersStayIsolated(int protocol)
    {
        await using var server = new RespireFakeServer();
        await using var otherServer = new RespireFakeServer();
        var options = server.CreateOptions() with { Protocol = (RespProtocol)protocol, Connections = 2 };
        await using var first = await RespireClient.ConnectAsync(options);
        await using var second = await RespireClient.ConnectAsync(options);
        await using var isolated = await RespireClient.ConnectAsync(otherServer.CreateOptions());
        var calls = Enumerable.Range(0, 200).Select(async index =>
        {
            using var response = await (index % 2 == 0 ? first : second).ExecuteAsync(RespireCommands.String.INCR, "counter");
            return response.AsInteger();
        });
        await Assert.That((await Task.WhenAll(calls)).Order()).IsEquivalentTo(Enumerable.Range(1, 200).Select(value => (long)value));
        await Assert.That(await second.GetStringAsync("counter")).IsEqualTo("200");
        await Assert.That(await isolated.GetStringAsync("counter")).IsNull();
        await first.DisposeAsync();
        await using var replacement = await RespireClient.ConnectAsync(server.CreateOptions());
        await Assert.That(await replacement.GetStringAsync("counter")).IsEqualTo("200");
    }

    [Test]
    public async Task ExpiryUsesControlledClockAndRedisRoundingAndConditions()
    {
        var clock = new RespireFakeClock(DateTimeOffset.FromUnixTimeSeconds(100));
        await using var server = new RespireFakeServer(clock);
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions());
        using var set = await client.ExecuteAsync(RespireCommands.String.SET, "key", "value", "PX", 1500);
        await Assert.That(await Number(client, "PTTL", "key")).IsEqualTo(1500);
        await Assert.That(await Number(client, "TTL", "key")).IsEqualTo(2);
        await Assert.That(async () => { using var reply = await client.ExecuteAsync("PEXPIRE", "key", 9000, "GT", "LT"); })
            .Throws<RespireServerException>();
        await Assert.That(await Number(client, "PTTL", "key")).IsEqualTo(1500);
        await Assert.That(await Number(client, "PEXPIRE", "key", 2000, "NX")).IsEqualTo(0);
        await Assert.That(await Number(client, "PEXPIRE", "key", 2000, "XX", "GT")).IsEqualTo(1);
        await Assert.That(await Number(client, "PEXPIRE", "key", 3000, "LT")).IsEqualTo(0);
        clock.Advance(TimeSpan.FromMilliseconds(1999));
        await Assert.That(await client.GetStringAsync("key")).IsEqualTo("value");
        clock.Advance(TimeSpan.FromMilliseconds(1));
        await Assert.That(await client.GetStringAsync("key")).IsNull();
        await Assert.That(await Number(client, "TTL", "key")).IsEqualTo(-2);
        await client.SetAsync("key", "persistent");
        await Assert.That(await Number(client, "TTL", "key")).IsEqualTo(-1);
        await Assert.That(await Number(client, "PEXPIRE", "key", 1, "GT")).IsEqualTo(0);
        await Assert.That(await Number(client, "PEXPIRE", "key", 1, "LT")).IsEqualTo(1);
        await Assert.That(await Number(client, "PERSIST", "key")).IsEqualTo(1);
        clock.Advance(TimeSpan.FromDays(1));
        await Assert.That(await client.GetStringAsync("key")).IsEqualTo("persistent");
    }

    [Test]
    public async Task SetConditionsKeepTtlAndInvalidOptionsDoNotMutate()
    {
        var clock = new RespireFakeClock();
        await using var server = new RespireFakeServer(clock);
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions());
        using var created = await client.ExecuteAsync(RespireCommands.String.SET, "key", "old", "PX", 1000);
        using var rejected = await client.ExecuteAsync(RespireCommands.String.SET, "key", "no", "NX", "GET");
        await Assert.That(rejected.AsString()).IsEqualTo("old");
        using var replaced = await client.ExecuteAsync(RespireCommands.String.SET, "key", "new", "XX", "GET", "KEEPTTL");
        await Assert.That(replaced.AsString()).IsEqualTo("old");
        await Assert.That(await Number(client, "PTTL", "key")).IsEqualTo(1000);
        await Assert.That(async () => { using var reply = await client.ExecuteAsync(RespireCommands.String.SET, "key", "bad", "NX", "XX"); })
            .Throws<RespireServerException>();
        await Assert.That(await client.GetStringAsync("key")).IsEqualTo("new");
        await Assert.That(async () => { using var reply = await client.ExecuteAsync("SET", "key", "bad", "KEEPTTL", "EX", 10); })
            .Throws<RespireServerException>();
        await Assert.That(await client.GetStringAsync("key")).IsEqualTo("new");
        await Assert.That(await Number(client, "PTTL", "key")).IsEqualTo(1000);
        using var overwrite = await client.ExecuteAsync(RespireCommands.String.SET, "key", "persistent");
        await Assert.That(await Number(client, "TTL", "key")).IsEqualTo(-1);
    }

    [Test]
    public async Task MultiKeyCommandsAreAtomicAndMissingValuesStayNull()
    {
        await using var server = new RespireFakeServer();
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions());
        using var set = await client.ExecuteAsync(RespireCommands.String.MSET, "a", "1", "b", "2");
        await Assert.That(await Number(client, "MSETNX", "a", "changed", "c", "3")).IsEqualTo(0);
        using var read = await client.ExecuteAsync(RespireCommands.String.MGET, "a", "b", "c", "a");
        await Assert.That(read.Count).IsEqualTo(4);
        await Assert.That(read[0].AsString()).IsEqualTo("1");
        await Assert.That(read[1].AsString()).IsEqualTo("2");
        await Assert.That(read[2].IsNull).IsTrue();
        await Assert.That(read[3].AsString()).IsEqualTo("1");
        await Assert.That(await Number(client, "EXISTS", "a", "a", "missing")).IsEqualTo(2);
        await Assert.That(await Number(client, "DEL", "a", "a", "missing")).IsEqualTo(1);
    }

    [Test]
    public async Task UnsupportedCommandsFailWithoutDesynchronizingFollowingReplies()
    {
        await using var server = new RespireFakeServer();
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions() with { Connections = 1 });
        var unsupported = client.ExecuteAsync("NOT-A-COMMAND", "key").AsTask();
        var valid = client.SetAsync("after-error", "ok").AsTask();
        var error = await Assert.That(async () => { using var reply = await unsupported; }).Throws<RespireServerException>();
        await Assert.That(error!.Message).Contains("NOT-A-COMMAND");
        await Assert.That(await valid).IsTrue();
        await Assert.That(await client.GetStringAsync("after-error")).IsEqualTo("ok");
    }

    [Test]
    public async Task CancellationAndServerDisposalFailPromptly()
    {
        await using var server = new RespireFakeServer();
        var options = server.CreateOptions() with { Connections = 1, ConnectTimeout = TimeSpan.FromSeconds(1), CommandTimeout = TimeSpan.FromSeconds(1) };
        await using var client = await RespireClient.ConnectAsync(options);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.That(async () => await client.SetAsync("cancelled", "bad", cancellationToken: cancellation.Token)).Throws<OperationCanceledException>();
        // The documented client contract cancels the wait, never an accepted send.
        // A following read on this connection proves the cancelled reply drained in FIFO order.
        await Assert.That(await client.GetStringAsync("cancelled")).IsEqualTo("bad");
        await Task.WhenAll(server.DisposeAsync().AsTask(), server.DisposeAsync().AsTask()).WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(() => server.CreateOptions()).ThrowsExactly<ObjectDisposedException>();
        var error = await Assert.That(async () => await client.GetStringAsync("key").AsTask().WaitAsync(TimeSpan.FromSeconds(5))).Throws<Exception>();
        await Assert.That(error is ObjectDisposedException or RespireConnectionException).IsTrue();
    }

    [Test]
    public async Task TypedSerializationPrefixingAndBatchUseTheSameWireServer()
    {
        await using var server = new RespireFakeServer();
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions() with { Connections = 1 });
        var prefixed = client.WithKeyPrefix("tenant:");
        await prefixed.SetAsync("person", new Person("Ada", 37));
        await Assert.That(await client.GetAsync<Person>("tenant:person")).IsEqualTo(new Person("Ada", 37));
        using var batch = prefixed.CreateBatch();
        var set = batch.Strings.Set("batch", "value");
        var get = batch.Strings.GetString("batch");
        await batch.ExecuteAsync();
        await Assert.That(await set).IsTrue();
        await Assert.That(await get).IsEqualTo("value");
    }

    private static async Task<long> Number(RespireClient client, RespireCommand command, params RespireValue[] arguments)
    {
        using var result = await client.ExecuteAsync(command, arguments);
        return result.AsInteger();
    }

    [Test]
    public async Task IntegerErrorsAndOverflowPreserveValuesAndExpiry()
    {
        var clock = new RespireFakeClock();
        await using var server = new RespireFakeServer(clock);
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions());
        using var set = await client.ExecuteAsync(RespireCommands.String.SET, "number", long.MaxValue, "PX", 1000);
        await Assert.That(async () => await Number(client, "INCR", "number")).Throws<RespireServerException>();
        await Assert.That(await client.GetStringAsync("number")).IsEqualTo(long.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture));
        await Assert.That(await Number(client, "PTTL", "number")).IsEqualTo(1000);
        await client.SetAsync("number", -1);
        await Assert.That(await Number(client, "DECRBY", "number", long.MinValue)).IsEqualTo(long.MaxValue);
        await client.SetAsync("number", "01");
        await Assert.That(async () => await Number(client, "INCR", "number")).Throws<RespireServerException>();
        await Assert.That(await client.GetStringAsync("number")).IsEqualTo("01");
    }

    [Test]
    public async Task UnsupportedHandshakeFeaturesAndEndpointsNeverOpenSockets()
    {
        await using var server = new RespireFakeServer();
        var options = server.CreateOptions();
        foreach (var unsupported in new[]
        {
            options with { Protocol = RespProtocol.Resp3, Password = "secret" },
            options with { Database = 1 },
        })
            await Assert.That(async () => await RespireClient.ConnectAsync(unsupported)).Throws<RespireException>();
        await Assert.That(async () => await RespireClient.ConnectAsync(options with { UseTls = true })).Throws<NotSupportedException>();
        await Assert.That(async () => await RespireClient.ConnectAsync(options with { Endpoints = [new("localhost", 1)] })).Throws<NotSupportedException>();
        await using var working = await RespireClient.ConnectAsync(options with { ClientName = "test-client", Protocol = RespProtocol.Resp3 });
        await Assert.That(await working.SetAsync("after-failed-handshake", "ok")).IsTrue();
    }

    [Test]
    public async Task LazyClientsAndFragmentedFramesShareTheSameServerState()
    {
        await using var server = new RespireFakeServer();
        var options = server.CreateOptions();
        await using var client = RespireClient.Create(options);
        await Assert.That(client.IsConnected).IsFalse();
        // Use the internal seam to split one valid request across arbitrary byte boundaries.
        await using (var stream = await options.TestingStreamFactory!(options.Endpoints[0].Host, 6379, default))
        {
            foreach (var value in "*3\r\n$3\r\nSET\r\n$3\r\nkey\r\n$5\r\nvalue\r\n"u8.ToArray())
                await stream.WriteAsync(new byte[] { value });
            var response = new byte[5];
            await stream.ReadExactlyAsync(response).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.That(System.Text.Encoding.ASCII.GetString(response)).IsEqualTo("+OK\r\n");
        }
        await Assert.That(await client.GetStringAsync("key")).IsEqualTo("value");
    }

    public sealed record Person(string Name, int Age);

    [Test]
    [Arguments("invalid-operation")]
    [Arguments("io")]
    [Arguments("cancellation")]
    [Arguments("disposed")]
    public async Task UnexpectedServerFailuresAreObservedDuringCleanup(string kind)
    {
        Exception expected = kind switch
        {
            "io" => new IOException("clock failure"),
            "cancellation" => new OperationCanceledException("clock failure"),
            "disposed" => new ObjectDisposedException("clock", "clock failure"),
            _ => new InvalidOperationException("clock failure"),
        };
        var server = new RespireFakeServer(new ThrowingClock(expected));
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions() with { Protocol = RespProtocol.Resp2 });
        await Assert.That(async () => await client.SetAsync("key", "value")).Throws<RespireConnectionException>();
        var failure = await Assert.That(async () => await Task.Run(async () => await server.DisposeAsync())
            .WaitAsync(TimeSpan.FromSeconds(5))).Throws<Exception>();
        await Assert.That(failure).IsSameReferenceAs(expected);
    }

    private sealed class ThrowingClock(Exception error) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => throw error;
    }
}
