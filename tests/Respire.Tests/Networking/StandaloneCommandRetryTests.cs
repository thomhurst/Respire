using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Respire.Internal;
using Respire.Networking;
using Respire.Protocol;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

[NotInParallel]
public class StandaloneCommandRetryTests
{
    [Test]
    [Arguments("LOADING", RespireRetryableErrors.Loading, true)]
    [Arguments("TRYAGAIN", RespireRetryableErrors.TryAgain, true)]
    [Arguments("MASTERDOWN", RespireRetryableErrors.MasterDown, true)]
    [Arguments("READONLY", RespireRetryableErrors.ReadOnly, true)]
    [Arguments("LOADING", RespireRetryableErrors.TryAgain, false)]
    [Arguments("TRYAGAIN", RespireRetryableErrors.None, false)]
    [Arguments("BUSY", RespireRetryableErrors.Loading, false)]
    [Arguments("CLUSTERDOWN", RespireRetryableErrors.TryAgain, false)]
    public async Task PublicReadHonorsIndividualTransientErrorSelection(string code, RespireRetryableErrors selection, bool retries)
    {
        var reads = 0;
        await using var server = Server();
        server.ReplyOverride = (_, command) => command == "GET key"
            ? Interlocked.Increment(ref reads) == 1 ? System.Text.Encoding.ASCII.GetBytes($"-{code} busy\r\n") : "$5\r\nvalue\r\n"u8.ToArray()
            : Setup(command);
#pragma warning disable RESP010 // Each wire case owns an isolated fake server and client retry state; sharing would leak fault and policy state.
        await using var client = await RespireClient.ConnectAsync(Options(server) with
        { RetryPolicy = new() { InitialBackoff = TimeSpan.Zero, RetryableErrors = selection } });
#pragma warning restore RESP010
        if (retries) await Assert.That(await client.GetStringAsync("key")).IsEqualTo("value");
        else await Assert.That(async () => await client.GetStringAsync("key")).ThrowsExactly<RespireServerException>();
        await Assert.That(reads).IsEqualTo(retries ? 2 : 1);
        // A later command confirms that the failure did not poison the next reply slot.
        await Assert.That(await client.GetStringAsync("key")).IsEqualTo("value");
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "RESP010", Justification =
        "Each wire case needs an isolated server, retry policy and capability negotiation.")]
    public async Task HealthyRetryOwnersPreserveTypedAndCatalogReplies(bool redirects, bool catalog)
    {
        var reads = 0;
        await using var server = Server();
        server.ReplyOverride = (_, command) => command == "STRLEN key"
            ? Interlocked.Increment(ref reads) == 1 ? "-TRYAGAIN busy\r\n"u8.ToArray() : ":5\r\n"u8.ToArray()
            : Setup(command);
        await using var client = await RespireClient.ConnectAsync(Options(server) with
        { EnableReplicaRedirects = redirects });
        for (var index = 0; index < 2; index++)
        {
            if (catalog)
            {
                using var result = await client.ExecuteAsync(RespireCommands.String.STRLEN, ["key"]);
                await Assert.That(result.AsInteger()).IsEqualTo(5L);
            }
            else await Assert.That(await client.Strings.LengthAsync("key")).IsEqualTo(5L);
        }
        await Assert.That(reads).IsEqualTo(3);
        await Assert.That(server.ReceivedCommands.Contains("CLIENT CAPA REDIRECT")).IsEqualTo(redirects);
    }

    [Test]
    [Arguments("string")]
    [Arguments("bytes")]
    [Arguments("converted")]
    [Arguments("catalog")]
    public async Task PublicReadShapesRetrySelectedServerErrors(string shape)
    {
        var reads = 0;
        await using var server = Server();
        server.ReplyOverride = (_, command) => command == "GET key"
            ? Interlocked.Increment(ref reads) == 1 ? "-TRYAGAIN busy\r\n"u8.ToArray() : "$5\r\nvalue\r\n"u8.ToArray()
            : Setup(command);
#pragma warning disable RESP010 // Each wire case owns an isolated fake server and client retry state; sharing would leak fault and policy state.
        await using var client = await RespireClient.ConnectAsync(Options(server));
#pragma warning restore RESP010
        switch (shape)
        {
            case "string": await Assert.That(await client.GetStringAsync("key")).IsEqualTo("value"); break;
            case "bytes": await Assert.That((await client.GetBytesAsync("key"))!).IsEquivalentTo("value"u8.ToArray()); break;
            case "converted": await Assert.That(await client.GetAsync<string>("key")).IsEqualTo("value"); break;
            case "catalog":
                using (var result = await client.ExecuteAsync(RespireCommands.String.GET, ["key"]))
                    await Assert.That(result.AsString()).IsEqualTo("value");
                break;
        }
        await Assert.That(reads).IsEqualTo(2);
    }

    [Test]
    [Arguments("BITFIELD_RO")]
    [Arguments("GEOSEARCH")]
    public async Task SpecializedTypedReadsRetrySelectedServerErrors(string name)
    {
        var reads = 0;
        await using var server = Server();
        server.ReplyOverride = (_, command) => command.StartsWith(name + " ", StringComparison.Ordinal)
            ? Interlocked.Increment(ref reads) == 1 ? "-TRYAGAIN busy\r\n"u8.ToArray()
                : name == "GEOSEARCH" ? "*1\r\n$3\r\none\r\n"u8.ToArray() : "*1\r\n:7\r\n"u8.ToArray()
            : Setup(command);
#pragma warning disable RESP010 // Each wire case owns an isolated fake server and client retry state; sharing would leak fault and policy state.
        await using var client = await RespireClient.ConnectAsync(Options(server));
#pragma warning restore RESP010
        if (name == "GEOSEARCH")
            await Assert.That((await client.Geo.SearchAsync("places", RespireGeoSearchOrigin.FromMember("origin"),
                RespireGeoSearchShape.Circle(1)))[0].Member).IsEqualTo("one");
        else
            await Assert.That((await client.Bitmaps.FieldReadOnlyAsync("bits",
                RespireBitFieldOperation.Get(RespireBitFieldEncoding.Unsigned(8), RespireBitFieldOffset.Bits(0))))[0]).IsEqualTo(7L);
        await Assert.That(reads).IsEqualTo(2);
    }

    [Test]
    public async Task SpecializedTypedCommandsForwardAuditedRetryCategories()
    {
        var get = RespireBitFieldOperation.Get(RespireBitFieldEncoding.Unsigned(8), RespireBitFieldOffset.Bits(0));
        await Assert.That(new BitFieldCommand(RespireCommands.Bitmap.BITFIELD_RO.Verb, "bits", [get]).RetryCategory)
            .IsEqualTo(RespireCommandRetryCategory.ReadOnly);
        await Assert.That(new BitFieldCommand(RespireCommands.Bitmap.BITFIELD.Verb, "bits", [get]).RetryCategory)
            .IsEqualTo(RespireCommands.Bitmap.BITFIELD.Verb.RetryCategory);
        await Assert.That(new BitOpCommand(RespireCommands.Bitmap.BITOP.Verb, "AND", "destination", ["source"]).RetryCategory)
            .IsEqualTo(RespireCommands.Bitmap.BITOP.Verb.RetryCategory);
        await Assert.That(new GeoAddCommand(RespireCommands.Geo.GEOADD.Verb, "places", RespireSetWhen.Always, false, []).RetryCategory)
            .IsEqualTo(RespireCommands.Geo.GEOADD.Verb.RetryCategory);
    }

    [Test]
    public async Task CircuitAwareRetryWaitsForReplacementWithoutSpendingAttempts()
    {
        var reads = 0;
        await using var server = Server();
        server.ReplyOverride = (_, command) =>
        {
            if (command != "GET key") return Setup(command);
            if (Interlocked.Increment(ref reads) == 1) { server.CloseConnections(); return null; }
            return "$5\r\nvalue\r\n"u8.ToArray();
        };
#pragma warning disable RESP010 // Each wire case owns an isolated fake server and client retry state; sharing would leak fault and policy state.
        await using var client = await RespireClient.ConnectAsync(Options(server) with
        {
            CircuitBreaker = new(),
            RetryPolicy = new() { MaxAttempts = 2, InitialBackoff = TimeSpan.Zero },
        });
#pragma warning restore RESP010
        await Assert.That(await client.GetStringAsync("key")).IsEqualTo("value");
        await Assert.That(reads).IsEqualTo(2);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task DisabledPolicyPreservesOneSend(bool policyPresent)
    {
        var reads = 0;
        await using var server = Server();
        server.ReplyOverride = (_, command) => command == "GET key"
            ? CountError() : Setup(command);
        byte[] CountError() { Interlocked.Increment(ref reads); return "-LOADING busy\r\n"u8.ToArray(); }
#pragma warning disable RESP010 // Each wire case owns an isolated fake server and client retry state; sharing would leak fault and policy state.
        await using var client = await RespireClient.ConnectAsync(Options(server) with
        { RetryPolicy = policyPresent ? new() { MaxAttempts = 1 } : null });
#pragma warning restore RESP010
        await Assert.That(async () => await client.GetStringAsync("key")).ThrowsExactly<RespireServerException>();
        await Assert.That(reads).IsEqualTo(1);
    }

    [Test]
    [Arguments(RespireCommandRetryCategory.ReadOnly, 1)]
    [Arguments(RespireCommandRetryCategory.WriteAccumulating, 2)]
    public async Task LostReplyDoesNotRepeatIncrementWithoutExplicitRisk(RespireCommandRetryCategory maximum, int expectedEffects)
    {
        var effects = 0;
        await using var server = Server();
        server.ReplyOverride = (_, command) =>
        {
            if (command != "INCR counter") return Setup(command);
            var current = Interlocked.Increment(ref effects);
            if (current == 1) { server.CloseConnections(); return null; }
            return ":2\r\n"u8.ToArray();
        };
#pragma warning disable RESP010 // Each wire case owns an isolated fake server and client retry state; sharing would leak fault and policy state.
        await using var client = await RespireClient.ConnectAsync(Options(server) with
        { RetryPolicy = new() { InitialBackoff = TimeSpan.Zero, MaxCommandRetryCategory = maximum } });
#pragma warning restore RESP010
        if (expectedEffects == 1)
            await Assert.That(async () => { using var result = await client.ExecuteAsync(RespireCommands.String.INCR, ["counter"]); })
                .Throws<RespireConnectionException>();
        else
        {
            using var result = await client.ExecuteAsync(RespireCommands.String.INCR, ["counter"]);
            await Assert.That(result.AsInteger()).IsEqualTo(2);
        }
        await Assert.That(effects).IsEqualTo(expectedEffects);
    }

    [Test]
    public async Task RetrySnapshotsCallerBytesAndHoldsMutationFence()
    {
        var sends = 0;
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var payload = "old"u8.ToArray();
        await using var server = Server();
        server.ReplyOverride = (_, command) =>
        {
            if (!command.StartsWith("SET key ", StringComparison.Ordinal)) return Setup(command);
            if (Interlocked.Increment(ref sends) != 1) return FakeRespServer.OkReply;
            first.TrySetResult();
            return "-TRYAGAIN busy\r\n"u8.ToArray();
        };
#pragma warning disable RESP010 // Each wire case owns an isolated fake server and client retry state; sharing would leak fault and policy state.
        await using var client = await RespireClient.ConnectAsync(Options(server) with
        {
            Protocol = RespireProtocol.Resp3, ClientSideCache = new(),
            RetryPolicy = new() { MaxCommandRetryCategory = RespireCommandRetryCategory.WriteLastWins,
                InitialBackoff = TimeSpan.FromMilliseconds(100), Jitter = 0 },
        });
#pragma warning restore RESP010
        var pending = client.SetAsync("key", payload).AsTask();
        await first.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(client.Core.ClientCache!.InspectForTests().ActiveMutationCount).IsEqualTo(1);
        "new"u8.CopyTo(payload);
        await pending;
        await Assert.That(server.ReceivedCommands.Count(command => command == "SET key old")).IsEqualTo(2);
        await Assert.That(client.Core.ClientCache!.InspectForTests().ActiveMutationCount).IsEqualTo(0);
    }

    [Test]
    public async Task CancellationAfterWriteKeepsCallerTokenAndStopsResend()
    {
        using var cancellation = new CancellationTokenSource();
        await using var server = Server();
        server.ReplyOverride = (_, command) =>
        {
            if (command != "GET key") return Setup(command);
            cancellation.Cancel();
            return "-TRYAGAIN busy\r\n"u8.ToArray();
        };
#pragma warning disable RESP010 // Each wire case owns an isolated fake server and client retry state; sharing would leak fault and policy state.
        await using var client = await RespireClient.ConnectAsync(Options(server) with
        { RetryPolicy = new() { InitialBackoff = TimeSpan.FromSeconds(1), Jitter = 0 } });
#pragma warning restore RESP010
        var error = await Failure(async () => await client.GetStringAsync("key", cancellation.Token));
        await Assert.That(error).IsTypeOf<OperationCanceledException>();
        await Assert.That(((OperationCanceledException)error).CancellationToken).IsEqualTo(cancellation.Token);
        await Assert.That(server.ReceivedCommands.Count(command => command == "GET key")).IsEqualTo(1);
    }

    [Test]
    public async Task CancellationDuringBackoffStopsResend()
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        var handled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var listener = new MeterListener();
        listener.InstrumentPublished = static (instrument, meter) =>
        {
            if (instrument.Name == "redis.client.errors") meter.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            foreach (var tag in tags)
                if (tag.Key == "redis.client.errors.internal" && Equals(tag.Value, true)) handled.TrySetResult();
        });
        listener.Start();
        await using var server = Server();
        server.ReplyOverride = (_, command) => command == "GET key" ? "-TRYAGAIN busy\r\n"u8.ToArray() : Setup(command);
#pragma warning disable RESP010 // Each wire case owns an isolated fake server and client retry state; sharing would leak fault and policy state.
        await using var client = await RespireClient.ConnectAsync(Options(server) with
        { RetryPolicy = new() { InitialBackoff = TimeSpan.FromSeconds(1), Jitter = 0 } });
#pragma warning restore RESP010
        using var cancellation = new CancellationTokenSource();
        var pending = client.GetStringAsync("key", cancellation.Token).AsTask();
        await handled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        var error = await Failure(async () => await pending);
        await Assert.That(error).IsTypeOf<OperationCanceledException>();
        await Assert.That(((OperationCanceledException)error).CancellationToken).IsEqualTo(cancellation.Token);
        await Assert.That(server.ReceivedCommands.Count(command => command == "GET key")).IsEqualTo(1);
    }

    [Test]
    public async Task CircuitRejectionDoesNotInventRetryMetrics()
    {
        var events = new ConcurrentDictionary<string, long>();
        using var listener = RetryListener(events);
        await using var server = Server();
#pragma warning disable RESP010 // Each wire case owns an isolated fake server and client retry state; sharing would leak fault and policy state.
        await using var client = await RespireClient.ConnectAsync(Options(server) with
        { CircuitBreaker = new() { MinimumFailureCount = 1, FailureRateThreshold = 0.1 } });
#pragma warning restore RESP010
        var admission = client.Core.Circuits!.Acquire(new("127.0.0.1", server.Port), default);
        admission.Failed(new RespireConnectionException("failed"), default);
        admission.Dispose();
        await Assert.That(async () => await client.GetStringAsync("key")).ThrowsExactly<RespireCircuitOpenException>();
        await Assert.That(server.ReceivedCommands.Contains("GET key")).IsFalse();
        await Assert.That(events.Count).IsEqualTo(0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task MetricsDescribeAcceptedRetriesAndOneFinalProducerOutcome(bool exhaust)
    {
        var events = new ConcurrentDictionary<string, long>();
        using var listener = RetryListener(events);
        var reads = 0;
        await using var server = Server();
        server.ReplyOverride = (_, command) => command == "GET key"
            ? Interlocked.Increment(ref reads) == 1 || exhaust ? "-TRYAGAIN busy\r\n"u8.ToArray() : "$1\r\nx\r\n"u8.ToArray()
            : Setup(command);
#pragma warning disable RESP010 // Each wire case owns an isolated fake server and client retry state; sharing would leak fault and policy state.
        await using var client = await RespireClient.ConnectAsync(Options(server) with
        { RetryPolicy = new() { MaxAttempts = 2, InitialBackoff = TimeSpan.Zero } });
#pragma warning restore RESP010
        if (exhaust) await Assert.That(async () => await client.GetStringAsync("key")).ThrowsExactly<RespireServerException>();
        else await Assert.That(await client.GetStringAsync("key")).IsEqualTo("x");
        await Assert.That(events["attempted"]).IsEqualTo(1);
        await Assert.That(events[exhaust ? "exhausted" : "succeeded"]).IsEqualTo(1);
        await Assert.That(events.Count).IsEqualTo(2);
    }

    [Test]
    [Arguments("PING")]
    [Arguments("CONFIG GET")]
    [Arguments("GET")]
    [Arguments("SETNX")]
    [Arguments("HSET")]
    [Arguments("INCR")]
    [Arguments("CONFIG SET")]
    public async Task DefinitelyUnwrittenPublicCatalogCommandsRetryAcrossEveryCategory(string name)
    {
        var occupied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var effects = 0;
        var descriptor = RespireCommands.All.ToArray().Single(command => command.Name == name);
        RespireValue[] arguments = name switch
        {
            "CONFIG GET" => ["maxclients"], "CONFIG SET" => ["maxclients", "100"],
            "GET" or "INCR" => ["key"], "SETNX" => ["key", "value"],
            "HSET" => ["key", "field", "value"], _ => [],
        };
        var expectedCommand = name + (arguments.Length == 0 ? "" : " " + string.Join(' ', arguments.Select(argument => argument.ToString())));
        await using var server = Server();
        server.SuppressReply = command =>
        {
            if (command != "ECHO occupy") return false;
            occupied.TrySetResult();
            return true;
        };
        server.ReplyOverride = (_, command) =>
        {
            if (command == expectedCommand) Interlocked.Increment(ref effects);
            return Setup(command) ?? FakeRespServer.OkReply;
        };
#pragma warning disable RESP010 // Each wire case owns an isolated fake server and client retry state; sharing would leak fault and policy state.
        await using var client = await RespireClient.ConnectAsync(Options(server) with { MaxInflightCommands = 1 });
#pragma warning restore RESP010
        var occupying = client.ExecuteAsync("ECHO", ["occupy"]).AsTask();
        await occupied.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var pending = client.ExecuteAsync(descriptor, arguments).AsTask();
        await Assert.That(pending.IsCompleted).IsFalse();
        server.CloseConnections();
        using var result = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(result.AsString()).IsEqualTo("OK");
        await Assert.That(effects).IsEqualTo(1);
        await Assert.That(async () => { using var ignored = await occupying; }).Throws<RespireConnectionException>();
    }

    [Test]
    public async Task CoalescedCacheMissOwnsOneRetryAndOnePublication()
    {
        var events = new ConcurrentDictionary<string, long>();
        using var listener = RetryListener(events);
        var reads = 0;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = Server();
        server.GateCommand("GET key", release.Task);
        server.ReplyOverride = (_, command) =>
        {
            if (command != "GET key") return Setup(command);
            return Interlocked.Increment(ref reads) == 1 ? "-TRYAGAIN busy\r\n"u8.ToArray() : "$5\r\nvalue\r\n"u8.ToArray();
        };
#pragma warning disable RESP010 // Each wire case owns an isolated fake server and client retry state; sharing would leak fault and policy state.
        await using var client = await RespireClient.ConnectAsync(Options(server) with
        { Protocol = RespireProtocol.Resp3, ClientSideCache = new() { CoalesceConcurrentMisses = true } });
#pragma warning restore RESP010
        var calls = Enumerable.Range(0, 10).Select(_ => client.GetStringAsync("key").AsTask()).ToArray();
        // Release the first reply only after all waiters have joined the same producer.
        release.TrySetResult();
        var values = await Task.WhenAll(calls).WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(values.All(value => value == "value")).IsTrue();
        await Assert.That(await client.GetStringAsync("key")).IsEqualTo("value");
        await Assert.That(reads).IsEqualTo(2);
        await Assert.That(events["attempted"]).IsEqualTo(1);
        await Assert.That(events["succeeded"]).IsEqualTo(1);
        await Assert.That(server.ReceivedCommands.Count(command => command == "CLIENT CACHING YES")).IsEqualTo(2);
    }

    [Test]
    public async Task CancelledMutationKeepsFenceThroughLateReplyAndFifoRetirement()
    {
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = Server();
        server.SuppressReply = command =>
        {
            if (command != "SET key value") return false;
            received.TrySetResult(); return true;
        };
#pragma warning disable RESP010 // Each wire case owns an isolated fake server and client retry state; sharing would leak fault and policy state.
        await using var client = await RespireClient.ConnectAsync(Options(server) with
        { Protocol = RespireProtocol.Resp3, ClientSideCache = new() });
#pragma warning restore RESP010
        using var cancellation = new CancellationTokenSource();
        var pending = client.SetAsync("key", "value", cancellationToken: cancellation.Token).AsTask();
        await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.That(async () => await pending).Throws<OperationCanceledException>();
        await Assert.That(client.Core.ClientCache!.InspectForTests().ActiveMutationCount).IsEqualTo(1);
        await server.SendRawAsync(FakeRespServer.OkReply, server.ReceivedConnectionIds[^1]);
        server.SuppressReply = null;
        using var barrier = await client.ExecuteAsync(RespireCommands.Connection.PING);
        await Assert.That(barrier.AsString()).IsEqualTo("OK");
        await Assert.That(client.Core.ClientCache!.InspectForTests().ActiveMutationCount).IsEqualTo(0);
        await Assert.That(server.ReceivedCommands.Count(command => command == "SET key value")).IsEqualTo(1);
    }

    [Test]
    public async Task ReusedRetryOwnerDoesNotChangeCanceledFramesAwaitingRetirement()
    {
        var firstSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = Server();
        server.SuppressReply = command =>
        {
            if (command == "SET key first") { firstSeen.TrySetResult(); return true; }
            if (command == "STRLEN second") { secondSeen.TrySetResult(); return true; }
            return false;
        };
#pragma warning disable RESP010 // Each wire case owns an isolated fake server and client retry state; sharing would leak fault and policy state.
        await using var client = await RespireClient.ConnectAsync(Options(server) with
        { Protocol = RespireProtocol.Resp3, ClientSideCache = new() });
#pragma warning restore RESP010
        using var cancellation = new CancellationTokenSource();
        var first = client.SetAsync("key", "first", cancellationToken: cancellation.Token).AsTask();
        await firstSeen.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.That(async () => await first).Throws<OperationCanceledException>();
        // The first owner can now be reused, but its transport copy and fence remain in FIFO.
        var second = client.Strings.LengthAsync("second").AsTask();
        await secondSeen.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await server.SendRawAsync("+OK\r\n:6\r\n"u8.ToArray(), server.ReceivedConnectionIds[^1]);
        await Assert.That(await second.WaitAsync(TimeSpan.FromSeconds(5))).IsEqualTo(6L);
        await Assert.That(client.Core.ClientCache!.InspectForTests().ActiveMutationCount).IsEqualTo(0);
        await Assert.That(server.ReceivedCommands.Count(command => command == "SET key first")).IsEqualTo(1);
    }

    [Test]
    public async Task OneDeadlineSpansRetriesAndStopsAcceptedLateReplies()
    {
        await using var server = Server();
        var reads = 0;
        server.ReplyOverride = (_, command) => command == "GET key"
            ? Interlocked.Increment(ref reads) == 1 ? "-TRYAGAIN busy\r\n"u8.ToArray() : "$1\r\nx\r\n"u8.ToArray()
            : Setup(command);
        // Compile both attempt paths before starting the deliberately short shared deadline.
#pragma warning disable RESP010 // Each wire case owns an isolated fake server and client retry state; sharing would leak fault and policy state.
        await using (var warmup = await RespireClient.ConnectAsync(Options(server) with
            { RetryPolicy = new() { InitialBackoff = TimeSpan.Zero } }))
#pragma warning restore RESP010
            await Assert.That(await warmup.GetStringAsync("key")).IsEqualTo("x");
        reads = 0;
        server.DelayCommand("GET key", 150);
#pragma warning disable RESP010 // Each wire case owns an isolated fake server and client retry state; sharing would leak fault and policy state.
        await using var client = await RespireClient.ConnectAsync(Options(server) with
        { CommandTimeout = TimeSpan.FromMilliseconds(250), RetryPolicy = new() { InitialBackoff = TimeSpan.Zero } });
#pragma warning restore RESP010
        await Assert.That(async () => await client.GetStringAsync("key")).ThrowsExactly<RespireTimeoutException>();
        using var barrier = await client.ExecuteAsync(RespireCommands.Connection.PING);
        await Assert.That(barrier.AsString()).IsEqualTo("OK");
        await Assert.That(reads).IsEqualTo(2);
    }

    [Test]
    public async Task ThrowingRetryListenerCannotChangeSuccessfulResponse()
    {
        using var listener = new MeterListener();
        listener.InstrumentPublished = static (instrument, meter) =>
        {
            if (instrument.Name == "respire.command.retries") meter.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>(static (_, _, _, _) => throw new InvalidOperationException("exporter failed"));
        listener.Start();
        var reads = 0;
        await using var server = Server();
        server.ReplyOverride = (_, command) => command == "GET key"
            ? Interlocked.Increment(ref reads) == 1 ? "-LOADING busy\r\n"u8.ToArray() : "$1\r\nx\r\n"u8.ToArray()
            : Setup(command);
#pragma warning disable RESP010 // Each wire case owns an isolated fake server and client retry state; sharing would leak fault and policy state.
        await using var client = await RespireClient.ConnectAsync(Options(server));
#pragma warning restore RESP010
        await Assert.That(await client.GetStringAsync("key")).IsEqualTo("x");
        await Assert.That(reads).IsEqualTo(2);
    }

    [Test]
    [Arguments("SET")]
    [Arguments("ZADD")]
    [Arguments("XADD")]
    public async Task OptionSensitiveWritesDoNotAcquireOverwriteRetryPermission(string name)
    {
        var effects = 0;
        await using var server = Server();
        server.ReplyOverride = (_, command) =>
        {
            if (!command.StartsWith(name + " key ", StringComparison.Ordinal)) return Setup(command);
            Interlocked.Increment(ref effects);
            return "-TRYAGAIN busy\r\n"u8.ToArray();
        };
#pragma warning disable RESP010 // Each wire case owns an isolated fake server and client retry state; sharing would leak fault and policy state.
        await using var client = await RespireClient.ConnectAsync(Options(server) with
        { RetryPolicy = new() { InitialBackoff = TimeSpan.Zero, MaxCommandRetryCategory = RespireCommandRetryCategory.WriteLastWins } });
#pragma warning restore RESP010
        var descriptor = RespireCommands.All.ToArray().Single(command => command.Name == name);
        RespireValue[] arguments = name switch
        {
            "SET" => ["key", "new", "GET"], "ZADD" => ["key", "INCR", "1", "member"],
            _ => ["key", "*", "field", "value"],
        };
        await Assert.That(async () => { using var result = await client.ExecuteAsync(descriptor, arguments); })
            .ThrowsExactly<RespireServerException>();
        await Assert.That(effects).IsEqualTo(1);
    }

    [Test]
    [Arguments(false, 1)]
    [Arguments(true, 2)]
    public async Task CatalogSetArgumentsDoNotRefineRiskWithoutDeclaration(bool declared, int expected)
    {
        var effects = 0;
        await using var server = Server();
        server.ReplyOverride = (_, command) =>
        {
            if (command != "SET key value") return Setup(command);
            return Interlocked.Increment(ref effects) == 1 ? "-TRYAGAIN busy\r\n"u8.ToArray() : FakeRespServer.OkReply;
        };
#pragma warning disable RESP010 // Each wire case owns an isolated fake server and client retry state; sharing would leak fault and policy state.
        await using var client = await RespireClient.ConnectAsync(Options(server) with
        { RetryPolicy = new() { InitialBackoff = TimeSpan.Zero, MaxCommandRetryCategory = RespireCommandRetryCategory.WriteLastWins } });
#pragma warning restore RESP010
        var descriptor = declared
            ? RespireCommands.String.SET.WithRetryCategory(RespireCommandRetryCategory.WriteLastWins)
            : RespireCommands.String.SET;
        RespireValue[] arguments = ["key", "value"];
        if (declared)
        {
            using var result = await client.ExecuteAsync(descriptor, arguments);
        }
        else
        {
            await Assert.That(async () => { using var result = await client.ExecuteAsync(descriptor, arguments); })
                .ThrowsExactly<RespireServerException>();
        }
        await Assert.That(effects).IsEqualTo(expected);
    }

    [Test]
    public async Task RawCommandNameDoesNotGrantAuditedReplayPermission()
    {
        var effects = 0;
        await using var server = Server();
        server.ReplyOverride = (_, command) =>
        {
            if (command != "GET key") return Setup(command);
            Interlocked.Increment(ref effects); return "-LOADING busy\r\n"u8.ToArray();
        };
#pragma warning disable RESP010 // Each wire case owns an isolated fake server and client retry state; sharing would leak fault and policy state.
        await using var client = await RespireClient.ConnectAsync(Options(server));
#pragma warning restore RESP010
        await Assert.That(async () => { using var result = await client.ExecuteAsync("GET", ["key"]); })
            .ThrowsExactly<RespireServerException>();
        await Assert.That(effects).IsEqualTo(1);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task DeferredMembersRetryOnlyAsCompleteUnits(bool transactional)
    {
        await using var server = Server();
        server.ReplyOverride = (_, command) => command switch
        {
            "GET key" => transactional ? "+QUEUED\r\n"u8.ToArray() : "-TRYAGAIN busy\r\n"u8.ToArray(),
            "GET other" => transactional ? "+QUEUED\r\n"u8.ToArray() : "$2\r\nok\r\n"u8.ToArray(),
            "EXEC" => "*2\r\n-TRYAGAIN busy\r\n$2\r\nok\r\n"u8.ToArray(),
            _ => Setup(command),
        };
#pragma warning disable RESP010 // Each wire case owns an isolated fake server and client retry state; sharing would leak fault and policy state.
        await using var client = await RespireClient.ConnectAsync(Options(server));
#pragma warning restore RESP010
        using var batch = client.CreateBatch();
        await using var transaction = client.CreateTransaction();
        IRespireCommandQueue queue = transactional ? transaction : batch;
        var pending = queue.Strings.GetString("key");
        var successful = queue.Strings.GetString("other");
        if (transactional) await transaction.CommitAsync();
        else await batch.TryExecuteAsync();
        await Assert.That(pending.Error).IsTypeOf<RespireServerException>();
        await Assert.That(await successful).IsEqualTo("ok");
        await Assert.That(server.ReceivedCommands.Count(command => command == "GET key")).IsEqualTo(3);
        await Assert.That(server.ReceivedCommands.Count(command => command == "GET other")).IsEqualTo(3);
        if (transactional)
        {
            await Assert.That(server.ReceivedCommands.Count(command => command == "MULTI")).IsEqualTo(3);
            await Assert.That(server.ReceivedCommands.Count(command => command == "EXEC")).IsEqualTo(3);
        }
    }

    [Test]
    public async Task AttemptErrorsAndFinalErrorsHaveSeparateOwnersAcrossReusedSources()
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        var internalErrors = 0L;
        var finalErrors = 0L;
        using var listener = new MeterListener();
        listener.InstrumentPublished = static (instrument, meter) =>
        {
            if (instrument.Meter.Name == "Respire" && instrument.Name == "redis.client.errors")
                meter.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            foreach (var tag in tags)
                if (tag.Key == "redis.client.errors.internal")
                {
                    if (Equals(tag.Value, true)) Interlocked.Add(ref internalErrors, value);
                    else Interlocked.Add(ref finalErrors, value);
                }
        });
        listener.Start();
        var reads = 0;
        await using var server = Server();
        server.ReplyOverride = (_, command) => command == "GET key"
            ? Interlocked.Increment(ref reads) % 2 != 0 ? "-LOADING busy\r\n"u8.ToArray() : "$1\r\nx\r\n"u8.ToArray()
            : Setup(command);
#pragma warning disable RESP010 // Each wire case owns an isolated fake server and client retry state; sharing would leak fault and policy state.
        await using var client = await RespireClient.ConnectAsync(Options(server));
#pragma warning restore RESP010
        for (var index = 0; index < 128; index++)
            await Assert.That(await client.GetStringAsync("key")).IsEqualTo("x");
        await Assert.That(internalErrors).IsEqualTo(128);
        await Assert.That(finalErrors).IsEqualTo(0);
        server.ReplyOverride = (_, command) => command == "GET key" ? "-LOADING busy\r\n"u8.ToArray() : Setup(command);
        await Assert.That(async () => await client.GetStringAsync("key")).ThrowsExactly<RespireServerException>();
        await Assert.That(internalErrors).IsEqualTo(130);
        await Assert.That(finalErrors).IsEqualTo(1);
    }

    private static MeterListener RetryListener(ConcurrentDictionary<string, long> events)
    {
        var listener = new MeterListener();
        listener.InstrumentPublished = static (instrument, meter) =>
        {
            if (instrument.Meter.Name == "Respire" && instrument.Name == "respire.command.retries")
                meter.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            foreach (var tag in tags)
                if (tag.Key == "respire.command.retry.outcome")
                    events.AddOrUpdate((string)tag.Value!, value, (_, old) => old + value);
        });
        listener.Start();
        return listener;
    }

    private static FakeRespServer Server() => new(32, FakeRespServer.OkReply)
    { ReplyOverride = (_, command) => Setup(command) };
    private static byte[]? Setup(string command) => command switch
    {
        _ when command.StartsWith("HELLO", StringComparison.Ordinal) => "%1\r\n$5\r\nproto\r\n:3\r\n"u8.ToArray(),
        "CLIENT ID" => ":1\r\n"u8.ToArray(),
        _ => null,
    };
    private static RespireOptions Options(FakeRespServer server) => new()
    {
        Endpoints = { new("127.0.0.1", server.Port) }, Protocol = RespireProtocol.Resp2,
        ThreadPoolMonitoring = false, CommandTimeout = TimeSpan.FromSeconds(3),
        RetryPolicy = new() { InitialBackoff = TimeSpan.Zero },
    };
    private static async Task<Exception> Failure(Func<Task> operation)
    {
        try { await operation(); }
        catch (Exception error) { return error; }
        throw new InvalidOperationException("Expected failure.");
    }
}
