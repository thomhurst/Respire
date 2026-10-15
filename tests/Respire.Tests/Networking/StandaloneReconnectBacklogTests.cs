using System.Diagnostics;
using System.Text;
using System.Net.Sockets;
using Respire.Infrastructure;
using TUnit.Assertions;
using TUnit.Assertions.Enums;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class StandaloneReconnectBacklogTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);
    private static readonly byte[] ValueReply = "$5\r\nvalue\r\n"u8.ToArray();

    [Test]
    public async Task SlowHeadKeepsLaterArrivalsQueuedAfterRecovery()
    {
        await using var fixture = await Fixture.CreateAsync(capacity: 1);
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = fixture.Client.GetStringAsync("slow").AsTask();
        await fixture.Handshake.Task.WaitAsync(Limit);
        fixture.Server.SuppressReply = command =>
        {
            if (command != "GET slow") return false;
            received.TrySetResult();
            return true;
        };
        await fixture.ReleaseAsync();
        await received.Task.WaitAsync(Limit);
        var second = fixture.Client.GetStringAsync("second").AsTask();
        await Assert.That(second.IsCompleted).IsFalse();
        await Assert.That(fixture.Server.ReceivedCommands.Contains("GET second")).IsFalse();
        await Assert.That(async () => await fixture.Client.GetStringAsync("overflow").AsTask().WaitAsync(Limit))
            .ThrowsExactly<RespireConnectionException>();
        await fixture.Server.SendRawAsync(ValueReply, 1);
        await Assert.That(await first.WaitAsync(Limit)).IsEqualTo("value");
        await Assert.That(await second.WaitAsync(Limit)).IsEqualTo("value");
        await Assert.That(await fixture.Client.GetStringAsync("healthy")).IsEqualTo("value");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task NullCommandTimeoutStillAllowsCancellationAndDisposal(bool dispose)
    {
        await using var fixture = await Fixture.CreateAsync(disableCommandTimeout: true);
        using var caller = new CancellationTokenSource();
        var pending = fixture.Client.GetStringAsync("unsent", caller.Token).AsTask();
        await fixture.Handshake.Task.WaitAsync(Limit);
        await Assert.That(pending.IsCompleted).IsFalse();
        if (dispose)
        {
            await fixture.Client.DisposeAsync();
            await Assert.That(async () => await pending.WaitAsync(Limit)).Throws<ObjectDisposedException>();
        }
        else
        {
            await caller.CancelAsync();
            await Assert.That(async () => await pending.WaitAsync(Limit)).Throws<OperationCanceledException>();
            await fixture.ReleaseAsync();
            await Assert.That(await fixture.Client.GetStringAsync("healthy")).IsEqualTo("value");
        }
        await Assert.That(fixture.Server.ReceivedCommands.Contains("GET unsent")).IsFalse();
    }

    [Test]
    [NotInParallel]
    [Arguments("string")]
    [Arguments("bytes")]
    [Arguments("typed")]
    [Arguments("catalog")]
    public async Task TracedReadsShareTheBacklogWithWrites(string surface)
    {
        using var listener = ListenToActivities();
        await Assert.That(Respire.Internal.RespireTelemetry.Source.HasListeners()).IsTrue();
        await using var fixture = await Fixture.CreateAsync();
        var read = ExecuteAsync(fixture.Client, surface, "first");
        await fixture.Handshake.Task.WaitAsync(Limit);
        var write = fixture.Client.SetAsync("second", "value").AsTask();
        await Assert.That(read.IsCompleted).IsFalse();
        await Assert.That(write.IsCompleted).IsFalse();
        await Assert.That(fixture.Server.ReceivedCommands.Any(IsApplicationCommand)).IsFalse();
        await fixture.ReleaseAsync();
        await Assert.That(await read.WaitAsync(Limit)).IsEqualTo("value");
        await Assert.That(await write.WaitAsync(Limit)).IsTrue();
        await Assert.That(fixture.Server.ReceivedCommands.Where(IsApplicationCommand).ToArray())
            .IsEquivalentTo(new[] { "GET first", "SET second value" }, CollectionOrdering.Matching);
    }

    [Test]
    [NotInParallel]
    public async Task TracedReadCancellationReleasesBacklogCapacity()
    {
        using var listener = ListenToActivities();
        await using var fixture = await Fixture.CreateAsync(capacity: 1);
        using var caller = new CancellationTokenSource();
        var pending = fixture.Client.GetStringAsync("cancelled", caller.Token).AsTask();
        await fixture.Handshake.Task.WaitAsync(Limit);
        await Assert.That(pending.IsCompleted).IsFalse();
        await caller.CancelAsync();
        var error = await Assert.That(async () => await pending.WaitAsync(Limit)).Throws<OperationCanceledException>();
        await Assert.That(error!.CancellationToken).IsEqualTo(caller.Token);
        var next = fixture.Client.GetStringAsync("next").AsTask();
        await fixture.ReleaseAsync();
        await Assert.That(await next.WaitAsync(Limit)).IsEqualTo("value");
        await Assert.That(fixture.Server.ReceivedCommands.Where(IsApplicationCommand).ToArray())
            .IsEquivalentTo(new[] { "GET next" }, CollectionOrdering.Matching);
    }

    [Test]
    [NotInParallel]
    public async Task TracedReadWaitTimeoutReportsConnecting()
    {
        using var listener = ListenToActivities();
        await using var fixture = await Fixture.CreateAsync(commandTimeout: TimeSpan.FromMilliseconds(250));
        var pending = fixture.Client.GetStringAsync("expired").AsTask();
        await fixture.Handshake.Task.WaitAsync(Limit);
        var error = await Assert.That(async () => await pending.WaitAsync(Limit)).ThrowsExactly<RespireTimeoutException>();
        await Assert.That(error!.Diagnostics.Stage).IsEqualTo(RespireCommandStage.Connecting);
        await fixture.ReleaseAsync();
        await Assert.That(await fixture.Client.GetStringAsync("healthy")).IsEqualTo("value");
        await Assert.That(fixture.Server.ReceivedCommands.Where(IsApplicationCommand).ToArray())
            .IsEquivalentTo(new[] { "GET healthy" }, CollectionOrdering.Matching);
    }

    private static ActivityListener ListenToActivities()
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Respire",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    [Test]
    [Arguments("string")]
    [Arguments("bytes")]
    [Arguments("typed")]
    [Arguments("stream")]
    [Arguments("raw")]
    [Arguments("catalog")]
    [Arguments("interpolated")]
    [Arguments("write")]
    [Arguments("fire-and-forget")]
    public async Task DefaultPolicyWaitsForReplacementWithoutCommandRetries(string surface)
    {
        await using var fixture = await Fixture.CreateAsync();
        var operation = ExecuteAsync(fixture.Client, surface, "key");
        await fixture.Handshake.Task.WaitAsync(Limit);
        await Assert.That(operation.IsCompleted).IsFalse();
        await Assert.That(fixture.Server.ReceivedCommands.Any(IsApplicationCommand)).IsFalse();

        await fixture.ReleaseAsync();
        await Assert.That(await operation.WaitAsync(Limit)).IsEqualTo(surface is "write" or "fire-and-forget" ? "OK" : "value");
        // The barrier also proves that a fire-and-forget reply was consumed in FIFO order.
        await Assert.That(await fixture.Client.GetStringAsync("barrier")).IsEqualTo("value");
        await Assert.That(fixture.Server.ReceivedCommands.Count(command => command is "GET key" or "SET key value")).IsEqualTo(1);
    }

    [Test]
    [Arguments("string")]
    [Arguments("write")]
    public async Task DroppedAcceptedCommandFailsOnceAndNextGetWaitsForRecovery(string surface)
    {
        await using var server = new FakeRespServer(3, FakeRespServer.OkReply)
        {
            CloseConnectionAfterCommand = 2,
            ReplyOverride = (_, command) => command.StartsWith("GET ", StringComparison.Ordinal) ? ValueReply : null,
        };
        await using var client = await RespireClient.ConnectAsync(Options(server.Port));
        var handshake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.SuppressReply = command =>
        {
            if (command != "SELECT 1") return false;
            handshake.TrySetResult();
            return true;
        };
        await Assert.That(async () => await ExecuteAsync(client, surface, "lost").WaitAsync(Limit))
            .Throws<RespireConnectionException>();
        var next = client.GetStringAsync("next").AsTask();
        await handshake.Task.WaitAsync(Limit);
        await Assert.That(next.IsCompleted).IsFalse();
        await server.SendRawAsync(FakeRespServer.OkReply, 1);
        await Assert.That(await next.WaitAsync(Limit)).IsEqualTo("value");
        await Assert.That(server.ReceivedCommands.Where(IsApplicationCommand).ToArray())
            .IsEquivalentTo(new[] { surface == "write" ? "SET lost value" : "GET lost", "GET next" }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task LaterArrivalsStayBehindQueuedWorkWhileDrainIsActive()
    {
        await using var fixture = await Fixture.CreateAsync();
        var firstReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Server.SuppressReply = command =>
        {
            if (command == "SELECT 1") { fixture.Handshake.TrySetResult(); return true; }
            if (command != "GET first") return false;
            firstReceived.TrySetResult();
            return true;
        };
        var first = fixture.Client.GetStringAsync("first").AsTask();
        await fixture.Handshake.Task.WaitAsync(Limit);
        var second = fixture.Client.GetStringAsync("second").AsTask();
        await fixture.ReleaseAsync();
        await firstReceived.Task.WaitAsync(Limit);
        var third = fixture.Client.GetStringAsync("third").AsTask();
        await Assert.That(fixture.Server.ReceivedCommands.Where(IsApplicationCommand).ToArray())
            .IsEquivalentTo(new[] { "GET first" }, CollectionOrdering.Matching);
        await fixture.Server.SendRawAsync(ValueReply, 1);
        await Task.WhenAll(first, second, third).WaitAsync(Limit);
        await Assert.That(fixture.Server.ReceivedCommands.Where(IsApplicationCommand).ToArray())
            .IsEquivalentTo(new[] { "GET first", "GET second", "GET third" }, CollectionOrdering.Matching);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task QueuedRawAndFireAndForgetWritesSnapshotBorrowedArguments(bool fireAndForget)
    {
        await using var fixture = await Fixture.CreateAsync();
        var bytes = "value"u8.ToArray();
        async Task SendAsync()
        {
            if (fireAndForget) await fixture.Client.ExecuteFireAndForgetAsync("SET", ["key", bytes]);
            else { using var reply = await fixture.Client.ExecuteAsync("SET", ["key", bytes]); }
        }
        var pending = SendAsync();
        await fixture.Handshake.Task.WaitAsync(Limit);
        Array.Fill(bytes, (byte)'x');
        await fixture.ReleaseAsync();
        await pending.WaitAsync(Limit);
        await fixture.Client.GetStringAsync("barrier");
        await Assert.That(fixture.Server.ReceivedCommands.Where(IsApplicationCommand).ToArray())
            .IsEquivalentTo(new[] { "SET key value", "GET barrier" }, CollectionOrdering.Matching);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task CancellationReleasesCapacityWithoutCancellingOtherWaiters(bool cancelHead)
    {
        await using var fixture = await Fixture.CreateAsync(capacity: 2);
        using var cancelled = new CancellationTokenSource();
        var first = fixture.Client.GetStringAsync("first", cancelHead ? cancelled.Token : default).AsTask();
        await fixture.Handshake.Task.WaitAsync(Limit);
        var second = fixture.Client.GetStringAsync("second", cancelHead ? default : cancelled.Token).AsTask();
        var overflow = await Assert.That(async () => await fixture.Client.GetStringAsync("overflow").AsTask().WaitAsync(Limit))
            .ThrowsExactly<RespireConnectionException>();
        await Assert.That(overflow!.Endpoint).IsEqualTo((RespireEndpoint?)new RespireEndpoint("127.0.0.1", fixture.Server.Port));
        await cancelled.CancelAsync();
        var cancellation = await Assert.That(async () => await (cancelHead ? first : second).WaitAsync(Limit))
            .Throws<OperationCanceledException>();
        await Assert.That(cancellation!.CancellationToken).IsEqualTo(cancelled.Token);
        var third = fixture.Client.GetStringAsync("third").AsTask();
        await fixture.ReleaseAsync();
        await Task.WhenAll(cancelHead ? second : first, third).WaitAsync(Limit);
        await Assert.That(fixture.Server.ReceivedCommands.Where(IsApplicationCommand).ToArray())
            .IsEquivalentTo(new[] { cancelHead ? "GET second" : "GET first", "GET third" }, CollectionOrdering.Matching);
    }

    [Test]
    [NotInParallel]
    [Arguments("string")]
    [Arguments("raw")]
    [Arguments("fire-and-forget")]
    public async Task WaitTimeoutReportsConnectingAndNeverSendsTheCommand(string surface)
    {
        await using var fixture = await Fixture.CreateAsync(commandTimeout: TimeSpan.FromMilliseconds(250));
        var operation = ExecuteAsync(fixture.Client, surface, "expired");
        await fixture.Handshake.Task.WaitAsync(Limit);
        var error = await Assert.That(async () => await operation.WaitAsync(Limit)).ThrowsExactly<RespireTimeoutException>();
        await Assert.That(error!.Diagnostics.Stage).IsEqualTo(RespireCommandStage.Connecting);
        await Assert.That(fixture.Server.ReceivedCommands.Any(IsApplicationCommand)).IsFalse();
        await fixture.ReleaseAsync();
        await Assert.That(await fixture.Client.GetStringAsync("healthy")).IsEqualTo("value");
        await Assert.That(fixture.Server.ReceivedCommands.Where(IsApplicationCommand).ToArray())
            .IsEquivalentTo(new[] { "GET healthy" }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task CallerCancellationStillBoundsExecutionAfterBacklogWait()
    {
        await using var fixture = await Fixture.CreateAsync();
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Server.SuppressReply = command =>
        {
            if (command == "SELECT 1") { fixture.Handshake.TrySetResult(); return true; }
            if (command != "GET cancelled") return false;
            received.TrySetResult();
            return true;
        };
        using var caller = new CancellationTokenSource();
        var pending = fixture.Client.GetStringAsync("cancelled", caller.Token).AsTask();
        await fixture.Handshake.Task.WaitAsync(Limit);
        await fixture.ReleaseAsync();
        await received.Task.WaitAsync(Limit);
        await caller.CancelAsync();
        await Assert.That(async () => await pending.WaitAsync(Limit)).Throws<OperationCanceledException>();
        await fixture.Server.SendRawAsync(ValueReply, 1);
        await Assert.That(await fixture.Client.GetStringAsync("healthy")).IsEqualTo("value");
        await Assert.That(fixture.Server.ReceivedCommands.Count(command => command == "GET cancelled")).IsEqualTo(1);
    }

    [Test]
    [Arguments("string")]
    [Arguments("bytes")]
    [Arguments("raw")]
    public async Task ResumedExecutionRetainsTheOriginalAbsoluteDeadline(string surface)
    {
        await using var fixture = await Fixture.CreateAsync();
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Server.SuppressReply = command =>
        {
            if (command == "SELECT 1") { fixture.Handshake.TrySetResult(); return true; }
            if (command != "GET pending") return false;
            received.TrySetResult();
            return true;
        };
        var earliest = Environment.TickCount64;
        var pending = ExecuteAsync(fixture.Client, surface, "pending");
        var latest = Environment.TickCount64;
        await fixture.Handshake.Task.WaitAsync(Limit);
        // Separate admission and transport dispatch by more than a clock tick. The assertion
        // inspects the retained deadline rather than relying on a wall-clock timeout window.
        await Task.Delay(25);
        await fixture.ReleaseAsync();
        await received.Task.WaitAsync(Limit);
        var connection = fixture.Client.Core.Multiplexer.GetConnection();
        var ring = connection.InspectForTests().Inflight;
        await Assert.That(ring.TryPeek(out var source)).IsTrue();
        await Assert.That(source.Deadline.Ticks).IsGreaterThanOrEqualTo(earliest + (long)Limit.TotalMilliseconds);
        await Assert.That(source.Deadline.Ticks).IsLessThanOrEqualTo(latest + (long)Limit.TotalMilliseconds);
        ring.SweepExpired(source.Deadline.Ticks + 1, Limit, connection);
        await Assert.That(async () => await pending.WaitAsync(Limit)).ThrowsExactly<RespireTimeoutException>();
        await fixture.Server.SendRawAsync(ValueReply, 1);
        await Assert.That(await fixture.Client.GetStringAsync("healthy")).IsEqualTo("value");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task BatchAndUnwatchedTransactionWaitAsOneCapacityUnit(bool transaction)
    {
        await using var fixture = await Fixture.CreateAsync(capacity: 1);
        var inTransaction = false;
        fixture.Server.ReplyOverride = (_, command) =>
        {
            if (command == "MULTI") { inTransaction = true; return FakeRespServer.OkReply; }
            if (command == "EXEC") { inTransaction = false; return "*2\r\n$5\r\nvalue\r\n+OK\r\n"u8.ToArray(); }
            if (inTransaction) return "+QUEUED\r\n"u8.ToArray();
            return command.StartsWith("GET ", StringComparison.Ordinal) ? ValueReply : FakeRespServer.OkReply;
        };
        using var batch = transaction ? null : fixture.Client.CreateBatch();
        await using var commit = transaction ? fixture.Client.CreateTransaction() : null;
        var first = transaction ? commit!.GetString("first") : batch!.GetString("first");
        var second = transaction ? commit!.Set("second", "value") : batch!.Set("second", "value");
        var execution = transaction ? CommitAsync(commit!) : batch!.ExecuteAsync().AsTask();
        await fixture.Handshake.Task.WaitAsync(Limit);
        await Assert.That(execution.IsCompleted).IsFalse();
        await Assert.That(fixture.Server.ReceivedCommands.Any(IsApplicationCommand)).IsFalse();
        await Assert.That(async () => await fixture.Client.GetStringAsync("overflow").AsTask().WaitAsync(Limit))
            .ThrowsExactly<RespireConnectionException>();
        await fixture.ReleaseAsync();
        await execution.WaitAsync(Limit);
#pragma warning disable RESP002 // The batch/transaction execution task above completed before either result is read.
        await Assert.That(await first).IsEqualTo("value");
        await Assert.That(await second).IsTrue();
#pragma warning restore RESP002
        await Assert.That(fixture.Server.ReceivedCommands.Where(IsApplicationCommand).ToArray())
            .IsEquivalentTo(transaction ? new[] { "MULTI", "GET first", "SET second value", "EXEC" }
                : new[] { "GET first", "SET second value" }, CollectionOrdering.Matching);
    }

    [Test]
    [NotInParallel]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task BatchAndTransactionCancellationOrTimeoutFailsEveryUnsentMember(bool transaction, bool cancelCaller)
    {
        await using var fixture = await Fixture.CreateAsync(
            commandTimeout: cancelCaller ? Limit : TimeSpan.FromMilliseconds(250));
        using var caller = new CancellationTokenSource();
        using var batch = transaction ? null : fixture.Client.CreateBatch();
        await using var commit = transaction ? fixture.Client.CreateTransaction() : null;
        var first = transaction ? commit!.GetString("first") : batch!.GetString("first");
        var second = transaction ? commit!.Set("second", "value") : batch!.Set("second", "value");
        var execution = transaction ? CommitAsync(commit!, caller.Token) : batch!.TryExecuteAsync(caller.Token).AsTask();
        await fixture.Handshake.Task.WaitAsync(Limit);
        if (cancelCaller) await caller.CancelAsync();
        if (transaction)
        {
            if (cancelCaller)
                await Assert.That(async () => await execution.WaitAsync(Limit)).Throws<OperationCanceledException>();
            else
                await Assert.That(async () => await execution.WaitAsync(Limit)).ThrowsExactly<RespireTimeoutException>();
        }
        else await execution.WaitAsync(Limit);
        if (cancelCaller)
        {
            await Assert.That(first.Error).IsTypeOf<OperationCanceledException>();
            await Assert.That(((OperationCanceledException)first.Error!).CancellationToken).IsEqualTo(caller.Token);
        }
        else
        {
            await Assert.That(first.Error).IsTypeOf<RespireTimeoutException>();
            await Assert.That(((RespireTimeoutException)first.Error!).Diagnostics.Stage).IsEqualTo(RespireCommandStage.Connecting);
        }
        await Assert.That(second.Error).IsSameReferenceAs(first.Error);
        await Assert.That(fixture.Server.ReceivedCommands.Any(IsApplicationCommand)).IsFalse();
        await fixture.ReleaseAsync();
        await Assert.That(await fixture.Client.GetStringAsync("healthy")).IsEqualTo("value");
    }

    [Test]
    public async Task FailFastOptsOutOfBacklogWaiting()
    {
        await using var fixture = await Fixture.CreateAsync(policy: RespireBacklogPolicy.FailFast);
        await Assert.That(async () => await fixture.Client.GetStringAsync("not-queued").AsTask().WaitAsync(Limit))
            .Throws<RespireConnectionException>();
        await Assert.That(fixture.Server.ReceivedCommands.Any(IsApplicationCommand)).IsFalse();
    }

    [Test]
    public async Task EnabledCommandRetriesRemainInsideTheQueuedOperation()
    {
        await using var fixture = await Fixture.CreateAsync(retryPolicy: new()
        {
            InitialBackoff = TimeSpan.Zero, MaxAttempts = 2,
        });
        var firstAttempts = 0;
        fixture.Server.ReplyOverride = (_, command) => command switch
        {
            "GET first" => Interlocked.Increment(ref firstAttempts) == 1 ? "-TRYAGAIN retry\r\n"u8.ToArray() : ValueReply,
            "GET second" => ValueReply,
            _ => null,
        };
        var first = fixture.Client.GetStringAsync("first").AsTask();
        await fixture.Handshake.Task.WaitAsync(Limit);
        var second = fixture.Client.GetStringAsync("second").AsTask();
        await fixture.ReleaseAsync();
        await Task.WhenAll(first, second).WaitAsync(Limit);
        await Assert.That(await first).IsEqualTo("value");
        await Assert.That(await second).IsEqualTo("value");
        await Assert.That(fixture.Server.ReceivedCommands.Where(IsApplicationCommand).ToArray())
            .IsEquivalentTo(new[] { "GET first", "GET first", "GET second" }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task DisposingClientFailsQueuedPublicOperations()
    {
        await using var fixture = await Fixture.CreateAsync();
        var first = fixture.Client.GetStringAsync("first").AsTask();
        await fixture.Handshake.Task.WaitAsync(Limit);
        var second = fixture.Client.SetAsync("second", "value").AsTask();
        await fixture.Client.DisposeAsync();
        await Assert.That(async () => await first.WaitAsync(Limit)).Throws<ObjectDisposedException>();
        await Assert.That(async () => await second.WaitAsync(Limit)).Throws<ObjectDisposedException>();
        await Assert.That(fixture.Server.ReceivedCommands.Any(IsApplicationCommand)).IsFalse();
    }

    [Test]
    public async Task ExhaustionFailsQueuedPublicOperationsAndFurtherAdmission()
    {
        await using var fixture = await Fixture.CreateAsync(reconnectPolicy: new()
        {
            InitialDelay = TimeSpan.Zero, JitterRatio = 0, MaxAttempts = 1,
        });
        var first = fixture.Client.GetStringAsync("first").AsTask();
        await fixture.Handshake.Task.WaitAsync(Limit);
        var second = fixture.Client.SetAsync("second", "value").AsTask();
        await fixture.Server.SendRawAsync("-TRYAGAIN replacement failed\r\n"u8.ToArray(), 1);
        await Assert.That(async () => await first.WaitAsync(Limit)).ThrowsExactly<RespireReconnectLimitException>();
        await Assert.That(async () => await second.WaitAsync(Limit)).ThrowsExactly<RespireReconnectLimitException>();
        await Assert.That(async () => await fixture.Client.GetStringAsync("later").AsTask().WaitAsync(Limit))
            .ThrowsExactly<RespireReconnectLimitException>();
        await Assert.That(fixture.Server.ReceivedCommands.Any(IsApplicationCommand)).IsFalse();
    }

    [Test]
    public async Task DefaultSettingsAndInvalidPolicyAreExplicit()
    {
        var options = Options(6379);
        await Assert.That(options.BacklogPolicy).IsEqualTo(RespireBacklogPolicy.Queue);
        await Assert.That(options.BacklogCapacity).IsEqualTo(1024);
        await Assert.That(() => RespireClient.Create(options with { BacklogPolicy = (RespireBacklogPolicy)42 }))
            .ThrowsExactly<RespireConfigurationException>();
    }

    [Test]
    [Arguments("string", false)]
    [Arguments("string", true)]
    [Arguments("write", false)]
    [Arguments("write", true)]
    [Arguments("raw", false)]
    [Arguments("raw", true)]
    [Arguments("fire-and-forget", false)]
    [Arguments("fire-and-forget", true)]
    public async Task CachedBacklogPreservesTrackedReadsAndReleasesMutationFences(string surface, bool cancelCaller)
    {
        var value = "old";
        await using var server = new FakeRespServer(4, FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => command switch
            {
                "HELLO 3" => "%1\r\n$5\r\nproto\r\n:3\r\n"u8.ToArray(),
                "GET key" => Encoding.UTF8.GetBytes($"$3\r\n{value}\r\n"),
                "SET key new" => SetValue(),
                _ => null,
            },
        };
        byte[] SetValue() { value = "new"; return FakeRespServer.OkReply; }
        await using var client = await RespireClient.ConnectAsync(Options(server.Port) with
        {
            Protocol = RespProtocol.Resp3, ClientSideCache = new(),
        });
        await Assert.That(await client.GetStringAsync("key")).IsEqualTo("old");
        var handshake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.SuppressReply = command =>
        {
            if (command != "SELECT 1") return false;
            handshake.TrySetResult();
            return true;
        };
        await client.Core.Multiplexer.GetConnection().DisposeAsync();
        using var caller = new CancellationTokenSource();
        async Task ExecuteAsync()
        {
            switch (surface)
            {
                case "string": await client.GetStringAsync("key", caller.Token); break;
                case "write": await client.SetAsync("key", "new", cancellationToken: caller.Token); break;
                case "fire-and-forget": await client.ExecuteFireAndForgetAsync("SET", ["key", "new"], caller.Token); break;
                default: using (var reply = await client.ExecuteAsync("SET", ["key", "new"], cancellationToken: caller.Token)) { } break;
            }
        }
        var pending = ExecuteAsync();
        await handshake.Task.WaitAsync(Limit);
        var cache = client.Core.ClientCache!;
        await Assert.That(cache.InspectForTests().ActiveMutationCount).IsEqualTo(surface == "string" ? 0 : 1);
        if (cancelCaller)
        {
            await caller.CancelAsync();
            await Assert.That(async () => await pending.WaitAsync(Limit)).Throws<OperationCanceledException>();
        }
        await server.SendRawAsync(FakeRespServer.OkReply, 1);
        if (!cancelCaller) await pending.WaitAsync(Limit);
        await Assert.That(cache.InspectForTests().ActiveMutationCount).IsEqualTo(0);
        var expected = cancelCaller || surface == "string" ? "old" : "new";
        await Assert.That(await client.GetStringAsync("key")).IsEqualTo(expected);
        var reads = server.ReceivedCommands.Count(command => command == "GET key");
        await Assert.That(await client.GetStringAsync("key")).IsEqualTo(expected);
        await Assert.That(server.ReceivedCommands.Count(command => command == "GET key")).IsEqualTo(reads);
        await Assert.That(server.ReceivedCommands.Count(command => command == "SET key new"))
            .IsEqualTo(cancelCaller || surface == "string" ? 0 : 1);
    }

    [Test]
    [Arguments(0)]
    [Arguments(-1)]
    public async Task CapacityMustBePositive(int capacity)
    {
        await Assert.That(() => RespireClient.Create(Options(6379) with { BacklogCapacity = capacity }))
            .ThrowsExactly<RespireConfigurationException>();
    }

    private static async Task<string> ExecuteAsync(RespireClient client, string surface, string key)
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
                using (var reply = surface switch
                {
                    "catalog" => await client.ExecuteAsync(RespireCommands.String.GET, key),
                    "interpolated" => await client.ExecuteAsync($"GET {key}"),
                    _ => await client.ExecuteAsync("GET", key),
                }) return reply.AsString()!;
        }
    }

    private static async Task CommitAsync(RespireTransaction transaction, CancellationToken token = default)
        => await transaction.CommitAsync(token);
    private static bool IsApplicationCommand(string command) => command != "SELECT 1";

    private static RespireOptions Options(int port) => new()
    {
        Endpoints = [new("127.0.0.1", port)], Connections = 1, Protocol = RespProtocol.Resp2, Database = 1,
        RetryPolicy = null, ConnectTimeout = Limit, CommandTimeout = Limit,
    };

    private sealed class Fixture(FakeRespServer server, RespireClient client, TaskCompletionSource handshake,
        TaskCompletionSource? resumeConnection) : IAsyncDisposable
    {
        internal FakeRespServer Server { get; } = server;
        internal RespireClient Client { get; } = client;
        internal TaskCompletionSource Handshake { get; } = handshake;

        internal static async Task<Fixture> CreateAsync(int capacity = 1024, TimeSpan? commandTimeout = null,
            RespireBacklogPolicy policy = RespireBacklogPolicy.Queue, RespireRetryPolicy? retryPolicy = null,
            RespireReconnectPolicy? reconnectPolicy = null, bool disableCommandTimeout = false)
        {
            var server = new FakeRespServer(4, FakeRespServer.OkReply)
            {
                ReplyOverride = (_, command) => command.StartsWith("GET ", StringComparison.Ordinal) ? ValueReply : null,
            };
            try
            {
                var handshake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                // A short command timeout also bounds SELECT itself. Gate before creating the
                // replacement transport so its independent handshake cannot expire alongside
                // the application waiter whose timeout this fixture is exercising.
                var resume = commandTimeout is { } timeout && timeout < Limit
                    ? new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) : null;
                var dials = 0;
                var client = await RespireClient.ConnectAsync(Options(server.Port) with
                {
                    BacklogCapacity = capacity, BacklogPolicy = policy,
                    CommandTimeout = disableCommandTimeout ? null : commandTimeout ?? Limit,
                    RetryPolicy = retryPolicy, ReconnectPolicy = reconnectPolicy,
                    TestingStreamFactory = resume is null ? null : async (host, port, token) =>
                    {
                        if (Interlocked.Increment(ref dials) > 1)
                        {
                            handshake.TrySetResult();
                            await resume.Task.WaitAsync(token);
                        }
                        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
                        try
                        {
                            await socket.ConnectAsync(host, port, token);
                            return new NetworkStream(socket, ownsSocket: true);
                        }
                        catch { socket.Dispose(); throw; }
                    },
                });
                server.SuppressReply = resume is not null ? null : command =>
                {
                    if (command != "SELECT 1") return false;
                    handshake.TrySetResult();
                    return true;
                };
                await client.Core.Multiplexer.GetConnection().DisposeAsync();
                return new(server, client, handshake, resume);
            }
            catch { await server.DisposeAsync(); throw; }
        }

        internal async Task ReleaseAsync()
        {
            if (resumeConnection is null)
            {
                await Server.SendRawAsync(FakeRespServer.OkReply, 1);
                return;
            }
            resumeConnection.TrySetResult();
            using var deadline = new CancellationTokenSource(Limit);
            await Client.Core.Multiplexer.GetHealthyConnectionAsync(deadline.Token);
        }
        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            await Server.DisposeAsync();
        }
    }
}
