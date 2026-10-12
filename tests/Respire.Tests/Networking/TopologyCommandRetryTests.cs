using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Text;
using Respire.Commands;
using Respire.Internal;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

[NotInParallel]
public class TopologyCommandRetryTests
{
    [Test]
    [Arguments("raw", false)]
    [Arguments("raw", true)]
    [Arguments("script", false)]
    [Arguments("script", true)]
    [Arguments("function", false)]
    [Arguments("function", true)]
    public async Task ExplicitRequestsNeedCallerCategoryEvenWhenReadOnly(string shape, bool declared)
    {
        var effects = 0;
        await using var server = Server();
        server.ReplyOverride = (_, command) => command.StartsWith("GET ", StringComparison.Ordinal)
            || command.StartsWith("EVAL", StringComparison.Ordinal) || command.StartsWith("FCALL", StringComparison.Ordinal)
            ? Interlocked.Increment(ref effects) == 1 ? Error("TRYAGAIN") : ":7\r\n"u8.ToArray() : Setup(command);
        await using var client = await RespireClient.ConnectAsync(Options(server));
        async Task Call()
        {
            if (shape == "raw")
            {
                RespireCommand command = "GET";
                if (declared) command = command.WithRetryCategory(RespireCommandRetryCategory.ReadOnly);
                using var result = await client.ExecuteAsync(command, ["key"]);
                await Assert.That(result.AsInteger()).IsEqualTo(7);
            }
            else if (shape == "script")
            {
                var script = RespireScript.Create("return 7", readOnly: true);
                if (declared) script = script.WithRetryCategory(RespireCommandRetryCategory.ReadOnly);
                await Assert.That(await client.Scripts.ExecuteIntegerAsync(script, ["key"])).IsEqualTo(7);
            }
            else
            {
                var function = RespireFunction.Create("read", readOnly: true);
                if (declared) function = function.WithRetryCategory(RespireCommandRetryCategory.ReadOnly);
                await Assert.That(await client.Functions.ExecuteIntegerAsync(function, ["key"])).IsEqualTo(7);
            }
        }
        if (declared) await Call();
        else await Assert.That(Call).ThrowsExactly<RespireServerException>();
        await Assert.That(effects).IsEqualTo(declared ? 2 : 1);
    }

    [Test]
    public async Task ScriptCorrectionAndRetriesKeepStablePayloadAndOneBudget()
    {
        var effects = 0;
        var bytes = "old"u8.ToArray();
        await using var server = Server();
        server.ReplyOverride = (_, command) =>
        {
            if (command.StartsWith("EVALSHA", StringComparison.Ordinal))
            {
                "new"u8.CopyTo(bytes);
                return Error("NOSCRIPT");
            }
            if (!command.StartsWith("EVAL ", StringComparison.Ordinal)) return Setup(command);
            return Interlocked.Increment(ref effects) == 1 ? Error("TRYAGAIN") : ":7\r\n"u8.ToArray();
        };
        await using var client = await RespireClient.ConnectAsync(Options(server));
        var script = RespireScript.Create("return 7").WithRetryCategory(RespireCommandRetryCategory.ReadOnly);
        await Assert.That(await client.Scripts.ExecuteIntegerAsync(script, ["key"], [bytes])).IsEqualTo(7);
        await Assert.That(server.ReceivedCommands.Count(command => command == "EVAL return 7 1 key old")).IsEqualTo(2);
        await Assert.That(effects).IsEqualTo(2);
    }

    [Test]
    [Arguments("MOVED")]
    [Arguments("ASK")]
    public async Task ClusterRedirectOwnerComposesWithTransientRetry(string redirect)
    {
        var reads = 0;
        await using var target = Server();
        target.ReplyOverride = (_, command) => command == "GET {key}"
            ? Interlocked.Increment(ref reads) == 1 ? Error("TRYAGAIN") : "$5\r\nvalue\r\n"u8.ToArray() : Setup(command);
        await using var source = Server();
        source.ReplyOverride = (_, command) => command == "GET {key}"
            ? Encoding.ASCII.GetBytes($"-{redirect} {ClusterHash.GetSlot("key")} 127.0.0.1:{target.Port}\r\n") : Setup(command);
        await using var seed = new FakeRespServer(Topology(source.Port));
        await using var client = await RespireClient.ConnectAsync(Options(seed) with { UseCluster = true });
        await Assert.That(await client.GetStringAsync("{key}")).IsEqualTo("value");
        await Assert.That(reads).IsEqualTo(2);
        if (redirect == "ASK") await Assert.That(target.ReceivedCommands.Count(command => command == "ASKING")).IsEqualTo(2);
    }

    [Test]
    public async Task ClusterNoRedirectAndCircuitRejectionDoNotResend()
    {
        await using var target = Server();
        await using var source = Server();
        source.ReplyOverride = (_, command) => command == "GET {key}"
            ? Encoding.ASCII.GetBytes($"-MOVED {ClusterHash.GetSlot("key")} 127.0.0.1:{target.Port}\r\n") : Setup(command);
        await using var seed = new FakeRespServer(Topology(source.Port));
        await using var client = await RespireClient.ConnectAsync(Options(seed) with { UseCluster = true, CircuitBreaker = new() });
        await Assert.That(async () => { using var result = await client.ExecuteAsync(RespireCommands.String.GET,
            ["{key}"], RespireCommandFlags.NoRedirect); }).ThrowsExactly<RespireServerException>();
        await Assert.That(target.ReceivedCommands.Contains("GET {key}")).IsFalse();
        // Each permit publishes one outcome; independent failures open the endpoint.
        for (var i = 0; i < 10; i++)
        {
            try
            {
                var failure = client.Core.Circuits!.Acquire(new("127.0.0.1", source.Port), default);
                failure.Failed(new RespireConnectionException("offline"), default);
                failure.Dispose();
            }
            catch (RespireCircuitOpenException) { break; }
        }
        await Assert.That(async () => await client.GetStringAsync("{key}")).ThrowsExactly<RespireCircuitOpenException>();
        await Assert.That(source.ReceivedCommands.Count(command => command == "GET {key}")).IsEqualTo(1);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task SentinelReadOnlyRediscoversBeforeRetry(bool discoveryFails)
    {
        var activePort = 0;
        await using var replacement = Server();
        replacement.ReplyOverride = (_, command) => command == "SET key value" ? FakeRespServer.OkReply : Setup(command);
        await using var original = Server();
        original.ReplyOverride = (_, command) =>
        {
            if (command != "SET key value") return Setup(command);
            Volatile.Write(ref activePort, replacement.Port);
            return Error("READONLY");
        };
        activePort = original.Port;
        await using var sentinel = Sentinel(() => Volatile.Read(ref activePort), original.Port);
        var sentinelReply = sentinel.ReplyOverride!;
        var discoveryFailures = 0;
        sentinel.ReplyOverride = (id, command) => command == "SENTINEL GET-MASTER-ADDR-BY-NAME mymaster"
            && Volatile.Read(ref activePort) == replacement.Port && discoveryFails
            && Interlocked.Increment(ref discoveryFailures) == 1
                ? "*0\r\n"u8.ToArray() : sentinelReply(id, command);
        await using var client = await RespireClient.ConnectAsync(Options(sentinel) with
        {
            SentinelPrimaryName = "mymaster",
            RetryPolicy = new() { InitialBackoff = TimeSpan.Zero, MaxCommandRetryCategory = RespireCommandRetryCategory.WriteLastWins },
        });
        await SentinelTestSetup.WaitForStartupAsync(client);
        await client.SetAsync("key", "value");
        await Assert.That(original.ReceivedCommands.Count(command => command == "SET key value")).IsEqualTo(1);
        await Assert.That(replacement.ReceivedCommands.Count(command => command == "SET key value")).IsEqualTo(1);
        if (discoveryFails) await Assert.That(discoveryFailures).IsGreaterThanOrEqualTo(2);
    }

    [Test]
    [Arguments("raw")]
    [Arguments("script")]
    [Arguments("function")]
    [Arguments("ZADD")]
    [Arguments("XADD")]
    public async Task LostReplyDoesNotRepeatAccumulatingEffectsAtCheckedRisk(string shape)
    {
        var effects = 0;
        await using var server = Server();
        server.ReplyOverride = (_, command) =>
        {
            if (!(command.StartsWith("INCR ", StringComparison.Ordinal) || command.StartsWith("EVAL", StringComparison.Ordinal)
                || command.StartsWith("FCALL", StringComparison.Ordinal) || command.StartsWith("ZADD ", StringComparison.Ordinal)
                || command.StartsWith("XADD ", StringComparison.Ordinal))) return Setup(command);
            Interlocked.Increment(ref effects);
            server.CloseConnections();
            return null;
        };
        await using var client = await RespireClient.ConnectAsync(Options(server) with
        { RetryPolicy = new() { InitialBackoff = TimeSpan.Zero, MaxCommandRetryCategory = RespireCommandRetryCategory.WriteChecked } });
        async Task Call()
        {
            if (shape == "script")
                await client.Scripts.ExecuteIntegerAsync(RespireScript.Create("return redis.call('INCR', KEYS[1])")
                    .WithRetryCategory(RespireCommandRetryCategory.WriteAccumulating), ["counter"]);
            else if (shape == "function")
                await client.Functions.ExecuteIntegerAsync(RespireFunction.Create("increment")
                    .WithRetryCategory(RespireCommandRetryCategory.WriteAccumulating), ["counter"]);
            else if (shape == "ZADD")
            {
                using var result = await client.ExecuteAsync(RespireCommands.SortedSet.ZADD, ["counter", "NX", "INCR", 1, "member"]);
            }
            else if (shape == "XADD") await client.Streams.AddAsync("counter", ("field", (RespireValue)"value"));
            else
            {
                using var result = await client.ExecuteAsync(RespireCommand.Create("INCR")
                    .WithRetryCategory(RespireCommandRetryCategory.WriteAccumulating), ["counter"]);
            }
        }
        await Assert.That(Call).Throws<RespireConnectionException>();
        await Assert.That(effects).IsEqualTo(1);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ClusterFailureMetricsRemainOwnedByOneLogicalCall(bool succeeds)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        var events = new ConcurrentDictionary<string, long>();
        using var listener = RetryListener(events);
        var reads = 0;
        await using var node = Server();
        node.ReplyOverride = (_, command) => command == "GET {key}"
            ? Interlocked.Increment(ref reads) == 2 && succeeds ? "$5\r\nvalue\r\n"u8.ToArray() : Error("TRYAGAIN") : Setup(command);
        await using var seed = new FakeRespServer(Topology(node.Port));
        await using var client = await RespireClient.ConnectAsync(Options(seed) with
        { UseCluster = true, RetryPolicy = new() { MaxAttempts = 2, InitialBackoff = TimeSpan.Zero } });
        if (succeeds) await Assert.That(await client.GetStringAsync("{key}")).IsEqualTo("value");
        else await Assert.That(async () => await client.GetStringAsync("{key}")).ThrowsExactly<RespireServerException>();
        await Assert.That(reads).IsEqualTo(2);
        await Assert.That(events["attempted"]).IsEqualTo(1);
        await Assert.That(events[succeeds ? "succeeded" : "exhausted"]).IsEqualTo(1);
        await Assert.That(events.GetValueOrDefault("final-errors")).IsEqualTo(succeeds ? 0 : 1);
        await Assert.That(events["handled-errors"]).IsEqualTo(1);
    }

    [Test]
    public async Task ClusterRedirectDiscoveryUsesOriginalDeadline()
    {
        var reads = 0;
        await using var target = Server();
        target.ReplyOverride = (_, command) => command == "GET {key}"
            ? Interlocked.Increment(ref reads) == 1 ? Error("TRYAGAIN") : "$5\r\nvalue\r\n"u8.ToArray() : Setup(command);
        await using var source = Server();
        source.ReplyOverride = (_, command) => command == "GET {key}"
            ? Encoding.ASCII.GetBytes($"-ASK {ClusterHash.GetSlot("key")} 127.0.0.1:{target.Port}\r\n") : Setup(command);
        await using var seed = new FakeRespServer(Topology(source.Port));
        await using var client = await RespireClient.ConnectAsync(Options(seed) with
        { UseCluster = true, CommandTimeout = TimeSpan.FromMilliseconds(100),
            RetryPolicy = new() { InitialBackoff = TimeSpan.FromSeconds(1), Jitter = 0 } });
        await Assert.That(async () => await client.GetStringAsync("{key}")).ThrowsExactly<RespireServerException>();
        await Assert.That(reads).IsEqualTo(1);
        await Assert.That(source.ReceivedCommands.Count(command => command == "GET {key}")).IsEqualTo(1);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task GroupRetryReadsNewActiveCandidateAndPublishesOneOutcome(bool succeeds)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        var retries = new ConcurrentDictionary<string, long>();
        using var listener = RetryListener(retries);
        var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var offline = false;
        await using var original = Server();
        original.ReplyOverride = (_, command) =>
        {
            if (command == "GET key") { offline = true; failed.TrySetResult(); return Error("TRYAGAIN"); }
            return command == "PING" && offline ? Error("LOADING") : Setup(command);
        };
        await using var replacement = Server();
        replacement.ReplyOverride = (_, command) => command == "GET key"
            ? succeeds ? "$5\r\nvalue\r\n"u8.ToArray() : Error("TRYAGAIN") : Setup(command);
        await using var group = await RespireFailoverGroup.ConnectAsync(
            [new(Options(original) with { RetryPolicy = new() { MaxAttempts = 10, InitialBackoff = TimeSpan.Zero } }),
                new(Options(replacement) with { RetryPolicy = new() { MaxAttempts = 10, InitialBackoff = TimeSpan.Zero } }, 1)],
            new() { ProbeInterval = TimeSpan.FromHours(1), FailureThreshold = 1,
                RetryPolicy = new() { MaxAttempts = 2, InitialBackoff = TimeSpan.FromSeconds(1), MaxBackoff = TimeSpan.FromSeconds(1), Jitter = 0 } });
        var pending = group.ExecuteAsync("GET", ["key"], RespireCommandRetryCategory.ReadOnly).AsTask();
        await failed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await group.ForTests.ProbeAsync(0);
        await group.ForTests.SelectAsync();
        if (succeeds)
        {
            using var result = await pending;
            await Assert.That(result.AsString()).IsEqualTo("value");
        }
        else await Assert.That(async () => { using var result = await pending; }).ThrowsExactly<RespireServerException>();
        await Assert.That(original.ReceivedCommands.Count(command => command == "GET key")).IsEqualTo(1);
        await Assert.That(replacement.ReceivedCommands.Count(command => command == "GET key")).IsEqualTo(1);
        await Assert.That(retries["attempted"]).IsEqualTo(1);
        await Assert.That(retries[succeeds ? "succeeded" : "exhausted"]).IsEqualTo(1);
        await Assert.That(retries.GetValueOrDefault("final-errors")).IsEqualTo(succeeds ? 0 : 1);
    }

    [Test]
    public async Task CompleteTypedOptionsRefineRiskWithoutGrantingAccumulatingReplay()
    {
        var effects = 0;
        await using var server = Server();
        server.ReplyOverride = (_, command) =>
        {
            if (!command.StartsWith("SET key ", StringComparison.Ordinal)) return Setup(command);
            if (Interlocked.Increment(ref effects) == 1) { server.CloseConnections(); return null; }
            return "$-1\r\n"u8.ToArray();
        };
        await using var client = await RespireClient.ConnectAsync(Options(server) with
        { RetryPolicy = new() { InitialBackoff = TimeSpan.Zero, MaxCommandRetryCategory = RespireCommandRetryCategory.WriteChecked } });
        await Assert.That(await client.SetAsync("key", "value", when: SetWhen.NotExists)).IsFalse();
        await Assert.That(effects).IsEqualTo(2);
        await Assert.That(new SortedSetAddCommand("key", RespireSortedSetAddOptions.Nx, "member", 1).RetryCategory)
            .IsEqualTo(RespireCommandRetryCategory.WriteChecked);
        await Assert.That(new SortedSetAddCommand("key", RespireSortedSetAddOptions.Nx, "member", 1, increment: true).RetryCategory)
            .IsEqualTo(RespireCommandRetryCategory.WriteAccumulating);
        await Assert.That(RespireCommands.Stream.XADD.RetryCategory).IsEqualTo(RespireCommandRetryCategory.WriteAccumulating);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ExplicitDeferredFunctionsStillDoNotReplay(bool transactional)
    {
        var effects = 0;
        await using var server = Server();
        server.ReplyOverride = (_, command) => command.StartsWith("FCALL ", StringComparison.Ordinal)
            ? CountReply() : command == "EXEC" ? "*1\r\n-TRYAGAIN busy\r\n"u8.ToArray() : Setup(command);
        byte[] CountReply()
        {
            Interlocked.Increment(ref effects);
            return transactional ? "+QUEUED\r\n"u8.ToArray() : Error("TRYAGAIN");
        }
        await using var client = await RespireClient.ConnectAsync(Options(server));
        using var batch = client.CreateBatch();
        await using var transaction = client.CreateTransaction();
        IRespireCommandQueue queue = transactional ? transaction : batch;
        var pending = queue.Functions.ExecuteInteger(RespireFunction.Create("increment")
            .WithRetryCategory(RespireCommandRetryCategory.WriteAccumulating), ["key"]);
        if (transactional) await transaction.CommitAsync();
        else await batch.TryExecuteAsync();
        await Assert.That(pending.Error).IsTypeOf<RespireServerException>();
        await Assert.That(effects).IsEqualTo(1);
    }

    private static byte[] Error(string code) => Encoding.ASCII.GetBytes($"-{code} busy\r\n");
    private static FakeRespServer Server() => new(32, FakeRespServer.OkReply) { ReplyOverride = (_, command) => Setup(command) };
    private static byte[]? Setup(string command) => command switch
    {
        "ROLE" => "*3\r\n+master\r\n:0\r\n*0\r\n"u8.ToArray(),
        "PING" => FakeRespServer.PongReply,
        _ => null,
    };
    private static RespireOptions Options(FakeRespServer server) => new()
    {
        Endpoints = { new("127.0.0.1", server.Port) }, Protocol = RespProtocol.Resp2, Connections = 1,
        ThreadPoolMonitoring = false, CommandTimeout = TimeSpan.FromSeconds(3),
        RetryPolicy = new() { InitialBackoff = TimeSpan.Zero },
    };
    private static byte[] Topology(int port) => Encoding.ASCII.GetBytes(
        $"*1\r\n*3\r\n:0\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{port}\r\n");
    private static FakeRespServer Sentinel(Func<int> port, int initial) => new(64, "*0\r\n"u8.ToArray())
    {
        ReplyOverride = (_, command) => command switch
        {
            "SENTINEL GET-MASTER-ADDR-BY-NAME mymaster" => Encoding.ASCII.GetBytes($"*2\r\n+127.0.0.1\r\n+{port()}\r\n"),
            "SENTINEL MASTER mymaster" => Encoding.ASCII.GetBytes(
                $"*8\r\n+ip\r\n+127.0.0.1\r\n+port\r\n+{port()}\r\n+config-epoch\r\n+{(port() == initial ? 1 : 2)}\r\n+flags\r\n+master\r\n"),
            "SUBSCRIBE +switch-master" => "*3\r\n+subscribe\r\n+switch-master\r\n:1\r\n"u8.ToArray(),
            "SUBSCRIBE +sdown" => "*3\r\n+subscribe\r\n+sdown\r\n:2\r\n"u8.ToArray(),
            "SUBSCRIBE +odown" => "*3\r\n+subscribe\r\n+odown\r\n:3\r\n"u8.ToArray(),
            _ => null,
        },
    };
    private static MeterListener RetryListener(ConcurrentDictionary<string, long> events)
    {
        var listener = new MeterListener();
        listener.InstrumentPublished = static (instrument, meter) =>
        {
            if (instrument.Name is "respire.command.retries" or "redis.client.errors") meter.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            if (instrument.Name == "respire.command.retries")
            {
                var applicationRetry = false;
                foreach (var tag in tags)
                    if (tag.Key == "respire.command.retry.category"
                        && Equals(tag.Value, nameof(RespireCommandRetryCategory.ReadOnly))) applicationRetry = true;
                // Candidate health probes keep their own Always-category retry policy.
                if (!applicationRetry) return;
            }
            foreach (var tag in tags)
            {
                if (instrument.Name == "redis.client.errors" && tag.Key == "redis.client.errors.internal")
                    events.AddOrUpdate(Equals(tag.Value, true) ? "handled-errors" : "final-errors", value, (_, old) => old + value);
                if (tag.Key == "respire.command.retry.outcome")
                    events.AddOrUpdate((string)tag.Value!, value, (_, old) => old + value);
            }
        });
        listener.Start();
        return listener;
    }
}
