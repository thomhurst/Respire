using System.Text;
using System.Reflection;
using System.Threading.Channels;
using Respire.Commands;
using Respire.Internal;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class ClusterScanTests
{
    [Test]
    public async Task CursorResumesNodeCursorAcrossClientsAndEmptyPages()
    {
        await using var cluster = new ScanCluster();
        cluster.First.Scan = command => command.Split(' ')[1] switch
        {
            "0" => Page("17", "first"), "17" => Page("18446744073709551615"), _ => Page("0", "last"),
        };
        cluster.Second.Scan = _ => Page("0");
        RespireClusterScanCursor checkpoint;
        await using (var client = await cluster.ConnectAsync())
        {
            var first = await client.Keys.ScanClusterPageAsync(RespireClusterScanCursor.Start);
            await Assert.That(first.Keys).IsEquivalentTo(["first"]);
            checkpoint = RespireClusterScanCursor.Parse(first.Cursor.ToString());
        }
        var saved = checkpoint.ToString();
        await using var resumed = await cluster.ConnectAsync();
        var empty = await resumed.Keys.ScanClusterPageAsync(checkpoint, countHint: 7);
        await Assert.That(empty.Keys).IsEmpty();
        await Assert.That(empty.Cursor.IsComplete).IsFalse();
        var last = await resumed.Keys.ScanClusterPageAsync(RespireClusterScanCursor.Parse(empty.Cursor.ToString()));
        await Assert.That(last.Keys).IsEquivalentTo(["last"]);
        var complete = await resumed.Keys.ScanClusterPageAsync(last.Cursor);
        await Assert.That(complete.Cursor.IsComplete).IsTrue();
        await Assert.That(complete.Cursor.CompletedSlotCount).IsEqualTo(16384);
        var commands = cluster.CommandCount;
        await Assert.That((await resumed.Keys.ScanClusterPageAsync(complete.Cursor)).Keys).IsEmpty();
        await Assert.That(cluster.CommandCount).IsEqualTo(commands);
        await Assert.That(checkpoint.ToString()).IsEqualTo(saved);
        await Assert.That(cluster.First.Server.ReceivedCommands.Where(command => command.StartsWith("SCAN ")))
            .IsEquivalentTo(["SCAN 0 COUNT 250", "SCAN 17 COUNT 7", "SCAN 18446744073709551615 COUNT 250"]);
    }

    [Test]
    public async Task MovingSlotToCompletedNodeRescansOnlyAffectedSlot()
    {
        await using var cluster = new ScanCluster();
        var stableFirst = KeyInSlot(0);
        var moved = KeyInSlot(8192);
        var stableSecond = KeyInSlot(8193);
        cluster.First.Scan = _ => Page("0", stableFirst, moved);
        cluster.Second.Scan = command => command.Split(' ')[1] == "0" ? Page("7", stableSecond) : Page("0");
        await using var client = await cluster.ConnectAsync();
        var first = await client.Keys.ScanClusterPageAsync(RespireClusterScanCursor.Start);
        var second = await client.Keys.ScanClusterPageAsync(first.Cursor);
        cluster.First.Slots = "0-8192";
        cluster.First.Epoch++;
        cluster.Second.Slots = "8193-16383";
        var third = await client.Keys.ScanClusterPageAsync(RespireClusterScanCursor.Parse(second.Cursor.ToString()));
        await Assert.That(third.Cursor.IsComplete).IsFalse();
        var fourth = await client.Keys.ScanClusterPageAsync(third.Cursor);
        await Assert.That(fourth.Cursor.IsComplete).IsTrue();
        await Assert.That(fourth.Keys).IsEquivalentTo([moved]);
        await Assert.That(cluster.Second.Server.ReceivedCommands.Where(command => command.StartsWith("SCAN ")))
            .IsEquivalentTo(["SCAN 0 COUNT 250", "SCAN 7 COUNT 250"]);
        await Assert.That(first.Cursor.CompletedSlotCount).IsEqualTo(8192);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RestartOrEpochChangeNeverReusesOldNodeCursor(bool restart)
    {
        await using var cluster = new ScanCluster();
        var firstPass = true;
        cluster.First.Scan = _ => firstPass ? Page("17") : Page("0", KeyInSlot(0));
        await using var client = await cluster.ConnectAsync();
        var first = await client.Keys.ScanClusterPageAsync(RespireClusterScanCursor.Start);
        firstPass = false;
        if (restart) cluster.First.RunId = "restarted";
        else cluster.First.Epoch++;
        var second = await client.Keys.ScanClusterPageAsync(first.Cursor);
        await Assert.That(second.Keys).IsEquivalentTo([KeyInSlot(0)]);
        await Assert.That(cluster.First.Server.ReceivedCommands.Where(command => command.StartsWith("SCAN ")))
            .IsEquivalentTo(["SCAN 0 COUNT 250", "SCAN 0 COUNT 250"]);
    }

    [Test]
    [Arguments(9223372036854775807UL)]
    [Arguments(18446744073709551614UL)]
    public async Task UnsignedEpochChangesRestartNodeCursor(ulong epoch)
    {
        await using var cluster = new ScanCluster();
        cluster.First.Epoch = epoch;
        var firstPass = true;
        cluster.First.Scan = _ => firstPass ? Page("17") : Page("0", KeyInSlot(0));
        await using var client = await cluster.ConnectAsync();
        var first = await client.Keys.ScanClusterPageAsync(RespireClusterScanCursor.Start);
        var checkpoint = RespireClusterScanCursor.Parse(first.Cursor.ToString());
        await Assert.That(checkpoint.State!.Epoch).IsEqualTo(epoch);
        firstPass = false;
        cluster.First.Epoch++;
        var second = await client.Keys.ScanClusterPageAsync(checkpoint);
        await Assert.That(second.Keys).IsEquivalentTo([KeyInSlot(0)]);
        await Assert.That(cluster.First.Server.ReceivedCommands.Where(command => command.StartsWith("SCAN ")))
            .IsEquivalentTo(["SCAN 0 COUNT 250", "SCAN 0 COUNT 250"]);
    }

    [Test]
    public async Task InProgressMigrationCannotCompleteItsSlot()
    {
        await using var cluster = new ScanCluster();
        cluster.First.Transitions = "[0->-second]";
        cluster.Second.Transitions = "[0-<-first]";
        await using var client = await cluster.ConnectAsync();
        var page = await client.Keys.ScanClusterPageAsync(RespireClusterScanCursor.Start);
        page = await client.Keys.ScanClusterPageAsync(page.Cursor);
        await Assert.That(page.Cursor.CompletedSlotCount).IsEqualTo(16383);
        var scansBeforeWait = cluster.First.Server.ReceivedCommands.Count(command => command.StartsWith("SCAN "));
        page = await client.Keys.ScanClusterPageAsync(page.Cursor);
        await Assert.That(page.Cursor.IsComplete).IsFalse();
        await Assert.That(page.WaitingOnMigration).IsTrue();
        await Assert.That(page.Keys).IsEmpty();
        await Assert.That(cluster.First.Server.ReceivedCommands.Count(command => command.StartsWith("SCAN ")))
            .IsEqualTo(scansBeforeWait);
        cluster.First.Transitions = cluster.Second.Transitions = "";
        cluster.First.Scan = _ => Page("0", KeyInSlot(0));
        page = await client.Keys.ScanClusterPageAsync(page.Cursor);
        await Assert.That(page.Cursor.IsComplete).IsTrue();
        await Assert.That(page.Keys).IsEquivalentTo([KeyInSlot(0)]);
        await Assert.That(cluster.Second.Server.ReceivedCommands.Count(command => command.StartsWith("SCAN "))).IsEqualTo(1);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task EnumerableBacksOffUntilMigrationSettlesOrCallerCancels(bool cancel)
    {
        await using var cluster = new ScanCluster();
        cluster.First.Transitions = "[0->-second]";
        cluster.Second.Transitions = "[0-<-first]";
        await using var client = await cluster.ConnectAsync();
        var clock = new ScanClock();
        var keys = new KeyCommands(client, clock);
        using var cancellation = new CancellationTokenSource();
        await using var enumerator = keys.ScanAsync(cancellationToken: cancellation.Token).GetAsyncEnumerator();
        var next = enumerator.MoveNextAsync().AsTask();
        foreach (var milliseconds in new[] { 50, 100, 200, 250, 250 })
        {
            var timer = await clock.Timers.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            await Assert.That(timer.Delay).IsEqualTo(TimeSpan.FromMilliseconds(milliseconds));
            await Assert.That(next.IsCompleted).IsFalse();
            // The manually controlled timer cannot fire by wall clock. No scan work runs while held.
            await Assert.That(cluster.First.Server.ReceivedCommands.Count(command => command.StartsWith("SCAN "))).IsEqualTo(1);
            await Assert.That(cluster.Second.Server.ReceivedCommands.Count(command => command.StartsWith("SCAN "))).IsEqualTo(1);
            if (milliseconds == 250 && clock.Created == 5)
            {
                if (cancel)
                {
                    var commands = cluster.CommandCount;
                    cancellation.Cancel();
                    await Assert.That(async () => await next.WaitAsync(TimeSpan.FromSeconds(10)))
                        .Throws<OperationCanceledException>();
                    await Assert.That(cluster.CommandCount).IsEqualTo(commands);
                    return;
                }
                cluster.First.Transitions = cluster.Second.Transitions = "";
                cluster.First.Scan = _ => Page("0", KeyInSlot(0));
            }
            timer.Fire();
        }
        await Assert.That(await next.WaitAsync(TimeSpan.FromSeconds(10))).IsTrue();
        await Assert.That(enumerator.Current).IsEqualTo(KeyInSlot(0));
        await Assert.That(await enumerator.MoveNextAsync()).IsFalse();
        await Assert.That(clock.Created).IsEqualTo(5);
    }

    [Test]
    public async Task InvalidatedActivePassYieldsToStableWorkOnAnotherNode()
    {
        await using var cluster = new ScanCluster();
        cluster.First.Slots = "0";
        cluster.Second.Slots = "1-16383";
        cluster.First.Scan = _ => Page("17");
        cluster.Second.Scan = _ => Page("0", KeyInSlot(1));
        await using var client = await cluster.ConnectAsync();
        var page = await client.Keys.ScanClusterPageAsync(RespireClusterScanCursor.Start);
        cluster.First.Transitions = "[0->-second]";
        cluster.Second.Transitions = "[0-<-first]";
        page = await client.Keys.ScanClusterPageAsync(page.Cursor);
        await Assert.That(page.Keys).IsEquivalentTo([KeyInSlot(1)]);
        await Assert.That(page.Cursor.CompletedSlotCount).IsEqualTo(16383);
        await Assert.That(cluster.First.Server.ReceivedCommands.Where(command => command.StartsWith("SCAN ")))
            .IsEquivalentTo(["SCAN 0 COUNT 250"]);

        // A settled slot starts a new pass; the abandoned node-local cursor cannot be reused.
        cluster.First.Transitions = cluster.Second.Transitions = "";
        cluster.First.Scan = _ => Page("0", KeyInSlot(0));
        page = await client.Keys.ScanClusterPageAsync(RespireClusterScanCursor.Parse(page.Cursor.ToString()));
        await Assert.That(page.Cursor.IsComplete).IsTrue();
        await Assert.That(page.Keys).IsEquivalentTo([KeyInSlot(0)]);
        await Assert.That(cluster.First.Server.ReceivedCommands.Where(command => command.StartsWith("SCAN ")))
            .IsEquivalentTo(["SCAN 0 COUNT 250", "SCAN 0 COUNT 250"]);
    }

    [Test]
    public async Task MigrationDuringFinalPageDoesNotCertifyLostSlot()
    {
        await using var cluster = new ScanCluster();
        var moved = KeyInSlot(0);
        cluster.First.Scan = _ =>
        {
            cluster.First.Slots = "1-8191";
            cluster.Second.Slots = "0 8192-16383";
            cluster.Second.Epoch++;
            return Page("0");
        };
        cluster.Second.Scan = _ => Page("0", moved);
        await using var client = await cluster.ConnectAsync();
        var keys = new List<string>();
        await foreach (var key in client.Keys.ScanAsync()) keys.Add(key);
        await Assert.That(keys).IsEquivalentTo([moved]);
    }

    [Test]
    public async Task EnumerableRecoversKeysMovedToAlreadyVisitedPrimary()
    {
        await using var cluster = new ScanCluster();
        var moved = KeyInSlot(8192);
        var stable = KeyInSlot(0);
        var changed = false;
        cluster.First.Scan = _ => changed ? Page("0", stable, moved) : Page("0", stable);
        cluster.Second.Scan = _ =>
        {
            changed = true;
            cluster.First.Slots = "0-8192";
            cluster.First.Epoch++;
            cluster.Second.Slots = "8193-16383";
            return Page("0");
        };
        await using var client = await cluster.ConnectAsync();
        var keys = new List<string>();
        await foreach (var key in client.Keys.ScanAsync()) keys.Add(key);
        await Assert.That(keys).IsEquivalentTo([stable, moved]);
    }

    [Test]
    public async Task FiltersBindCursorAndPrefixDoesNotLeak()
    {
        await using var cluster = new ScanCluster();
        cluster.First.Scan = _ => Page("17", "tenant:*:{x}first", "other:{x}secret");
        await using var client = await cluster.ConnectAsync();
        var view = client.WithKeyPrefix("tenant:*:");
        var page = await view.Keys.ScanClusterPageAsync(RespireClusterScanCursor.Start, "*", RespireKeyType.String, 9);
        await Assert.That(page.Keys).IsEquivalentTo(["{x}first"]);
        var count = cluster.CommandCount;
        await Assert.That(async () => await client.Keys.ScanClusterPageAsync(page.Cursor, "*", RespireKeyType.String))
            .Throws<ArgumentException>();
        await Assert.That(async () => await view.Keys.ScanClusterPageAsync(page.Cursor, "different", RespireKeyType.String))
            .Throws<ArgumentException>();
        await Assert.That(cluster.CommandCount).IsEqualTo(count);
        await Assert.That(cluster.First.Server.ReceivedCommands.Last()).IsEqualTo(@"SCAN 0 MATCH tenant:\*:* COUNT 9 TYPE string");
    }

    [Test]
    public async Task ErrorsAndCancellationLeavePublishedCursorUnchanged()
    {
        await using var cluster = new ScanCluster();
        cluster.First.Scan = _ => Page("17");
        await using var client = await cluster.ConnectAsync();
        var page = await client.Keys.ScanClusterPageAsync(RespireClusterScanCursor.Start);
        var saved = page.Cursor.ToString();
        cluster.First.Scan = _ => "-NOPERM blocked\r\n"u8.ToArray();
        await Assert.That(async () => await client.Keys.ScanClusterPageAsync(page.Cursor)).Throws<RespireServerException>();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var before = cluster.CommandCount;
        await Assert.That(async () => await client.Keys.ScanClusterPageAsync(page.Cursor, cancellationToken: cancelled.Token))
            .Throws<OperationCanceledException>();
        await Assert.That(cluster.CommandCount).IsEqualTo(before);
        await Assert.That(page.Cursor.ToString()).IsEqualTo(saved);
    }

    [Test]
    public async Task CursorRejectsCorruptionAndSupportsInitialState()
    {
        await Assert.That(RespireClusterScanCursor.Parse(RespireClusterScanCursor.Start.ToString()).IsComplete).IsFalse();
        foreach (var token in new[] { "", "not base64", "AAAAAA==", RespireClusterScanCursor.Start.ToString() + "AAAA" })
            await Assert.That(RespireClusterScanCursor.TryParse(token, out _)).IsFalse();
        await Assert.That(RespireClusterScanCursor.TryParse(null, out _)).IsFalse();
        var state = new ClusterScanState("*", null, null) { ActiveNode = "first", RunId = "run", Cursor = ulong.MaxValue };
        Array.Fill(state.Owners, "first");
        state.PassSlots[0] = true;
        var original = new RespireClusterScanCursor(state);
        var parsed = RespireClusterScanCursor.Parse(original.ToString());
        await Assert.That(parsed.ToString()).IsEqualTo(original.ToString());
        var bytes = Convert.FromBase64String(original.ToString());
        foreach (var length in new[] { 0, 5, 12, bytes.Length - 1 })
            await Assert.That(RespireClusterScanCursor.TryParse(Convert.ToBase64String(bytes.AsSpan(0, length)), out _)).IsFalse();
    }

    [Test]
    public async Task StandaloneAndInvalidArgumentsFailBeforeConnecting()
    {
        await using var standalone = RespireClient.Create(new RespireOptions { Endpoints = { new RespireEndpoint("unused.invalid") } });
        await Assert.That(async () => await standalone.Keys.ScanClusterPageAsync(RespireClusterScanCursor.Start))
            .Throws<InvalidOperationException>();
        await Assert.That(async () => await standalone.Keys.ScanClusterPageAsync(RespireClusterScanCursor.Start, countHint: 0))
            .Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RepeatedPageRetirementSharesOneDiscoveryBudget(bool configured)
    {
        await using var cluster = new ScanCluster();
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            UseCluster = true, Protocol = RespProtocol.Resp2, Connections = 1,
            Endpoints = [new("127.0.0.1", cluster.First.Server.Port)],
            ReconnectPolicy = configured ? new() { InitialDelay = TimeSpan.Zero, JitterRatio = 0, MaxAttempts = 1 } : null,
        });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var router = client.Core.Cluster!;
        var retired = 0;
        cluster.First.BeforeMetadata = () =>
        {
            if (++retired > 2) { cluster.First.BeforeMetadata = null; return; }
            cluster.Second.Id = "replacement-" + retired;
            cluster.Second.RunId = "replacement-run-" + retired;
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var version = (long)typeof(ClusterRouter).GetField("_topologyVersion", flags)!.GetValue(router)!;
            var generationField = typeof(ClusterRouter).GetField("_nextDiscoveryGeneration", flags)!;
            var generation = (long)generationField.GetValue(router)! + 1;
            generationField.SetValue(router, generation);
            List<ClusterTopologyRange> ranges =
            [
                new(0, 8191, new("127.0.0.1", cluster.First.Server.Port), "first", []),
                new(8192, 16383, new("127.0.0.1", cluster.Second.Server.Port), cluster.Second.Id, []),
            ];
            typeof(ClusterRouter).GetMethod("ApplyTopology", flags)!.Invoke(router, [ranges, version, generation]);
        };
        var cursor = RespireClusterScanCursor.Start;
        if (configured)
        {
            await Assert.That(async () => await client.Keys.ScanClusterPageAsync(cursor, cancellationToken: timeout.Token))
                .ThrowsExactly<RespireReconnectLimitException>();
            await Assert.That(retired).IsEqualTo(2);
            await Assert.That(cluster.First.Server.ReceivedCommands.Any(command => command.StartsWith("SCAN "))).IsFalse();
        }
        else
        {
            _ = await client.Keys.ScanClusterPageAsync(cursor, cancellationToken: timeout.Token);
            await Assert.That(retired).IsEqualTo(3);
            await Assert.That(cluster.First.Server.ReceivedCommands.Count(command => command.StartsWith("SCAN "))).IsEqualTo(1);
        }
        await Assert.That(cursor.IsComplete).IsFalse();
        await router.WaitForRetirementAsync().WaitAsync(timeout.Token);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task PageRetirementAtRedirectLimitReportsFailedDiscovery(bool unlimited)
    {
        await using var cluster = new ScanCluster();
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            UseCluster = true, Protocol = RespProtocol.Resp2, Connections = 1,
            Endpoints = [new("127.0.0.1", cluster.First.Server.Port)],
            ReconnectPolicy = new() { InitialDelay = TimeSpan.Zero, JitterRatio = 0, MaxAttempts = unlimited ? null : 5 },
        });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var router = client.Core.Cluster!;
        var retired = 0;
        cluster.First.BeforeMetadata = () =>
        {
            cluster.Second.Id = "replacement-" + ++retired;
            cluster.Second.RunId = "replacement-run-" + retired;
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var version = (long)typeof(ClusterRouter).GetField("_topologyVersion", flags)!.GetValue(router)!;
            var generationField = typeof(ClusterRouter).GetField("_nextDiscoveryGeneration", flags)!;
            var generation = (long)generationField.GetValue(router)! + 1;
            generationField.SetValue(router, generation);
            List<ClusterTopologyRange> ranges =
            [
                new(0, 8191, new("127.0.0.1", cluster.First.Server.Port), "first", []),
                new(8192, 16383, new("127.0.0.1", cluster.Second.Server.Port), cluster.Second.Id, []),
            ];
            typeof(ClusterRouter).GetMethod("ApplyTopology", flags)!.Invoke(router, [ranges, version, generation]);
        };
        var changes = new System.Collections.Concurrent.ConcurrentQueue<RespireConnectionStateChange>();
        var completed = new TaskCompletionSource<RespireConnectionStateChange>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ConnectionStateChanged += change =>
        {
            if (change.ReconnectSource != RespireReconnectSource.ClusterDiscovery) return;
            changes.Enqueue(change);
            if (change.NextReconnectDelay is null) completed.TrySetResult(change);
        };
        var cursor = RespireClusterScanCursor.Start;
        var checkpoint = cursor.ToString();
        var error = await Assert.That(async () => await client.Keys.ScanClusterPageAsync(cursor, cancellationToken: timeout.Token))
            .Throws<Respire.Networking.RespireConnectionRetiredException>();
        var terminal = await completed.Task.WaitAsync(timeout.Token);
        await Assert.That(terminal.SourceState).IsEqualTo(RespireConnectionState.Disconnected);
        await Assert.That(terminal.Error).IsSameReferenceAs(error);
        await Assert.That(terminal.ReconnectAttempt).IsEqualTo(5);
        await Assert.That(terminal.ReconnectExhausted).IsFalse();
        await Assert.That(retired).IsEqualTo(6);
        await Assert.That(changes.Count).IsEqualTo(6);
        await Assert.That(changes.Select(change => change.ReconnectEpisodeId).Distinct().Count()).IsEqualTo(1);
        await Assert.That(cursor.ToString()).IsEqualTo(checkpoint);
        await Assert.That(cluster.First.Server.ReceivedCommands.Concat(cluster.Second.Server.ReceivedCommands)
            .Any(command => command.StartsWith("SCAN "))).IsFalse();
        await router.WaitForRetirementAsync().WaitAsync(timeout.Token);
    }

    internal static async Task AssertRetirementPreservesAcceptedPagesAsync()
    {
        await using var cluster = new ScanCluster();
        cluster.First.Scan = _ => Page("0", KeyInSlot(0));
        cluster.Second.Scan = _ => Page("0", KeyInSlot(8192));
        await using var client = await cluster.ConnectAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var first = await client.Keys.ScanClusterPageAsync(RespireClusterScanCursor.Start, cancellationToken: timeout.Token);
        var router = client.Core.Cluster!;
        var old = await router.GetConnectionAsync(8192, timeout.Token, discovery: null);
        var full = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pings = 0;
        cluster.Second.Server.SuppressReply = command =>
        {
            if (command != "PING") return false;
            if (Interlocked.Increment(ref pings) == 4) full.TrySetResult();
            return true;
        };
        var accepted = Enumerable.Range(0, 4)
            .Select(_ => old.SendAsync(new RawCommand(FakeRespServer.PingFrame), timeout.Token).AsTask()).ToArray();
        await full.Task.WaitAsync(timeout.Token);
        cluster.First.BeforeMetadata = () =>
        {
            cluster.First.BeforeMetadata = null;
            cluster.Second.Id = "replacement";
            cluster.Second.RunId = "replacement-run";
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var version = (long)typeof(ClusterRouter).GetField("_topologyVersion", flags)!.GetValue(router)!;
            var generationField = typeof(ClusterRouter).GetField("_nextDiscoveryGeneration", flags)!;
            var generation = (long)generationField.GetValue(router)! + 1;
            generationField.SetValue(router, generation);
            List<ClusterTopologyRange> ranges =
            [
                new(0, 8191, new("127.0.0.1", cluster.First.Server.Port), "first", []),
                new(8192, 16383, new("127.0.0.1", cluster.Second.Server.Port), "replacement", []),
            ];
            typeof(ClusterRouter).GetMethod("ApplyTopology", flags)!.Invoke(router, [ranges, version, generation]);
        };
        var final = await client.Keys.ScanClusterPageAsync(first.Cursor, cancellationToken: timeout.Token);
        await Assert.That(final.Cursor.IsComplete).IsTrue();
        await Assert.That(final.Keys).IsEquivalentTo([KeyInSlot(8192)]);
        await Assert.That(cluster.First.Server.ReceivedCommands.Count(command => command.StartsWith("SCAN "))).IsEqualTo(1);
        await Assert.That(cluster.Second.Server.ReceivedCommands.Count(command => command.StartsWith("SCAN "))).IsEqualTo(1);
        await Assert.That(accepted.All(task => !task.IsCompleted)).IsTrue();
        await cluster.Second.Server.SendRawAsync("+PONG\r\n+PONG\r\n+PONG\r\n+PONG\r\n"u8.ToArray(), 0);
        foreach (var task in accepted)
        {
            using var reply = await task.WaitAsync(timeout.Token);
            await Assert.That(reply.AsString()).IsEqualTo("PONG");
        }
        await router.WaitForRetirementAsync().WaitAsync(timeout.Token);
    }

    [Test]
    public async Task BinaryPrefixCheckpointRetainsExactIdentityAndStripsBeforeDecoding()
    {
        await using var cluster = new ScanCluster();
        byte[] prefix = [255, (byte)'*', 0];
        byte[] other = [254, (byte)'*', 0];
        cluster.First.Scan = _ => [.. "*2\r\n$2\r\n17\r\n*2\r\n"u8,
            .. BinaryPrefixTests.Bulk([.. prefix, .. "visible"u8]),
            .. BinaryPrefixTests.Bulk([.. other, .. "secret"u8])];
        await using var client = await cluster.ConnectAsync();
        var view = client.WithKeyPrefix((RespireKey)prefix);
        var page = await view.Keys.ScanClusterPageAsync(RespireClusterScanCursor.Start);
        await Assert.That(page.Keys).IsEquivalentTo(["visible"]);
        var serialized = page.Cursor.ToString();
        var parsed = RespireClusterScanCursor.Parse(serialized);
        await Assert.That(parsed.ToString()).IsEqualTo(serialized);
        var received = cluster.First.Server.ReceivedArguments.Last();
        await Assert.That(received[3].SequenceEqual(new byte[] { 255, (byte)'\\', (byte)'*', 0, (byte)'*' })).IsTrue();
        var before = cluster.CommandCount;
        await Assert.That(async () => await client.WithKeyPrefix((RespireKey)other).Keys.ScanClusterPageAsync(parsed))
            .Throws<ArgumentException>();
        await Assert.That(cluster.CommandCount).IsEqualTo(before);
        var resumed = await client.WithKeyPrefix((RespireKey)prefix.ToArray()).Keys.ScanClusterPageAsync(parsed);
        await Assert.That(resumed.Keys).IsEquivalentTo(["visible"]);
        var bytes = Convert.FromBase64String(serialized);
        await Assert.That(RespireClusterScanCursor.TryParse(Convert.ToBase64String(bytes.AsSpan(0, 13)), out _)).IsFalse();
    }

    private static string KeyInSlot(int slot)
    {
        for (var index = 0; ; index++)
        {
            var key = "scan:" + index;
            if (ClusterHash.GetSlot(key) == slot) return key;
        }
    }

    private static byte[] Bulk(string text) => Encoding.UTF8.GetBytes($"${Encoding.UTF8.GetByteCount(text)}\r\n{text}\r\n");
    private static byte[] Page(string cursor, params string[] keys)
        => Encoding.UTF8.GetBytes($"*2\r\n${cursor.Length}\r\n{cursor}\r\n*{keys.Length}\r\n" +
            string.Concat(keys.Select(key => $"${Encoding.UTF8.GetByteCount(key)}\r\n{key}\r\n")));

    private sealed class ScanClock : TimeProvider
    {
        internal Channel<ScanTimer> Timers { get; } = Channel.CreateUnbounded<ScanTimer>();
        internal int Created;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ScanTimer(callback, state, dueTime);
            Interlocked.Increment(ref Created);
            Timers.Writer.TryWrite(timer);
            return timer;
        }
    }

    private sealed class ScanTimer(TimerCallback callback, object? state, TimeSpan delay) : ITimer
    {
        private bool _disposed;
        internal TimeSpan Delay => delay;
        internal void Fire() { if (!_disposed) callback(state); }
        public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException();
        public void Dispose() => _disposed = true;
        public ValueTask DisposeAsync() { Dispose(); return default; }
    }

    private sealed class ScanCluster : IAsyncDisposable
    {
        internal Node First { get; } = new("first", "0-8191");
        internal Node Second { get; } = new("second", "8192-16383");
        internal int CommandCount => First.Server.CommandsSeen + Second.Server.CommandsSeen;
        internal ScanCluster()
        {
            foreach (var node in new[] { First, Second })
                node.Server.ReplyOverride = (_, command) => command switch
                {
                    "CLUSTER SLOTS" => SlotsReply(),
                    "CLUSTER NODES" => Metadata(node),
                    "INFO server" => Bulk($"# Server\r\nrun_id:{node.RunId}\r\n"),
                    _ when command.StartsWith("SCAN ") => node.Scan(command),
                    _ => FakeRespServer.PongReply,
                };
        }
        private static byte[] Metadata(Node node)
        {
            node.BeforeMetadata?.Invoke();
            return Bulk($"{node.Id} 127.0.0.1:{node.Server.Port}@17000 myself,master - 0 0 {node.Epoch} connected {node.Slots} {node.Transitions}\n");
        }
        internal ValueTask<RespireClient> ConnectAsync() => RespireClient.ConnectAsync(new RespireOptions
        {
            UseCluster = true, Protocol = RespProtocol.Resp2, Connections = 1, MaxInflightCommands = 4,
            Endpoints = { new RespireEndpoint("127.0.0.1", First.Server.Port) },
        });
        private byte[] SlotsReply()
        {
            List<string> ranges = [];
            foreach (var node in new[] { First, Second })
                foreach (var range in node.Slots.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    var ends = range.Split('-');
                    ranges.Add($"*3\r\n:{ends[0]}\r\n:{ends[^1]}\r\n*3\r\n$9\r\n127.0.0.1\r\n:{node.Server.Port}\r\n${node.Id.Length}\r\n{node.Id}\r\n");
                }
            return Encoding.ASCII.GetBytes($"*{ranges.Count}\r\n" + string.Concat(ranges));
        }
        public async ValueTask DisposeAsync()
        {
            await First.Server.DisposeAsync();
            await Second.Server.DisposeAsync();
        }
    }

    private sealed class Node(string id, string slots)
    {
        internal string Id = id;
        internal Action? BeforeMetadata;
        internal string Slots = slots;
        internal string Transitions = "";
        internal ulong Epoch = 1;
        internal string RunId = id + "-run";
        internal FakeRespServer Server { get; } = new(8, FakeRespServer.PongReply);
        internal Func<string, byte[]> Scan = _ => Page("0");
    }
}
