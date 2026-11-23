using System.Text;
using Respire.Tests.Networking;
using StackExchange.Redis;
using TUnit.Core;
using Assert = Xunit.Assert;

namespace Respire.StackExchangeCompat.Tests;

public class ClusterDispatchTests
{
    private const string FirstKey = "{bar}:held";
    private const string SecondKey = "{foo}:shared";
    private const string IndependentKey = "{qux}:independent";
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(5);

    [Test]
    public async Task SplitCommandWaitsForEverySlotAndDoesNotBlockAnUnrelatedSlot()
    {
        await using var first = new FakeRespServer(":1\r\n"u8.ToArray());
        await using var second = new FakeRespServer(":1\r\n"u8.ToArray());
        await using var adapter = CreateAdapter(first, second);
        var database = adapter.GetDatabase();
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        first.GateCommand($"HSET {FirstKey} ", release.Task, () => received.TrySetResult());
        var write = database.HashSetAsync(FirstKey, "field", "value");

        try
        {
            await received.Task.WaitAsync(s_timeout);
            var split = database.KeyExistsAsync([FirstKey, SecondKey]);
            var later = database.HashSetAsync(SecondKey, "field", "next");

            Assert.True(await database.KeyExistsAsync(IndependentKey).WaitAsync(s_timeout));
            Assert.False(write.IsCompleted);
            Assert.False(split.IsCompleted);
            Assert.DoesNotContain($"EXISTS {SecondKey}", second.ReceivedCommands);
            Assert.DoesNotContain($"HSET {SecondKey} field next", second.ReceivedCommands);

            release.SetResult();
            Assert.True(await write.WaitAsync(s_timeout));
            Assert.Equal(2L, await split.WaitAsync(s_timeout));
            Assert.True(await later.WaitAsync(s_timeout));
            Assert.Equal(
                [$"EXISTS {IndependentKey}", $"EXISTS {SecondKey}", $"HSET {SecondKey} field next"],
                second.ReceivedCommands.Where(command => command.StartsWith("EXISTS ") || command.StartsWith("HSET ")));
        }
        finally
        {
            release.TrySetResult();
            await adapter.CloseAsync(allowCommandsToComplete: false);
        }
    }

    [Test]
    public async Task UnclassifiedScriptOrdersCommandsOnBothSides()
    {
        await using var first = new FakeRespServer(":1\r\n"u8.ToArray());
        await using var second = new FakeRespServer(":1\r\n"u8.ToArray());
        await using var adapter = CreateAdapter(first, second);
        var database = adapter.GetDatabase();
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        second.GateCommand($"HSET {SecondKey} ", release.Task, () => received.TrySetResult());
        var scriptReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseScript = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        first.GateCommand("EVAL ", releaseScript.Task, () => scriptReceived.TrySetResult());
        var laterReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        second.GateCommand($"EXISTS {SecondKey}", Task.CompletedTask, () => laterReceived.TrySetResult());
        var write = database.HashSetAsync(SecondKey, "field", "value");

        try
        {
            await received.Task.WaitAsync(s_timeout);
            var script = database.ScriptEvaluateAsync("return 1", [FirstKey]);
            var later = database.KeyExistsAsync(SecondKey);
            await Assert.ThrowsAsync<TimeoutException>(() => scriptReceived.Task.WaitAsync(TimeSpan.FromMilliseconds(250)));
            Assert.False(laterReceived.Task.IsCompleted);

            release.SetResult();
            Assert.True(await write.WaitAsync(s_timeout));
            await scriptReceived.Task.WaitAsync(s_timeout);
            await Assert.ThrowsAsync<TimeoutException>(() => laterReceived.Task.WaitAsync(TimeSpan.FromMilliseconds(250)));

            releaseScript.SetResult();
            Assert.Equal(1L, (long)await script.WaitAsync(s_timeout));
            Assert.True(await later.WaitAsync(s_timeout));
        }
        finally
        {
            release.TrySetResult();
            releaseScript.TrySetResult();
            await adapter.CloseAsync(allowCommandsToComplete: false);
        }
    }

    private static RespireConnectionMultiplexer CreateAdapter(FakeRespServer first, FakeRespServer second)
    {
        var topology = Encoding.ASCII.GetBytes(
            $"*2\r\n*3\r\n:0\r\n:8191\r\n*3\r\n$9\r\n127.0.0.1\r\n:{first.Port}\r\n$5\r\nfirst\r\n"
            + $"*3\r\n:8192\r\n:16383\r\n*3\r\n$9\r\n127.0.0.1\r\n:{second.Port}\r\n$6\r\nsecond\r\n");
        first.ReplyOverride = (_, command) => command == "CLUSTER SLOTS" ? topology : null;
        second.ReplyOverride = first.ReplyOverride;
        return RespireConnectionMultiplexer.Create(new RespireOptions
        {
            Endpoints = [new("127.0.0.1", first.Port)],
            Protocol = RespireProtocol.Resp2,
            UseCluster = true,
            ClusterTopologyRefreshInterval = null,
            SplitCrossSlotCommands = true,
            RetryPolicy = null,
            CommandTimeout = s_timeout * 4,
        });
    }
}
