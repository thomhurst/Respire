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
public class TopologyCommandRetryTests
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
        Endpoints = { new("127.0.0.1", server.Port) }, Protocol = RespProtocol.Resp2,
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
        Endpoints = [new("127.0.0.1", server.Port)], UseCluster = true, Connections = 1, Protocol = RespProtocol.Resp2,
        ThreadPoolMonitoring = false, ClusterTopologyRefreshInterval = null, CommandTimeout = TimeSpan.FromSeconds(5),
        RetryPolicy = new() { InitialBackoff = TimeSpan.Zero },
    };

    private static byte[] Slots(int port) => Encoding.ASCII.GetBytes(
        $"*1\r\n*3\r\n:0\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{port}\r\n");

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
        Protocol = RespProtocol.Resp2, ThreadPoolMonitoring = false, CommandTimeout = TimeSpan.FromSeconds(5),
        RetryPolicy = new() { InitialBackoff = TimeSpan.Zero },
    };

    private static byte[] RoleReply(string role)
        => Encoding.ASCII.GetBytes($"*3\r\n${role.Length}\r\n{role}\r\n:0\r\n*0\r\n");

    private static byte[] PrimaryReply(int port)
        => Encoding.ASCII.GetBytes($"*2\r\n$9\r\n127.0.0.1\r\n${port.ToString().Length}\r\n{port}\r\n");

    private static RespireFailoverCandidate Candidate(FakeRespServer server, int priority) => new(new RespireOptions
    {
        Protocol = RespProtocol.Resp2, Connections = 1, ThreadPoolMonitoring = false,
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
