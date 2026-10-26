using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class WatchedTransactionPoolTests
{
    [Test]
    [Arguments(4, 12)]
    [Arguments(8, 8)]
    public async Task ConcurrentBursts_RetainOnlyConfiguredIdleConnections(int maximumIdle, int expectedConnections)
    {
        await using var server = CreateServer();
        await using var client = await ConnectAsync(server.Port, new() { MaximumIdle = maximumIdle });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        for (var burst = 0; burst < 2; burst++)
        {
            var transactions = new List<RespireWatchedTransaction>();
            try
            {
                // Hold eight leases at once so a burst cannot reuse its own earlier returns.
                for (var index = 0; index < 8; index++)
                    transactions.Add(await client.CreateTransactionAsync([$"watched:{index}"], deadline.Token));

                foreach (var transaction in transactions)
                {
                    _ = transaction.Set("result", "value");
                    await Assert.That(await transaction.CommitAsync(deadline.Token)).IsTrue();
                }
            }
            finally
            {
                foreach (var transaction in transactions) await transaction.DisposeAsync();
            }
        }

        await Assert.That(WatchConnectionIds(server).Distinct().Count()).IsEqualTo(expectedConnections);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ExhaustedPool_AllowsMultiplexedReads_ThenReusesCompletedWatch(bool abort)
    {
        await using var server = CreateServer(abort);
        await using var client = await ConnectAsync(server.Port, new() { MaximumSize = 2, MaximumIdle = 2 });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var first = await client.CreateTransactionAsync(["first"], deadline.Token);
        await using var second = await client.CreateTransactionAsync(["second"], deadline.Token);
        var waiting = client.CreateTransactionAsync(["waiting"], deadline.Token).AsTask();

        await Assert.That(waiting.IsCompleted).IsFalse();
        await Assert.That(await client.GetStringAsync("ordinary", deadline.Token)).IsEqualTo("value");
        await Assert.That(waiting.IsCompleted).IsFalse();
        await Assert.That(WatchConnectionIds(server).Length).IsEqualTo(2);

        _ = first.Set("result", "value");
        await Assert.That(await first.CommitAsync(deadline.Token)).IsEqualTo(!abort);
        await using var third = await waiting.WaitAsync(deadline.Token);
        _ = third.Set("result", "value");
        await Assert.That(await third.CommitAsync(deadline.Token)).IsEqualTo(!abort);

        var watchIds = WatchConnectionIds(server);
        await Assert.That(watchIds.Length).IsEqualTo(3);
        await Assert.That(watchIds[2]).IsEqualTo(watchIds[0]);
        await Assert.That(watchIds.Distinct().Count()).IsEqualTo(2);
        var getIndex = server.ReceivedCommands.ToList().IndexOf("GET ordinary");
        await Assert.That(watchIds.Contains(server.ReceivedConnectionIds[getIndex])).IsFalse();
    }

    private static FakeRespServer CreateServer(bool abort = false)
        => new(17, FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => command switch
            {
                "EXEC" => abort ? "*-1\r\n"u8.ToArray() : "*1\r\n+OK\r\n"u8.ToArray(),
                "GET ordinary" => "$5\r\nvalue\r\n"u8.ToArray(),
                "SET result value" => "+QUEUED\r\n"u8.ToArray(),
                _ => FakeRespServer.OkReply,
            },
        };

    private static ValueTask<RespireClient> ConnectAsync(int port, RespireDedicatedPoolOptions pool)
    {
#pragma warning disable RESP010 // Test-owned client isolates dedicated pool capacity from multiplexed reads.
        return RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespireProtocol.Resp2,
            Connections = 1,
            Endpoints = [new("127.0.0.1", port)],
            DedicatedPool = pool,
        });
#pragma warning restore RESP010
    }

    private static int[] WatchConnectionIds(FakeRespServer server)
    {
        var commands = server.ReceivedCommands;
        var connectionIds = server.ReceivedConnectionIds;
        return commands.Select((command, index) => (command, ConnectionId: connectionIds[index]))
            .Where(item => item.command.StartsWith("WATCH ", StringComparison.Ordinal))
            .Select(item => item.ConnectionId)
            .ToArray();
    }
}
