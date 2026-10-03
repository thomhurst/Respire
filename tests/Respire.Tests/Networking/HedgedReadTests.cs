using System.Text;
using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Respire.Internal;
using Respire.Networking;
using Respire.Protocol;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class HedgedReadTests
{
    private static readonly byte[] ReplicaRole = "*5\r\n$5\r\nslave\r\n$9\r\n127.0.0.1\r\n:6379\r\n$9\r\nconnected\r\n:0\r\n"u8.ToArray();
    private static byte[] Bulk(string value) => Encoding.ASCII.GetBytes($"${value.Length}\r\n{value}\r\n");

    private static FakeRespServer Replica(bool holdReads = false) => new(16, FakeRespServer.OkReply)
    {
        ReplyOverride = (_, command) => NodeReply(command, "replica"),
        SuppressReply = command => holdReads && command.StartsWith("GET ", StringComparison.Ordinal),
    };

    private static byte[] NodeReply(string command, string value) => command switch
    {
        "HELLO 3" => "%1\r\n+proto\r\n:3\r\n"u8.ToArray(),
        "ROLE" => value == "primary" ? "*3\r\n$6\r\nmaster\r\n:0\r\n*0\r\n"u8.ToArray() : ReplicaRole,
        "PING" => FakeRespServer.PongReply,
        _ when command.StartsWith("GET ", StringComparison.Ordinal) => Bulk(value),
        _ => FakeRespServer.OkReply,
    };

    private static byte[] SlotReply(int primary, params int[] replicas)
    {
        var text = new StringBuilder($"*1\r\n*{3 + replicas.Length}\r\n:0\r\n:16383\r\n");
        foreach (var port in new[] { primary }.Concat(replicas)) text.Append($"*2\r\n$9\r\n127.0.0.1\r\n:{port}\r\n");
        return Encoding.ASCII.GetBytes(text.ToString());
    }

    private static RespireOptions Options(FakeRespServer primary, params FakeRespServer[] replicas) => new()
    {
        Protocol = RespProtocol.Resp2, Connections = 1,
        Endpoints = [new("127.0.0.1", primary.Port)],
        ReplicaEndpoints = replicas.Select(replica => new RespireEndpoint("127.0.0.1", replica.Port)).ToArray(),
        ReadFrom = RespireReadFrom.ReplicaPreferred,
        HedgedReads = new() { Delay = TimeSpan.FromMilliseconds(10), MaximumExtraLoadPercent = 100 },
    };

    private static async Task WaitForCommandAsync(FakeRespServer server, string command, CancellationToken token)
    {
        while (!server.ReceivedCommands.Contains(command)) await Task.Delay(1, token);
    }

    [Test]
    public async Task SlowReplicaCanLoseToPrimaryWithoutBlockingTheCaller()
    {
        await using var primary = new FakeRespServer(Bulk("primary"));
        await using var replica = new FakeRespServer(ReplicaRole)
        {
            SuppressReply = command => command.StartsWith("GET ", StringComparison.Ordinal),
        };
        await using var client = RespireClient.Create(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, Connections = 1,
            Endpoints = [new("127.0.0.1", primary.Port)],
            ReplicaEndpoints = [new("127.0.0.1", replica.Port)],
            ReadFrom = RespireReadFrom.ReplicaPreferred,
            HedgedReads = new() { Delay = TimeSpan.FromMilliseconds(10), MaximumExtraLoadPercent = 100 },
        });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await Assert.That(await client.GetStringAsync("key", deadline.Token)).IsEqualTo("primary");
        await Assert.That(primary.ReceivedCommands).Contains("GET key");
        await Assert.That(replica.ReceivedCommands).Contains("GET key");
        // The original reply still owns its FIFO slot after the hedge has won.
        await replica.SendRawAsync(Bulk("loser"));
    }

    [Test]
    public async Task SlowPrimaryCanLoseToRoleValidatedReplica()
    {
        await using var primary = new FakeRespServer(Bulk("primary")) { SuppressReply = _ => true };
        await using var replica = Replica();
        await using var client = RespireClient.Create(Options(primary, replica) with { ReadFrom = RespireReadFrom.PrimaryPreferred });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await Assert.That(await client.GetStringAsync("key", deadline.Token)).IsEqualTo("replica");
        await Assert.That(replica.ReceivedCommands).IsEquivalentTo(["ROLE", "GET key"]);
        await primary.SendRawAsync(Bulk("loser"));
    }

    [Test]
    public async Task StrictReplicaSelectionNeverHedgesOntoPrimaryOrSamePeer()
    {
        await using var primary = new FakeRespServer(Bulk("primary"));
        await using var replica = Replica();
        await using var client = RespireClient.Create(Options(primary, replica));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var first = await client.Core.ReadRouter.GetConnectionAsync(RespireReadFrom.Replica, deadline.Token);
        var alternate = await client.Core.ReadRouter.GetHedgeConnectionAsync(RespireReadFrom.Replica, first, deadline.Token);
        await Assert.That(alternate).IsNull();
        await Assert.That(primary.CommandsSeen).IsEqualTo(0);
        await Assert.That(replica.ReceivedCommands).IsEquivalentTo(["ROLE"]);
    }

    [Test]
    public async Task StrictReplicaCanHedgeOntoAnotherReplica()
    {
        await using var primary = new FakeRespServer(Bulk("primary"));
        await using var firstReplica = Replica();
        await using var secondReplica = Replica();
        await using var client = RespireClient.Create(Options(primary, firstReplica, secondReplica) with { ReadFrom = RespireReadFrom.Replica });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var selected = await client.Core.ReadRouter.GetConnectionAsync(RespireReadFrom.Replica, deadline.Token);
        var slow = selected.Port == firstReplica.Port ? firstReplica : secondReplica;
        var fast = ReferenceEquals(slow, firstReplica) ? secondReplica : firstReplica;
        // Pin the next round-robin start to the selected server by advancing selection once.
        await client.Core.ReadRouter.GetConnectionAsync(RespireReadFrom.Replica, deadline.Token);
        slow.SuppressReply = command => command.StartsWith("GET ", StringComparison.Ordinal);
        fast.ReplyOverride = (_, command) => command == "ROLE" ? ReplicaRole : Bulk("hedge");
        await Assert.That(await client.GetStringAsync("key", deadline.Token)).IsEqualTo("hedge");
        await Assert.That(slow.ReceivedCommands).Contains("GET key");
        await Assert.That(fast.ReceivedCommands).Contains("GET key");
        await Assert.That(primary.CommandsSeen).IsEqualTo(0);
        await slow.SendRawAsync(Bulk("loser"));
    }

    [Test]
    [NotInParallel]
    public async Task HedgeRetirementBetweenSelectionAndDispatchDoesNotReplaceOriginalSuccess()
    {
        await using var primary = new FakeRespServer(Bulk("primary"));
        await using var replica = Replica(holdReads: true);
        await using var client = RespireClient.Create(Options(primary, replica));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var selected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meter) =>
        {
            if (instrument.Meter.Name == "Respire" && instrument.Name == "respire.read.hedge.sent")
                meter.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (tag.Key != "server.port" || !Equals(tag.Value, primary.Port)) continue;
                // This callback runs after selection, immediately before the optional send.
                client.Core.Multiplexer.GetConnection().StopAcceptingCommands();
                selected.TrySetResult();
            }
        });
        listener.Start();
        // Also run this regression with TUNIT_DISABLE_HTML_REPORTER=true: TUnit's automatic
        // ActivityListener otherwise wraps synchronous send errors in an instrumented ValueTask.
        if (string.Equals(Environment.GetEnvironmentVariable("TUNIT_DISABLE_HTML_REPORTER"), "true", StringComparison.OrdinalIgnoreCase))
            await Assert.That(RespireTelemetry.IsEnabled).IsFalse();
        var read = client.GetStringAsync("held", deadline.Token).AsTask();
        await selected.Task.WaitAsync(deadline.Token);
        await replica.SendRawAsync(Bulk("original"));
        await Assert.That(await read.WaitAsync(deadline.Token)).IsEqualTo("original");
        await Assert.That(primary.ReceivedCommands.Contains("GET held")).IsFalse();
    }

    [Test]
    public async Task OptionalServerErrorDoesNotReplaceOriginalSuccess()
    {
        await using var primary = new FakeRespServer("-ERR optional failed\r\n"u8.ToArray());
        await using var replica = Replica(holdReads: true);
        await using var client = RespireClient.Create(Options(primary, replica));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var read = client.GetStringAsync("key", deadline.Token).AsTask();
        await WaitForCommandAsync(primary, "GET key", deadline.Token);
        await replica.SendRawAsync(Bulk("original"));
        await Assert.That(await read.WaitAsync(deadline.Token)).IsEqualTo("original");
    }

    [Test]
    public async Task TwoFailuresPreserveOriginalServerError()
    {
        await using var primary = new FakeRespServer("-ERR optional failed\r\n"u8.ToArray());
        await using var replica = Replica(holdReads: true);
        await using var client = RespireClient.Create(Options(primary, replica));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var read = client.GetStringAsync("key", deadline.Token).AsTask();
        await WaitForCommandAsync(primary, "GET key", deadline.Token);
        await replica.SendRawAsync("-ERR original failed\r\n"u8.ToArray());
        var error = await Assert.That(async () => await read.WaitAsync(deadline.Token)).Throws<RespireServerException>();
        await Assert.That(error!.Message).Contains("original failed");
    }

    [Test]
    public async Task CallerCancellationDrainsBothRepliesBeforeLaterCommands()
    {
        await using var primary = new FakeRespServer(Bulk("primary")) { SuppressReply = command => command == "GET held" };
        await using var replica = Replica(holdReads: true);
        await using var client = RespireClient.Create(Options(primary, replica));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var caller = new CancellationTokenSource();
        var read = client.GetStringAsync("held", caller.Token).AsTask();
        await WaitForCommandAsync(primary, "GET held", deadline.Token);
        caller.Cancel();
        await Assert.That(async () => await read.WaitAsync(deadline.Token)).Throws<OperationCanceledException>();
        await primary.SendRawAsync(Bulk("discarded-primary"));
        await replica.SendRawAsync(Bulk("discarded-replica"));
        replica.SuppressReply = null;
        await Assert.That(await client.WithReadFrom(RespireReadFrom.Primary).GetStringAsync("next", deadline.Token)).IsEqualTo("primary");
        await Assert.That(await client.WithReadFrom(RespireReadFrom.Replica).GetStringAsync("next", deadline.Token)).IsEqualTo("replica");
    }

    [Test]
    public async Task DelayedOriginalOwnsArgumentsAfterHedgeReturns()
    {
        await using var primary = new FakeRespServer(Bulk("primary"));
        await using var replica = Replica(holdReads: true);
        await using var client = RespireClient.Create(Options(primary, replica) with { MaxInflightCommands = 1 });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var strict = client.WithReadFrom(RespireReadFrom.Replica);
        var blocker = strict.GetStringAsync("blocker", deadline.Token).AsTask();
        await WaitForCommandAsync(replica, "GET blocker", deadline.Token);
        var bytes = "mutable"u8.ToArray();
        await Assert.That(await client.GetStringAsync(bytes, deadline.Token)).IsEqualTo("primary");
        "changed"u8.CopyTo(bytes);
        await Assert.That(replica.ReceivedCommands.Contains("GET mutable")).IsFalse();
        await replica.SendRawAsync(Bulk("blocker"));
        await Assert.That(await blocker.WaitAsync(deadline.Token)).IsEqualTo("blocker");
        await WaitForCommandAsync(replica, "GET mutable", deadline.Token);
        await Assert.That(replica.ReceivedCommands.Contains("GET changed")).IsFalse();
        await replica.SendRawAsync(Bulk("discarded"));
        replica.SuppressReply = null;
        await Assert.That(await strict.GetStringAsync("next", deadline.Token)).IsEqualTo("replica");
    }

    [Test]
    [Arguments(RespProtocol.Resp2)]
    [Arguments(RespProtocol.Resp3)]
    public async Task ClusterHedgeUsesSlotTopologyAndReadonlyHandshake(RespProtocol protocol)
    {
        await using var replica = Replica(holdReads: true);
        await using var primary = new FakeRespServer(16, FakeRespServer.OkReply);
        primary.ReplyOverride = (_, command) => command == "CLUSTER SLOTS"
            ? SlotReply(primary.Port, replica.Port) : NodeReply(command, "primary");
        await using var client = RespireClient.Create(Options(primary) with
        {
            Protocol = protocol, UseCluster = true, ClusterTopologyRefreshInterval = null,
        });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await Assert.That(await client.GetStringAsync("{hedge}:key", deadline.Token)).IsEqualTo("primary");
        await Assert.That(replica.ReceivedCommands).Contains("READONLY");
        await Assert.That(replica.ReceivedCommands).Contains("GET {hedge}:key");
        await Assert.That(primary.ReceivedCommands).Contains("GET {hedge}:key");
        await replica.SendRawAsync(Bulk("loser"));
    }

    [Test]
    public async Task HedgedClusterLegFollowsMovedWithoutStartingAnotherHedge()
    {
        await using var replica = Replica(holdReads: true);
        await using var replacement = new FakeRespServer(16, FakeRespServer.OkReply);
        replacement.ReplyOverride = (_, command) => command == "CLUSTER SLOTS"
            ? SlotReply(replacement.Port) : NodeReply(command, "replacement");
        await using var primary = new FakeRespServer(16, FakeRespServer.OkReply);
        var slot = ClusterHash.GetSlot("key");
        primary.ReplyOverride = (_, command) => command == "CLUSTER SLOTS"
            ? SlotReply(primary.Port, replica.Port)
            : command == "GET key" ? Encoding.ASCII.GetBytes($"-MOVED {slot} 127.0.0.1:{replacement.Port}\r\n")
            : NodeReply(command, "primary");
        await using var client = RespireClient.Create(Options(primary) with { UseCluster = true, ClusterTopologyRefreshInterval = null });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await Assert.That(await client.GetStringAsync("key", deadline.Token)).IsEqualTo("replacement");
        await Assert.That(primary.ReceivedCommands.Count(command => command == "GET key")).IsEqualTo(1);
        await Assert.That(replacement.ReceivedCommands.Count(command => command == "GET key")).IsEqualTo(1);
        await replica.SendRawAsync(Bulk("loser"));
    }

    [Test]
    public async Task NearestReusesExistingLatencySamplerForOriginalSelection()
    {
        await using var primary = new FakeRespServer(16, FakeRespServer.OkReply)
        { ReplyOverride = (_, command) => NodeReply(command, "primary") };
        await using var replica = Replica(holdReads: true);
        await using var client = RespireClient.Create(Options(primary, replica) with { ReadFrom = RespireReadFrom.Nearest });
        var sampler = new ReadLatencySampler<RespireConnection>((connection, _) =>
            ValueTask.FromResult(connection.Port == replica.Port ? 10L : 100L));
        client.Core.ReadRouter.NearestLatency = sampler;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await Assert.That(await client.GetStringAsync("key", deadline.Token)).IsEqualTo("primary");
        await Assert.That(sampler.SamplesStarted).IsEqualTo(2L);
        await Assert.That(replica.ReceivedCommands).Contains("GET key");
        await replica.SendRawAsync(Bulk("loser"));
    }

    [Test]
    public async Task StalledOptionalHandshakeCannotDelayOriginalReply()
    {
        await using var primary = new FakeRespServer(16, FakeRespServer.OkReply)
        {
            SuppressReply = command => command.StartsWith("AUTH ", StringComparison.Ordinal),
            ReplyOverride = (_, command) => NodeReply(command, "primary"),
        };
        await using var replica = Replica(holdReads: true);
        await using var client = RespireClient.Create(Options(primary, replica) with { Password = "secret" });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var read = client.GetStringAsync("key", deadline.Token).AsTask();
        try
        {
            await WaitForCommandAsync(primary, "AUTH secret", deadline.Token);
            await replica.SendRawAsync(Bulk("original"));
            await Assert.That(await read.WaitAsync(deadline.Token)).IsEqualTo("original");
            await Assert.That(primary.ReceivedCommands.Contains("GET key")).IsFalse();
        }
        finally { primary.CloseConnections(); }
    }

    [Test]
    [NotInParallel]
    [Arguments(false)]
    [Arguments(true)]
    public async Task MetricsReportExtraLoadAndContainThrowingListeners(bool throwing)
    {
        await using var primary = new FakeRespServer(Bulk("primary"));
        await using var replica = Replica();
        using var listener = new MeterListener();
        var counts = new ConcurrentDictionary<string, long>();
        var loads = new ConcurrentQueue<double>();
        listener.InstrumentPublished = (instrument, current) =>
        {
            if (instrument.Meter.Name == "Respire" && instrument.Name.StartsWith("respire.read.hedge.", StringComparison.Ordinal))
                current.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, count, tags, _) =>
        {
            if (!Matches(tags)) return;
            counts.AddOrUpdate(instrument.Name, count, (_, current) => current + count);
            if (throwing) throw new InvalidOperationException("Injected hedge metric failure.");
        });
        listener.SetMeasurementEventCallback<double>((_, load, tags, _) =>
        {
            if (!Matches(tags)) return;
            loads.Enqueue(load);
            if (throwing) throw new InvalidOperationException("Injected extra-load metric failure.");
        });
        bool Matches(ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            foreach (var tag in tags)
                if (tag.Key == "server.port" && (Equals(tag.Value, primary.Port) || Equals(tag.Value, replica.Port))) return true;
            return false;
        }
        await using var client = RespireClient.Create(Options(primary, replica) with
        {
            HedgedReads = new() { Delay = TimeSpan.FromMilliseconds(10), MaximumExtraLoadPercent = 50 },
        });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await client.GetStringAsync("warm", deadline.Token);
        // One warm read funds the slow read's hedge. The final fast read has no remaining
        // budget, making the zero-extra-load assertion independent of timer scheduling.
        replica.SuppressReply = command => command.StartsWith("GET ", StringComparison.Ordinal);
        listener.Start();
        await Assert.That(await client.GetStringAsync("slow", deadline.Token)).IsEqualTo("primary");
        await replica.SendRawAsync(Bulk("loser"));
        replica.SuppressReply = null;
        await Assert.That(await client.GetStringAsync("fast", deadline.Token)).IsEqualTo("replica");
        await Assert.That(counts["respire.read.hedge.sent"]).IsEqualTo(1L);
        await Assert.That(counts["respire.read.hedge.won"]).IsEqualTo(1L);
        await Assert.That(loads.ToArray()).IsEquivalentTo([1d, 0d]);
    }

    [Test]
    public async Task WireLoadBudgetIsSharedAcrossViews()
    {
        await using var primary = new FakeRespServer(Bulk("primary"));
        await using var replica = Replica(holdReads: true);
        await using var client = RespireClient.Create(Options(primary, replica) with
        {
            HedgedReads = new() { Delay = TimeSpan.FromMilliseconds(1), MaximumExtraLoadPercent = 5 },
        });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        const int count = 200;
        var reads = Enumerable.Range(0, count).Select(index =>
            client.WithKeyPrefix($"view-{index}:").GetStringAsync("key", deadline.Token).AsTask()).ToArray();
        while (replica.ReceivedCommands.Count(command => command.StartsWith("GET ", StringComparison.Ordinal)) != count
            || !primary.ReceivedCommands.Any(command => command.StartsWith("GET ", StringComparison.Ordinal)))
            await Task.Delay(1, deadline.Token);
        await replica.SendRawAsync(Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("$8\r\noriginal\r\n", count))));
        var values = await Task.WhenAll(reads).WaitAsync(deadline.Token);
        await Assert.That(values.Count(value => value == "primary")).IsGreaterThan(0);
        await Assert.That(values.All(value => value is "primary" or "original")).IsTrue();
        var extra = primary.ReceivedCommands.Count(command => command.StartsWith("GET ", StringComparison.Ordinal));
        await Assert.That(extra).IsGreaterThan(0);
        await Assert.That(extra).IsLessThanOrEqualTo(count * 5 / 100);
    }

    [Test]
    public async Task OnlyEligibleLogicalReadsFundBudgetIncludingFastUnhedgeableReads()
    {
        await using var primary = new FakeRespServer(16, FakeRespServer.OkReply)
        { ReplyOverride = (_, command) => NodeReply(command, "primary") };
        await using var replica = Replica(holdReads: true);
        await using var client = RespireClient.Create(Options(primary, replica) with
        {
            HedgedReads = new() { Delay = TimeSpan.FromMilliseconds(1), MaximumExtraLoadPercent = 25 },
        });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        for (var i = 0; i < 4; i++)
        {
            await client.SetAsync("excluded", "value", cancellationToken: deadline.Token);
            await client.WithReadFrom(RespireReadFrom.Primary).GetStringAsync("excluded", deadline.Token);
        }
        var first = client.GetStringAsync("first", deadline.Token).AsTask();
        await WaitForCommandAsync(replica, "GET first", deadline.Token);
        await Task.Delay(50, deadline.Token);
        var duplicatedFirst = primary.ReceivedCommands.Contains("GET first");
        await replica.SendRawAsync(Bulk("first"));
        await first;
        await Assert.That(duplicatedFirst).IsFalse();

        // These fast strict-Replica reads cannot hedge with only one replica, but they are
        // eligible logical reads and must contribute to the shared denominator.
        replica.SuppressReply = null;
        for (var i = 0; i < 2; i++)
            await client.WithReadFrom(RespireReadFrom.Replica).GetStringAsync("warm", deadline.Token);
        replica.SuppressReply = command => command.StartsWith("GET ", StringComparison.Ordinal);
        await Assert.That(await client.GetStringAsync("fourth", deadline.Token)).IsEqualTo("primary");
        await replica.SendRawAsync(Bulk("loser"));
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task SingleReplicaDoesNotSnapshotBeforeAdmission(bool cluster)
    {
        await using var replica = Replica(holdReads: true);
        await using var primary = new FakeRespServer(16, FakeRespServer.OkReply);
        primary.ReplyOverride = (_, command) => command == "CLUSTER SLOTS"
            ? SlotReply(primary.Port, replica.Port) : NodeReply(command, "primary");
        await using var client = RespireClient.Create(Options(primary, replica) with
        {
            ReadFrom = RespireReadFrom.Replica, UseCluster = cluster,
            ReplicaEndpoints = cluster ? [] : [new("127.0.0.1", replica.Port)],
            ClusterTopologyRefreshInterval = null, MaxInflightCommands = 1,
        });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var blocker = client.GetStringAsync("blocker", deadline.Token).AsTask();
        await WaitForCommandAsync(replica, "GET blocker", deadline.Token);
        var serializations = new SerializationCount();
        var pending = client.SendAsync("GET", new SerializationProbe(serializations), deadline.Token).AsTask();
        await Task.Delay(20, deadline.Token);
        var writesBeforeAdmission = Volatile.Read(ref serializations.Value);
        await replica.SendRawAsync(Bulk("blocker"));
        await blocker;
        await WaitForCommandAsync(replica, "GET probe", deadline.Token);
        await replica.SendRawAsync(Bulk("value"));
        using var reply = await pending;
        await Assert.That(writesBeforeAdmission).IsEqualTo(0);
        await Assert.That(serializations.Value).IsEqualTo(1);
    }

    private sealed class SerializationCount { internal int Value; }

    private readonly struct SerializationProbe(SerializationCount count) : IRespCommand
    {
        public ReadCommandKind ReadKind => ReadCommandKind.Read;
        public bool TryGetClusterSlot(out int slot)
        {
            slot = ClusterHash.GetSlot("probe");
            return true;
        }
        public void Write(ref RespWriter writer)
        {
            Interlocked.Increment(ref count.Value);
            writer.WriteRaw("*2\r\n$3\r\nGET\r\n$5\r\nprobe\r\n"u8);
        }
    }

    [Test]
    [Arguments(false, RespireReadFrom.ReplicaPreferred)]
    [Arguments(true, RespireReadFrom.Primary)]
    public async Task DisabledHedgingAndStrictPrimaryNeverDuplicateReads(bool enabled, RespireReadFrom policy)
    {
        await using var primary = new FakeRespServer(Bulk("primary"));
        await using var replica = Replica();
        var selected = policy == RespireReadFrom.Primary ? primary : replica;
        var other = policy == RespireReadFrom.Primary ? replica : primary;
        selected.SuppressReply = command => command.StartsWith("GET ", StringComparison.Ordinal);
        await using var client = RespireClient.Create(Options(primary, replica) with
        {
            ReadFrom = policy,
            HedgedReads = enabled ? new() { Delay = TimeSpan.FromMilliseconds(1), MaximumExtraLoadPercent = 100 } : null,
        });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var read = client.GetStringAsync("held", deadline.Token).AsTask();
        await WaitForCommandAsync(selected, "GET held", deadline.Token);
        await Task.Delay(50, deadline.Token);
        await Assert.That(read.IsCompleted).IsFalse();
        await Assert.That(other.ReceivedCommands.Any(command => command.StartsWith("GET ", StringComparison.Ordinal))).IsFalse();
        await selected.SendRawAsync(Bulk("original"));
        await Assert.That(await read).IsEqualTo("original");
    }

    [Test]
    public async Task SlowWriteIsNeverDuplicated()
    {
        await using var primary = new FakeRespServer(FakeRespServer.OkReply)
        {
            SuppressReply = command => command.StartsWith("SET ", StringComparison.Ordinal),
        };
        await using var replica = Replica();
        await using var client = RespireClient.Create(Options(primary, replica));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var write = client.SetAsync("key", "value", cancellationToken: deadline.Token).AsTask();
        await WaitForCommandAsync(primary, "SET key value", deadline.Token);
        await Task.Delay(50, deadline.Token);
        await Assert.That(write.IsCompleted).IsFalse();
        await Assert.That(replica.ReceivedCommands.Any(command => command.StartsWith("SET ", StringComparison.Ordinal))).IsFalse();
        await primary.SendRawAsync(FakeRespServer.OkReply);
        await write;
        await Assert.That(primary.ReceivedCommands.Count(command => command.StartsWith("SET ", StringComparison.Ordinal))).IsEqualTo(1);
    }

    [Test]
    public async Task DisposalCompletesWithAnOutstandingLosingRead()
    {
        await using var primary = new FakeRespServer(Bulk("primary"));
        await using var replica = Replica(holdReads: true);
        var client = RespireClient.Create(Options(primary, replica));
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await Assert.That(await client.GetStringAsync("held", deadline.Token)).IsEqualTo("primary");
            await client.DisposeAsync().AsTask().WaitAsync(deadline.Token);
        }
        finally { await client.DisposeAsync(); }
    }

    [Test]
    [Arguments(0)]
    [Arguments(-1)]
    [Arguments(101)]
    public async Task InvalidPercentagesAreRejected(int percentage)
        => await Assert.That(() => RespireClient.Create(new RespireOptions
        {
            Endpoints = [new("127.0.0.1", 1)], HedgedReads = new() { MaximumExtraLoadPercent = percentage },
        })).Throws<RespireConfigurationException>();

    [Test]
    [Arguments(0)]
    [Arguments(-1)]
    [Arguments(60_001)]
    public async Task InvalidDelaysAreRejected(int milliseconds)
        => await Assert.That(() => RespireClient.Create(new RespireOptions
        {
            Endpoints = [new("127.0.0.1", 1)], HedgedReads = new() { Delay = TimeSpan.FromMilliseconds(milliseconds) },
        })).Throws<RespireConfigurationException>();

    [Test]
    [Arguments(1)]
    [Arguments(5)]
    [Arguments(37)]
    [Arguments(100)]
    public async Task BudgetBoundsConcurrentExtraLoad(int percentage)
    {
        var budget = new HedgedReadBudget(percentage);
        var hedges = 0;
        Parallel.For(0, 10_000, _ =>
        {
            budget.RecordRead();
            if (budget.TrySpend()) Interlocked.Increment(ref hedges);
        });
        await Assert.That(hedges).IsLessThanOrEqualTo(100 * percentage);
        await Assert.That(hedges).IsGreaterThan(0);
    }

    [Test]
    public async Task FastReadsSaveAtMostOneHedge()
    {
        var budget = new HedgedReadBudget(5);
        await Assert.That(budget.TrySpend()).IsFalse();
        for (var i = 0; i < 10_000; i++) budget.RecordRead();
        await Assert.That(budget.TrySpend()).IsTrue();
        await Assert.That(budget.TrySpend()).IsFalse();
        for (var i = 0; i < 19; i++) budget.RecordRead();
        await Assert.That(budget.TrySpend()).IsFalse();
        budget.RecordRead();
        await Assert.That(budget.TrySpend()).IsTrue();
    }

    [Test]
    [Arguments("SET")]
    [Arguments("GETDEL")]
    [Arguments("GETEX")]
    [Arguments("EVAL_RO")]
    [Arguments("FCALL_RO")]
    [Arguments("RANDOMKEY")]
    [Arguments("HRANDFIELD")]
    [Arguments("SRANDMEMBER")]
    [Arguments("ZRANDMEMBER")]
    [Arguments("SCAN")]
    [Arguments("XREAD")]
    [Arguments("CUSTOM.READ")]
    public async Task UnauditedOrNonIdempotentCommandsAreExcluded(string operation)
        => await Assert.That(HedgedReadPolicy.IsEligible(operation)).IsFalse();
}
