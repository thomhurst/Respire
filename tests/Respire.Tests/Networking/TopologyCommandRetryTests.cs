using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text;
using Respire.Internal;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

[NotInParallel]
public partial class TopologyCommandRetryTests
{
    private static readonly byte[] TryAgain = "-TRYAGAIN busy\r\n"u8.ToArray();
    private static readonly byte[] Value = "$5\r\nvalue\r\n"u8.ToArray();

    [Test]
    [Arguments("caller-supplied")]
    [Arguments("created")]
    [Arguments("catalog")]
    public async Task DeclaredRawCategoryAuthorizesRetry(string shape)
    {
        var sends = 0;
        await using var server = Standalone((command, _) => command == "MY.READ key" || command == "GET key"
            ? Interlocked.Increment(ref sends) == 1 ? TryAgain : Value : null);
        await using var client = await RespireClient.ConnectAsync(StandaloneOptions(server));
        RespireCommand descriptor = shape switch
        {
            "caller-supplied" => ((RespireCommand)"MY.READ").WithRetryCategory(RespireCommandRetryCategory.ReadOnly),
            "created" => RespireCommand.Create("MY.READ").WithRetryCategory(RespireCommandRetryCategory.ReadOnly),
            _ => RespireCommands.String.GET.WithRetryCategory(RespireCommandRetryCategory.ReadOnly),
        };
        await Assert.That(descriptor.RetryCategory).IsEqualTo(RespireCommandRetryCategory.ReadOnly);
        using var result = await client.ExecuteAsync(descriptor, ["key"]);
        await Assert.That(result.AsString()).IsEqualTo("value");
        await Assert.That(sends).IsEqualTo(2);
    }

    [Test]
    public async Task UndeclaredCallerCommandNeverInfersRetryFromItsName()
    {
        var sends = 0;
        await using var server = Standalone((command, _) => command == "GET key"
            ? Count(ref sends, TryAgain) : null);
        await using var client = await RespireClient.ConnectAsync(StandaloneOptions(server));
        await Assert.That(RespireCommand.Create("GET").RetryCategory).IsEqualTo(RespireCommandRetryCategory.Never);
        await Assert.That(async () => { using var result = await client.ExecuteAsync(RespireCommand.Create("GET"), ["key"]); })
            .ThrowsExactly<RespireServerException>();
        await Assert.That(sends).IsEqualTo(1);
    }

    [Test]
    [Arguments(RespireCommandRetryCategory.ReadOnly, 1)]
    [Arguments(RespireCommandRetryCategory.WriteAccumulating, 2)]
    public async Task DeclaredAccumulatingRawCommandNeedsExplicitPolicyRisk(RespireCommandRetryCategory maximum, int expectedEffects)
    {
        var effects = 0;
        await using var server = Standalone((command, current) =>
        {
            if (command != "MY.INCR counter") return null;
            if (Interlocked.Increment(ref effects) == 1) { current.CloseConnections(); return null; }
            return ":2\r\n"u8.ToArray();
        });
        await using var client = await RespireClient.ConnectAsync(StandaloneOptions(server) with
        { RetryPolicy = new() { InitialBackoff = TimeSpan.Zero, MaxCommandRetryCategory = maximum } });
        var descriptor = RespireCommand.Create("MY.INCR").WithRetryCategory(RespireCommandRetryCategory.WriteAccumulating);
        if (expectedEffects == 1)
            await Assert.That(async () => { using var result = await client.ExecuteAsync(descriptor, ["counter"]); })
                .Throws<RespireConnectionException>();
        else
        {
            using var result = await client.ExecuteAsync(descriptor, ["counter"]);
            await Assert.That(result.AsInteger()).IsEqualTo(2);
        }
        await Assert.That(effects).IsEqualTo(expectedEffects);
    }

    [Test]
    public async Task InvalidDeclaredCategoriesAreRejected()
    {
        await Assert.That(() => RespireCommand.Create("X").WithRetryCategory((RespireCommandRetryCategory)99))
            .ThrowsExactly<ArgumentOutOfRangeException>();
        await Assert.That(() => default(RespireCommand).WithRetryCategory(RespireCommandRetryCategory.ReadOnly))
            .ThrowsExactly<InvalidOperationException>();
        await Assert.That(() => RespireScript.Create("return 1").WithRetryCategory((RespireCommandRetryCategory)99))
            .ThrowsExactly<ArgumentOutOfRangeException>();
        await Assert.That(() => RespireFunction.Create("f").WithRetryCategory((RespireCommandRetryCategory)99))
            .ThrowsExactly<ArgumentOutOfRangeException>();
    }

    [Test]
    [Arguments(false, 1)]
    [Arguments(true, 2)]
    public async Task ScriptsRetryOnlyWithDeclaredCategory(bool declared, int expectedSends)
    {
        var sends = 0;
        await using var server = Standalone((command, _) => command.StartsWith("EVALSHA ", StringComparison.Ordinal)
            ? Interlocked.Increment(ref sends) == 1 ? TryAgain : ":7\r\n"u8.ToArray() : null);
        await using var client = await RespireClient.ConnectAsync(StandaloneOptions(server));
        var script = RespireScript.Create("return 7", readOnly: false);
        if (declared) script = script.WithRetryCategory(RespireCommandRetryCategory.ReadOnly);
        await Assert.That(RespireScript.Create("return 7", readOnly: true).RetryCategory).IsEqualTo(RespireCommandRetryCategory.Never);
        if (declared)
            await Assert.That(await client.Scripts.ExecuteIntegerAsync(script, ["key"])).IsEqualTo(7L);
        else
            await Assert.That(async () => await client.Scripts.ExecuteIntegerAsync(script, ["key"]))
                .ThrowsExactly<RespireServerException>();
        await Assert.That(sends).IsEqualTo(expectedSends);
        await Assert.That(server.ReceivedCommands.Any(command => command.StartsWith("EVAL ", StringComparison.Ordinal))).IsFalse();
    }

    [Test]
    [Arguments("script")]
    [Arguments("function")]
    public async Task DeclaredRetryWaitsForTheReplacementConnection(string shape)
    {
        var sends = 0;
        await using var server = Standalone((command, current) =>
        {
            if (!command.StartsWith(shape == "script" ? "EVALSHA " : "FCALL_RO ", StringComparison.Ordinal)) return null;
            if (Interlocked.Increment(ref sends) == 1) { current.CloseConnections(); return null; }
            return ":7\r\n"u8.ToArray();
        });
        await using var client = await RespireClient.ConnectAsync(StandaloneOptions(server));
        var result = shape == "script"
            ? await client.Scripts.ExecuteIntegerAsync(
                RespireScript.Create("return 7").WithRetryCategory(RespireCommandRetryCategory.ReadOnly), ["key"])
            : await client.Functions.ExecuteIntegerAsync(
                RespireFunction.Create("reader", readOnly: true).WithRetryCategory(RespireCommandRetryCategory.ReadOnly), ["key"]);
        await Assert.That(result).IsEqualTo(7L);
        await Assert.That(sends).IsEqualTo(2);
    }

    [Test]
    public async Task RetriedScriptResendsTheBytesOfItsFirstAttempt()
    {
        var sends = new ConcurrentQueue<string>();
        await using var server = Standalone((command, _) =>
        {
            if (!command.StartsWith("EVALSHA ", StringComparison.Ordinal)) return null;
            sends.Enqueue(command);
            return sends.Count == 1 ? TryAgain : ":1\r\n"u8.ToArray();
        });
        await using var client = await RespireClient.ConnectAsync(StandaloneOptions(server));
        var argument = "first"u8.ToArray();
        var script = RespireScript.Create("return 1").WithRetryCategory(RespireCommandRetryCategory.ReadOnly);
        var pending = client.Scripts.ExecuteIntegerAsync(script, ["key"], [argument]);
        "later"u8.CopyTo(argument);
        await Assert.That(await pending).IsEqualTo(1L);
        await Assert.That(sends.Count).IsEqualTo(2);
        await Assert.That(sends.All(command => command.EndsWith(" key first", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    [Arguments(false, 1)]
    [Arguments(true, 2)]
    public async Task FunctionsRetryOnlyWithDeclaredCategory(bool declared, int expectedSends)
    {
        var sends = 0;
        await using var server = Standalone((command, _) => command == "FCALL_RO reader 1 key"
            ? Interlocked.Increment(ref sends) == 1 ? TryAgain : Value : null);
        await using var client = await RespireClient.ConnectAsync(StandaloneOptions(server));
        var function = RespireFunction.Create("reader", readOnly: true);
        if (declared) function = function.WithRetryCategory(RespireCommandRetryCategory.ReadOnly);
        if (declared)
            await Assert.That(await client.Functions.ExecuteStringAsync(function, ["key"])).IsEqualTo("value");
        else
            await Assert.That(async () => await client.Functions.ExecuteStringAsync(function, ["key"]))
                .ThrowsExactly<RespireServerException>();
        await Assert.That(sends).IsEqualTo(expectedSends);
    }

    [Test]
    [Arguments("result", true)]
    [Arguments("result", false)]
    [Arguments("span", true)]
    [Arguments("span", false)]
    [Arguments("string", true)]
    [Arguments("string", false)]
    [Arguments("integer", true)]
    [Arguments("integer", false)]
    [Arguments("typed", true)]
    [Arguments("typed", false)]
    public async Task ImmediateFunctionRetriesRespectOneAttemptBudget(string shape, bool readOnly)
    {
        var events = new ConcurrentDictionary<string, long>();
        using var listener = RetryListener(events);
        var calls = 0;
        var command = readOnly ? "FCALL_RO reader 1 key" : "FCALL reader 1 key";
        await using var server = Standalone((received, _) => received == command
            ? Count(ref calls, TryAgain) : null);
        await using var client = await RespireClient.ConnectAsync(StandaloneOptions(server) with
        {
            RetryPolicy = new()
            {
                MaxAttempts = 3, InitialBackoff = TimeSpan.Zero,
                MaxCommandRetryCategory = RespireCommandRetryCategory.WriteAccumulating,
            },
        });
        var category = readOnly ? RespireCommandRetryCategory.ReadOnly : RespireCommandRetryCategory.WriteAccumulating;
        var function = RespireFunction.Create("reader", readOnly).WithRetryCategory(category);
        await Assert.That(async () =>
        {
            switch (shape)
            {
                case "result":
                    using (await client.Functions.ExecuteAsync(function, ["key"])) { }
                    break;
                case "span":
                    using (await client.Functions.ExecuteSpanAsync(function, ["key"], [])) { }
                    break;
                case "string":
                    await client.Functions.ExecuteStringAsync(function, ["key"]);
                    break;
                case "integer":
                    await client.Functions.ExecuteIntegerAsync(function, ["key"]);
                    break;
                case "typed":
                    await client.Functions.ExecuteAsync<string>(function, ["key"]);
                    break;
            }
        }).ThrowsExactly<RespireServerException>();
        await Assert.That(calls).IsEqualTo(3);
        await Assert.That(events.GetValueOrDefault("attempted")).IsEqualTo(2L);
        await Assert.That(events.GetValueOrDefault("exhausted")).IsEqualTo(1L);
        await Assert.That(events.GetValueOrDefault("succeeded")).IsEqualTo(0L);
    }

    [Test]
    [Arguments(RespireCommandRetryCategory.ReadOnly, false)]
    [Arguments(RespireCommandRetryCategory.WriteLastWins, true)]
    public async Task SentinelReadOnlyRetryWritesToTheNewlyDiscoveredPrimary(RespireCommandRetryCategory maximum, bool retries)
    {
        var demoted = 0;
        await using var replacement = Primary(_ => null);
        await using var former = Primary(command =>
        {
            if (command == "ROLE") return RoleReply(Volatile.Read(ref demoted) == 0 ? "master" : "slave");
            if (command != "SET key value") return null;
            Volatile.Write(ref demoted, 1);
            return "-READONLY You can't write against a read only replica.\r\n"u8.ToArray();
        });
        await using var sentinel = Sentinel(() => Volatile.Read(ref demoted) == 0 ? former.Port : replacement.Port);
        await using var client = await RespireClient.ConnectAsync(SentinelOptions(sentinel) with
        { RetryPolicy = new() { InitialBackoff = TimeSpan.Zero, MaxCommandRetryCategory = maximum } });
        if (retries) await Assert.That(await client.SetAsync("key", "value")).IsTrue();
        else await Assert.That(async () => await client.SetAsync("key", "value")).ThrowsExactly<RespireServerException>();
        await Assert.That(former.ReceivedCommands.Count(command => command == "SET key value")).IsEqualTo(1);
        await Assert.That(replacement.ReceivedCommands.Count(command => command == "SET key value")).IsEqualTo(retries ? 1 : 0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task SentinelStreamAppendDetectsReplacementVersion(bool newerFirst)
    {
        var demoted = 0;
        var formerAdds = 0;
        byte[] AppendReply(string command, bool supportsIdempotency)
            => !supportsIdempotency && command.Contains(" IDMP ", StringComparison.Ordinal)
                ? "-ERR Invalid stream ID specified as stream command argument\r\n"u8.ToArray()
                : "$3\r\n1-0\r\n"u8.ToArray();
        await using var replacement = Primary(command => command.StartsWith("XADD ", StringComparison.Ordinal)
            ? AppendReply(command, !newerFirst) : null);
        await using var former = Primary(command =>
        {
            if (command == "ROLE") return RoleReply(Volatile.Read(ref demoted) == 0 ? "master" : "slave");
            if (!command.StartsWith("XADD ", StringComparison.Ordinal)) return null;
            // An old primary receives both the unsupported attempt and its plain fallback.
            if (Interlocked.Increment(ref formerAdds) > (newerFirst ? 1 : 2))
            {
                Volatile.Write(ref demoted, 1);
                return "-READONLY You can't write against a read only replica.\r\n"u8.ToArray();
            }
            return AppendReply(command, newerFirst);
        });
        await using var sentinel = Sentinel(() => Volatile.Read(ref demoted) == 0 ? former.Port : replacement.Port);
        await using var client = await RespireClient.ConnectAsync(SentinelOptions(sentinel) with
        { RetryPolicy = new() { InitialBackoff = TimeSpan.Zero, MaxCommandRetryCategory = RespireCommandRetryCategory.WriteChecked } });

        await client.Streams.AddAsync("s", ("f", "v"));
        await client.Streams.AddAsync("s", ("f", "v"));
        await client.Streams.AddAsync("s", ("f", "v"));

        var replacementAdds = replacement.ReceivedArguments.Where(command => command[0].AsSpan().SequenceEqual("XADD"u8)).ToArray();
        await Assert.That(replacementAdds.Length).IsEqualTo(newerFirst ? 4 : 2);
        await Assert.That(replacementAdds.Count(command => command[2].AsSpan().SequenceEqual("IDMP"u8))).IsEqualTo(2);
        await Assert.That(replacementAdds.Count(command => command[2].AsSpan().SequenceEqual("*"u8))).IsEqualTo(newerFirst ? 2 : 0);
    }

    [Test]
    public async Task SentinelConnectionLossRetryRediscoversThePrimary()
    {
        var failed = 0;
        await using var replacement = Primary(command => command == "GET key" ? Value : null);
        FakeRespServer? formerServer = null;
        await using var former = Primary(command =>
        {
            if (command != "GET key") return null;
            Volatile.Write(ref failed, 1);
            formerServer!.CloseConnections();
            return null;
        });
        formerServer = former;
        await using var sentinel = Sentinel(() => Volatile.Read(ref failed) == 0 ? former.Port : replacement.Port);
        await using var client = await RespireClient.ConnectAsync(SentinelOptions(sentinel));
        await Assert.That(await client.GetStringAsync("key")).IsEqualTo("value");
        await Assert.That(replacement.ReceivedCommands.Count(command => command == "GET key")).IsEqualTo(1);
    }

    [Test]
    public async Task ClusterRetriesSelectedFailureAndRecordsOneProducerOutcome()
    {
        var events = new ConcurrentDictionary<string, long>();
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        using var listener = RetryListener(events);
        var reads = 0;
        await using var server = ClusterNode((command, _) => command == "GET key"
            ? Interlocked.Increment(ref reads) == 1 ? TryAgain : Value : null);
        await using var client = await RespireClient.ConnectAsync(ClusterOptions(server));
        await Assert.That(await client.GetStringAsync("key")).IsEqualTo("value");
        await Assert.That(reads).IsEqualTo(2);
        await Assert.That(events.GetValueOrDefault("attempted")).IsEqualTo(1);
        await Assert.That(events.GetValueOrDefault("succeeded")).IsEqualTo(1);
        await Assert.That(events.GetValueOrDefault("exhausted")).IsEqualTo(0);
    }

    [Test]
    [Arguments("MOVED")]
    [Arguments("ASK")]
    public async Task ClusterRedirectsRemainOwnedByRoutingWithoutRetryAttempts(string code)
    {
        var events = new ConcurrentDictionary<string, long>();
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        using var listener = RetryListener(events);
        await using var target = ClusterNode((command, _) => command == "GET key" ? Value : null);
        var redirects = 0;
        await using var source = ClusterNode((command, _) => command == "GET key" && Interlocked.Increment(ref redirects) == 1
            ? Encoding.ASCII.GetBytes($"-{code} {ClusterHash.GetSlot("key")} 127.0.0.1:{target.Port}\r\n") : null);
        await using var client = await RespireClient.ConnectAsync(ClusterOptions(source));
        await Assert.That(await client.GetStringAsync("key")).IsEqualTo("value");
        await Assert.That(source.ReceivedCommands.Count(command => command == "GET key")).IsEqualTo(1);
        await Assert.That(target.ReceivedCommands.Count(command => command == "GET key")).IsEqualTo(1);
        await Assert.That(events.GetValueOrDefault("attempted")).IsEqualTo(0);
    }

    [Test]
    [Arguments("ZADD")]
    [Arguments("XADD")]
    public async Task ClusterAccumulatingWritesAreNotResentUnderOverwritePolicy(string name)
    {
        var effects = 0;
        await using var server = ClusterNode((command, current) =>
        {
            if (!command.StartsWith(name + " ", StringComparison.Ordinal)) return null;
            Interlocked.Increment(ref effects);
            current.CloseConnections();
            return null;
        });
        await using var client = await RespireClient.ConnectAsync(ClusterOptions(server) with
        {
            RetryPolicy = new() { InitialBackoff = TimeSpan.Zero, MaxCommandRetryCategory = RespireCommandRetryCategory.WriteLastWins },
        });
        var descriptor = name == "ZADD" ? RespireCommands.SortedSet.ZADD : RespireCommands.Stream.XADD;
        RespireValue[] arguments = name == "ZADD" ? ["key", "INCR", "1", "member"] : ["key", "*", "field", "value"];
        await Assert.That(descriptor.RetryCategory).IsEqualTo(RespireCommandRetryCategory.WriteAccumulating);
        await Assert.That(async () => { using var result = await client.ExecuteAsync(descriptor, arguments); })
            .Throws<RespireConnectionException>();
        await Assert.That(effects).IsEqualTo(1);
    }

    [Test]
    public async Task ClusterCircuitRejectionIsNeverRetried()
    {
        var events = new ConcurrentDictionary<string, long>();
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        using var listener = RetryListener(events);
        await using var server = ClusterNode((command, _) => command.StartsWith("GET ", StringComparison.Ordinal) ? Value : null);
        await using var client = await RespireClient.ConnectAsync(ClusterOptions(server) with
        {
            CircuitBreaker = new() { MinimumFailureCount = 1, FailureRateThreshold = 0.01, OpenDuration = TimeSpan.FromMinutes(1) },
        });
        await Assert.That(await client.GetStringAsync("warm")).IsEqualTo("value");
        var admission = client.Core.Circuits!.Acquire(new("127.0.0.1", server.Port), default);
        try { admission.Failed(new RespireConnectionException("Injected node failure."), default); }
        finally { admission.Dispose(); }
        await Assert.That(async () => await client.GetStringAsync("rejected")).ThrowsExactly<RespireCircuitOpenException>();
        await Assert.That(server.ReceivedCommands.Contains("GET rejected")).IsFalse();
        await Assert.That(events.GetValueOrDefault("attempted")).IsEqualTo(0);
    }

    [Test]
    public async Task ClusterRetriesShareOneDeadline()
    {
        var reads = 0;
        await using var server = ClusterNode((command, _) => command == "GET key" ? Count(ref reads, TryAgain) : null);
        await using var client = await RespireClient.ConnectAsync(ClusterOptions(server) with
        {
            CommandTimeout = TimeSpan.FromMilliseconds(400),
            RetryPolicy = new() { MaxAttempts = 100, InitialBackoff = TimeSpan.FromMilliseconds(50),
                MaxBackoff = TimeSpan.FromMilliseconds(50), Jitter = 0 },
        });
        var elapsed = Stopwatch.StartNew();
        var error = await Failure(async () => await client.GetStringAsync("key"));
        elapsed.Stop();
        await Assert.That(error is RespireServerException or RespireTimeoutException).IsTrue();
        await Assert.That(reads).IsGreaterThan(1);
        await Assert.That(reads).IsLessThan(20);
        await Assert.That(elapsed.Elapsed).IsLessThan(TimeSpan.FromSeconds(3));
    }

    [Test]
    public async Task ClusterRetryFollowsFailoverInsteadOfWaitingForTheLostOwner()
    {
        // "key" hashes to slot 12539. Its owner stops serving; the survivor then reports that
        // it owns every slot, as Cluster does after promoting the lost primary's replica.
        var failedOver = 0;
        Task? stopping = null;
        await using var survivor = new FakeRespServer(32, FakeRespServer.OkReply);
        await using var owner = new FakeRespServer(32, FakeRespServer.OkReply);
        byte[] Map() => Volatile.Read(ref failedOver) == 0 ? SplitSlots(survivor.Port, owner.Port) : Slots(survivor.Port);
        survivor.ReplyOverride = (_, command) => command switch
        {
            "CLUSTER SLOTS" => Map(),
            "GET key" => Value,
            _ => null,
        };
        owner.ReplyOverride = (_, command) =>
        {
            if (command == "CLUSTER SLOTS") return Map();
            if (command != "GET key") return null;
            Volatile.Write(ref failedOver, 1);
            // Stop listening before the connection drops, so the owner cannot reconnect.
            stopping = owner.DisposeAsync().AsTask();
            owner.CloseConnections();
            return null;
        };
        await using var client = await RespireClient.ConnectAsync(ClusterOptions(owner) with
        {
            CommandTimeout = TimeSpan.FromSeconds(10),
            RetryPolicy = new() { MaxAttempts = 2, InitialBackoff = TimeSpan.Zero },
        });
        var elapsed = Stopwatch.StartNew();
        await Assert.That(await client.GetStringAsync("key")).IsEqualTo("value");
        elapsed.Stop();
        if (stopping is not null) await stopping;
        await Assert.That(owner.ReceivedCommands.Count(command => command == "GET key")).IsEqualTo(1);
        await Assert.That(survivor.ReceivedCommands.Count(command => command == "GET key")).IsEqualTo(1);
        // The retry must not hold the lost owner until the logical command deadline.
        await Assert.That(elapsed.Elapsed).IsLessThan(TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task ClusterRetryWaitsForALoneOwnerThatReconnectsSlowly()
    {
        // With no other master, nothing can take the slot over. A reconnect slower than the
        // refresh window must not turn the retry into "No healthy connections".
        var reads = 0;
        await using var server = ClusterNode((command, current) =>
        {
            if (command != "GET key") return null;
            if (Interlocked.Increment(ref reads) > 1) return Value;
            current.CloseConnections();
            return null;
        });
        await using var client = await RespireClient.ConnectAsync(ClusterOptions(server) with
        {
            ReconnectPolicy = new() { InitialDelay = TimeSpan.FromMilliseconds(1500), MaxDelay = TimeSpan.FromMilliseconds(1500), JitterRatio = 0 },
            RetryPolicy = new() { MaxAttempts = 2, InitialBackoff = TimeSpan.Zero },
        });
        await Assert.That(await client.GetStringAsync("key")).IsEqualTo("value");
        await Assert.That(reads).IsEqualTo(2);
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task ClusterRetryStartsReplacementForDisconnectedRoute(bool clearSlotOwner, bool cancel)
    {
        await using var server = ClusterNode((command, _) => command == "GET key" ? Value : null);
        await using var client = await RespireClient.ConnectAsync(ClusterOptions(server) with { ClientName = "retry-seed" });
        var router = client.Core.Cluster!;
        var owner = router.GetKnownSlotOwner(12539)!;
        var connection = owner.GetConnection();
        var handshake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // Hold replacement handshakes so route selection cannot race with a ready seed.
        server.ReadGate = handshake.Task;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            if (clearSlotOwner) router.ClearSlotOwner(12539, owner);
            await connection.DisposeAsync();
            var wait = router.WaitForRouteReplacementAsync(12539, cancellation.Token).AsTask();
            await Assert.That(wait.IsCompleted).IsFalse();
            await Assert.That(owner.IsReconnecting).IsTrue();
            if (cancel)
            {
                cancellation.Cancel();
                var error = await Failure(async () => await wait);
                await Assert.That(error).IsTypeOf<OperationCanceledException>();
                await Assert.That(((OperationCanceledException)error).CancellationToken).IsEqualTo(cancellation.Token);
            }
            else
            {
                handshake.TrySetResult();
                await wait;
                await Assert.That(await client.GetStringAsync("key")).IsEqualTo("value");
            }
        }
        finally
        {
            cancellation.Cancel();
            handshake.TrySetResult();
        }
    }

    [Test]
    public async Task FailoverGroupRetryUsesTheNewlyActiveMember()
    {
        var down = 0;
        await using var primary = new FakeRespServer(8, FakeRespServer.PongReply)
        {
            ReplyOverride = (_, command) => command switch
            {
                "PING" when Volatile.Read(ref down) == 1 => "-ERR down\r\n"u8.ToArray(),
                "GET key" => "-LOADING busy\r\n"u8.ToArray(),
                _ => null,
            },
        };
        await using var secondary = new FakeRespServer(8, FakeRespServer.PongReply)
        { ReplyOverride = (_, command) => command == "GET key" ? Value : null };
        await using var group = await RespireFailoverGroup.ConnectAsync(
            [Candidate(primary, 0), Candidate(secondary, 1)], GroupOptions());
        var clients = new List<IRespireClient>();
        var result = await group.ExecuteAsync(async (client, token) =>
        {
            clients.Add(client);
            if (clients.Count == 1)
            {
                // The probe opens the primary circuit while this attempt still holds its member.
                Volatile.Write(ref down, 1);
                await group.ForTests.ProbeAsync(0, token);
                await group.ForTests.SelectActiveAsync();
            }
            return await client.GetStringAsync("key", token);
        }, RespireCommandRetryCategory.ReadOnly);
        await Assert.That(result).IsEqualTo("value");
        await Assert.That(clients.Count).IsEqualTo(2);
        await Assert.That(ReferenceEquals(clients[0], clients[1])).IsFalse();
        await Assert.That(ReferenceEquals(clients[1], group.ActiveClient)).IsTrue();
        await Assert.That(primary.ReceivedCommands.Count(command => command == "GET key")).IsEqualTo(1);
    }

    [Test]
    [Arguments(false, RespireCommandRetryCategory.ReadOnly)]
    [Arguments(true, RespireCommandRetryCategory.Never)]
    public async Task FailoverGroupRunsOnceWithoutPolicyOrCategory(bool policy, RespireCommandRetryCategory category)
    {
        await using var primary = new FakeRespServer(8, FakeRespServer.PongReply)
        { ReplyOverride = (_, command) => command == "GET key" ? "-LOADING busy\r\n"u8.ToArray() : null };
        await using var group = await RespireFailoverGroup.ConnectAsync([Candidate(primary, 0)],
            GroupOptions() with { RetryPolicy = policy ? new() { InitialBackoff = TimeSpan.Zero } : null });
        var attempts = 0;
        await Assert.That(async () => await group.ExecuteAsync((client, token) =>
            {
                attempts++;
                return client.GetStringAsync("key", token);
            }, category))
            .ThrowsExactly<RespireServerException>();
        await Assert.That(attempts).IsEqualTo(1);
    }

    [Test]
    public async Task FailoverGroupValidatesItsRetryPolicy()
    {
        await using var primary = new FakeRespServer(8, FakeRespServer.PongReply);
        await Assert.That(async () => await RespireFailoverGroup.ConnectAsync([Candidate(primary, 0)],
                GroupOptions() with { RetryPolicy = new() { MaxAttempts = 0 } }))
            .ThrowsExactly<ArgumentOutOfRangeException>();
    }

    [Test]
    [Arguments("Cluster", false)]
    [Arguments("Cluster", true)]
    [Arguments("Sentinel", false)]
    [Arguments("Sentinel", true)]
    [Arguments("Group", false)]
    [Arguments("Group", true)]
    public async Task LostIncrementReplyRequiresExplicitRiskAcrossOwners(string topology, bool allowDuplicate)
    {
        var counter = 0;
        var events = new ConcurrentDictionary<string, long>();
        using var listener = RetryListener(events);
        FakeRespServer? endpoint = null;
        byte[]? Reply(string command)
        {
            if (command == "GET counter") return Encoding.ASCII.GetBytes($"${counter.ToString().Length}\r\n{counter}\r\n");
            if (command != "INCR counter") return null;
            var value = Interlocked.Increment(ref counter);
            if (value == 1) { endpoint!.CloseConnections(); return null; }
            return Encoding.ASCII.GetBytes($":{value}\r\n");
        }
        await using var server = topology == "Cluster" ? ClusterNode((command, _) => Reply(command)) : Primary(command =>
            topology == "Group" && command == "PING" && Volatile.Read(ref counter) > 0 ? "-ERR down\r\n"u8.ToArray() : Reply(command));
        endpoint = server;
        await using var replacement = Primary(Reply);
        await using var sentinel = Sentinel(() => server.Port);
        var policy = new RespireRetryPolicy
        {
            MaxAttempts = 2, InitialBackoff = TimeSpan.Zero,
            MaxCommandRetryCategory = allowDuplicate ? RespireCommandRetryCategory.WriteAccumulating : RespireCommandRetryCategory.ReadOnly,
        };
        // Group members have no policy: only the group owns these retries.
        await using var group = topology == "Group"
            ? await RespireFailoverGroup.ConnectAsync([Candidate(server, 0), Candidate(replacement, 1)], GroupOptions() with { RetryPolicy = policy }) : null;
        await using var client = group is null
            ? await RespireClient.ConnectAsync((topology == "Cluster" ? ClusterOptions(server) : SentinelOptions(sentinel)) with { RetryPolicy = policy })
            : null;
        ValueTask<long> Increment() => group is null ? client!.IncrementAsync("counter")
            : group.ExecuteAsync(async (member, token) =>
            {
                try { return await member.IncrementAsync("counter", cancellationToken: token); }
                catch (RespireConnectionException)
                {
                    // Drive a probe/selection at the failed attempt boundary, without timing assumptions.
                    await group.ForTests.ProbeAsync(0, token);
                    await group.ForTests.SelectActiveAsync();
                    throw;
                }
            }, RespireCommandRetryCategory.WriteAccumulating);
        if (allowDuplicate) await Assert.That(await Increment()).IsEqualTo(2L);
        else await Assert.That(async () => await Increment()).Throws<RespireConnectionException>();
        // Read through an independent public client, rather than trusting resend counts.
        await using var observer = await RespireClient.ConnectAsync(StandaloneOptions(server) with { RetryPolicy = null });
        await Assert.That(await observer.GetStringAsync("counter")).IsEqualTo(allowDuplicate ? "2" : "1");
        await Assert.That(events.GetValueOrDefault("attempted")).IsEqualTo(allowDuplicate ? 1L : 0L);
        await Assert.That(events.GetValueOrDefault("succeeded")).IsEqualTo(allowDuplicate ? 1L : 0L);
        await Assert.That(events.GetValueOrDefault("exhausted")).IsEqualTo(0L);
    }

    [Test]
    [Arguments("raw", false)]
    [Arguments("raw", true)]
    [Arguments("script", false)]
    [Arguments("script", true)]
    [Arguments("function", false)]
    [Arguments("function", true)]
    [Arguments("Cluster", false)]
    [Arguments("Cluster", true)]
    public async Task ReusedOwnersPublishOneTerminalOutcomeDespiteThrowingListener(string shape, bool exhaust)
    {
        var events = new ConcurrentDictionary<string, long>();
        using var listener = RetryListener(events);
        using var throwing = new MeterListener();
        throwing.InstrumentPublished = static (instrument, meter) =>
        {
            if (instrument.Name == "respire.command.retries") meter.EnableMeasurementEvents(instrument);
        };
        throwing.SetMeasurementEventCallback<long>(static (_, _, _, _) => throw new InvalidOperationException("exporter failed"));
        throwing.Start();
        var calls = 0;
        byte[]? Reply(string command)
        {
            if (!(command == "GET key" || command.StartsWith("EVALSHA ", StringComparison.Ordinal) || command == "FCALL_RO reader 1 key")) return null;
            return Interlocked.Increment(ref calls) % 2 == 1 || exhaust ? TryAgain : Value;
        }
        await using var server = shape == "Cluster" ? ClusterNode((command, _) => Reply(command)) : Standalone((command, _) => Reply(command));
        await using var client = await RespireClient.ConnectAsync((shape == "Cluster" ? ClusterOptions(server) : StandaloneOptions(server)) with
        { RetryPolicy = new() { MaxAttempts = 2, InitialBackoff = TimeSpan.Zero } });
        var script = RespireScript.Create("return redis.call('GET', KEYS[1])").WithRetryCategory(RespireCommandRetryCategory.ReadOnly);
        var function = RespireFunction.Create("reader", readOnly: true).WithRetryCategory(RespireCommandRetryCategory.ReadOnly);
        async ValueTask<string?> Read()
        {
            if (shape == "script") return await client.Scripts.ExecuteStringAsync(script, ["key"]);
            if (shape == "function") return await client.Functions.ExecuteStringAsync(function, ["key"]);
            if (shape == "Cluster") return await client.GetStringAsync("key");
            using var result = await client.ExecuteAsync(RespireCommand.Create("GET").WithRetryCategory(RespireCommandRetryCategory.ReadOnly), ["key"]);
            return result.AsString();
        }
        for (var i = 0; i < 32; i++)
        {
            if (exhaust) await Assert.That(async () => await Read()).ThrowsExactly<RespireServerException>();
            else await Assert.That(await Read()).IsEqualTo("value");
        }
        await Assert.That(calls).IsEqualTo(64);
        await Assert.That(events["attempted"]).IsEqualTo(32L);
        await Assert.That(events[exhaust ? "exhausted" : "succeeded"]).IsEqualTo(32L);
        await Assert.That(events.Count).IsEqualTo(2);
    }

    [Test]
    [Arguments("raw")]
    [Arguments("script")]
    [Arguments("function")]
    [Arguments("Cluster")]
    public async Task CancelledAttemptCannotPublishIntoTheNextLogicalCommand(string shape)
    {
        CancellationTokenSource? cancellation = null;
        var reads = 0;
        byte[]? Reply(string command)
        {
            if (!(command == "GET key" || command.StartsWith("EVALSHA ", StringComparison.Ordinal) || command == "FCALL_RO reader 1 key")) return null;
            Interlocked.Increment(ref reads);
            if (cancellation is not null) { cancellation.Cancel(); return TryAgain; }
            return Value;
        }
        await using var server = shape == "Cluster" ? ClusterNode((command, _) => Reply(command)) : Standalone((command, _) => Reply(command));
        await using var client = await RespireClient.ConnectAsync(shape == "Cluster" ? ClusterOptions(server) : StandaloneOptions(server));
        var script = RespireScript.Create("return redis.call('GET', KEYS[1])").WithRetryCategory(RespireCommandRetryCategory.ReadOnly);
        var function = RespireFunction.Create("reader", readOnly: true).WithRetryCategory(RespireCommandRetryCategory.ReadOnly);
        async ValueTask<string?> Read(CancellationToken token)
        {
            if (shape == "script") return await client.Scripts.ExecuteStringAsync(script, ["key"], cancellationToken: token);
            if (shape == "function") return await client.Functions.ExecuteStringAsync(function, ["key"], cancellationToken: token);
            if (shape == "Cluster") return await client.GetStringAsync("key", token);
            using var result = await client.ExecuteAsync(RespireCommand.Create("GET").WithRetryCategory(RespireCommandRetryCategory.ReadOnly), ["key"], cancellationToken: token);
            return result.AsString();
        }
        for (var i = 0; i < 16; i++)
        {
            using var source = new CancellationTokenSource();
            cancellation = source;
            var error = await Failure(async () => await Read(source.Token));
            await Assert.That(error).IsTypeOf<OperationCanceledException>();
            await Assert.That(((OperationCanceledException)error).CancellationToken).IsEqualTo(source.Token);
            cancellation = null;
            await Assert.That(await Read(default)).IsEqualTo("value");
        }
        await Assert.That(reads).IsEqualTo(32);
    }

    private static byte[] Count(ref int counter, byte[] reply)
    {
        Interlocked.Increment(ref counter);
        return reply;
    }

    private static FakeRespServer Standalone(Func<string, FakeRespServer, byte[]?> reply)
    {
        var server = new FakeRespServer(32, FakeRespServer.OkReply);
        server.ReplyOverride = (_, command) => command == "CLIENT ID" ? ":1\r\n"u8.ToArray() : reply(command, server);
        return server;
    }

    private static RespireOptions StandaloneOptions(FakeRespServer server) => new()
    {
        Endpoints = { new("127.0.0.1", server.Port) }, Protocol = RespireProtocol.Resp2,
        ThreadPoolMonitoring = false, CommandTimeout = TimeSpan.FromSeconds(3),
        RetryPolicy = new() { InitialBackoff = TimeSpan.Zero },
    };

    private static FakeRespServer ClusterNode(Func<string, FakeRespServer, byte[]?> reply)
    {
        var server = new FakeRespServer(32, FakeRespServer.OkReply);
        server.ReplyOverride = (_, command) => command == "CLUSTER SLOTS" ? Slots(server.Port) : reply(command, server);
        return server;
    }

    private static RespireOptions ClusterOptions(FakeRespServer server) => new()
    {
        Endpoints = [new("127.0.0.1", server.Port)], UseCluster = true, Connections = 1, Protocol = RespireProtocol.Resp2,
        ThreadPoolMonitoring = false, ClusterTopologyRefreshInterval = null, CommandTimeout = TimeSpan.FromSeconds(5),
        RetryPolicy = new() { InitialBackoff = TimeSpan.Zero },
    };

    private static byte[] Slots(int port) => Encoding.ASCII.GetBytes(
        $"*1\r\n*3\r\n:0\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{port}\r\n");

    private static byte[] SplitSlots(int lowPort, int highPort) => Encoding.ASCII.GetBytes(
        $"*2\r\n*3\r\n:0\r\n:8191\r\n*2\r\n$9\r\n127.0.0.1\r\n:{lowPort}\r\n"
        + $"*3\r\n:8192\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{highPort}\r\n");

    private static FakeRespServer Primary(Func<string, byte[]?> reply) => new(16, FakeRespServer.OkReply)
    { ReplyOverride = (_, command) => reply(command) ?? (command == "ROLE" ? RoleReply("master") : null) };

    private static FakeRespServer Sentinel(Func<int> primaryPort) => new(16, FakeRespServer.PongReply)
    {
        ReplyOverride = (_, command) => command switch
        {
            "SENTINEL GET-MASTER-ADDR-BY-NAME mymaster" => PrimaryReply(primaryPort()),
            "SENTINEL SENTINELS mymaster" => "*0\r\n"u8.ToArray(),
            _ => null,
        },
    };

    private static RespireOptions SentinelOptions(FakeRespServer sentinel) => new()
    {
        Endpoints = [new("127.0.0.1", sentinel.Port)], SentinelPrimaryName = "mymaster", Connections = 1,
        Protocol = RespireProtocol.Resp2, ThreadPoolMonitoring = false, CommandTimeout = TimeSpan.FromSeconds(5),
        RetryPolicy = new() { InitialBackoff = TimeSpan.Zero },
    };

    private static byte[] RoleReply(string role)
        => Encoding.ASCII.GetBytes($"*3\r\n${role.Length}\r\n{role}\r\n:0\r\n*0\r\n");

    private static byte[] PrimaryReply(int port)
        => Encoding.ASCII.GetBytes($"*2\r\n$9\r\n127.0.0.1\r\n${port.ToString().Length}\r\n{port}\r\n");

    private static RespireFailoverCandidate Candidate(FakeRespServer server, int priority) => new(new RespireOptions
    {
        Protocol = RespireProtocol.Resp2, Connections = 1, ThreadPoolMonitoring = false,
        ConnectTimeout = TimeSpan.FromSeconds(2), CommandTimeout = TimeSpan.FromSeconds(2),
        Endpoints = [new RespireEndpoint("127.0.0.1", server.Port)],
    }, priority);

    // Probes run only when a test drives them, so selection changes happen at known points.
    private static RespireFailoverGroupOptions GroupOptions() => new()
    {
        ProbeInterval = TimeSpan.FromHours(1), ProbeTimeout = TimeSpan.FromSeconds(2), FailureThreshold = 1,
        CircuitOpenDuration = TimeSpan.FromMinutes(1), FailbackGracePeriod = TimeSpan.Zero,
        RetryPolicy = new() { InitialBackoff = TimeSpan.Zero },
    };

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

    private static async Task<Exception> Failure(Func<Task> operation)
    {
        try { await operation(); }
        catch (Exception error) { return error; }
        throw new InvalidOperationException("Expected failure.");
    }
}
