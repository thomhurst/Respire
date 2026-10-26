using System.Net.Sockets;
using FluentAssertions;
using TUnit.Core;
using static Respire.IntegrationTests.StandaloneCommandRetryIntegrationTests;

namespace Respire.IntegrationTests;

// Whole-unit retries against real Redis. Each case loses a reply after Redis executed the
// complete batch or MULTI/EXEC unit, then reads the stored counter with a separate client.
[ClassDataSource<RedisTestContainer>(Shared = SharedType.Keyed, Key = "standalone-unit-retry")]
[Category(TestCategories.ProtocolIndependent)]
[NotInParallel]
public class StandaloneUnitRetryIntegrationTests(RedisTestContainer fixture)
{
    [Test]
    [Arguments("batch", false)]
    [Arguments("batch", true)]
    [Arguments("transaction", false)]
    [Arguments("transaction", true)]
    public async Task LostUnitReplyPreservesRealRedisEffectAndExplicitRisk(string unit, bool allowDuplicate)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var options = RespireOptions.Parse(fixture.ConnectionString) with
        { Protocol = RespireProtocol.Resp2, ThreadPoolMonitoring = false, CommandTimeout = TimeSpan.FromSeconds(10) };
        await using var observer = await RespireClient.ConnectAsync(options, deadline.Token);
        var key = "unit-retry-effects:" + Guid.NewGuid().ToString("N");
        var fault = new LostReply(UnitVerb(unit))
        {
            // The first reply read may hold only part of the unit's replies. Keep the
            // connection open until Redis has applied the complete unit, then lose it.
            BeforeDrop = token => WaitForValueAsync(observer, key, "1", token),
        };
        await using var client = await RespireClient.ConnectAsync(options with
        {
            Connections = 1,
            RetryPolicy = Policy(allowDuplicate),
            TestingStreamFactory = async (host, port, token) =>
            {
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    await socket.ConnectAsync(host, port, token);
                    return new DropReplyStream(new NetworkStream(socket, ownsSocket: true), fault);
                }
                catch { socket.Dispose(); throw; }
            },
        }, deadline.Token);
        fault.Armed = true;

        var outcome = await RunUnitAsync(client, unit, key, deadline.Token);

        AssertOutcome(outcome, allowDuplicate);
        fault.Dropped.Should().Be(1);
        fault.Sends.Should().Be(allowDuplicate ? 2 : 1);
        (await observer.GetStringAsync(key, deadline.Token)).Should().Be(allowDuplicate ? "2" : "1");
        // The retried connection stays usable for independent commands.
        (await client.GetStringAsync(key, deadline.Token)).Should().Be(allowDuplicate ? "2" : "1");
        await observer.Keys.DeleteAsync(key);
    }

    // Default risk permits read-only replay only, so the executed INCR blocks a second effect.
    // Explicit accumulating risk resends the complete unit and permits a second effect.
    internal static RespireRetryPolicy Policy(bool allowDuplicate) => new()
    {
        InitialBackoff = TimeSpan.Zero,
        MaxCommandRetryCategory = allowDuplicate ? RespireCommandRetryCategory.WriteAccumulating : new RespireRetryPolicy().MaxCommandRetryCategory,
    };

    internal static string UnitVerb(string unit) => unit == "transaction" ? "EXEC" : "INCR";

    internal readonly record struct UnitOutcome(long? Increment, string? Read, Exception? Failure);

    // Both members target one key, so one slot owns the unit on Cluster too.
    internal static async Task<UnitOutcome> RunUnitAsync(IRespireClient client, string unit, string key, CancellationToken token)
    {
        if (unit == "transaction")
        {
            await using var transaction = client.CreateTransaction();
            var increment = transaction.Increment(key);
            var read = transaction.GetString(key);
            try { await transaction.CommitAsync(token); }
            catch (Exception error)
            {
                // Every member observes the same final failure as the commit.
                (await Capture(() => Task.FromResult(increment.Result))).Should().BeOfType(error.GetType());
                (await Capture(() => Task.FromResult(read.Result))).Should().BeOfType(error.GetType());
                return new(null, null, error);
            }
            return new(increment.Result, read.Result, null);
        }
        using var batch = client.CreateBatch();
        var incrementing = batch.Increment(key);
        var reading = batch.GetString(key);
        var result = await batch.TryExecuteAsync(token);
        if (result.FailureCount != 0)
        {
            result.FailureCount.Should().Be(2);
            incrementing.HasResult.Should().BeFalse();
            reading.HasResult.Should().BeFalse();
            return new(null, null, result.FirstError);
        }
        return new(incrementing.Result, reading.Result, null);
    }

    internal static void AssertOutcome(UnitOutcome outcome, bool allowDuplicate)
    {
        if (!allowDuplicate)
        {
            outcome.Failure.Should().BeAssignableTo<RespireConnectionException>();
            return;
        }
        outcome.Failure.Should().BeNull();
        // Results come from the final attempt only.
        outcome.Increment.Should().Be(2);
        outcome.Read.Should().Be("2");
    }

    internal static async Task WaitForValueAsync(IRespireClient observer, string key, string expected, CancellationToken token)
    {
        while (await observer.GetStringAsync(key, token) != expected) await Task.Delay(20, token);
    }

    private static async Task<Exception?> Capture(Func<Task> operation)
    {
        try { await operation(); return null; }
        catch (Exception error) { return error; }
    }
}
