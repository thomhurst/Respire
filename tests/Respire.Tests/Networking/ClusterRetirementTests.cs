using System.Reflection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Respire.Commands;
using Respire.Infrastructure;
using Respire.Internal;
using Respire.Networking;
using Respire.Protocol;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class ClusterRetirementTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(5);

    private readonly struct AdmissionCallbackCommand(Action onAccepted) : IRespCommand
    {
        public ReadCommandKind ReadKind => ReadCommandKind.None;
        public void Write(ref RespWriter writer) { }
        public void OnAccepted() => onAccepted();
    }

    [Test]
    public async Task PrefixedCommandForwardsAdmissionCallback()
    {
        var accepted = 0;
        var prefix = new Cmd(RespireCommands.String.SET.Verb);
        var command = new AdmissionCallbackCommand(() => accepted++);
        var wrapper = typeof(RespireConnection).GetNestedType("PrefixedCommand`2", BindingFlags.NonPublic)!
            .MakeGenericType(typeof(Cmd), typeof(AdmissionCallbackCommand));
        var prefixed = (IRespCommand)Activator.CreateInstance(wrapper, prefix, command)!;

        prefixed.OnAccepted();

        await Assert.That(accepted).IsEqualTo(1);
    }

    [Test]
    public async Task RetirementSnapshotsAreOwnedAndRequireALiveClient()
    {
        await using var standalone = RespireClient.Create(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            Endpoints = [new RespireEndpoint("seed.invalid")],
        });
        await Assert.That(standalone.GetClusterRetirementSnapshot()).IsNull();
        await using var client = CreateClient();
        var snapshot = client.GetClusterRetirementSnapshot()!;
        await Assert.That(snapshot.RetiringGenerationCount).IsEqualTo(0);
        await Assert.That(snapshot.OldestRetirementAge).IsEqualTo(TimeSpan.Zero);
        await Assert.That(snapshot.PendingCorrectionFenceCount).IsEqualTo(0);
        var view = (RespireClient)client.WithKeyPrefix("tenant:");
        await Assert.That(view.GetClusterRetirementSnapshot()!.RetiringGenerationCount).IsEqualTo(0);
        await view.DisposeAsync();
        await Assert.That(client.GetClusterRetirementSnapshot()).IsNotNull();
        await client.DisposeAsync();
        await Assert.That(() => client.GetClusterRetirementSnapshot()).ThrowsExactly<ObjectDisposedException>();
        await Assert.That(() => view.GetClusterRetirementSnapshot()).ThrowsExactly<ObjectDisposedException>();
        await Assert.That(snapshot.RetiringGenerationCount).IsEqualTo(0);
    }

    [Test]
    [Arguments("keyed")]
    [Arguments("unkeyed")]
    [Arguments("dedicated")]
    public async Task RouteAcquisitionRetriesAnUnpublishedRetiredHandshake(string path)
    {
        await using var target = new FakeRespServer(path == "dedicated" ? 2 : 1, FakeRespServer.PongReply);
        await using var client = CreateClient();
        var router = client.Core.Cluster!;
        var endpoint = new RespireEndpoint("127.0.0.1", target.Port);
        Publish(router, endpoint, "old", 1);
        var old = router.GetMultiplexer(endpoint);
        var gate = (SemaphoreSlim)typeof(RespireConnectionMultiplexer).GetField("_connectGate", Private)!.GetValue(old)!;
        using var timeout = new CancellationTokenSource(Limit);
        await gate.WaitAsync(timeout.Token);
        Task<RespireConnection>? connectionTask = null;
        Task<DedicatedConnectionPool>? poolTask = null;
        try
        {
            if (path == "dedicated") poolTask = router.GetDedicatedPoolAsync(42, timeout.Token, discovery: null).AsTask();
            else connectionTask = router.GetConnectionAsync(path == "keyed" ? 42 : null, timeout.Token, discovery: null).AsTask();
            // The old node has not created a socket or accepted any application command.
            Publish(router, endpoint, "new", 2);
        }
        finally { gate.Release(); }
        DedicatedConnectionPool? pool = poolTask is null ? null : await poolTask.WaitAsync(timeout.Token);
        var connection = pool is null ? await connectionTask!.WaitAsync(timeout.Token) : await pool.RentAsync(timeout.Token);
        try
        {
            using var reply = await connection.SendAsync(new RawCommand(FakeRespServer.PingFrame), timeout.Token);
            await Assert.That(reply.AsString()).IsEqualTo("PONG");
            await Assert.That(target.ReceivedCommands).IsEquivalentTo(["PING"]);
            await Assert.That(ReferenceEquals(old, router.GetMultiplexer(endpoint))).IsFalse();
        }
        finally { pool?.Return(connection); }
        await router.WaitForRetirementAsync().WaitAsync(timeout.Token);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public Task ClusterWideRetirementRetriesOnlyTheRejectedTarget(bool fireAndForget)
        => AssertFanOutRetirementAsync(fireAndForget ? "fire-forget" : "raw");

    [Test]
    [Arguments("function")]
    [Arguments("script")]
    [Arguments("flush")]
    [Arguments("size")]
    [Arguments("scan")]
    public Task FacetFanOutRetirementRetriesOnlyTheRejectedTarget(string path)
        => AssertFanOutRetirementAsync(path);

    private static async Task AssertFanOutRetirementAsync(string path)
    {
        if (path == "scan")
        {
            // Resumable SCAN validates primary metadata before each page. Exercise retirement
            // during that acquisition while preserving an already accepted page and in-flight work.
            await ClusterScanTests.AssertRetirementPreservesAcceptedPagesAsync();
            return;
        }
        var commandName = path switch
        {
            "script" => "SCRIPT FLUSH", "flush" => "FLUSHDB", "size" => "DBSIZE",
            "scan" => "SCAN 0 COUNT 250", _ => "FUNCTION FLUSH",
        };
        var commandReply = path switch
        {
            "size" => ":1\r\n"u8.ToArray(),
            "scan" => "*2\r\n$1\r\n0\r\n*1\r\n$3\r\nkey\r\n"u8.ToArray(),
            _ => FakeRespServer.OkReply,
        };
        var topologyRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstAccepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondFull = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondAccepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pingCount = 0;
        await using var first = new FakeRespServer
        {
            SuppressReply = command =>
            {
                if (command == "CLUSTER SLOTS") topologyRequested.TrySetResult();
                else if (command == commandName) firstAccepted.TrySetResult();
                return true;
            },
        };
        await using var second = new FakeRespServer(2, commandReply)
        {
            SuppressReply = command =>
            {
                if (command != "PING") { secondAccepted.TrySetResult(); return false; }
                if (Interlocked.Increment(ref pingCount) == 4) secondFull.TrySetResult();
                return true;
            },
        };
        await using var client = CreateClient(maxInflightCommands: 4, allowAdmin: true);
        using var timeout = new CancellationTokenSource(Limit);
        var router = client.Core.Cluster!;
        PublishTargets("old", 1);
        var old = await router.GetConnectionAsync(9000, timeout.Token, discovery: null);
        var accepted = Enumerable.Range(0, 4)
            .Select(_ => old.SendAsync(new RawCommand(FakeRespServer.PingFrame), timeout.Token).AsTask()).ToArray();
        await secondFull.Task.WaitAsync(timeout.Token);

        var pending = SendAsync();
        await topologyRequested.Task.WaitAsync(timeout.Token);
        var topology = System.Text.Encoding.ASCII.GetBytes(
            $"*2\r\n*3\r\n:0\r\n:8191\r\n*3\r\n$9\r\n127.0.0.1\r\n:{first.Port}\r\n$5\r\nfirst\r\n" +
            $"*3\r\n:8192\r\n:16383\r\n*3\r\n$9\r\n127.0.0.1\r\n:{second.Port}\r\n$3\r\nold\r\n");
        await first.SendRawAsync(topology);
        await firstAccepted.Task.WaitAsync(timeout.Token);
        PublishTargets("new", 2);
        await first.SendRawAsync(commandReply);
        await pending.WaitAsync(timeout.Token);
        await secondAccepted.Task.WaitAsync(timeout.Token);

        await Assert.That(first.ReceivedCommands).IsEquivalentTo(["CLUSTER SLOTS", commandName]);
        await Assert.That(second.ReceivedCommands).IsEquivalentTo(["PING", "PING", "PING", "PING", commandName]);
        await Assert.That(second.ReceivedConnectionIds).IsEquivalentTo([0, 0, 0, 0, 1]);
        await Assert.That(accepted.All(task => !task.IsCompleted)).IsTrue();
        await second.SendRawAsync("+PONG\r\n+PONG\r\n+PONG\r\n+PONG\r\n"u8.ToArray(), 0);
        foreach (var task in accepted)
        {
            using var reply = await task.WaitAsync(timeout.Token);
            await Assert.That(reply.AsString()).IsEqualTo("PONG");
        }
        await router.WaitForRetirementAsync().WaitAsync(timeout.Token);

        async Task SendAsync()
        {
            switch (path)
            {
                case "raw":
                    using (var reply = await client.ExecuteAsync($"FUNCTION FLUSH", cancellationToken: timeout.Token))
                        await Assert.That(reply.AsString()).IsEqualTo("OK");
                    break;
                case "fire-forget": await client.ExecuteFireAndForgetAsync($"FUNCTION FLUSH", timeout.Token); break;
                case "function": await client.Functions.FlushAsync(cancellationToken: timeout.Token); break;
                case "script": await client.Scripts.FlushAsync(cancellationToken: timeout.Token); break;
                case "flush": await client.Server.FlushDatabaseAsync(timeout.Token); break;
                case "scan":
                    var keys = new List<string>();
                    await foreach (var key in client.Keys.ScanAsync(cancellationToken: timeout.Token)) keys.Add(key);
                    await Assert.That(keys).IsEquivalentTo(["key", "key"]);
                    break;
                default: await Assert.That(await client.Server.DatabaseSizeAsync(timeout.Token)).IsEqualTo(2); break;
            }
        }
        void PublishTargets(string secondId, long generation)
        {
            var version = (long)typeof(ClusterRouter).GetField("_topologyVersion", Private)!.GetValue(router)!;
            List<ClusterTopologyRange> ranges =
            [
                new(0, 8191, new("127.0.0.1", first.Port), "first", []),
                new(8192, 16383, new("127.0.0.1", second.Port), secondId, []),
            ];
            typeof(ClusterRouter).GetMethod("ApplyTopology", Private)!.Invoke(router, [ranges, version, generation]);
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RepeatedRejectedSendsCannotRestartTheRetirementBudget(bool configuredPolicy)
    {
        var full = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var acceptedCount = 0;
        await using var first = new FakeRespServer(FakeRespServer.PongReply)
        {
            SuppressReply = command =>
            {
                if (Interlocked.Increment(ref acceptedCount) == 4) full.TrySetResult();
                return true;
            },
        };
        await using var second = new FakeRespServer(FakeRespServer.OkReply);
        await using var last = new FakeRespServer(FakeRespServer.OkReply);
        await using var client = CreateClient(maxInflightCommands: 4,
            reconnectPolicy: configuredPolicy ? new() { InitialDelay = TimeSpan.Zero, JitterRatio = 0, MaxAttempts = 1 } : null);
        using var timeout = new CancellationTokenSource(Limit);
        var router = client.Core.Cluster!;
        Publish(router, new("127.0.0.1", first.Port), "first", 1);
        var original = await router.GetConnectionAsync(42, timeout.Token, discovery: null);
        var accepted = Enumerable.Range(0, 4)
            .Select(_ => original.SendAsync(new RawCommand(FakeRespServer.PingFrame), timeout.Token).AsTask()).ToArray();
        await full.Task.WaitAsync(timeout.Token);
        var changes = new System.Collections.Concurrent.ConcurrentQueue<RespireConnectionStateChange>();
        var exhausted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ConnectionStateChanged += change =>
        {
            if (change.ReconnectSource != RespireReconnectSource.ClusterDiscovery) return;
            changes.Enqueue(change);
            if (change.ReconnectExhausted) exhausted.TrySetResult();
        };
        var replacements = 0;
        // This callback runs after replacement selection and before the tracked command's next send.
        // Retire that replacement deterministically, without racing the notification dispatcher.
        Action onRedirect = () =>
        {
            if (++replacements == 1) Publish(router, new("127.0.0.1", last.Port), "last", 3);
        };
        var method = typeof(RespireClient).GetMethod("SendTrackedClusterAsync", Private)!.MakeGenericMethod(typeof(Cmd2));
        var pending = ((ValueTask<Respire.Protocol.RespValue>)method.Invoke(client,
            ["SET", router, new Cmd2(RespireCommands.String.SET.Verb, "key", "value"), timeout.Token, onRedirect])!).AsTask();
        Publish(router, new("127.0.0.1", second.Port), "second", 2);
        try
        {
            if (configuredPolicy)
            {
                await Assert.That(async () => { using var reply = await pending.WaitAsync(timeout.Token); })
                    .ThrowsExactly<RespireReconnectLimitException>();
                await exhausted.Task.WaitAsync(timeout.Token);
                await Assert.That(replacements).IsEqualTo(1);
                await Assert.That(last.CommandsSeen).IsEqualTo(0);
                var events = changes.ToArray();
                await Assert.That(events.Length).IsEqualTo(2);
                await Assert.That(events[0].ReconnectAttempt).IsEqualTo(1);
                await Assert.That(events[1].ReconnectAttempt).IsEqualTo(1);
                await Assert.That(events[0].ReconnectEpisodeId).IsEqualTo(events[1].ReconnectEpisodeId);
            }
            else
            {
                using var reply = await pending.WaitAsync(timeout.Token);
                await Assert.That(reply.AsString()).IsEqualTo("OK");
                await Assert.That(replacements).IsEqualTo(2);
                await Assert.That(last.ReceivedCommands).IsEquivalentTo(["CLIENT CACHING YES", "SET key value"]);
            }
            await Assert.That(second.CommandsSeen).IsEqualTo(0);
            await Assert.That(accepted.All(task => !task.IsCompleted)).IsTrue();
        }
        finally
        {
            await first.SendRawAsync("+PONG\r\n+PONG\r\n+PONG\r\n+PONG\r\n"u8.ToArray());
            foreach (var task in accepted) { using var reply = await task.WaitAsync(timeout.Token); }
        }
        await router.WaitForRetirementAsync().WaitAsync(timeout.Token);
    }

    [Test]
    [NotInParallel] // The ActivityListener deterministically retires each selected generation.
    [Arguments("ordinary", false)]
    [Arguments("ordinary", true)]
    [Arguments("no-redirect", false)]
    [Arguments("no-redirect", true)]
    [Arguments("tracked", false)]
    [Arguments("tracked", true)]
    [Arguments("pinned", false)]
    [Arguments("pinned", true)]
    [Arguments("fire-forget", false)]
    [Arguments("fire-forget", true)]
    public async Task RetirementAtRedirectLimitReportsFailedDiscovery(string path, bool unlimited)
    {
        await using var server = new FakeRespServer(8, FakeRespServer.OkReply);
        await using var client = CreateClient(allowAdmin: true, reconnectPolicy: new()
        {
            InitialDelay = TimeSpan.Zero, JitterRatio = 0, MaxAttempts = unlimited ? null : 5,
        });
        using var timeout = new CancellationTokenSource(Limit);
        var router = client.Core.Cluster!;
        var endpoint = new RespireEndpoint("127.0.0.1", server.Port);
        Publish(router, endpoint, "generation-0", 1);
        var original = await router.GetConnectionAsync(42, timeout.Token, discovery: null);
        var attempts = 0;
        using var listener = new System.Diagnostics.ActivityListener
        {
            ShouldListenTo = source => source.Name == "Respire",
            Sample = (ref System.Diagnostics.ActivityCreationOptions<System.Diagnostics.ActivityContext> _) =>
                System.Diagnostics.ActivitySamplingResult.AllData,
            ActivityStarted = activity =>
            {
                if (activity.GetTagItem("server.port") is not int port || port != server.Port) return;
                if (activity.OperationName is not ("SET" or "SHUTDOWN")) return;
                var generation = Interlocked.Increment(ref attempts);
                Publish(router, endpoint, "generation-" + generation, generation + 1);
            },
        };
        System.Diagnostics.ActivitySource.AddActivityListener(listener);
        var changes = new System.Collections.Concurrent.ConcurrentQueue<RespireConnectionStateChange>();
        var completed = new TaskCompletionSource<RespireConnectionStateChange>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ConnectionStateChanged += change =>
        {
            if (change.ReconnectSource != RespireReconnectSource.ClusterDiscovery) return;
            changes.Enqueue(change);
            if (change.NextReconnectDelay is null) completed.TrySetResult(change);
        };
        var error = await Assert.That(async () => await SendAsync().WaitAsync(timeout.Token))
            .Throws<RespireConnectionRetiredException>();
        var terminal = await completed.Task.WaitAsync(timeout.Token);
        await Assert.That(terminal.SourceState).IsEqualTo(RespireConnectionState.Disconnected);
        await Assert.That(terminal.Error).IsSameReferenceAs(error);
        await Assert.That(terminal.ReconnectAttempt).IsEqualTo(5);
        // The command's redirect cap ended recovery, not the policy's fallback budget.
        await Assert.That(terminal.ReconnectExhausted).IsFalse();
        await Assert.That(attempts).IsEqualTo(6);
        await Assert.That(changes.Count).IsEqualTo(6);
        await Assert.That(changes.Select(change => change.ReconnectEpisodeId).Distinct().Count()).IsEqualTo(1);
        await Assert.That(server.CommandsSeen).IsEqualTo(0);

        async Task SendAsync()
        {
            var command = new Cmd2(RespireCommands.String.SET.Verb, "key", "value");
            if (path == "tracked")
            {
                var method = typeof(RespireClient).GetMethod("SendTrackedClusterAsync", Private)!.MakeGenericMethod(typeof(Cmd2));
                using var reply = await (ValueTask<Respire.Protocol.RespValue>)method.Invoke(client,
                    ["SET", router, command, timeout.Token, null])!;
            }
            else if (path == "pinned")
            {
                using var reply = await client.SendToClusterTargetAsync("SET", original, command, timeout.Token);
            }
            else if (path == "fire-forget")
                await client.ExecuteFireAndForgetAsync($"SHUTDOWN NOSAVE", timeout.Token);
            else
            {
                using var reply = await client.ExecuteAsync(RespireCommands.String.SET, ["key", "value"],
                    path == "no-redirect" ? RespireCommandFlags.NoRedirect : RespireCommandFlags.None, timeout.Token);
            }
        }
    }

    [Test]
    [Arguments("ordinary", "MOVED", false)]
    [Arguments("ordinary", "MOVED", true)]
    [Arguments("ordinary", "ASK", false)]
    [Arguments("ordinary", "ASK", true)]
    [Arguments("ordinary", "READONLY", false)]
    [Arguments("ordinary", "READONLY", true)]
    [Arguments("tracked", "MOVED", false)]
    [Arguments("tracked", "MOVED", true)]
    [Arguments("tracked", "ASK", false)]
    [Arguments("tracked", "ASK", true)]
    [Arguments("tracked", "READONLY", false)]
    [Arguments("tracked", "READONLY", true)]
    [Arguments("batch", "MOVED", false)]
    [Arguments("batch", "MOVED", true)]
    [Arguments("batch", "ASK", false)]
    [Arguments("batch", "ASK", true)]
    [Arguments("batch", "READONLY", false)]
    [Arguments("batch", "READONLY", true)]
    [Arguments("script", "MOVED", false)]
    [Arguments("script", "MOVED", true)]
    [Arguments("script", "ASK", false)]
    [Arguments("script", "ASK", true)]
    [Arguments("script", "READONLY", false)]
    [Arguments("script", "READONLY", true)]
    [Arguments("native-lock", "MOVED", false)]
    [Arguments("native-lock", "MOVED", true)]
    [Arguments("native-lock", "ASK", false)]
    [Arguments("native-lock", "ASK", true)]
    [Arguments("native-lock", "READONLY", false)]
    [Arguments("native-lock", "READONLY", true)]
    [Arguments("transaction", "MOVED", false)]
    [Arguments("transaction", "MOVED", true)]
    [Arguments("transaction", "ASK", false)]
    [Arguments("transaction", "ASK", true)]
    [Arguments("transaction", "READONLY", false)]
    [Arguments("transaction", "READONLY", true)]
    [Arguments("blocking", "MOVED", false)]
    [Arguments("blocking", "MOVED", true)]
    [Arguments("blocking", "ASK", false)]
    [Arguments("blocking", "ASK", true)]
    [Arguments("blocking", "READONLY", false)]
    [Arguments("blocking", "READONLY", true)]
    public async Task TerminalRoutingRejectionReportsFailedDiscovery(string path, string code, bool unlimited)
    {
        await using var server = new FakeRespServer(2, FakeRespServer.OkReply);
        await using var client = CreateClient(reconnectPolicy: new()
        {
            InitialDelay = TimeSpan.Zero, JitterRatio = 0, MaxAttempts = unlimited ? null : 5,
        });
        using var timeout = new CancellationTokenSource(Limit);
        var router = client.Core.Cluster!;
        Publish(router, new("127.0.0.1", server.Port), "owner", 1);
        var attempts = 0;
        var slot = ClusterHash.GetSlot("key");
        server.ReplyOverride = (_, command) =>
        {
            if (command == "CLIENT ID") return ":42\r\n"u8.ToArray();
            if (command.StartsWith("CLIENT KILL ", StringComparison.Ordinal)) return ":0\r\n"u8.ToArray();
            if (command == "EXEC") return "-EXECABORT rejected queue\r\n"u8.ToArray();
            if (!command.StartsWith("SET ", StringComparison.Ordinal)
                && !command.StartsWith("EVALSHA ", StringComparison.Ordinal)
                && !command.StartsWith("BLPOP ", StringComparison.Ordinal)) return null;
            var final = Interlocked.Increment(ref attempts) > ClusterRouter.RedirectLimit;
            // Five successful reselections start one episode. The last route still rejects
            // the command, including READONLY after selection no longer has a pending failure.
            var rejection = final ? code : "MOVED";
            return System.Text.Encoding.ASCII.GetBytes(rejection == "READONLY"
                ? "-READONLY demoted\r\n" : $"-{rejection} {slot} 127.0.0.1:{server.Port}\r\n");
        };
        var changes = new System.Collections.Concurrent.ConcurrentQueue<RespireConnectionStateChange>();
        var completed = new TaskCompletionSource<RespireConnectionStateChange>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ConnectionStateChanged += change =>
        {
            if (change.ReconnectSource != RespireReconnectSource.ClusterDiscovery) return;
            changes.Enqueue(change);
            if (change.NextReconnectDelay is null) completed.TrySetResult(change);
        };
        var error = await Assert.That(async () => await SendAsync().WaitAsync(timeout.Token))
            .ThrowsExactly<RespireServerException>();
        await Assert.That(error!.Code).IsEqualTo(code);
        var terminal = await completed.Task.WaitAsync(timeout.Token);
        await Assert.That(terminal.SourceState).IsEqualTo(RespireConnectionState.Disconnected);
        await Assert.That(terminal.Error).IsSameReferenceAs(error);
        await Assert.That(terminal.ReconnectAttempt).IsEqualTo(5);
        await Assert.That(terminal.ReconnectExhausted).IsFalse();
        await Assert.That(attempts).IsEqualTo(6);
        await Assert.That(changes.Count).IsEqualTo(6);
        await Assert.That(changes.Select(change => change.ReconnectEpisodeId).Distinct().Count()).IsEqualTo(1);

        async Task SendAsync()
        {
            switch (path)
            {
                case "tracked":
                    var method = typeof(RespireClient).GetMethod("SendTrackedClusterAsync", Private)!.MakeGenericMethod(typeof(Cmd2));
                    using (await (ValueTask<Respire.Protocol.RespValue>)method.Invoke(client,
                        ["SET", router, new Cmd2(RespireCommands.String.SET.Verb, "key", "value"), timeout.Token, null])!) { }
                    break;
                case "batch":
                    var batch = client.CreateBatch();
                    var batched = batch.Set("key", "value");
                    await batch.ExecuteAsync(timeout.Token);
                    _ = batched.Result;
                    break;
                case "script":
                    var execution = await client.StartTrackedScriptExecutionAsync(
                        RespireScript.Create("return redis.call('SET', KEYS[1], ARGV[1])"), ["key"], ["value"], timeout.Token);
                    using (await execution.Response) { }
                    break;
                case "native-lock":
                    await client.ExecuteLockAsync("key", new RespireLockToken("owner"), 1000, timeout.Token);
                    break;
                case "transaction":
                    await using (var transaction = client.CreateTransaction())
                    {
                        transaction.Set("key", "value");
                        await transaction.CommitAsync(timeout.Token);
                    }
                    break;
                case "blocking":
                    using (await client.SendBlockingAsync("BLPOP",
                        new Cmd2(RespireCommands.List.BLPOP.Verb, "key", "0"), timeout.Token)) { }
                    break;
                default:
                    using (await client.ExecuteAsync(RespireCommands.String.SET, ["key", "value"], cancellationToken: timeout.Token)) { }
                    break;
            }
        }
    }

    [Test]
    [NotInParallel] // The ActivityListener enables process-wide operation instrumentation.
    [Arguments("batch", "MOVED", false)]
    [Arguments("batch", "MOVED", true)]
    [Arguments("batch", "ASK", false)]
    [Arguments("batch", "ASK", true)]
    [Arguments("batch", "READONLY", false)]
    [Arguments("batch", "READONLY", true)]
    [Arguments("ordinary", "MOVED", false)]
    [Arguments("ordinary", "MOVED", true)]
    [Arguments("ordinary", "ASK", false)]
    [Arguments("ordinary", "ASK", true)]
    [Arguments("ordinary", "READONLY", false)]
    [Arguments("ordinary", "READONLY", true)]
    [Arguments("tracked", "MOVED", false)]
    [Arguments("tracked", "MOVED", true)]
    [Arguments("tracked", "ASK", false)]
    [Arguments("tracked", "ASK", true)]
    [Arguments("tracked", "READONLY", false)]
    [Arguments("tracked", "READONLY", true)]
    public async Task RedirectThenRetirementKeepsOneCommandBudget(string path, string code, bool configured)
    {
        await using var first = new FakeRespServer(FakeRespServer.OkReply);
        await using var second = new FakeRespServer(3, FakeRespServer.OkReply);
        await using var last = new FakeRespServer(FakeRespServer.OkReply);
        await using var client = CreateClient(reconnectPolicy: configured
            ? new() { InitialDelay = TimeSpan.Zero, JitterRatio = 0, MaxAttempts = 1 } : null);
        using var timeout = new CancellationTokenSource(Limit);
        var router = client.Core.Cluster!;
        Publish(router, new("127.0.0.1", first.Port), "first", 1);
        var slot = ClusterHash.GetSlot("key");
        first.ReplyOverride = (_, command) =>
        {
            if (command != "SET key value") return null;
            Publish(router, new("127.0.0.1", second.Port), "second", 2);
            return System.Text.Encoding.ASCII.GetBytes(code == "READONLY"
                ? "-READONLY demoted\r\n" : $"-{code} {slot} 127.0.0.1:{second.Port}\r\n");
        };
        second.ReplyOverride = (_, command) => command switch
        {
            "CLUSTER SLOTS" => FullTopology(second.Port),
            "ASKING" => FakeRespServer.OkReply,
            _ => null,
        };
        var retired = 0;
        using var listener = new System.Diagnostics.ActivityListener
        {
            ShouldListenTo = source => source.Name == "Respire",
            Sample = (ref System.Diagnostics.ActivityCreationOptions<System.Diagnostics.ActivityContext> _) =>
                System.Diagnostics.ActivitySamplingResult.AllData,
            ActivityStarted = activity =>
            {
                if (activity.OperationName == "SET" && activity.GetTagItem("server.port") is int port
                    && port == second.Port && Interlocked.CompareExchange(ref retired, 1, 0) == 0)
                    Publish(router, new("127.0.0.1", last.Port), "last", 3);
            },
        };
        System.Diagnostics.ActivitySource.AddActivityListener(listener);
        var exhausted = new TaskCompletionSource<RespireConnectionStateChange>(TaskCreationOptions.RunContinuationsAsynchronously);
        var changes = new System.Collections.Concurrent.ConcurrentQueue<RespireConnectionStateChange>();
        client.ConnectionStateChanged += change =>
        {
            if (change.ReconnectSource != RespireReconnectSource.ClusterDiscovery) return;
            changes.Enqueue(change);
            if (change.ReconnectExhausted) exhausted.TrySetResult(change);
        };
        async Task Execute()
        {
            if (path == "batch")
            {
                using var batch = client.CreateBatch();
                var pending = batch.Set("key", "value");
                await batch.ExecuteAsync(timeout.Token);
                await Assert.That(await pending).IsTrue();
            }
            else if (path == "tracked")
            {
                var method = typeof(RespireClient).GetMethod("SendTrackedClusterAsync", Private)!.MakeGenericMethod(typeof(Cmd2));
                using var response = await (ValueTask<Respire.Protocol.RespValue>)method.Invoke(client,
                    ["SET", router, new Cmd2(RespireCommands.String.SET.Verb, "key", "value"), timeout.Token, null])!;
                await Assert.That(response.AsString()).IsEqualTo("OK");
            }
            else await Assert.That(await client.SetAsync("key", "value", cancellationToken: timeout.Token)).IsTrue();
        }
        if (configured)
        {
            await Assert.That(async () => await Execute()).Throws<RespireReconnectLimitException>();
            var terminal = await exhausted.Task.WaitAsync(timeout.Token);
            await Assert.That(terminal.ReconnectAttempt).IsEqualTo(1);
            var events = changes.ToArray();
            await Assert.That(events.Length).IsEqualTo(2);
            await Assert.That(events[0].ReconnectEpisodeId).IsEqualTo(terminal.ReconnectEpisodeId);
            await Assert.That(second.ReceivedCommands.Count(command => command == "SET key value")).IsEqualTo(0);
            await Assert.That(last.CommandsSeen).IsEqualTo(0);
        }
        else await Execute();
        await Assert.That(retired).IsEqualTo(1);
        await Assert.That(first.ReceivedCommands.Count(command => command == "SET key value")).IsEqualTo(1);
        await router.WaitForRetirementAsync().WaitAsync(timeout.Token);
    }

    [Test]
    [Arguments("MOVED", false)]
    [Arguments("MOVED", true)]
    [Arguments("ASK", false)]
    [Arguments("ASK", true)]
    [Arguments("READONLY", false)]
    [Arguments("READONLY", true)]
    public async Task BlockingRedirectThenPoolRetirementKeepsOneBudget(string code, bool configured)
    {
        await using var first = new FakeRespServer(2, FakeRespServer.OkReply);
        await using var second = new FakeRespServer(4, "*-1\r\n"u8.ToArray());
        await using var last = new FakeRespServer(2, "*-1\r\n"u8.ToArray());
        using var logger = new DedicatedConnectCallbackLogger(second.Port);
        await using var client = CreateClient(loggerFactory: logger, reconnectPolicy: configured
            ? new() { InitialDelay = TimeSpan.Zero, JitterRatio = 0, MaxAttempts = 1 } : null);
        using var timeout = new CancellationTokenSource(Limit);
        var router = client.Core.Cluster!;
        Publish(router, new("127.0.0.1", first.Port), "first", 1);
        var slot = ClusterHash.GetSlot("key");
        first.ReplyOverride = (_, command) =>
        {
            if (!command.StartsWith("BLPOP ", StringComparison.Ordinal)) return null;
            Publish(router, new("127.0.0.1", second.Port), "second", 2);
            return System.Text.Encoding.ASCII.GetBytes(code == "READONLY"
                ? "-READONLY demoted\r\n" : $"-{code} {slot} 127.0.0.1:{second.Port}\r\n");
        };
        second.ReplyOverride = (_, command) => command switch
        {
            "CLUSTER SLOTS" => FullTopology(second.Port),
            "ASKING" => FakeRespServer.OkReply,
            _ => null,
        };
        logger.OnConnected = () => Publish(router, new("127.0.0.1", last.Port), "last", 3);
        var exhausted = new TaskCompletionSource<RespireConnectionStateChange>(TaskCreationOptions.RunContinuationsAsynchronously);
        var changes = new System.Collections.Concurrent.ConcurrentQueue<RespireConnectionStateChange>();
        client.ConnectionStateChanged += change =>
        {
            if (change.ReconnectSource != RespireReconnectSource.ClusterDiscovery) return;
            changes.Enqueue(change);
            if (change.ReconnectExhausted) exhausted.TrySetResult(change);
        };
        async Task Execute()
        {
            using var reply = await client.SendBlockingAsync("BLPOP",
                new Cmd2(RespireCommands.List.BLPOP.Verb, "key", "0"), timeout.Token);
            await Assert.That(reply.IsNull).IsTrue();
        }
        if (configured)
        {
            if (code == "ASK")
            {
                var rejection = await Assert.That(async () => await Execute()).ThrowsExactly<RespireServerException>();
                await Assert.That(rejection.Code).IsEqualTo("ASK");
            }
            else await Assert.That(async () => await Execute()).ThrowsExactly<RespireReconnectLimitException>();
            var terminal = await exhausted.Task.WaitAsync(timeout.Token);
            await Assert.That(terminal.ReconnectAttempt).IsEqualTo(1);
            var events = changes.ToArray();
            await Assert.That(events.Length).IsEqualTo(2);
            await Assert.That(events[0].ReconnectEpisodeId).IsEqualTo(terminal.ReconnectEpisodeId);
            await Assert.That(second.ReceivedCommands.Any(command => command.StartsWith("BLPOP "))).IsFalse();
            await Assert.That(last.CommandsSeen).IsEqualTo(0);
        }
        else await Execute();
        await Assert.That(logger.Calls).IsEqualTo(1);
        await Assert.That(first.ReceivedCommands.Count(command => command.StartsWith("BLPOP "))).IsEqualTo(1);
        await router.WaitForRetirementAsync().WaitAsync(timeout.Token);
    }

    private static byte[] FullTopology(int port) => System.Text.Encoding.ASCII.GetBytes(
        $"*1\r\n*3\r\n:0\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{port}\r\n");

    private sealed class DedicatedConnectCallbackLogger(int port) : ILoggerFactory, ILogger
    {
        internal Action? OnConnected;
        internal int Calls;
        public ILogger CreateLogger(string categoryName)
            => categoryName == $"Respire.Cluster.Blocking.127.0.0.1:{port}" ? this : NullLogger.Instance;
        public void AddProvider(ILoggerProvider provider) { }
        public void Dispose() { }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? error, Func<TState, Exception?, string> formatter)
        {
            if (level == LogLevel.Debug && formatter(state, error).StartsWith("Connected to", StringComparison.Ordinal)
                && Interlocked.CompareExchange(ref Calls, 1, 0) == 0) OnConnected?.Invoke();
        }
    }

    [Test]
    [Arguments("ordinary", "server-error")]
    [Arguments("ordinary", "accepted-cancellation")]
    [Arguments("no-redirect", "MOVED")]
    [Arguments("no-redirect", "ASK")]
    [Arguments("no-redirect", "READONLY")]
    [Arguments("no-redirect", "server-error")]
    [Arguments("no-redirect", "accepted-cancellation")]
    [Arguments("tracked", "server-error")]
    [Arguments("tracked", "accepted-cancellation")]
    [Arguments("pinned", "server-error")]
    [Arguments("pinned", "accepted-cancellation")]
    [Arguments("ordinary", "recovery-cancellation")]
    public async Task ApplicationFailureAfterRetirementDoesNotFailDiscovery(string path, string outcome)
    {
        var errorCode = outcome is "MOVED" or "ASK" or "READONLY" ? outcome : "WRONGTYPE";
        var cancelAfterAcceptance = outcome == "accepted-cancellation";
        var cancelDuringRecovery = outcome == "recovery-cancellation";
        var scheduled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var full = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var retried = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var terminal = new TaskCompletionSource<RespireConnectionStateChange>(TaskCreationOptions.RunContinuationsAsynchronously);
        var acceptedCount = 0;
        await using var server = new FakeRespServer(2, FakeRespServer.OkReply)
        {
            SuppressReply = command =>
            {
                if (command == "PING")
                {
                    if (Interlocked.Increment(ref acceptedCount) == 4) full.TrySetResult();
                    return true;
                }
                if (command == "SET key value")
                {
                    retried.TrySetResult();
                    return cancelAfterAcceptance;
                }
                return false;
            },
            ReplyOverride = (_, command) => command == "SET key value" ? System.Text.Encoding.ASCII.GetBytes($"-{errorCode} test application error\r\n") : null,
        };
        await using var client = CreateClient(maxInflightCommands: 4,
            reconnectPolicy: new() { InitialDelay = cancelDuringRecovery ? TimeSpan.FromSeconds(30) : TimeSpan.Zero,
                MaxDelay = TimeSpan.FromSeconds(30), JitterRatio = 0, MaxAttempts = 1 });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        var router = client.Core.Cluster!;
        var endpoint = new RespireEndpoint("127.0.0.1", server.Port);
        Publish(router, endpoint, "old", 1);
        var old = await router.GetConnectionAsync(42, timeout.Token, discovery: null);
        var accepted = Enumerable.Range(0, 4)
            .Select(_ => old.SendAsync(new RawCommand(FakeRespServer.PingFrame), timeout.Token, armCommandDeadline: false).AsTask()).ToArray();
        await full.Task.WaitAsync(timeout.Token);
        client.ConnectionStateChanged += change =>
        {
            if (change.ReconnectSource != RespireReconnectSource.ClusterDiscovery) return;
            if (change.NextReconnectDelay is not null) scheduled.TrySetResult();
            else terminal.TrySetResult(change);
        };
        var pending = SendAsync();
        await Assert.That(pending.IsCompleted).IsFalse();
        Publish(router, endpoint, "new", 2);
        try
        {
            await (cancelDuringRecovery ? scheduled.Task : retried.Task).WaitAsync(timeout.Token);
            OperationCanceledException? cancellationError = null;
            if (cancelAfterAcceptance || cancelDuringRecovery)
            {
                caller.Cancel();
                cancellationError = await Assert.That(async () => await pending.WaitAsync(timeout.Token)).Throws<OperationCanceledException>();
                await Assert.That(cancellationError!.CancellationToken).IsEqualTo(caller.Token);
            }
            else
            {
                var error = await Assert.That(async () => await pending.WaitAsync(timeout.Token)).Throws<RespireServerException>();
                await Assert.That(error!.Code).IsEqualTo(errorCode);
            }
            var recovered = await terminal.Task.WaitAsync(timeout.Token);
            await Assert.That(recovered.SourceState).IsEqualTo(cancelDuringRecovery
                ? RespireConnectionState.Disconnected : RespireConnectionState.Connected);
            if (cancelDuringRecovery) await Assert.That(recovered.Error).IsSameReferenceAs(cancellationError);
            else await Assert.That(recovered.Error).IsNull();
            await Assert.That(recovered.ReconnectAttempt).IsEqualTo(1);
            await Assert.That(recovered.ReconnectExhausted).IsFalse();
            await Assert.That(server.ReceivedCommands.Count(command => command == "SET key value")).IsEqualTo(cancelDuringRecovery ? 0 : 1);
            await Assert.That(accepted.All(task => !task.IsCompleted)).IsTrue();
        }
        finally
        {
            await server.SendRawAsync("+PONG\r\n+PONG\r\n+PONG\r\n+PONG\r\n"u8.ToArray(), 0);
            foreach (var task in accepted) { using var reply = await task.WaitAsync(timeout.Token); }
        }
        await router.WaitForRetirementAsync().WaitAsync(timeout.Token);

        async Task SendAsync()
        {
            var command = new Cmd2(RespireCommands.String.SET.Verb, "key", "value");
            if (path == "pinned")
            {
                using var reply = await client.SendToClusterTargetAsync("SET", old, command, caller.Token);
            }
            else if (path == "tracked")
            {
                var method = typeof(RespireClient).GetMethod("SendTrackedClusterAsync", Private)!.MakeGenericMethod(typeof(Cmd2));
                using var reply = await (ValueTask<Respire.Protocol.RespValue>)method.Invoke(client,
                    ["SET", router, command, caller.Token, null])!;
            }
            else
            {
                using var reply = await client.ExecuteAsync(RespireCommands.String.SET, ["key", "value"],
                    path == "no-redirect" ? RespireCommandFlags.NoRedirect : RespireCommandFlags.None, caller.Token);
            }
        }
    }

    [Test]
    [Arguments("ordinary", false)]
    [Arguments("ordinary", true)]
    [Arguments("no-redirect", false)]
    [Arguments("no-redirect", true)]
    [Arguments("tracked", false)]
    [Arguments("tracked", true)]
    [Arguments("fire-forget", false)]
    [Arguments("fire-forget", true)]
    [Arguments("batch", false)]
    [Arguments("batch", true)]
    [Arguments("transaction", false)]
    [Arguments("transaction", true)]
    public async Task RejectedCommandRetriesWithoutReplayingAcceptedWork(string path, bool configuredPolicy)
    {
        var full = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var retried = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var acceptedCount = 0;
        byte[][] replies = path == "transaction"
            ? [FakeRespServer.OkReply, "+QUEUED\r\n"u8.ToArray(), "*1\r\n+OK\r\n"u8.ToArray()]
            : [FakeRespServer.OkReply];
        await using var server = new FakeRespServer(2, replies)
        {
            SuppressReply = command =>
            {
                if (command != "PING") { retried.TrySetResult(); return false; }
                if (Interlocked.Increment(ref acceptedCount) == 4) full.TrySetResult();
                return true;
            },
        };
        await using var client = CreateClient(maxInflightCommands: 4, allowAdmin: true,
            reconnectPolicy: configuredPolicy ? new() { InitialDelay = TimeSpan.FromMilliseconds(10),
                JitterRatio = 0, MaxAttempts = 1 } : null);
        var scheduled = new TaskCompletionSource<RespireConnectionStateChange>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ConnectionStateChanged += change =>
        {
            if (change.ReconnectSource == RespireReconnectSource.ClusterDiscovery && change.NextReconnectDelay is not null)
                scheduled.TrySetResult(change);
        };
        using var timeout = new CancellationTokenSource(Limit);
        var router = client.Core.Cluster!;
        var endpoint = new RespireEndpoint("127.0.0.1", server.Port);
        Publish(router, endpoint, "old", 1);
        var old = await router.GetConnectionAsync(42, timeout.Token, discovery: null);
        var accepted = Enumerable.Range(0, 4)
            .Select(_ => old.SendAsync(new RawCommand(FakeRespServer.PingFrame), timeout.Token).AsTask()).ToArray();
        await full.Task.WaitAsync(timeout.Token);

        var retry = SendAsync();
        await Assert.That(retry.IsCompleted).IsFalse();
        Publish(router, endpoint, "new", 2);
        await retry.WaitAsync(timeout.Token);
        await retried.Task.WaitAsync(timeout.Token);
        if (configuredPolicy)
        {
            var change = await scheduled.Task.WaitAsync(timeout.Token);
            await Assert.That(change.ReconnectAttempt).IsEqualTo(1);
            await Assert.That(change.NextReconnectDelay).IsEqualTo(TimeSpan.FromMilliseconds(10));
            await Assert.That(change.Error).IsTypeOf<RespireConnectionRetiredException>();
        }
        await Assert.That(accepted.All(task => !task.IsCompleted)).IsTrue();
        await Assert.That(server.ReceivedCommands.Take(4)).IsEquivalentTo(["PING", "PING", "PING", "PING"]);
        await Assert.That(server.ReceivedConnectionIds.Take(4)).IsEquivalentTo([0, 0, 0, 0]);
        await Assert.That(server.ReceivedConnectionIds.Skip(4).All(id => id == 1)).IsTrue();
        string[] expected = path switch
        {
            "tracked" => ["CLIENT CACHING YES", "SET key value"],
            "fire-forget" => ["SHUTDOWN NOSAVE"],
            "transaction" => ["MULTI", "SET key value", "EXEC"],
            _ => ["SET key value"],
        };
        await Assert.That(server.ReceivedCommands.Skip(4)).IsEquivalentTo(expected);
        await server.SendRawAsync("+PONG\r\n+PONG\r\n+PONG\r\n+PONG\r\n"u8.ToArray(), 0);
        foreach (var task in accepted)
        {
            using var reply = await task.WaitAsync(timeout.Token);
            await Assert.That(reply.AsString()).IsEqualTo("PONG");
        }
        await router.WaitForRetirementAsync().WaitAsync(timeout.Token);

        async Task SendAsync()
        {
            switch (path)
            {
                case "tracked":
                    var rebased = 0;
                    Action onRedirect = () => rebased++;
                    var method = typeof(RespireClient).GetMethod("SendTrackedClusterAsync", Private)!.MakeGenericMethod(typeof(Cmd2));
                    var pending = (ValueTask<Respire.Protocol.RespValue>)method.Invoke(client,
                        ["SET", router, new Cmd2(RespireCommands.String.SET.Verb, "key", "value"), timeout.Token, onRedirect])!;
                    using (var reply = await pending) await Assert.That(reply.AsString()).IsEqualTo("OK");
                    await Assert.That(rebased).IsEqualTo(1);
                    break;
                case "fire-forget":
                    await client.ExecuteFireAndForgetAsync($"SHUTDOWN NOSAVE", timeout.Token);
                    break;
                case "batch":
                    var batch = client.CreateBatch();
                    var batched = batch.Set("key", "value");
                    await batch.ExecuteAsync(timeout.Token);
                    await Assert.That(batched.Result).IsTrue();
                    break;
                case "transaction":
                    await using (var transaction = client.CreateTransaction())
                    {
                        var committed = transaction.Set("key", "value");
                        await transaction.CommitAsync(timeout.Token);
                        await Assert.That(committed.Result).IsTrue();
                    }
                    break;
                default:
                    using (var reply = await client.ExecuteAsync(RespireCommands.String.SET, ["key", "value"],
                        path == "no-redirect" ? RespireCommandFlags.NoRedirect : RespireCommandFlags.None, timeout.Token))
                        await Assert.That(reply.AsString()).IsEqualTo("OK");
                    break;
            }
        }
    }

    [Test]
    [Arguments(false, false, false)]
    [Arguments(false, false, true)]
    [Arguments(false, true, false)]
    [Arguments(false, true, true)]
    [Arguments(true, false, false)]
    [Arguments(true, false, true)]
    [Arguments(true, true, false)]
    [Arguments(true, true, true)]
    public async Task RetiredPoolSelectionRetriesBeforeRentAndKeepsItsOwner(bool asking, bool reuseIdle, bool configuredPolicy)
    {
        await using var oldServer = new FakeRespServer(3, FakeRespServer.PongReply);
        await using var newServer = new FakeRespServer(2, FakeRespServer.PongReply);
        await using var client = CreateClient(reconnectPolicy: configuredPolicy
            ? new() { InitialDelay = TimeSpan.Zero, JitterRatio = 0, MaxAttempts = 1 } : null);
        using var timeout = new CancellationTokenSource(Limit);
        var router = client.Core.Cluster!;
        var oldEndpoint = new RespireEndpoint("127.0.0.1", oldServer.Port);
        Publish(router, oldEndpoint, "old", 1);
        var source = await router.GetConnectionAsync(42, timeout.Token, discovery: null);
        using (var ready = await source.SendAsync(new RawCommand(FakeRespServer.PingFrame), timeout.Token))
            await Assert.That(ready.AsString()).IsEqualTo("PONG");
        var selected = await router.GetDedicatedPoolAsync(42, timeout.Token, discovery: null);
        Publish(router, new("127.0.0.1", newServer.Port), "new", 2);
        await Assert.That(selected.IsStopping).IsTrue();
        // The ASK error originates at the slot owner, not at the temporary target.
        var redirectSource = await router.GetConnectionAsync(42, timeout.Token, discovery: null);
        var (pool, connection) = await router.RentDedicatedConnectionAsync(
            selected, 42, timeout.Token, discovery: null, reuseIdle,
            asking ? new RespireServerException($"ASK 42 127.0.0.1:{oldServer.Port}") : null, redirectSource);
        try
        {
            await Assert.That(ReferenceEquals(pool, selected)).IsFalse();
            await Assert.That(connection.Port).IsEqualTo(asking ? oldServer.Port : newServer.Port);
            using var reply = await connection.SendAsync(new RawCommand(FakeRespServer.PingFrame), timeout.Token);
            await Assert.That(reply.AsString()).IsEqualTo("PONG");
            var owner = await router.GetConnectionAsync(42, timeout.Token, discovery: null);
            await Assert.That(owner.Port).IsEqualTo(newServer.Port);
        }
        catch (OperationCanceledException error)
        {
            throw new TimeoutException($"Rent target {connection.Port}; old wire: {string.Join(", ", oldServer.ReceivedCommands)}; "
                + $"old connections: {string.Join(", ", oldServer.ReceivedConnectionIds)}; "
                + $"new wire: {string.Join(", ", newServer.ReceivedCommands)}", error);
        }
        finally { pool.Return(connection); }
        await router.WaitForRetirementAsync().WaitAsync(timeout.Token);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RejectedAskCommandKeepsTemporaryTarget(bool tracked)
    {
        var full = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pings = 0;
        await using var target = new FakeRespServer(2, FakeRespServer.OkReply)
        {
            SuppressReply = command =>
            {
                if (command != "PING") return false;
                if (Interlocked.Increment(ref pings) == 4) full.TrySetResult();
                return true;
            },
        };
        var slot = ClusterHash.GetSlot("key");
        var ask = System.Text.Encoding.ASCII.GetBytes($"-ASK {slot} 127.0.0.1:{target.Port}\r\n");
        await using var source = new FakeRespServer(tracked ? [FakeRespServer.OkReply, ask] : [ask]);
        await using var client = CreateClient(maxInflightCommands: 4);
        using var timeout = new CancellationTokenSource(Limit);
        var router = client.Core.Cluster!;
        var sourceEndpoint = new RespireEndpoint("127.0.0.1", source.Port);
        Publish(router, sourceEndpoint, "source", 1);
        _ = await router.GetConnectionAsync(slot, timeout.Token, discovery: null);
        var oldTarget = router.GetMultiplexer(new("127.0.0.1", target.Port));
        await oldTarget.EnsureConnectedAsync(timeout.Token);
        var old = oldTarget.GetConnection();
        var accepted = Enumerable.Range(0, 4)
            .Select(_ => old.SendAsync(new RawCommand(FakeRespServer.PingFrame), timeout.Token).AsTask()).ToArray();
        await full.Task.WaitAsync(timeout.Token);
        var command = new Cmd2(RespireCommands.String.SET.Verb, "key", "value");
        var rebased = new List<bool>();
        Task<Respire.Protocol.RespValue> pending;
        if (tracked)
        {
            var method = typeof(RespireClient).GetMethod("SendTrackedClusterAsync", Private)!.MakeGenericMethod(typeof(Cmd2));
            Action onRedirect = () => rebased.Add(true);
            pending = ((ValueTask<Respire.Protocol.RespValue>)method.Invoke(client,
                ["SET", router, command, timeout.Token, onRedirect])!).AsTask();
        }
        else pending = client.SendAsync("SET", command, timeout.Token).AsTask();
        var signal = typeof(RespireConnection).GetField("_capacitySignal", Private)!.GetValue(old)!;
        var waiters = signal.GetType().GetField("_waiters", Private)!;
        while (waiters.GetValue(signal) is null)
        {
            if (pending.IsCompleted) { using var unexpected = await pending; throw new InvalidOperationException("ASK did not reach the full target queue."); }
            await Task.Delay(1, timeout.Token);
        }
        // The source stays the slot owner; only its temporary ASK target is detached.
        Publish(router, sourceEndpoint, "source", 2);
        using (var reply = await pending.WaitAsync(timeout.Token))
            await Assert.That(reply.AsString()).IsEqualTo("OK");
        await Assert.That(oldTarget.IsRetired).IsTrue();
        string[] expectedCommands = tracked
            ? ["ASKING", "CLIENT CACHING YES", "SET key value"]
            : ["ASKING", "SET key value"];
        await Assert.That(target.ReceivedCommands.Skip(4)).IsEquivalentTo(expectedCommands);
        await Assert.That(source.ReceivedCommands.Count(value => value == "SET key value")).IsEqualTo(1);
        await Assert.That((await router.GetConnectionAsync(slot, timeout.Token, discovery: null)).Port).IsEqualTo(source.Port);
        if (tracked) await Assert.That(rebased).IsEquivalentTo([true, true]);
        await target.SendRawAsync("+PONG\r\n+PONG\r\n+PONG\r\n+PONG\r\n"u8.ToArray(), 0);
        foreach (var task in accepted) { using var reply = await task.WaitAsync(timeout.Token); }
        await router.WaitForRetirementAsync().WaitAsync(timeout.Token);
    }

    [Test]
    [Arguments("retire")]
    [Arguments("cancel")]
    [Arguments("dispose")]
    public async Task DedicatedHandshakeRetriesOnlyRetirement(string action)
    {
        var handshake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var oldServer = new FakeRespServer(FakeRespServer.OkReply)
        {
            SuppressReply = command => { handshake.TrySetResult(); return true; },
        };
        await using var replacement = new FakeRespServer(2, FakeRespServer.OkReply);
        await using var client = CreateClient();
        using var timeout = new CancellationTokenSource(Limit);
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        var router = client.Core.Cluster!;
        // Use a dedicated pool with a parked SELECT handshake; the router only sends
        // application commands after rent succeeds and returns the owning pool.
        await using var selected = new DedicatedConnectionPool("127.0.0.1", oldServer.Port,
            new RespireConnectionOptions { Database = 1 }, null);
        Publish(router, new("127.0.0.1", replacement.Port), "new", 1);
        var pending = router.RentDedicatedConnectionAsync(selected, 42, caller.Token, discovery: null).AsTask();
        await handshake.Task.WaitAsync(timeout.Token);
        if (action == "cancel") caller.Cancel();
        if (action == "dispose") await client.DisposeAsync();
        await selected.RetireAsync();
        if (action != "retire")
        {
            await Assert.That(async () => await pending.WaitAsync(timeout.Token)).Throws<OperationCanceledException>();
            await Assert.That(replacement.CommandsSeen).IsEqualTo(0);
            return;
        }
        var (pool, connection) = await pending.WaitAsync(timeout.Token);
        try
        {
            using var reply = await connection.SendAsync(new RawCommand(FakeRespServer.PingFrame), timeout.Token);
            await Assert.That(reply.AsString()).IsEqualTo("OK");
            await Assert.That(connection.Port).IsEqualTo(replacement.Port);
            await Assert.That(ReferenceEquals(selected, pool)).IsFalse();
        }
        finally { pool.Return(connection); }
        await Assert.That(oldServer.ReceivedCommands).IsEquivalentTo(["SELECT 1"]);
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task RejectedTrackedExecutionPublishesNewIdentityBeforeWriting(bool script, bool configuredPolicy)
    {
        var full = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pings = 0;
        await using var oldServer = new FakeRespServer(":41\r\n"u8.ToArray(), ":0\r\n"u8.ToArray())
        {
            SuppressReply = command =>
            {
                if (command != "PING") return false;
                if (Interlocked.Increment(ref pings) == 4) full.TrySetResult();
                return true;
            },
        };
        Func<RespireClient.TrackedConnectionIdentity>? currentIdentity = null;
        RespireClient.TrackedConnectionIdentity? identityAtWrite = null;
        await using var newServer = new FakeRespServer(":42\r\n"u8.ToArray(), ":0\r\n"u8.ToArray(), ":1\r\n"u8.ToArray())
        {
            SuppressReply = command =>
            {
                if (command.StartsWith("EVALSHA ", StringComparison.Ordinal) || command.StartsWith("DELEX ", StringComparison.Ordinal))
                    identityAtWrite = currentIdentity!();
                return false;
            },
        };
        await using var client = CreateClient(maxInflightCommands: 4, reconnectPolicy: configuredPolicy
            ? new() { InitialDelay = TimeSpan.FromMilliseconds(10), JitterRatio = 0, MaxAttempts = 1 } : null);
        var scheduled = new TaskCompletionSource<RespireConnectionStateChange>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ConnectionStateChanged += change =>
        {
            if (change.ReconnectSource == RespireReconnectSource.ClusterDiscovery && change.NextReconnectDelay is not null)
                scheduled.TrySetResult(change);
        };
        using var timeout = new CancellationTokenSource(Limit);
        var router = client.Core.Cluster!;
        Publish(router, new("127.0.0.1", oldServer.Port), "old", 1);
        var old = await router.GetTrackedConnectionAsync(42, true, timeout.Token, discovery: null);
        var accepted = Enumerable.Range(0, 4)
            .Select(_ => old.SendAsync(new RawCommand(FakeRespServer.PingFrame), timeout.Token).AsTask()).ToArray();
        await full.Task.WaitAsync(timeout.Token);
        Task operation;
        if (script)
        {
            var execution = await client.StartTrackedScriptExecutionAsync(
                RespireScript.Create("return 1"), ["key"], [], timeout.Token, true);
            currentIdentity = () => execution.ConnectionIdentity;
            operation = CompleteScriptAsync(execution.Response);
        }
        else
        {
            var execution = await client.StartLockExecutionAsync("key", "token", null, true, false, timeout.Token);
            currentIdentity = () => execution.ConnectionIdentity;
            operation = execution.Response.AsTask();
        }
        await Assert.That(operation.IsCompleted).IsFalse();
        Publish(router, new("127.0.0.1", newServer.Port), "new", 2);
        await operation.WaitAsync(timeout.Token);
        if (configuredPolicy)
        {
            var change = await scheduled.Task.WaitAsync(timeout.Token);
            await Assert.That(change.ReconnectAttempt).IsEqualTo(1);
            await Assert.That(change.NextReconnectDelay).IsEqualTo(TimeSpan.FromMilliseconds(10));
            await Assert.That(change.Error).IsTypeOf<RespireConnectionRetiredException>();
        }
        await Assert.That(identityAtWrite.HasValue).IsTrue();
        await Assert.That(identityAtWrite!.Value.ServerClientId).IsEqualTo(42);
        await Assert.That(identityAtWrite.Value.Endpoint.Port).IsEqualTo(newServer.Port);
        await Assert.That(ReferenceEquals(identityAtWrite.Value.Connection, old)).IsFalse();
        await Assert.That(newServer.ReceivedCommands.Count).IsEqualTo(3);
        await Assert.That(oldServer.ReceivedCommands.Skip(2)).IsEquivalentTo(["PING", "PING", "PING", "PING"]);
        await oldServer.SendRawAsync("+PONG\r\n+PONG\r\n+PONG\r\n+PONG\r\n"u8.ToArray(), 0);
        foreach (var task in accepted) { using var reply = await task.WaitAsync(timeout.Token); }
        await router.WaitForRetirementAsync().WaitAsync(timeout.Token);

        static async Task CompleteScriptAsync(ValueTask<RespireResult> response)
        {
            using var result = await response;
            await Assert.That(result.AsInteger()).IsEqualTo(1);
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ReplacementDrainsAcceptedWorkAndKeepsNewGeneration(bool dedicated)
    {
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var commands = 0;
        await using var server = new FakeRespServer(4, FakeRespServer.PongReply)
        {
            SuppressReply = _ => { if (Interlocked.Increment(ref commands) != 1) return false; received.TrySetResult(); return true; },
        };
        await using var client = CreateClient();
        var router = client.Core.Cluster!;
        var endpoint = new RespireEndpoint("127.0.0.1", server.Port);
        Publish(router, endpoint, "old", 1);
        var old = router.GetMultiplexer(endpoint);
        await old.EnsureConnectedAsync();
        var pool = router.GetDedicatedPool(endpoint);
        var connection = dedicated ? await pool.RentAsync(default) : old.GetConnection();
        var pending = connection.SendAsync(new RawCommand(FakeRespServer.PingFrame)).AsTask();
        await received.Task.WaitAsync(Limit);
        Publish(router, endpoint, "new", 2);
        var current = router.GetMultiplexer(endpoint);
        await Assert.That(ReferenceEquals(old, current)).IsFalse();
        await Assert.That(old.IsRetired).IsTrue();
        await Assert.That(connection.IsConnected).IsTrue();
        await Assert.That(router.WaitForRetirementAsync().IsCompleted).IsFalse();
        await Assert.That(ReferenceEquals(pool, router.GetDedicatedPool(endpoint))).IsFalse();
        var snapshot = client.GetClusterRetirementSnapshot()!;
        await Assert.That(snapshot.RetiringGenerationCount).IsEqualTo(1);
        await Assert.That(snapshot.BorrowedDedicatedConnectionCount).IsEqualTo(dedicated ? 1L : 0L);
        await Assert.That(snapshot.PendingCorrectionFenceCount).IsEqualTo(0);
        await Assert.That(snapshot.CleanupFailedGenerationCount).IsEqualTo(0);
        if (!dedicated) await Assert.That(snapshot.UndrainedGenerationCount).IsEqualTo(1);
        await current.EnsureConnectedAsync();
        using (var reply = await current.SendAsync(new RawCommand(FakeRespServer.PingFrame)))
            await Assert.That(reply.AsString()).IsEqualTo("PONG");
        await server.SendRawAsync(FakeRespServer.PongReply, dedicated ? 1 : 0);
        using (var reply = await pending.WaitAsync(Limit))
            await Assert.That(reply.AsString()).IsEqualTo("PONG");
        if (dedicated) pool.Return(connection);
        await router.WaitForRetirementAsync().WaitAsync(Limit);
        await Assert.That(connection.IsConnected).IsFalse();
        await Assert.That(ReferenceEquals(current, router.GetMultiplexer(endpoint))).IsTrue();
        await Assert.That(Count(router, "_retiringNodes")).IsEqualTo(0);
        await Assert.That(RetainedNodes(router)).IsEqualTo(2);
        await Assert.That(client.GetClusterRetirementSnapshot()!.RetiringGenerationCount).IsEqualTo(0);
        await Assert.That(client.GetClusterRetirementSnapshot()!.OldestRetirementAge).IsEqualTo(TimeSpan.Zero);
        await Assert.That(snapshot.RetiringGenerationCount).IsEqualTo(1); // Owned historical observation.
    }

    [Test]
    public async Task RepeatedCompletedChurnBoundsIndexesHandlersAndPools()
    {
        await using var server = new FakeRespServer(32, FakeRespServer.PongReply);
        await using var client = CreateClient();
        var router = client.Core.Cluster!;
        var endpoint = new RespireEndpoint("127.0.0.1", server.Port);
        for (var generation = 1; generation <= 12; generation++)
        {
            Publish(router, endpoint, $"node-{generation}", generation);
            await router.WaitForRetirementAsync().WaitAsync(Limit);
            var node = router.GetMultiplexer(endpoint);
            await node.EnsureConnectedAsync();
            var pool = router.GetDedicatedPool(endpoint);
            using (var ready = await node.SendAsync(new RawCommand(FakeRespServer.PingFrame)))
                await Assert.That(ready.AsString()).IsEqualTo("PONG");
            var borrowed = await pool.RentAsync(default);
            using (var ready = await borrowed.SendAsync(new RawCommand(FakeRespServer.PingFrame)))
                await Assert.That(ready.AsString()).IsEqualTo("PONG");
            pool.Return(borrowed);
            await using var correction = router.GetCorrectionLease(node.GetConnection());
            await Assert.That(RetainedNodes(router)).IsEqualTo(2);
            await Assert.That(Count(router, "_nodeStateHandlers")).IsEqualTo(1);
            await Assert.That(Count(router, "_dedicatedPools")).IsEqualTo(1);
            await Assert.That(Count(router, "_correctionPools")).IsEqualTo(1);
            await Assert.That(Count(router, "_correctionStateHandlers")).IsEqualTo(1);
            await Assert.That(Count(router, "_ownedPools")).IsEqualTo(2);
            var identities = Identities(router);
            await Assert.That(identities.NodeIdCount).IsEqualTo(1);
            await Assert.That(identities.ReverseNodeIdCount).IsEqualTo(1);
            await Assert.That(identities.Endpoints.Count()).IsEqualTo(3); // seed, preferred, advertised alias
            await Assert.That(client.GetClusterRetirementSnapshot()!.RetiringGenerationCount).IsEqualTo(0);
        }
        await Assert.That(client.Core.Multiplexer.IsRetired).IsFalse();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ClientDisposalAbortsRetiringAcceptedWork(bool dedicated)
    {
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new FakeRespServer(2, FakeRespServer.PongReply)
        {
            SuppressReply = _ => { received.TrySetResult(); return true; },
        };
        await using var client = CreateClient();
        var router = client.Core.Cluster!;
        var endpoint = new RespireEndpoint("127.0.0.1", server.Port);
        Publish(router, endpoint, "old", 1);
        var node = router.GetMultiplexer(endpoint);
        await node.EnsureConnectedAsync();
        var pool = router.GetDedicatedPool(endpoint);
        var connection = dedicated ? await pool.RentAsync(default) : node.GetConnection();
        var pending = connection.SendAsync(new RawCommand(FakeRespServer.PingFrame)).AsTask();
        await received.Task.WaitAsync(Limit);
        Publish(router, endpoint, "new", 2);
        var snapshot = client.GetClusterRetirementSnapshot()!;
        await client.DisposeAsync().AsTask().WaitAsync(Limit);
        await Assert.That(async () => await pending).Throws<RespireConnectionException>();
        await router.WaitForRetirementAsync().WaitAsync(Limit);
        await Assert.That(connection.IsConnected).IsFalse();
        await Assert.That(snapshot.RetiringGenerationCount).IsEqualTo(1);
        await Assert.That(() => client.GetClusterRetirementSnapshot()).ThrowsExactly<ObjectDisposedException>();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RedirectRetriesGenerationRetiredDuringConnectionSetup(bool dedicated)
    {
        await using var sourceServer = new FakeRespServer(FakeRespServer.PongReply);
        await using var source = await RespireConnection.ConnectAsync("127.0.0.1", sourceServer.Port);
        using (var ready = await source.SendAsync(new RawCommand(FakeRespServer.PingFrame)))
            await Assert.That(ready.AsString()).IsEqualTo("PONG");
        await using var target = new FakeRespServer(2, FakeRespServer.PongReply);
        await using var client = CreateClient();
        var router = client.Core.Cluster!;
        var endpoint = new RespireEndpoint("127.0.0.1", target.Port);
        Publish(router, endpoint, "old", 1);
        var old = router.GetMultiplexer(endpoint);
        var gate = (SemaphoreSlim)typeof(RespireConnectionMultiplexer).GetField("_connectGate", Private)!.GetValue(old)!;
        using var timeout = new CancellationTokenSource(Limit);
        await gate.WaitAsync(timeout.Token);
        Task<RespireConnection>? connectionTask = null;
        Task<DedicatedConnectionPool>? poolTask = null;
        try
        {
            var moved = new RespireServerException($"MOVED 42 127.0.0.1:{target.Port}");
            if (dedicated) poolTask = router.GetRedirectDedicatedPoolAsync(moved, source, timeout.Token, commandSlot: null, discovery: null).AsTask();
            else connectionTask = router.GetRedirectConnectionAsync(moved, source, timeout.Token, commandSlot: null, discovery: null).AsTask();
            // Redirect setup is parked inside the old generation's connection gate.
            Publish(router, endpoint, "new", 2);
        }
        finally
        {
            gate.Release();
        }

        var current = router.GetMultiplexer(endpoint);
        await Assert.That(ReferenceEquals(old, current)).IsFalse();
        await Assert.That(old.IsRetired).IsTrue();
        DedicatedConnectionPool? pool = dedicated ? await poolTask!.WaitAsync(timeout.Token) : null;
        var connection = pool is null ? await connectionTask!.WaitAsync(timeout.Token) : await pool.RentAsync(timeout.Token);
        try
        {
            using var reply = await connection.SendAsync(new RawCommand(FakeRespServer.PingFrame), timeout.Token);
            await Assert.That(reply.AsString()).IsEqualTo("PONG");
            await Assert.That(ReferenceEquals(current, router.GetMultiplexer(endpoint))).IsTrue();
        }
        finally
        {
            pool?.Return(connection);
        }
        await router.WaitForRetirementAsync().WaitAsync(timeout.Token);
    }

    [Test]
    public async Task FailedNodeCleanupBeforeDrainRemainsOwnedAndFaultsRetirement()
    {
        await using var server = new FakeRespServer(FakeRespServer.PongReply);
        using var logger = new FailingPoolDisconnectLogger(failNodeDisconnect: true);
        await using var client = CreateClient(logger);
        var router = client.Core.Cluster!;
        var endpoint = new RespireEndpoint("127.0.0.1", server.Port);
        Publish(router, endpoint, "old", 1);
        var old = router.GetMultiplexer(endpoint);
        await old.EnsureConnectedAsync();
        using (var ready = await old.SendAsync(new RawCommand(FakeRespServer.PingFrame)))
            await Assert.That(ready.AsString()).IsEqualTo("PONG");
        Publish(router, endpoint, "new", 2);
        try
        {
            var error = await Assert.That(async () => await router.WaitForRetirementAsync().WaitAsync(Limit))
                .ThrowsExactly<InvalidOperationException>();
            await Assert.That(error).IsSameReferenceAs(logger.Failure);
            await Assert.That(old.RetirementDrained).IsFalse();
            await Assert.That(old.HasPendingCorrectionFences).IsFalse();
            await Assert.That(Count(router, "_retiringNodes")).IsEqualTo(1);
            await Assert.That(RetainedNodes(router)).IsEqualTo(3);
            var snapshot = client.GetClusterRetirementSnapshot()!;
            await Assert.That(snapshot.CleanupFailedGenerationCount).IsEqualTo(1);
            await Assert.That(snapshot.UndrainedGenerationCount).IsEqualTo(1);
            await Assert.That(snapshot.AwaitingFenceGenerationCount).IsEqualTo(0);
        }
        finally
        {
            await Assert.That(async () => await client.DisposeAsync().AsTask().WaitAsync(Limit))
                .ThrowsExactly<InvalidOperationException>();
        }
    }

    [Test]
    public async Task FailedDedicatedCleanupRemainsOwnedAndFaultsGenerationRetirement()
    {
        await using var server = new FakeRespServer(2, FakeRespServer.PongReply);
        using var logger = new FailingPoolDisconnectLogger();
        await using var client = CreateClient(logger);
        var router = client.Core.Cluster!;
        var endpoint = new RespireEndpoint("127.0.0.1", server.Port);
        Publish(router, endpoint, "old", 1);
        var node = router.GetMultiplexer(endpoint);
        await node.EnsureConnectedAsync();
        using (var ready = await node.SendAsync(new RawCommand(FakeRespServer.PingFrame)))
            await Assert.That(ready.AsString()).IsEqualTo("PONG");
        var pool = router.GetDedicatedPool(endpoint);
        var borrowed = await pool.RentAsync(default);
        using (var ready = await borrowed.SendAsync(new RawCommand(FakeRespServer.PingFrame)))
            await Assert.That(ready.AsString()).IsEqualTo("PONG");
        Publish(router, endpoint, "new", 2);
        var retirement = router.WaitForRetirementAsync();
        pool.Return(borrowed);
        var error = await Assert.That(async () => await retirement.WaitAsync(Limit)).ThrowsExactly<InvalidOperationException>();
        await Assert.That(error).IsSameReferenceAs(logger.Failure);
        await Assert.That(Count(router, "_retiringNodes")).IsEqualTo(1);
        await Assert.That(Count(router, "_ownedPools")).IsEqualTo(1);
        var snapshot = client.GetClusterRetirementSnapshot()!;
        await Assert.That(snapshot.CleanupFailedGenerationCount).IsEqualTo(1);
        await Assert.That(snapshot.BorrowedDedicatedConnectionCount).IsEqualTo(0);
        await Assert.That(snapshot.AwaitingFenceGenerationCount).IsEqualTo(0);
        await Assert.That(async () => await client.DisposeAsync().AsTask().WaitAsync(Limit)).ThrowsExactly<InvalidOperationException>();
        await Assert.That(borrowed.IsConnected).IsFalse();
    }

    [Test]
    public async Task UnexpectedRetirementFailureStillObservesLatePoolFailure()
    {
        var killSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new FakeRespServer(3, ":42\r\n"u8.ToArray())
        {
            SuppressReply = command =>
            {
                if (command != "CLIENT KILL ID 42") return false;
                killSeen.TrySetResult();
                return true;
            },
        };
        using var logger = new FailingPoolDisconnectLogger(failRetirementLog: true);
        await using var client = CreateClient(logger);
        var router = client.Core.Cluster!;
        var endpoint = new RespireEndpoint("127.0.0.1", server.Port);
        Publish(router, endpoint, "old", 1);
        var node = router.GetMultiplexer(endpoint);
        await node.EnsureConnectedAsync();
        var original = node.GetConnection();
        await original.EnsureServerClientIdAsync();
        var pool = router.GetDedicatedPool(endpoint);
        var borrowed = await pool.RentAsync(default);
        using (var ready = await borrowed.SendAsync(new RawCommand(FakeRespServer.PingFrame)))
            await Assert.That(ready.AsInteger()).IsEqualTo(42);
        await original.DisposeAsync();
        Publish(router, endpoint, "new", 2);
        var retirement = router.WaitForRetirementAsync();
        await killSeen.Task.WaitAsync(Limit);
        await server.SendRawAsync("-ERR unavailable\r\n"u8.ToArray(), 2);
        await logger.RetirementFailureSeen.Task.WaitAsync(Limit);
        pool.Return(borrowed);
        try
        {
            var error = await Assert.That(async () => await retirement.WaitAsync(Limit)).ThrowsExactly<AggregateException>();
            await Assert.That(error!.InnerExceptions).Contains(logger.RetirementFailure);
            await Assert.That(error.InnerExceptions).Contains(logger.Failure);
        }
        finally
        {
            // Explicit disposal completes all owned cleanup, then reports the same stored failure.
            await Assert.That(async () => await client.DisposeAsync().AsTask().WaitAsync(Limit)).Throws<Exception>();
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task DisposedFenceEntryHasDistinctSignal(bool waitForGate)
    {
        await using var node = RespireConnectionMultiplexer.Create("retired.invalid");
        var gate = (SemaphoreSlim)typeof(RespireConnectionMultiplexer).GetField("_retiredFenceGate", Private)!.GetValue(node)!;
        Task fencing;
        Task disposal;
        if (waitForGate)
        {
            await gate.WaitAsync();
            try
            {
                fencing = node.FenceRetiredConnectionsAsync().AsTask();
                disposal = node.DisposeAsync().AsTask();
            }
            finally
            {
                gate.Release();
            }
        }
        else
        {
            disposal = node.DisposeAsync().AsTask();
            await disposal.WaitAsync(Limit);
            fencing = node.FenceRetiredConnectionsAsync().AsTask();
        }
        await Assert.That(async () => await fencing.WaitAsync(Limit))
            .ThrowsExactly<RespireConnectionMultiplexer.CorrectionFenceDisposedException>();
        await disposal.WaitAsync(Limit);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ShutdownSignalDoesNotHideUnrelatedNodeFailure(bool disposeNode)
    {
        await using var client = CreateClient();
        var router = client.Core.Cluster!;
        var endpoint = new RespireEndpoint("retired.invalid");
        Publish(router, endpoint, "old", 1);
        var node = router.GetMultiplexer(endpoint);
        var nodeRetirement = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        typeof(RespireConnectionMultiplexer).GetField("_retirementCompletion", Private)!.SetValue(node, nodeRetirement);
        var failure = new ObjectDisposedException("unrelated resource");
        try
        {
            Publish(router, endpoint, "new", 2);
            var retirement = router.WaitForRetirementAsync();
            // The unrelated failure stays unexpected even if node disposal wins before
            // the asynchronous retirement continuation observes that failure.
            var stop = (CancellationTokenSource)typeof(ClusterRouter).GetField("_stopRetirement", Private)!.GetValue(router)!;
            stop.Cancel();
            var disposal = disposeNode ? node.DisposeAsync().AsTask() : Task.CompletedTask;
            nodeRetirement.SetException(failure);
            var error = await Assert.That(async () => await retirement.WaitAsync(Limit)).ThrowsExactly<ObjectDisposedException>();
            await Assert.That(error).IsSameReferenceAs(failure);
            await disposal.WaitAsync(Limit);
            await Assert.That(client.GetClusterRetirementSnapshot()!.CleanupFailedGenerationCount).IsEqualTo(1);
            await Assert.That(async () => await client.DisposeAsync().AsTask().WaitAsync(Limit)).ThrowsExactly<ObjectDisposedException>();
        }
        finally
        {
            nodeRetirement.TrySetException(failure);
            _ = nodeRetirement.Task.Exception;
        }
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task DisposalOnlySuppressesExpectedRetiredNodeShutdown(bool disposeFirst, bool unexpectedFailure)
    {
        await using var client = CreateClient();
        var router = client.Core.Cluster!;
        var endpoint = new RespireEndpoint("retired.invalid");
        Publish(router, endpoint, "old", 1);
        var node = router.GetMultiplexer(endpoint);
        // Hold the node's retirement completion at the boundary observed in the CI race.
        // Inject its exact terminal error after router disposal starts, without a timing loop.
        var nodeRetirement = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        typeof(RespireConnectionMultiplexer).GetField("_retirementCompletion", Private)!.SetValue(node, nodeRetirement);
        Exception failure = unexpectedFailure ? new InvalidOperationException("Unexpected retirement failure")
            : new RespireConnectionMultiplexer.CorrectionFenceDisposedException();
        try
        {
            Publish(router, endpoint, "new", 2);
            var retirement = router.WaitForRetirementAsync();
            await Assert.That(retirement.IsCompleted).IsFalse();
            if (disposeFirst)
            {
                var disposal = client.DisposeAsync().AsTask();
                var stop = (CancellationTokenSource)typeof(ClusterRouter).GetField("_stopRetirement", Private)!.GetValue(router)!;
                await Assert.That(stop.IsCancellationRequested).IsTrue();
                await Assert.That(disposal.IsCompleted).IsFalse();
                nodeRetirement.SetException(failure);
                if (unexpectedFailure)
                {
                    var error = await Assert.That(async () => await disposal.WaitAsync(Limit)).ThrowsExactly<InvalidOperationException>();
                    await Assert.That(error).IsSameReferenceAs(failure);
                }
                else
                {
                    await disposal.WaitAsync(Limit);
                    await Assert.That(Count(router, "_retiringNodes")).IsEqualTo(0);
                }
            }
            else
            {
                nodeRetirement.SetException(failure);
                var error = await Assert.That(async () => await retirement.WaitAsync(Limit)).Throws<Exception>();
                await Assert.That(error).IsSameReferenceAs(failure);
                await Assert.That(client.GetClusterRetirementSnapshot()!.CleanupFailedGenerationCount).IsEqualTo(1);
                await Assert.That(async () => await client.DisposeAsync().AsTask().WaitAsync(Limit)).Throws<Exception>();
            }
        }
        finally
        {
            nodeRetirement.TrySetException(failure);
            _ = nodeRetirement.Task.Exception; // Observe even when an earlier assertion failed.
        }
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    public async Task FenceDeadlineIsDistinctFromCallerCancellation(bool cancelCaller, bool blockControlConnection)
    {
        var killSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new FakeRespServer(2, ":42\r\n"u8.ToArray())
        {
            SuppressReply = command =>
            {
                if (command != "CLIENT KILL ID 42") return false;
                killSeen.TrySetResult();
                return true;
            },
        };
        await using var node = RespireConnectionMultiplexer.Create("unresolvable.invalid",
            options: new RespireConnectionOptions
            {
                ConnectTimeout = cancelCaller ? Limit : TimeSpan.FromMilliseconds(100),
                TestingStreamFactory = blockControlConnection ? async (_, _, token) =>
                {
                    // Expire the fence during connection setup, before CLIENT KILL can be sent.
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                    return Stream.Null;
                } : null,
            });
        await using var connection = await RespireConnection.ConnectAsync("127.0.0.1", server.Port);
        await connection.EnsureServerClientIdAsync();
        InstallPhysicalConnection(node, connection);
        await connection.DisposeAsync();
        using var cancel = new CancellationTokenSource();
        var fencing = node.FenceRetiredConnectionsAsync(cancel.Token).AsTask();
        if (cancelCaller)
        {
            // Caller cancellation specifically exercises an accepted, unacknowledged kill.
            await killSeen.Task.WaitAsync(Limit);
            cancel.Cancel();
            var error = await Assert.That(async () => await fencing.WaitAsync(Limit)).Throws<OperationCanceledException>();
            await Assert.That(error!.CancellationToken.IsCancellationRequested).IsTrue();
        }
        else
        {
            // The deadline covers connect, handshake, and reply. It may legitimately win
            // before the server sees CLIENT KILL, so observe the fence rather than receipt.
            var error = await Assert.That(async () => await fencing.WaitAsync(Limit)).ThrowsExactly<RespireTimeoutException>();
            await Assert.That(error!.Message).Contains("CLIENT KILL");
            await Assert.That(cancel.IsCancellationRequested).IsFalse();
        }
        if (blockControlConnection)
            await Assert.That(server.ReceivedCommands).DoesNotContain("CLIENT KILL ID 42");
        await Assert.That(node.HasPendingCorrectionFences).IsTrue();
    }

    [Test]
    public async Task LateFenceOfDrainedSocketDoesNotContactReplacementWithSameClientId()
    {
        await using var server = new FakeRespServer(3, ":42\r\n"u8.ToArray());
        await using var client = CreateClient();
        var router = client.Core.Cluster!;
        var endpoint = new RespireEndpoint("127.0.0.1", server.Port);
        Publish(router, endpoint, "old", 1);
        var node = router.GetMultiplexer(endpoint);
        await node.EnsureConnectedAsync();
        var original = node.GetConnection();
        await original.EnsureServerClientIdAsync();
        Publish(router, endpoint, "new", 2);
        await router.WaitForRetirementAsync().WaitAsync(Limit);
        var replacement = router.GetMultiplexer(endpoint);
        await replacement.EnsureConnectedAsync();
        await replacement.GetConnection().EnsureServerClientIdAsync();
        var identity = new RespireClient.TrackedConnectionIdentity(endpoint, 42, Connection: original);
        await client.FenceCorrectionConnectionAsync(identity).AsTask().WaitAsync(Limit);
        await Assert.That(replacement.GetConnection().IsConnected).IsTrue();
        await Assert.That(original.DrainedSuccessfully).IsTrue();
        await Assert.That(server.ReceivedCommands).DoesNotContain("CLIENT KILL ID 42");
        await Assert.That(Count(router, "_correctionPools")).IsEqualTo(0);
        await Assert.That(Count(router, "_ownedPools")).IsEqualTo(0);
    }

    [Test]
    public async Task CorrectionReservationSurvivesTopologyRemovalBeforeRent()
    {
        await using var server = new FakeRespServer(2, FakeRespServer.PongReply);
        await using var client = CreateClient();
        var router = client.Core.Cluster!;
        var endpoint = new RespireEndpoint("127.0.0.1", server.Port);
        Publish(router, endpoint, "old", 1);
        var old = router.GetMultiplexer(endpoint);
        await old.EnsureConnectedAsync();
        // Complete a round trip before retiring: TCP connect can finish before the fake
        // listener accepts, and Windows AcceptEx can fail if that socket closes first.
        using (var ready = await old.SendAsync(new RawCommand(FakeRespServer.PingFrame)))
            await Assert.That(ready.AsString()).IsEqualTo("PONG");
        var lease = router.GetCorrectionLease(old.GetConnection());
        Publish(router, endpoint, "new", 2);
        await router.WaitForRetirementAsync().WaitAsync(Limit);
        var control = await lease.Pool.RentAsync(default);
        using (var reply = await control.SendAsync(new RawCommand(FakeRespServer.PingFrame)))
            await Assert.That(reply.AsString()).IsEqualTo("PONG");
        lease.Pool.Return(control);
        await lease.DisposeAsync();
        await Assert.That(control.IsConnected).IsFalse();
        await Assert.That(Count(router, "_ownedPools")).IsEqualTo(0);
    }

    [Test]
    public async Task PhysicalPeerChangesPrunePoolsButSamePeerReconnectReusesPool()
    {
        await using var firstServer = new FakeRespServer(4, ":42\r\n"u8.ToArray());
        await using var secondServer = new FakeRespServer(2, ":42\r\n"u8.ToArray());
        await using var client = CreateClient();
        var router = client.Core.Cluster!;
        var endpoint = new RespireEndpoint("dns.invalid");
        Publish(router, endpoint, "stable", 1);
        var node = router.GetMultiplexer(endpoint);
        await using var original = await RespireConnection.ConnectAsync("127.0.0.1", firstServer.Port);
        await original.EnsureServerClientIdAsync();
        InstallPhysicalConnection(node, original);
        var firstLease = router.GetCorrectionLease(original);
        var firstPool = firstLease.Pool;
        var control = await firstPool.RentAsync(default);
        using (var ready = await control.SendAsync(new RawCommand(FakeRespServer.PingFrame)))
            await Assert.That(ready.AsInteger()).IsEqualTo(42);
        firstPool.Return(control);
        await firstLease.DisposeAsync();
        await using var reconnected = await RespireConnection.ConnectAsync("127.0.0.1", firstServer.Port);
        await reconnected.EnsureServerClientIdAsync();
        InstallPhysicalConnection(node, reconnected);
        await using (var samePeer = router.GetCorrectionLease(reconnected))
            await Assert.That(ReferenceEquals(samePeer.Pool, firstPool)).IsTrue();
        await using var replacement = await RespireConnection.ConnectAsync("127.0.0.1", secondServer.Port);
        await replacement.EnsureServerClientIdAsync();
        InstallPhysicalConnection(node, replacement);
        var handlers = (Action<int, RespireConnectionStateChange>?)typeof(RespireConnectionMultiplexer)
            .GetField("SlotStateChanged", Private)!.GetValue(node);
        handlers?.Invoke(0, new(endpoint, RespireConnectionState.Connected, null));
        await Assert.That(Count(router, "_correctionPools")).IsEqualTo(0);
        await Assert.That(Count(router, "_correctionStateHandlers")).IsEqualTo(0);
        await using (var newPeer = router.GetCorrectionLease(replacement))
            await Assert.That(ReferenceEquals(newPeer.Pool, firstPool)).IsFalse();
        // Await the already-started cleanup rather than depending on background timing.
        await firstPool.RetireAsync();
        await Assert.That(control.IsConnected).IsFalse();
        await client.FenceCorrectionConnectionAsync(new(endpoint, 42, Connection: original));
        await Assert.That(firstServer.ReceivedCommands).Contains("CLIENT KILL ID 42");
        await Assert.That(secondServer.ReceivedCommands).DoesNotContain("CLIENT KILL ID 42");
        await Assert.That(replacement.IsConnected).IsTrue();
        await Assert.That(Count(router, "_correctionPools")).IsEqualTo(1);
    }

    [Test]
    public async Task RetirementFenceUsesCapturedNetworkPeerInsteadOfResolvingHostname()
    {
        await using var server = new FakeRespServer(2, ":42\r\n"u8.ToArray());
        await using var node = RespireConnectionMultiplexer.Create("unresolvable.invalid");
        await using var connection = await RespireConnection.ConnectAsync("127.0.0.1", server.Port);
        await connection.EnsureServerClientIdAsync();
        InstallPhysicalConnection(node, connection);
        await connection.DisposeAsync();
        await node.RetireAsync().WaitAsync(Limit);
        await Assert.That(server.ReceivedCommands).Contains("CLIENT KILL ID 42");
        await Assert.That(node.HasPendingCorrectionFences).IsFalse();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task FailedFenceRetainsGenerationUntilAcknowledged(bool disposeBeforeAcknowledgement)
    {
        var firstSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var retrySeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var kills = 0;
        await using var server = new FakeRespServer(4, ":42\r\n"u8.ToArray(), "-ERR try again\r\n"u8.ToArray())
        {
            SuppressReply = command =>
            {
                if (command != "CLIENT KILL ID 42") return false;
                if (Interlocked.Increment(ref kills) == 1) firstSeen.TrySetResult();
                else retrySeen.TrySetResult();
                return true;
            },
        };
        await using var client = CreateClient();
        var router = client.Core.Cluster!;
        var endpoint = new RespireEndpoint("127.0.0.1", server.Port);
        Publish(router, endpoint, "old", 1);
        var old = router.GetMultiplexer(endpoint);
        await old.EnsureConnectedAsync();
        var connection = old.GetConnection();
        await connection.EnsureServerClientIdAsync();
        await connection.DisposeAsync();
        Publish(router, endpoint, "new", 2);
        await firstSeen.Task.WaitAsync(Limit);
        await server.SendRawAsync("-ERR try again\r\n"u8.ToArray(), 1);
        await retrySeen.Task.WaitAsync(Limit);
        await Assert.That(old.HasPendingCorrectionFences).IsTrue();
        await Assert.That(RetainedNodes(router)).IsEqualTo(3);
        await Assert.That(router.WaitForRetirementAsync().IsCompleted).IsFalse();
        var snapshot = client.GetClusterRetirementSnapshot()!;
        await Assert.That(snapshot.RetiringGenerationCount).IsEqualTo(1);
        await Assert.That(snapshot.PendingCorrectionFenceCount).IsEqualTo(1);
        await Assert.That(snapshot.AwaitingFenceGenerationCount).IsEqualTo(1);
        await Assert.That(snapshot.UndrainedGenerationCount).IsEqualTo(0);
        await Assert.That(snapshot.CleanupFailedGenerationCount).IsEqualTo(0);
        await Assert.That(snapshot.OldestRetirementAge).IsGreaterThan(TimeSpan.Zero);
        var later = client.GetClusterRetirementSnapshot()!;
        await Assert.That(later.OldestRetirementAge).IsGreaterThanOrEqualTo(snapshot.OldestRetirementAge);
        if (disposeBeforeAcknowledgement)
        {
            await client.DisposeAsync().AsTask().WaitAsync(Limit);
            await Assert.That(() => client.GetClusterRetirementSnapshot()).ThrowsExactly<ObjectDisposedException>();
            await Assert.That(snapshot.PendingCorrectionFenceCount).IsEqualTo(1);
            return;
        }
        await server.SendRawAsync(":1\r\n"u8.ToArray(), 2);
        await router.WaitForRetirementAsync().WaitAsync(Limit);
        await Assert.That(RetainedNodes(router)).IsEqualTo(2);
        await Assert.That(old.HasPendingCorrectionFences).IsFalse();
        await Assert.That(client.GetClusterRetirementSnapshot()!.PendingCorrectionFenceCount).IsEqualTo(0);
        await Assert.That(client.GetClusterRetirementSnapshot()!.RetiringGenerationCount).IsEqualTo(0);
        await Assert.That(snapshot.PendingCorrectionFenceCount).IsEqualTo(1);
        // The shared retirement task still contains its first failure after the router's
        // successful fence retry. A later correction must use the now-safe original peer.
        await Assert.That(old.RetireAsync().IsFaulted).IsTrue();
        await client.ExecuteOnAllConnectionsAsync(RespireScript.Create("return 1"), ["key"], [],
            new(endpoint, 42, Connection: connection)).AsTask().WaitAsync(Limit);
        await Assert.That(server.ReceivedCommands).Contains("EVAL return 1 1 key");
        await Assert.That(kills).IsEqualTo(2);
        await Assert.That(Count(router, "_ownedPools")).IsEqualTo(0);
    }

    // Model DNS resolution changes without mutating machine-wide DNS. Every installed
    // connection has a real socket, captured network peer and server-local client identity.
    private static void InstallPhysicalConnection(RespireConnectionMultiplexer node, RespireConnection connection)
    {
        var connections = (RespireConnection?[])typeof(RespireConnectionMultiplexer).GetField("_connections", Private)!.GetValue(node)!;
        connections[0] = connection;
        connection.Multiplexer = node;
    }

    [Test]
    [Arguments(false, "script")]
    [Arguments(true, "CLIENT ID / CLIENT KILL")]
    public async Task TrackedConnectionTimeoutNamesTheOperationThatWaited(bool requireIdentity, string operation)
    {
        // The RESP3 handshake never completes, so selecting the slot owner's connection times out.
        await using var server = new FakeRespServer(":1\r\n"u8.ToArray()) { SuppressReply = _ => true };
        await using var client = RespireClient.Create(new RespireOptions
        {
            Protocol = RespProtocol.Resp3,
            UseCluster = true, Connections = 1, Endpoints = { new RespireEndpoint("seed.invalid") },
            CommandTimeout = TimeSpan.FromMilliseconds(200),
        });
        Publish(client.Core.Cluster!, new("127.0.0.1", server.Port), "node", 1);

        var error = await Assert.That(async () =>
            {
                var execution = await client.StartTrackedScriptExecutionAsync(
                    RespireScript.Create("return 1"), ["key"], [], CancellationToken.None,
                    requireReliableCorrectionOrdering: requireIdentity, captureSendTimestampOnly: !requireIdentity);
                using var _ = await execution.Response;
            }).Throws<RespireTimeoutException>();

        // A capture-only script never asks for a client identity, so ACL guidance would mislead.
        await Assert.That(error!.CommandName).IsEqualTo(operation);
    }

    [Test]
    public async Task TrackedScriptSendTimestampFollowsTheRedirectedSend()
    {
        await using var server = new FakeRespServer(2, FakeRespServer.OkReply);
        await using var client = CreateClient();
        using var timeout = new CancellationTokenSource(Limit);
        Publish(client.Core.Cluster!, new("127.0.0.1", server.Port), "owner", 1);
        var slot = ClusterHash.GetSlot("key");
        var evals = 0;
        long redirectedAt = 0;
        server.ReplyOverride = (_, command) =>
        {
            if (command == "CLIENT ID") return ":42\r\n"u8.ToArray();
            if (!command.StartsWith("EVALSHA ", StringComparison.Ordinal)) return null;
            if (Interlocked.Increment(ref evals) > 1) return ":1\r\n"u8.ToArray();
            Volatile.Write(ref redirectedAt, System.Diagnostics.Stopwatch.GetTimestamp());
            return System.Text.Encoding.ASCII.GetBytes($"-MOVED {slot} 127.0.0.1:{server.Port}\r\n");
        };

        var execution = await client.StartTrackedScriptExecutionAsync(
            RespireScript.Create("return 1"), ["key"], [], timeout.Token, captureSendTimestampOnly: true);
        using (await execution.Response) { }

        // Lease-based callers measure validity from this timestamp, so it must belong to the
        // redirected send, not the rejected first attempt.
        await Assert.That(evals).IsEqualTo(2);
        await Assert.That(execution.StartedTimestamp).IsGreaterThan(Volatile.Read(ref redirectedAt));
    }

    private static RespireClient CreateClient(ILoggerFactory? loggerFactory = null, int maxInflightCommands = 16384,
        bool allowAdmin = false, RespireReconnectPolicy? reconnectPolicy = null) => RespireClient.Create(new RespireOptions
    {
        Protocol = RespProtocol.Resp2,
        UseCluster = true, Connections = 1, Endpoints = { new RespireEndpoint("seed.invalid") },
        LoggerFactory = loggerFactory,
        MaxInflightCommands = maxInflightCommands,
        AllowAdmin = allowAdmin,
        ReconnectPolicy = reconnectPolicy,
    });

    private sealed class FailingPoolDisconnectLogger(bool failRetirementLog = false, bool failNodeDisconnect = false) : ILoggerFactory, ILogger
    {
        internal readonly InvalidOperationException Failure = new("Test dedicated disconnect failure.");
        internal readonly InvalidOperationException RetirementFailure = new("Test retirement failure.");
        internal readonly TaskCompletionSource RetirementFailureSeen = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ILogger CreateLogger(string categoryName) => categoryName.Contains(".Blocking.", StringComparison.Ordinal)
            || (failNodeDisconnect && categoryName.StartsWith("Respire.Cluster.127.", StringComparison.Ordinal))
            || (failRetirementLog && categoryName == "Respire.Cluster") ? this : NullLogger.Instance;
        public void AddProvider(ILoggerProvider provider) { }
        public void Dispose() { }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (failRetirementLog && logLevel == LogLevel.Debug
                && formatter(state, exception).StartsWith("Cluster generation retirement needs", StringComparison.Ordinal))
            {
                RetirementFailureSeen.TrySetResult();
                throw RetirementFailure;
            }
            if (logLevel == LogLevel.Debug && formatter(state, exception).StartsWith("Disconnected from", StringComparison.Ordinal))
                throw Failure;
        }
    }

    private static ClusterNodeIdentityIndex Identities(ClusterRouter router)
        => (ClusterNodeIdentityIndex)typeof(ClusterRouter).GetField("_identities", Private)!.GetValue(router)!;

    private static int RetainedNodes(ClusterRouter router)
    {
        lock (router.NodeStateGate) return Identities(router).All.Count();
    }

    private static int Count(ClusterRouter router, string field)
    {
        lock (router.NodeStateGate)
        {
            var value = typeof(ClusterRouter).GetField(field, Private)!.GetValue(router)!;
            return (int)value.GetType().GetProperty("Count")!.GetValue(value)!;
        }
    }

    private static void Publish(ClusterRouter router, RespireEndpoint endpoint, string id, long generation)
    {
        var version = (long)typeof(ClusterRouter).GetField("_topologyVersion", Private)!.GetValue(router)!;
        List<ClusterTopologyRange> ranges = [new(0, 16383, endpoint, id, [new("alias.invalid", endpoint.Port)])];
        typeof(ClusterRouter).GetMethod("ApplyTopology", Private)!.Invoke(router, [ranges, version, generation]);
    }
}

