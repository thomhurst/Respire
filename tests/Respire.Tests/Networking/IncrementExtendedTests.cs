using System.Text;
using Respire.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class IncrementExtendedTests
{
    [Test]
    [Arguments("immediate")]
    [Arguments("batch")]
    [Arguments("transaction")]
    public async Task OptionsAndBothResultsSurviveDeferredExecution(string mode)
    {
        byte[][] results = ["*2\r\n:9223372036854775807\r\n:2\r\n"u8.ToArray(), "*2\r\n$3\r\n1.5\r\n$4\r\n-0.5\r\n"u8.ToArray()];
        byte[][] replies = mode == "transaction"
            ? [FakeRespServer.OkReply, "+QUEUED\r\n"u8.ToArray(), "+QUEUED\r\n"u8.ToArray(), [.. "*2\r\n"u8, .. results.SelectMany(x => x)]] : results;
        await using var server = new FakeRespServer(replies);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var view = client.WithKeyPrefix("tenant:");
        byte[] key = "counter"u8.ToArray();
        var integer = new IntegerIncrementOptions { LowerBound = long.MinValue, UpperBound = long.MaxValue,
            Saturate = true, Expiry = TimeSpan.FromSeconds(5), ExpireOnlyWhenPersistent = true };
        var floating = new FloatIncrementOptions { LowerBound = -1.5, UpperBound = 2.5, Expiry = RespireExpiry.Persist };
        RespireIncrementResult<long> first;
        RespireIncrementResult<double> second;
        if (mode == "immediate")
        {
            first = await view.Strings.IncrementExtendedAsync(key, 3, integer);
            second = await view.Strings.IncrementExtendedAsync("float", -0.5, floating);
        }
        else
        {
            using var batch = mode == "batch" ? view.CreateBatch() : null;
            await using var transaction = mode == "transaction" ? view.CreateTransaction() : null;
            IRespireCommandQueue queue = transaction ?? (IRespireCommandQueue)batch!;
            var pendingFirst = queue.Strings.IncrementExtended(key, 3, integer);
            var pendingSecond = queue.Strings.IncrementExtended("float", -0.5, floating);
            Array.Fill(key, (byte)'x');
            if (transaction is not null) await transaction.CommitAsync();
            else await batch!.ExecuteAsync();
            first = pendingFirst.Result;
            second = pendingSecond.Result;
        }
        await Assert.That(first).IsEqualTo(new RespireIncrementResult<long>(long.MaxValue, 2));
        await Assert.That(second).IsEqualTo(new RespireIncrementResult<double>(1.5, -0.5));
        await Assert.That(server.ReceivedCommands.Where(command => command is not "MULTI" and not "EXEC"))
            .IsEquivalentTo(["INCREX tenant:counter BYINT 3 LBOUND -9223372036854775808 UBOUND 9223372036854775807 SATURATE PX 5000 ENX",
                "INCREX tenant:float BYFLOAT -0.5 LBOUND -1.5 UBOUND 2.5 PERSIST"]);
    }

    [Test]
    public async Task FloatingInfinityBoundsRepresentAnUnboundedInterval()
    {
        await using var server = new RespireFakeServer();
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions());
        var result = await client.Strings.IncrementExtendedAsync("float", 0.5,
            new() { LowerBound = double.NegativeInfinity, UpperBound = double.PositiveInfinity });
        await Assert.That(result).IsEqualTo(new RespireIncrementResult<double>(0.5, 0.5));
    }

    [Test]
    public async Task InvalidOptionsAndCancellationDoNotSendOrEnqueue()
    {
        await using var server = new FakeRespServer(FakeRespServer.OkReply);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        using var batch = client.CreateBatch();
        IntegerIncrementOptions[] invalid = [new() { LowerBound = 2, UpperBound = 1 },
            new() { Expiry = TimeSpan.Zero }, new() { Expiry = RespireExpiry.At(DateTimeOffset.UnixEpoch) },
            new() { ExpireOnlyWhenPersistent = true }, new() { Expiry = RespireExpiry.Persist, ExpireOnlyWhenPersistent = true },
            new() { Expiry = RespireExpiry.Keep, ExpireOnlyWhenPersistent = true }];
        foreach (var options in invalid)
        {
            await Assert.That(async () => await client.Strings.IncrementExtendedAsync("key", options: options)).Throws<ArgumentException>();
            await Assert.That(() => batch.Strings.IncrementExtended("key", options: options)).Throws<ArgumentException>();
        }
        foreach (var by in new[] { double.NaN, double.NegativeInfinity, double.PositiveInfinity })
        {
            await Assert.That(async () => await client.Strings.IncrementExtendedAsync("key", by)).Throws<ArgumentOutOfRangeException>();
            await Assert.That(() => batch.Strings.IncrementExtended("key", by)).Throws<ArgumentOutOfRangeException>();
        }
        await Assert.That(async () => await client.Strings.IncrementExtendedAsync("key", 0.5, new() { UpperBound = double.NaN }))
            .Throws<ArgumentException>();
        await Assert.That(async () => await client.Strings.IncrementExtendedAsync("key", cancellationToken: new(true)))
            .Throws<OperationCanceledException>();
        await Assert.That(async () => await client.Strings.IncrementExtendedAsync("key", 0.5, cancellationToken: new(true)))
            .Throws<OperationCanceledException>();
        await batch.ExecuteAsync();
        await Assert.That(server.ReceivedCommands).IsEmpty();
    }

    [Test]
    [Arguments("+OK\r\n", false)]
    [Arguments("*1\r\n:1\r\n", false)]
    [Arguments("*3\r\n:1\r\n:2\r\n:3\r\n", false)]
    [Arguments("*2\r\n$1\r\n1\r\n:1\r\n", false)]
    [Arguments("*2\r\n:1\r\n:1\r\n", true)]
    [Arguments("*2\r\n$3\r\nbad\r\n$1\r\n1\r\n", true)]
    [Arguments("*2\r\n$3\r\nnan\r\n$1\r\n1\r\n", true)]
    public async Task MalformedRepliesCannotBecomeSuccessfulZeroes(string reply, bool floating)
    {
        await using var server = new FakeRespServer(Encoding.ASCII.GetBytes(reply));
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        await Assert.That(async () =>
        {
            if (floating) await client.Strings.IncrementExtendedAsync("key", 0.5);
            else await client.Strings.IncrementExtendedAsync("key");
        }).Throws<RespireException>();
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task FakeIntegerBoundsOverflowExpiryAndContention(int protocol)
    {
        var clock = new RespireFakeClock(DateTimeOffset.FromUnixTimeSeconds(100));
        await using var server = new RespireFakeServer(clock);
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions() with { Protocol = (RespProtocol)protocol });
        await Assert.That(await client.Strings.IncrementExtendedAsync("missing", options: new() { UpperBound = 0 })).IsEqualTo(new RespireIncrementResult<long>(0, 0));
        await Assert.That(await client.ExistsAsync("missing")).IsFalse();
        await client.SetAsync("key", long.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture), TimeSpan.FromSeconds(5));
        await Assert.That(await client.Strings.IncrementExtendedAsync("key", options: new() { Expiry = RespireExpiry.Persist }))
            .IsEqualTo(new RespireIncrementResult<long>(long.MaxValue, 0));
        await Assert.That((await client.Keys.ExpiryAsync("key")).HasExpiry).IsTrue();
        await Assert.That(await client.Strings.IncrementExtendedAsync("key", -2)).IsEqualTo(new RespireIncrementResult<long>(long.MaxValue - 2, -2));
        await Assert.That(await client.Strings.IncrementExtendedAsync("key", 3, new() { Saturate = true, Expiry = RespireExpiry.Persist }))
            .IsEqualTo(new RespireIncrementResult<long>(long.MaxValue, 2));
        await Assert.That((await client.Keys.ExpiryAsync("key")).HasExpiry).IsFalse();
        await client.SetAsync("overflow", long.MinValue.ToString(System.Globalization.CultureInfo.InvariantCulture));
        await Assert.That(async () => await client.Strings.IncrementExtendedAsync("overflow", 0,
            new() { LowerBound = long.MaxValue, Saturate = true })).Throws<RespireServerException>();
        await Assert.That(await client.GetStringAsync("overflow")).IsEqualTo(long.MinValue.ToString(System.Globalization.CultureInfo.InvariantCulture));
        await client.Strings.IncrementExtendedAsync("ttl", options: new() { Expiry = TimeSpan.FromSeconds(5), ExpireOnlyWhenPersistent = true });
        clock.Advance(TimeSpan.FromSeconds(2));
        await client.Strings.IncrementExtendedAsync("ttl", options: new() { Expiry = TimeSpan.FromSeconds(5), ExpireOnlyWhenPersistent = true });
        await Assert.That((await client.Keys.ExpiryAsync("ttl")).TimeToLive).IsEqualTo(TimeSpan.FromSeconds(3));
        await client.Strings.IncrementExtendedAsync("ttl", options: new() { Expiry = RespireExpiry.At(DateTimeOffset.FromUnixTimeSeconds(110)) });
        await Assert.That((await client.Keys.ExpiryAsync("ttl")).TimeToLive).IsEqualTo(TimeSpan.FromSeconds(8));
        clock.Advance(TimeSpan.FromSeconds(8));
        await Assert.That(await client.ExistsAsync("ttl")).IsFalse();
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 30).Select(async _ =>
            await client.Strings.IncrementExtendedAsync("limit", options: new() { UpperBound = 7 })));
        await Assert.That(outcomes.Sum(result => result.AppliedIncrement)).IsEqualTo(7);
        await Assert.That(await client.GetStringAsync("limit")).IsEqualTo("7");
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task FakeFloatReportsActualClampDeltaAndRejectsInvalidInput(int protocol)
    {
        await using var server = new RespireFakeServer();
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions() with { Protocol = (RespProtocol)protocol });
        await Assert.That(await client.Strings.IncrementExtendedAsync("float", 2.5)).IsEqualTo(new RespireIncrementResult<double>(2.5, 2.5));
        await Assert.That(await client.Strings.IncrementExtendedAsync("float", -4.0, new() { LowerBound = 0.5 }))
            .IsEqualTo(new RespireIncrementResult<double>(2.5, 0));
        await Assert.That(await client.Strings.IncrementExtendedAsync("float", -4.0, new() { LowerBound = 0.5, Saturate = true }))
            .IsEqualTo(new RespireIncrementResult<double>(0.5, -2));
        await Assert.That(async () => await client.Strings.IncrementExtendedAsync("float")).Throws<RespireServerException>();
        await Assert.That(await client.GetStringAsync("float")).IsEqualTo("0.5");
        await Assert.That(async () => { using var invalid = await client.ExecuteAsync("INCREX", ["float", "BYINT", 1, "BYINT", 2]); })
            .Throws<RespireServerException>();
    }
}
