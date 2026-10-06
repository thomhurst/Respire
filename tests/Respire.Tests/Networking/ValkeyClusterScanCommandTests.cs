using System.Text;
using Respire.Internal;
using TUnit.Assertions;
using TUnit.Assertions.Enums;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class ValkeyClusterScanCommandTests
{
    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task InitialSlotAndOpaqueContinuationRouteToPrimariesWithExactOptions(int protocol)
    {
        await using var cluster = new ScanCluster();
        await using var client = await cluster.ConnectAsync(protocol);
        var keys = client.WithReadFrom(RespireReadFrom.Replica).Keys;
        await keys.ScanValkeyClusterPageAsync();
        await keys.ScanValkeyClusterPageAsync(slot: 16383);
        var cursor = $"future-{{{Tag(9000)}}}-18446744073709551615";
        await keys.ScanValkeyClusterPageAsync(cursor, "key with space:*", RespireKeyType.Hash, 7, 9000);
        await Assert.That(Scans(cluster.First)).IsEquivalentTo(["CLUSTERSCAN 0 COUNT 250"], CollectionOrdering.Matching);
        await Assert.That(Scans(cluster.Second)).IsEquivalentTo([
            "CLUSTERSCAN 0 COUNT 250 SLOT 16383",
            $"CLUSTERSCAN {cursor} MATCH key with space:* COUNT 7 TYPE hash SLOT 9000"], CollectionOrdering.Matching);
        var arguments = cluster.Second.ReceivedArguments.Last(row => Encoding.UTF8.GetString(row[0]) == "CLUSTERSCAN");
        await Assert.That(arguments.Select(bytes => Encoding.UTF8.GetString(bytes))).IsEquivalentTo([
            "CLUSTERSCAN", cursor, "MATCH", "key with space:*", "COUNT", "7", "TYPE", "hash", "SLOT", "9000"], CollectionOrdering.Matching);
        await Assert.That(cluster.Replica.ConnectionAccepted.IsCompleted).IsFalse();
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task PrefixNeverChangesCursorAndOwnedBinaryResultsRoundTripThroughView(int protocol)
    {
        await using var cluster = new ScanCluster();
        var prefix = $"tenant:*:{{{Tag(0)}}}:";
        var cursor = $"fingerprint-{{{Tag(9000)}}}-123";
        byte[] suffix = [255, 0, 13, 10];
        byte[] physical = [.. Encoding.UTF8.GetBytes(prefix), .. suffix];
        cluster.Scan = _ => Page("opaque-{next}-17", physical, "outside"u8.ToArray());
        await using var client = await cluster.ConnectAsync(protocol);
        var view = client.WithKeyPrefix(prefix);
        var page = await view.Keys.ScanValkeyClusterPageAsync(cursor);
        await Assert.That(page.Keys.Count).IsEqualTo(1);
        await Assert.That(page.Keys[0]).IsEqualTo(new RespireKey(suffix));
        await Assert.That(await view.GetStringAsync(page.Keys[0])).IsEqualTo("value");
        var sent = cluster.Second.ReceivedArguments.Single(row => Encoding.UTF8.GetString(row[0]) == "CLUSTERSCAN");
        await Assert.That(Encoding.UTF8.GetString(sent[1])).IsEqualTo(cursor);
        await Assert.That(Encoding.UTF8.GetString(sent[3])).IsEqualTo(prefix.Replace("*", "\\*", StringComparison.Ordinal) + "*");
        var get = cluster.First.ReceivedArguments.Single(row => Encoding.UTF8.GetString(row[0]) == "GET");
        await Assert.That(get[1]).IsEquivalentTo(physical);
        await client.DisposeAsync(); physical.AsSpan().Clear();
        await Assert.That(page.Cursor).IsEqualTo("opaque-{next}-17");
        await Assert.That(page.Keys[0]).IsEqualTo(new RespireKey(suffix));
    }

    [Test]
    [Arguments("null")]
    [Arguments("empty")]
    [Arguments("zero-count")]
    [Arguments("negative-count")]
    [Arguments("negative-slot")]
    [Arguments("large-slot")]
    [Arguments("none-type")]
    [Arguments("unknown-type")]
    [Arguments("invalid-type")]
    public async Task InvalidArgumentsDoNotConnect(string shape)
    {
        await using var target = new FakeRespServer(FakeRespServer.OkReply);
        await using var client = RespireClient.Create(Options(target.Port, 2));
        var cursor = shape switch { "null" => null!, "empty" => "", _ => "0" };
        var count = shape switch { "zero-count" => 0, "negative-count" => -1, _ => 250 };
        int? slot = shape switch { "negative-slot" => -1, "large-slot" => 16384, _ => null };
        RespireKeyType? type = shape switch
        {
            "none-type" => RespireKeyType.None,
            "unknown-type" => RespireKeyType.Unknown,
            "invalid-type" => (RespireKeyType)999,
            _ => null,
        };
        await Assert.That(async () => await client.Keys.ScanValkeyClusterPageAsync(
            cursor, countHint: count, slot: slot, type: type)).Throws<ArgumentException>();
        await Assert.That(target.ConnectionAccepted.IsCompleted).IsFalse();
    }

    [Test]
    public async Task StandaloneAndPreCancellationDoNotConnect()
    {
        await using var target = new FakeRespServer(FakeRespServer.OkReply);
        await using var standalone = RespireClient.Create(Options(target.Port, 2) with { UseCluster = false });
        await Assert.That(async () => await standalone.Keys.ScanValkeyClusterPageAsync()).ThrowsExactly<InvalidOperationException>();
        await using var client = RespireClient.Create(Options(target.Port, 2));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.That(async () => await client.Keys.ScanValkeyClusterPageAsync(cancellationToken: cancellation.Token))
            .Throws<OperationCanceledException>();
        await Assert.That(target.ConnectionAccepted.IsCompleted).IsFalse();
    }

    [Test]
    public async Task CancellationAfterSubmissionLeavesTheOpaqueInputAvailable()
    {
        await using var cluster = new ScanCluster();
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        cluster.First.SuppressReply = command =>
        {
            if (!command.StartsWith("CLUSTERSCAN ", StringComparison.Ordinal)) return false;
            received.TrySetResult(); return true;
        };
        await using var client = await cluster.ConnectAsync(2);
        using var cancellation = new CancellationTokenSource();
        var cursor = $"future-{{{Tag(0)}}}-55";
        var pending = client.Keys.ScanValkeyClusterPageAsync(cursor, cancellationToken: cancellation.Token).AsTask();
        await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.That(async () => await pending).Throws<OperationCanceledException>();
        await Assert.That(Scans(cluster.First).Single()).IsEqualTo($"CLUSTERSCAN {cursor} COUNT 250");
    }

    [Test]
    [Arguments("MOVED")]
    [Arguments("ASK")]
    public async Task RedirectsKeepTheSameCursorAndOptions(string redirect)
    {
        await using var cluster = new ScanCluster();
        cluster.Scan = server => server == cluster.First
            ? Encoding.ASCII.GetBytes($"-{redirect} 0 127.0.0.1:{cluster.Second.Port}\r\n")
            : Page("0", "owned"u8.ToArray());
        await using var client = await cluster.ConnectAsync(2);
        var cursor = $"future-{{{Tag(0)}}}-33";
        var page = await client.Keys.ScanValkeyClusterPageAsync(cursor, "*", RespireKeyType.String, 3, 0);
        await Assert.That(page.IsComplete).IsTrue();
        await Assert.That(page.Keys.Single()).IsEqualTo(new RespireKey("owned"));
        await Assert.That(Scans(cluster.First)).IsEquivalentTo(Scans(cluster.Second), CollectionOrdering.Matching);
        await Assert.That(cluster.Second.ReceivedCommands.Contains("ASKING")).IsEqualTo(redirect == "ASK");
    }

    private static RespireOptions Options(int port, int protocol) => new()
    {
        UseCluster = true, Connections = 1, Protocol = protocol == 2 ? RespProtocol.Resp2 : RespProtocol.Resp3,
        Endpoints = [new("127.0.0.1", port)], MaintenanceNotifications = RespireMaintenanceNotificationMode.Disabled,
    };
    private static IEnumerable<string> Scans(FakeRespServer server)
        => server.ReceivedCommands.Where(command => command.StartsWith("CLUSTERSCAN ", StringComparison.Ordinal));
    private static string Tag(int slot) => Enumerable.Range(0, 1_000_000).Select(index => $"scan-{index}")
        .First(tag => ClusterHash.GetSlot(tag) == slot);
    private static byte[] Bulk(byte[] bytes) => [.. Encoding.ASCII.GetBytes($"${bytes.Length}\r\n"), .. bytes, 13, 10];
    private static byte[] Page(string cursor, params byte[][] keys)
        => [.. "*2\r\n"u8, .. Bulk(Encoding.UTF8.GetBytes(cursor)), .. Encoding.ASCII.GetBytes($"*{keys.Length}\r\n"), .. keys.SelectMany(Bulk)];

    private sealed class ScanCluster : IAsyncDisposable
    {
        internal FakeRespServer First { get; } = new(8, FakeRespServer.OkReply);
        internal FakeRespServer Second { get; } = new(8, FakeRespServer.OkReply);
        internal FakeRespServer Replica { get; } = new(8, FakeRespServer.OkReply);
        internal Func<FakeRespServer, byte[]> Scan = _ => Page("0");

        internal ScanCluster()
        {
            foreach (var server in new[] { First, Second, Replica })
                server.ReplyOverride = (_, command) => command switch
                {
                    "HELLO 3" => "%1\r\n$5\r\nproto\r\n:3\r\n"u8.ToArray(),
                    "CLUSTER SLOTS" => Slots(),
                    _ when command.StartsWith("CLUSTERSCAN ", StringComparison.Ordinal) => Scan(server),
                    _ when command.StartsWith("GET ", StringComparison.Ordinal) => Bulk("value"u8.ToArray()),
                    _ => FakeRespServer.OkReply,
                };
        }

        internal ValueTask<RespireClient> ConnectAsync(int protocol) => RespireClient.ConnectAsync(Options(First.Port, protocol));
        private byte[] Slots() => Encoding.ASCII.GetBytes($"*2\r\n*4\r\n:0\r\n:8191\r\n{Node(First, "first")}{Node(Replica, "replica")}*3\r\n:8192\r\n:16383\r\n{Node(Second, "second")}");
        private static string Node(FakeRespServer server, string id) => $"*3\r\n$9\r\n127.0.0.1\r\n:{server.Port}\r\n${id.Length}\r\n{id}\r\n";
        public async ValueTask DisposeAsync()
        {
            await First.DisposeAsync(); await Second.DisposeAsync(); await Replica.DisposeAsync();
        }
    }
}
