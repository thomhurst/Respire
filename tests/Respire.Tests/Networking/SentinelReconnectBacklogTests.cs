using System.Net.Sockets;
using System.Text;
using Respire.Commands;
using Respire.Internal;
using Respire.Protocol;
using TUnit.Assertions;
using TUnit.Assertions.Enums;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public partial class SentinelRoutingTests
{
    private static readonly byte[] BacklogValueReply = "$5\r\nvalue\r\n"u8.ToArray();

    [Test]
    public async Task BacklogSnapshotOwnsKeysAndPreservesCommandCallbacks()
    {
        var bytes = "key"u8.ToArray();
        var callbacks = new BacklogCallbacks();
        using var responseCancellation = new CancellationTokenSource();
        var command = new BorrowedBacklogCommand(bytes, callbacks, responseCancellation.Token);
        var snapshot = new RespireClient.BacklogCommand<BorrowedBacklogCommand>(command, "GET");
        Array.Fill(bytes, (byte)'x');

        await Assert.That(snapshot.TryGetPrimaryKey(out var primaryKey)).IsTrue();
        await Assert.That(primaryKey.ToString()).IsEqualTo("key");
        await Assert.That(snapshot.TryGetClientCacheKey("GET", out var query)).IsTrue();
        await Assert.That(query.GetArgument(0).ToString()).IsEqualTo("key");
        await Assert.That(snapshot.TryGetArgument(0, out var argument)).IsTrue();
        await Assert.That(argument.ToString()).IsEqualTo("key");
        await Assert.That(snapshot.GetCacheMutation("GET")).IsEqualTo(RespireCacheMutation.ReadOnly);
        await Assert.That(snapshot.GetClientCacheMetadata("GET").CacheableRead).IsTrue();
        await Assert.That(snapshot.GetResponseCancellationToken(default)).IsEqualTo(responseCancellation.Token);
        await Assert.That(callbacks.Accepted).IsEqualTo(0);
        snapshot.ValidateAdmission();
        snapshot.OnAccepted();
        await Assert.That(callbacks.Validated).IsEqualTo(1);
        await Assert.That(callbacks.Accepted).IsEqualTo(1);
    }

    private sealed class BacklogCallbacks
    {
        internal int Validated;
        internal int Accepted;
    }

    private readonly struct BorrowedBacklogCommand(byte[] key, BacklogCallbacks callbacks,
        CancellationToken responseToken) : IRespCommand
    {
        public ReadCommandKind ReadKind => ReadCommandKind.None;
        public void Write(ref RespWriter writer)
        {
            writer.WriteArrayHeader(2);
            writer.WriteBulkString("GET"u8);
            writer.WriteBulkString(key);
        }
        public bool TryGetPrimaryKey(out RespireValue value) { value = key; return true; }
        public bool TryGetClientCacheKey(string operation, out ClientCacheCommandKey value)
        { value = new(operation, (RespireValue)key); return true; }
        public bool TryGetArgument(int index, out RespireValue value)
        { value = index == 0 ? key : default; return index == 0; }
        public void ValidateAdmission() => callbacks.Validated++;
        public void OnAccepted() => callbacks.Accepted++;
        public CancellationToken GetResponseCancellationToken(CancellationToken token) => responseToken;
    }

    [Test]
    [Arguments("string")]
    [Arguments("bytes")]
    [Arguments("typed")]
    [Arguments("raw")]
    [Arguments("catalog")]
    [Arguments("stream")]
    [Arguments("write")]
    [Arguments("fire-and-forget")]
    public async Task SentinelBacklogReselectsPrimaryBeforeDispatch(string surface)
    {
        await using var fixture = await SentinelBacklogFixture.CreateAsync();
        var pending = SendSentinelBacklogCommandAsync(fixture.Client, surface, "queued");
        await fixture.Selecting.Task.WaitAsync(Limit);
        await Assert.That(pending.IsCompleted).IsFalse();
        await Assert.That(fixture.Client.Core.Sentinel!.GetReconnectBacklog().Count).IsEqualTo(1);
        fixture.Resume.TrySetResult();
        await Assert.That(await pending.WaitAsync(Limit)).IsEqualTo(surface is "write" or "fire-and-forget" ? "OK" : "value");
        await Assert.That(await fixture.Client.GetStringAsync("barrier")).IsEqualTo("value");
        await Assert.That(fixture.Client.Endpoint.Port).IsEqualTo(fixture.Replacement.Port);
        await Assert.That(fixture.Original.ReceivedCommands.Any(IsSentinelBacklogApplicationCommand)).IsFalse();
        await Assert.That(fixture.Replacement.ReceivedCommands.Count(command => command is "GET queued" or "SET queued value"))
            .IsEqualTo(1);
    }

    [Test]
    public async Task SentinelBacklogKeepsArrivalsBehindDispatchedHeadAndBoundsCapacity()
    {
        await using var fixture = await SentinelBacklogFixture.CreateAsync(capacity: 1);
        var accepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Replacement.SuppressReply = command =>
        {
            if (command != "GET first") return false;
            accepted.TrySetResult();
            return true;
        };
        var first = fixture.Client.GetStringAsync("first").AsTask();
        await fixture.Selecting.Task.WaitAsync(Limit);
        await Assert.That(async () => await fixture.Client.GetStringAsync("overflow-before-recovery").AsTask().WaitAsync(Limit))
            .ThrowsExactly<RespireConnectionException>();
        fixture.Resume.TrySetResult();
        await accepted.Task.WaitAsync(Limit);
        var second = fixture.Client.SetAsync("second", "value").AsTask();
        await Assert.That(second.IsCompleted).IsFalse();
        await Assert.That(fixture.Replacement.ReceivedCommands.Contains("SET second value")).IsFalse();
        await Assert.That(async () => await fixture.Client.GetStringAsync("overflow-during-drain").AsTask().WaitAsync(Limit))
            .ThrowsExactly<RespireConnectionException>();
        await fixture.Replacement.SendRawAsync(BacklogValueReply);
        await Assert.That(await first.WaitAsync(Limit)).IsEqualTo("value");
        await Assert.That(await second.WaitAsync(Limit)).IsTrue();
        await Assert.That(fixture.Replacement.ReceivedCommands.Where(IsSentinelBacklogApplicationCommand).ToArray())
            .IsEquivalentTo(new[] { "GET first", "SET second value" }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task SentinelBacklogCancellationReleasesCapacityAndPreservesCallerToken()
    {
        await using var fixture = await SentinelBacklogFixture.CreateAsync(capacity: 1, noCommandTimeout: true);
        using var caller = new CancellationTokenSource();
        var pending = fixture.Client.GetStringAsync("cancelled", caller.Token).AsTask();
        await fixture.Selecting.Task.WaitAsync(Limit);
        await caller.CancelAsync();
        var error = await Assert.That(async () => await pending.WaitAsync(Limit)).Throws<OperationCanceledException>();
        await Assert.That(error!.CancellationToken).IsEqualTo(caller.Token);
        await Assert.That(fixture.Client.Core.Sentinel!.GetReconnectBacklog().Count).IsEqualTo(0);
        var next = fixture.Client.GetStringAsync("next").AsTask();
        fixture.Resume.TrySetResult();
        await Assert.That(await next.WaitAsync(Limit)).IsEqualTo("value");
        await Assert.That(fixture.Replacement.ReceivedCommands.Where(IsSentinelBacklogApplicationCommand).ToArray())
            .IsEquivalentTo(new[] { "GET next" }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task SentinelBacklogCommandDeadlineIncludesDiscoveryWait()
    {
        await using var fixture = await SentinelBacklogFixture.CreateAsync(commandTimeout: TimeSpan.FromSeconds(1));
        // Let the logical command deadline own the failure, independently of the resolver's
        // per-candidate deadline, which normally uses the same timeout value.
        fixture.Client.Core.Sentinel!.CreateDiscoveryTimeout = (token, _) => CancellationTokenSource.CreateLinkedTokenSource(token);
        var pending = fixture.Client.GetStringAsync("expired").AsTask();
        await fixture.Selecting.Task.WaitAsync(Limit);
        var error = await Assert.That(async () => await pending.WaitAsync(Limit)).ThrowsExactly<RespireTimeoutException>();
        await Assert.That(error!.Diagnostics.Stage).IsEqualTo(RespireCommandStage.Connecting);
        await Assert.That(error.Endpoint).IsEqualTo((RespireEndpoint?)new RespireEndpoint("127.0.0.1", fixture.Replacement.Port));
        await Assert.That(fixture.Client.Core.Sentinel.GetReconnectBacklog().Count).IsEqualTo(0);
        await Assert.That(fixture.Replacement.ReceivedCommands.Any(IsSentinelBacklogApplicationCommand)).IsFalse();
    }

    [Test]
    public async Task SentinelBacklogDisposalCompletesEveryUnsentWaiter()
    {
        await using var fixture = await SentinelBacklogFixture.CreateAsync(noCommandTimeout: true);
        var first = fixture.Client.GetStringAsync("first").AsTask();
        await fixture.Selecting.Task.WaitAsync(Limit);
        var second = fixture.Client.SetAsync("second", "value").AsTask();
        await fixture.Client.DisposeAsync().AsTask().WaitAsync(Limit);
        await Assert.That(async () => await first.WaitAsync(Limit)).Throws<ObjectDisposedException>();
        await Assert.That(async () => await second.WaitAsync(Limit)).Throws<ObjectDisposedException>();
        await Assert.That(fixture.Replacement.ReceivedCommands.Any(IsSentinelBacklogApplicationCommand)).IsFalse();
    }

    [Test]
    public async Task SentinelFailFastDoesNotQueueFailedDiscovery()
    {
        await using var fixture = await SentinelBacklogFixture.CreateAsync(policy: RespireBacklogPolicy.FailFast);
        fixture.FailReplacement = true;
        fixture.Resume.TrySetResult();
        await Assert.That(async () => await fixture.Client.GetStringAsync("unsent").AsTask().WaitAsync(Limit))
            .Throws<RespireConnectionException>();
        await Assert.That(fixture.Client.Core.Sentinel!.IsBacklogDraining).IsFalse();
        await Assert.That(fixture.Replacement.ReceivedCommands.Any(IsSentinelBacklogApplicationCommand)).IsFalse();
    }

    [Test]
    public async Task SentinelBacklogRecoveryExhaustionFailsAllPendingUnits()
    {
        await using var fixture = await SentinelBacklogFixture.CreateAsync(reconnectPolicy: new()
        {
            MaxAttempts = 1, InitialDelay = TimeSpan.Zero, MaxDelay = TimeSpan.Zero, JitterRatio = 0,
        });
        fixture.FailReplacement = true;
        var first = fixture.Client.GetStringAsync("first").AsTask();
        await fixture.Selecting.Task.WaitAsync(Limit);
        var second = fixture.Client.SetAsync("second", "value").AsTask();
        fixture.Resume.TrySetResult();
        await Assert.That(async () => await first.WaitAsync(Limit)).ThrowsExactly<RespireReconnectLimitException>();
        await Assert.That(async () => await second.WaitAsync(Limit)).ThrowsExactly<RespireReconnectLimitException>();
        await Assert.That(fixture.Client.Core.Sentinel!.GetReconnectBacklog().Count).IsEqualTo(0);
        await Assert.That(fixture.ReplacementAttempts).IsEqualTo(2);
        await Assert.That(fixture.Replacement.ReceivedCommands.Any(IsSentinelBacklogApplicationCommand)).IsFalse();
    }

    [Test]
    public async Task SentinelBacklogNeverReplaysAcceptedFailure()
    {
        await using var fixture = await SentinelBacklogFixture.CreateAsync();
        var accepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Replacement.SuppressReply = command =>
        {
            if (command != "SET accepted value") return false;
            accepted.TrySetResult();
            return true;
        };
        var pending = fixture.Client.SetAsync("accepted", "value").AsTask();
        await fixture.Selecting.Task.WaitAsync(Limit);
        fixture.Resume.TrySetResult();
        await accepted.Task.WaitAsync(Limit);
        fixture.Replacement.CloseConnections();
        await Assert.That(async () => await pending.WaitAsync(Limit)).Throws<RespireConnectionException>();
        await Assert.That(await fixture.Client.GetStringAsync("barrier").AsTask().WaitAsync(Limit)).IsEqualTo("value");
        await Assert.That(fixture.Replacement.ReceivedCommands.Count(command => command == "SET accepted value")).IsEqualTo(1);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task SentinelBacklogChecksTheReplacementCircuitInsteadOfTheRetiredPrimary(bool replacementOpen)
    {
        await using var fixture = await SentinelBacklogFixture.CreateAsync(circuitBreaker: new()
        {
            MinimumFailureCount = 1, FailureRateThreshold = 1, OpenDuration = TimeSpan.FromMinutes(1),
        });
        OpenBacklogCircuit(fixture.Client, fixture.Original.Port);
        var pending = fixture.Client.GetStringAsync("queued").AsTask();
        await fixture.Selecting.Task.WaitAsync(Limit);
        await Assert.That(pending.IsCompleted).IsFalse();
        if (replacementOpen) OpenBacklogCircuit(fixture.Client, fixture.Replacement.Port);
        fixture.Resume.TrySetResult();
        if (replacementOpen)
        {
            var error = await Assert.That(async () => await pending.WaitAsync(Limit)).ThrowsExactly<RespireCircuitOpenException>();
            await Assert.That(error!.Endpoint).IsEqualTo(new RespireEndpoint("127.0.0.1", fixture.Replacement.Port));
            await Assert.That(fixture.Replacement.ReceivedCommands.Contains("GET queued")).IsFalse();
        }
        else
        {
            await Assert.That(await pending.WaitAsync(Limit)).IsEqualTo("value");
            await Assert.That(fixture.Replacement.ReceivedCommands.Count(command => command == "GET queued")).IsEqualTo(1);
        }
        await Assert.That(fixture.Original.ReceivedCommands.Any(IsSentinelBacklogApplicationCommand)).IsFalse();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task SentinelBacklogSnapshotsBorrowedWriteArguments(bool fireAndForget)
    {
        await using var fixture = await SentinelBacklogFixture.CreateAsync();
        var bytes = "value"u8.ToArray();
        async Task SendAsync()
        {
            if (fireAndForget) await fixture.Client.ExecuteFireAndForgetAsync("SET", ["key", bytes]);
            else { using var reply = await fixture.Client.ExecuteAsync("SET", ["key", bytes]); }
        }
        var pending = SendAsync();
        await fixture.Selecting.Task.WaitAsync(Limit);
        Array.Fill(bytes, (byte)'x');
        fixture.Resume.TrySetResult();
        await pending.WaitAsync(Limit);
        await fixture.Client.GetStringAsync("barrier");
        await Assert.That(fixture.Replacement.ReceivedCommands.Where(IsSentinelBacklogApplicationCommand).ToArray())
            .IsEquivalentTo(new[] { "SET key value", "GET barrier" }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task SentinelBacklogRecoveryDoesNotSpendTheCommandRetryBudgetOrReleaseItsFifoTurn()
    {
        await using var fixture = await SentinelBacklogFixture.CreateAsync(retryPolicy: new()
        {
            MaxAttempts = 2, InitialBackoff = TimeSpan.Zero, MaxBackoff = TimeSpan.Zero,
        }, reconnectPolicy: new() { InitialDelay = TimeSpan.Zero, MaxDelay = TimeSpan.Zero, JitterRatio = 0 });
        fixture.ReplacementFailuresRemaining = 1;
        var sends = 0;
        fixture.Replacement.ReplyOverride = (_, command) => command switch
        {
            "ROLE" => PrimaryRole,
            "GET first" => Interlocked.Increment(ref sends) == 1 ? "-TRYAGAIN retry\r\n"u8.ToArray() : BacklogValueReply,
            "GET second" => BacklogValueReply,
            _ => FakeRespServer.OkReply,
        };
        var first = fixture.Client.GetStringAsync("first").AsTask();
        await fixture.Selecting.Task.WaitAsync(Limit);
        var second = fixture.Client.GetStringAsync("second").AsTask();
        fixture.Resume.TrySetResult();
        await Assert.That(await first.WaitAsync(Limit)).IsEqualTo("value");
        await Assert.That(await second.WaitAsync(Limit)).IsEqualTo("value");
        await Assert.That(fixture.ReplacementAttempts).IsEqualTo(2);
        await Assert.That(fixture.Replacement.ReceivedCommands.Where(IsSentinelBacklogApplicationCommand).ToArray())
            .IsEquivalentTo(new[] { "GET first", "GET first", "GET second" }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task SentinelBacklogPreservesTrackedReadAndReplacementCachePopulation()
    {
        await using var fixture = await SentinelBacklogFixture.CreateAsync(clientCache: true);
        var pending = fixture.Client.GetStringAsync("key").AsTask();
        await fixture.Selecting.Task.WaitAsync(Limit);
        fixture.Resume.TrySetResult();
        await Assert.That(await pending.WaitAsync(Limit)).IsEqualTo("value");
        await Assert.That(fixture.Replacement.ReceivedCommands.Count(command => command == "CLIENT CACHING YES")).IsEqualTo(1);
        await Assert.That(fixture.Replacement.ReceivedCommands.Count(command => command == "GET key")).IsEqualTo(1);
        await Assert.That(await fixture.Client.GetStringAsync("key")).IsEqualTo("value");
        await Assert.That(fixture.Replacement.ReceivedCommands.Count(command => command == "GET key")).IsEqualTo(1);
    }

    private static void OpenBacklogCircuit(RespireClient client, int port)
    {
        var admission = client.Core.Circuits!.Acquire(new("127.0.0.1", port), default);
        try { admission.Failed(new RespireConnectionException("Controlled endpoint failure."), default); }
        finally { admission.Dispose(); }
    }

    [Test]
    public async Task SentinelBacklogDiscoveryCannotUpdateReturnedObservationStorage()
    {
        var caller = Internal.RespireTelemetry.ErrorObservation.Rent(force: true);
        var forwarding = new Internal.BacklogDiscoveryObservation(caller);
        var borrowed = forwarding.Observation;
        var selected = new RespireEndpoint("candidate", 6379);
        borrowed.UseEndpoint(selected);
        borrowed.Retry();
        await Assert.That(caller.Endpoint).IsEqualTo((RespireEndpoint?)selected);
        await Assert.That(caller.Attempts).IsEqualTo(1);
        forwarding.Detach();
        caller.Dispose();
        using var next = Internal.RespireTelemetry.ErrorObservation.Rent(force: true);
        // A canceled queue head can still finish discovery after its caller consumed the
        // failure. Exercise every mutation the old selector can make after detachment.
        borrowed.UseEndpoint(new("late", 6380));
        borrowed.Retry();
        borrowed.SetAttempts(4);
        borrowed.Handled(new RespireConnectionException("Late selector cleanup."));
        await Assert.That(borrowed.IsOpen).IsFalse();
        await Assert.That(next.Endpoint).IsNull();
        await Assert.That(next.Attempts).IsEqualTo(0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task SentinelBacklogQueuesBatchOrTransactionAsOneUnit(bool transaction)
    {
        await using var fixture = await SentinelBacklogFixture.CreateAsync(capacity: 1);
        var inTransaction = false;
        fixture.Replacement.ReplyOverride = (_, command) =>
        {
            if (command == "ROLE") return PrimaryRole;
            if (command == "MULTI") { inTransaction = true; return FakeRespServer.OkReply; }
            if (command == "EXEC") { inTransaction = false; return "*2\r\n$5\r\nvalue\r\n+OK\r\n"u8.ToArray(); }
            if (inTransaction) return "+QUEUED\r\n"u8.ToArray();
            return command.StartsWith("GET ", StringComparison.Ordinal) ? BacklogValueReply : FakeRespServer.OkReply;
        };
        using var batch = transaction ? null : fixture.Client.CreateBatch();
        await using var commit = transaction ? fixture.Client.CreateTransaction() : null;
        var first = transaction ? commit!.GetString("first") : batch!.GetString("first");
        var second = transaction ? commit!.Set("second", "value") : batch!.Set("second", "value");
        var execution = transaction ? CommitSentinelBacklogTransactionAsync(commit!) : batch!.ExecuteAsync().AsTask();
        await fixture.Selecting.Task.WaitAsync(Limit);
        await Assert.That(execution.IsCompleted).IsFalse();
        await Assert.That(async () => await fixture.Client.GetStringAsync("overflow").AsTask().WaitAsync(Limit))
            .ThrowsExactly<RespireConnectionException>();
        fixture.Resume.TrySetResult();
        await execution.WaitAsync(Limit);
#pragma warning disable RESP002 // The owning execution completed before the queued results are consumed.
        await Assert.That(await first).IsEqualTo("value");
        await Assert.That(await second).IsTrue();
#pragma warning restore RESP002
        await Assert.That(fixture.Original.ReceivedCommands.Any(IsSentinelBacklogApplicationCommand)).IsFalse();
        await Assert.That(fixture.Replacement.ReceivedCommands.Where(IsSentinelBacklogApplicationCommand).ToArray())
            .IsEquivalentTo(transaction ? new[] { "MULTI", "GET first", "SET second value", "EXEC" }
                : new[] { "GET first", "SET second value" }, CollectionOrdering.Matching);
    }

    private static async Task CommitSentinelBacklogTransactionAsync(RespireTransaction transaction)
        => await transaction.CommitAsync();

    private static bool IsSentinelBacklogApplicationCommand(string command)
        => command.StartsWith("GET ", StringComparison.Ordinal) || command.StartsWith("SET ", StringComparison.Ordinal)
            || command is "MULTI" or "EXEC";

    private static async Task<string> SendSentinelBacklogCommandAsync(RespireClient client, string surface, string key)
    {
        switch (surface)
        {
            case "string": return (await client.GetStringAsync(key))!;
            case "bytes": return Encoding.UTF8.GetString((await client.GetBytesAsync(key))!);
            case "typed": return (await client.GetAsync<string>(key))!;
            case "stream":
                await using (var stream = await client.Strings.GetStreamAsync(key))
                using (var reader = new StreamReader(stream!)) return await reader.ReadToEndAsync();
            case "write": return await client.SetAsync(key, "value") ? "OK" : "failed";
            case "fire-and-forget": await client.ExecuteFireAndForgetAsync("SET", key, "value"); return "OK";
            default:
                using (var reply = surface == "catalog" ? await client.ExecuteAsync(RespireCommands.String.GET, key)
                    : await client.ExecuteAsync("GET", key)) return reply.AsString()!;
        }
    }

    private sealed class SentinelBacklogFixture : IAsyncDisposable
    {
        internal FakeRespServer Original { get; private set; } = null!;
        internal FakeRespServer Replacement { get; private set; } = null!;
        private FakeRespServer _sentinel = null!;
        internal RespireClient Client { get; private set; } = null!;
        internal TaskCompletionSource Selecting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool FailReplacement;
        internal int ReplacementAttempts;
        internal int ReplacementFailuresRemaining;

        internal static async Task<SentinelBacklogFixture> CreateAsync(int capacity = 4,
            TimeSpan? commandTimeout = null, bool noCommandTimeout = false,
            RespireBacklogPolicy policy = RespireBacklogPolicy.Queue, RespireReconnectPolicy? reconnectPolicy = null,
            RespireRetryPolicy? retryPolicy = null, RespireCircuitBreakerOptions? circuitBreaker = null, bool clientCache = false)
        {
            var fixture = new SentinelBacklogFixture();
            try
            {
                static byte[]? Reply(int _, string command) => command == "HELLO 3"
                    ? "%1\r\n$5\r\nproto\r\n:3\r\n"u8.ToArray()
                    : command.StartsWith("GET ", StringComparison.Ordinal) ? BacklogValueReply : null;
                fixture.Original = Primary(Reply);
                fixture.Replacement = Primary(Reply);
                var port = fixture.Original.Port;
                fixture._sentinel = Sentinel(() => Volatile.Read(ref port));
                fixture.Client = await RespireClient.ConnectAsync(Options(fixture._sentinel.Port) with
                {
                    BacklogCapacity = capacity,
                    BacklogPolicy = policy,
                    ReconnectPolicy = reconnectPolicy,
                    RetryPolicy = retryPolicy,
                    CircuitBreaker = circuitBreaker,
                    Protocol = clientCache ? RespireProtocol.Resp3 : RespireProtocol.Resp2,
                    ClientSideCache = clientCache ? new() : null,
                    CommandTimeout = noCommandTimeout ? null : commandTimeout ?? Limit,
                    TestingStreamFactory = async (host, targetPort, token) =>
                    {
                        if (targetPort == fixture.Replacement.Port)
                        {
                            Interlocked.Increment(ref fixture.ReplacementAttempts);
                            fixture.Selecting.TrySetResult();
                            await fixture.Resume.Task.WaitAsync(token);
                            if (fixture.FailReplacement) throw new IOException("Controlled replacement connection failure.");
                            if (Volatile.Read(ref fixture.ReplacementFailuresRemaining) > 0
                                && Interlocked.Decrement(ref fixture.ReplacementFailuresRemaining) >= 0)
                                throw new IOException("Controlled transient replacement connection failure.");
                        }
                        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
                        try
                        {
                            await socket.ConnectAsync(host, targetPort, token);
                            return new NetworkStream(socket, ownsSocket: true);
                        }
                        catch { socket.Dispose(); throw; }
                    },
                });
                await WaitForInitialSentinelValidationAsync(fixture.Client, fixture._sentinel);
                Volatile.Write(ref port, fixture.Replacement.Port);
                var generation = fixture.Client.Core.Sentinel!.Current!;
                var connection = generation.Multiplexer.GetConnection();
                using var rejection = Respire.Protocol.RespValue.Error("READONLY demoted");
                generation.ObserveResponse(connection, "SET", in rejection);
                return fixture;
            }
            catch { await fixture.DisposeAsync(); throw; }
        }

        public async ValueTask DisposeAsync()
        {
            if (Client is not null) await Client.DisposeAsync();
            if (_sentinel is not null) await _sentinel.DisposeAsync();
            if (Replacement is not null) await Replacement.DisposeAsync();
            if (Original is not null) await Original.DisposeAsync();
        }
    }
}
