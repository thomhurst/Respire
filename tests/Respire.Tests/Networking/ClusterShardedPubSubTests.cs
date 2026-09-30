using System.Text;
using Respire.Internal;
using Respire.Commands;
using Respire.Networking;
using Respire.Protocol;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class ClusterShardedPubSubTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task DifferentSlotsUseOneDedicatedConnectionPerPrimary(int protocol)
    {
        await using var cluster = new Cluster(protocol);
        await using var client = cluster.CreateClient();
        await using var subscription = await client.SubscribeShardedAsync(["bar", "baz", "foo", "bar"]);
        await using var duplicate = await client.SubscribeShardedAsync("bar");
        await Assert.That(cluster.First.ReceivedCommands.Count(command => command == "SSUBSCRIBE bar")).IsEqualTo(1);
        var firstIds = ControlIds(cluster.First);
        await Assert.That(firstIds.Distinct().Count()).IsEqualTo(1);
        await Assert.That(ControlIds(cluster.Second).Distinct().Count()).IsEqualTo(1);
        await using var reader = subscription.GetAsyncEnumerator();
        await cluster.SendAsync(cluster.First, "bar", "one");
        await cluster.SendAsync(cluster.Second, "foo", "two");
        var received = new HashSet<string>();
        for (var i = 0; i < 2; i++)
        {
            await Assert.That(await reader.MoveNextAsync().AsTask().WaitAsync(Deadline)).IsTrue();
            received.Add(reader.Current.Text!);
        }
        await Assert.That(received).IsEquivalentTo(["one", "two"]);
        await duplicate.DisposeAsync();
        await Assert.That(cluster.First.ReceivedCommands.Any(command => command == "SUNSUBSCRIBE bar")).IsFalse();
        await subscription.DisposeAsync();
        await Assert.That(cluster.First.ReceivedCommands.Count(command => command == "SUNSUBSCRIBE bar")).IsEqualTo(1);
        await Assert.That(cluster.Second.ReceivedCommands.Count(command => command == "SUNSUBSCRIBE foo")).IsEqualTo(1);
    }

    [Test]
    public async Task MovedSubscriptionUpdatesPublishRoutingWithoutCrossSlotCommands()
    {
        await using var cluster = new Cluster(2);
        cluster.SecondOverride = (_, command) => command == "SSUBSCRIBE foo"
            ? Encoding.ASCII.GetBytes($"-MOVED {ClusterHash.GetSlot("foo")} 127.0.0.1:{cluster.First.Port}\r\n") : null;
        await using var client = cluster.CreateClient();
        await using var subscription = await client.SubscribeShardedAsync("foo");
        await Assert.That(await client.PublishShardedAsync("foo", "payload")).IsEqualTo(1L);
        await Assert.That(cluster.First.ReceivedCommands).Contains("SPUBLISH foo payload");
        await Assert.That(cluster.First.ReceivedCommands).Contains("SSUBSCRIBE foo");
        await Assert.That(cluster.Second.ReceivedCommands.Any(command => command.StartsWith("SPUBLISH "))).IsFalse();
        var commands = cluster.First.ReceivedCommands;
        var ids = cluster.First.ReceivedConnectionIds;
        await Assert.That(ids[commands.ToList().IndexOf("SPUBLISH foo payload")])
            .IsNotEqualTo(ids[commands.ToList().IndexOf("SSUBSCRIBE foo")]);
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task TopologyMoveResubscribesAndPublishesGapBeforeSameReadMessage(int protocol)
    {
        await using var cluster = new Cluster(protocol);
        await using var client = cluster.CreateClient();
        await using var subscription = await client.SubscribeShardedAsync("foo");
        cluster.FirstOverride = (_, command) => command == "SSUBSCRIBE foo"
            ? [.. cluster.Confirmation("ssubscribe", "foo"), .. cluster.Message("foo", "after")] : null;
        var router = client.Core.Cluster!;
        router.SetSlotOwner(ClusterHash.GetSlot("foo"), router.GetMultiplexer(new("127.0.0.1", cluster.First.Port)));
        await using var reader = subscription.GetAsyncEnumerator();
        await Assert.That(await reader.MoveNextAsync().AsTask().WaitAsync(Deadline)).IsTrue();
        await Assert.That(reader.Current.Kind).IsEqualTo(RespireMessageKind.Gap);
        await Assert.That(await reader.MoveNextAsync().AsTask().WaitAsync(Deadline)).IsTrue();
        await Assert.That(reader.Current.Text).IsEqualTo("after");
        await Assert.That(cluster.Second.ReceivedCommands).Contains("SUNSUBSCRIBE foo");
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task UnsolicitedSunsubscribeDoesNotCompleteAnotherPendingSubscription(int protocol)
    {
        await using var cluster = new Cluster(protocol);
        await using var client = cluster.CreateClient();
        await using var subscription = await client.SubscribeShardedAsync(["bar", "baz"]);
        cluster.First.SuppressReply = command => command == "SSUBSCRIBE b";
        var pending = client.SubscribeShardedAsync("b").AsTask();
        await WaitAsync(() => cluster.First.ReceivedCommands.Contains("SSUBSCRIBE b"));
        var socket = ControlIds(cluster.First).Last();
        await cluster.First.SendRawAsync(cluster.Confirmation("sunsubscribe", "bar"), socket);
        // A subsequent ordinary push proves the preceding SUNSUBSCRIBE was processed.
        await using var reader = subscription.GetAsyncEnumerator();
        await cluster.First.SendRawAsync(cluster.Message("baz", "bar removed"), socket);
        await Assert.That(await reader.MoveNextAsync().AsTask().WaitAsync(Deadline)).IsTrue();
        await Assert.That(reader.Current.Text).IsEqualTo("bar removed");
        await Assert.That(pending.IsCompleted).IsFalse();
        cluster.FirstOverride = (_, command) => command == "SSUBSCRIBE bar"
            ? [.. cluster.Confirmation("ssubscribe", "bar"), .. cluster.Message("bar", "restored")] : null;
        await cluster.First.SendRawAsync(cluster.Confirmation("ssubscribe", "b"), socket);
        await using var added = await pending.WaitAsync(Deadline);
        await Assert.That(await reader.MoveNextAsync().AsTask().WaitAsync(Deadline)).IsTrue();
        await Assert.That(reader.Current.Kind).IsEqualTo(RespireMessageKind.Gap);
        await Assert.That(await reader.MoveNextAsync().AsTask().WaitAsync(Deadline)).IsTrue();
        await Assert.That(reader.Current.Text).IsEqualTo("restored");
    }

    [Test]
    public async Task ClosedPrimaryRestoresOnlyItsChannelsAndPreservesOtherPrimary()
    {
        await using var cluster = new Cluster(2);
        await using var client = cluster.CreateClient();
        await using var subscription = await client.SubscribeShardedAsync(["bar", "foo"]);
        cluster.First.CloseConnectionAfterCommand = cluster.First.CommandsSeen + 1;
        await Assert.That(async () => await client.SubscribeShardedAsync("baz")).Throws<RespireConnectionException>();
        await WaitAsync(() => cluster.First.ReceivedCommands.Count(command => command == "SSUBSCRIBE bar") == 2);
        await cluster.SendAsync(cluster.Second, "foo", "healthy");
        await using var reader = subscription.GetAsyncEnumerator();
        var messages = new List<RespireMessage>();
        for (var i = 0; i < 2; i++)
        {
            await Assert.That(await reader.MoveNextAsync().AsTask().WaitAsync(Deadline)).IsTrue();
            messages.Add(reader.Current);
        }
        await Assert.That(messages.Count(message => message.Kind == RespireMessageKind.Gap)).IsEqualTo(1);
        await Assert.That(messages.Count(message => message.Text == "healthy")).IsEqualTo(1);
        await Assert.That(cluster.Second.ReceivedCommands.Count(command => command == "SSUBSCRIBE foo")).IsEqualTo(1);
    }

    [Test]
    public async Task ConfiguredExhaustionCompletesShardedSubscriptionsAndLeavesRegularSubscriptionsUsable()
    {
        await using var cluster = new Cluster(2);
        await using var client = cluster.CreateClient(new()
        {
            InitialDelay = TimeSpan.FromMilliseconds(10), MaxDelay = TimeSpan.FromMilliseconds(10),
            JitterRatio = 0, MaxAttempts = 2,
        });
        await using var subscription = await client.SubscribeShardedAsync("bar");
        cluster.FirstOverride = (_, command) => command == "SSUBSCRIBE bar" ? "-ERR denied\r\n"u8.ToArray() : null;
        await cluster.First.SendRawAsync(cluster.Confirmation("sunsubscribe", "bar"), ControlIds(cluster.First).Last());
        await Assert.That(await subscription.Completion.WaitAsync(Deadline)).IsEqualTo(RespireSubscriptionEndReason.ReconnectExhausted);
        await Assert.That(cluster.First.ReceivedCommands.Count(command => command == "SSUBSCRIBE bar")).IsEqualTo(3);
        await Assert.That(async () => await client.SubscribeShardedAsync("foo")).Throws<RespireReconnectLimitException>();
        await using var regular = await client.SubscribeAsync("regular");
        await Assert.That(regular.IsDisposed).IsFalse();
    }

    [Test]
    public async Task ClientDisposalInterruptsStalledShardedActivation()
    {
        await using var cluster = new Cluster(2);
        await using var client = cluster.CreateClient();
        await using var subscription = await client.SubscribeShardedAsync("bar");
        cluster.Second.SuppressReply = command => command == "SSUBSCRIBE foo";
        var pending = client.SubscribeShardedAsync("foo").AsTask();
        await WaitAsync(() => cluster.Second.ReceivedCommands.Contains("SSUBSCRIBE foo"));
        await client.DisposeAsync().AsTask().WaitAsync(Deadline);
        await Assert.That(async () => await pending).Throws<Exception>();
        await Assert.That(await subscription.Completion).IsEqualTo(RespireSubscriptionEndReason.ClientDisposed);
    }

    [Test]
    public async Task CancelledActivationPreservesCallerTokenAndHealthyPrimary()
    {
        await using var cluster = new Cluster(2);
        await using var client = cluster.CreateClient();
        await using var subscription = await client.SubscribeShardedAsync("bar");
        cluster.Second.SuppressReply = command => command == "SSUBSCRIBE foo";
        using var cancellation = new CancellationTokenSource();
        var pending = client.SubscribeShardedAsync("foo", cancellation.Token).AsTask();
        await WaitAsync(() => cluster.Second.ReceivedCommands.Contains("SSUBSCRIBE foo"));
        await cancellation.CancelAsync();
        var error = await Assert.That(async () => await pending.WaitAsync(Deadline)).Throws<OperationCanceledException>();
        await Assert.That(error!.CancellationToken).IsEqualTo(cancellation.Token);
        await cluster.SendAsync(cluster.First, "bar", "still live");
        await using var reader = subscription.GetAsyncEnumerator();
        await Assert.That(await reader.MoveNextAsync().AsTask().WaitAsync(Deadline)).IsTrue();
        await Assert.That(reader.Current.Text).IsEqualTo("still live");
        await Assert.That(cluster.First.ReceivedCommands.Count(command => command == "SSUBSCRIBE bar")).IsEqualTo(1);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task SubscriptionPushFilterSeesWhetherCommandHasEnteredFifo(bool push)
    {
        var confirmation = Encoding.ASCII.GetBytes($"{(push ? '>' : '*')}3\r\n$12\r\nsunsubscribe\r\n$3\r\nbar\r\n:0\r\n");
        await using var server = new FakeRespServer(confirmation);
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var unsolicited = 0;
        await using var connection = await RespireConnection.ConnectAsync("127.0.0.1", server.Port,
            new RespireConnectionOptions
            {
                PushHandler = (in RespValue _) => observed.TrySetResult(),
                SubscriptionPushFilter = (in RespValue _, bool pending) =>
                {
                    if (!pending) Interlocked.Increment(ref unsolicited);
                    return !pending;
                },
            });
        await server.SendRawAsync([.. confirmation, .. "*3\r\n$8\r\nsmessage\r\n$3\r\nbar\r\n$4\r\nsync\r\n"u8]);
        await observed.Task.WaitAsync(Deadline);
        await Assert.That(unsolicited).IsEqualTo(1);
        using var reply = await connection.SendAsync(new Cmd1(Verbs.SUnsubscribe, "bar"), CancellationToken.None)
            .AsTask().WaitAsync(Deadline);
        await Assert.That(reply.AsArray()[0].AsString()).IsEqualTo("sunsubscribe");
        await Assert.That(unsolicited).IsEqualTo(1);
    }

    [Test]
    public async Task RefreshedTopologyMovesSubscriptionsOnlyWhenOwnersChange()
    {
        await using var cluster = new Cluster(2);
        await using var client = cluster.CreateClient();
        await using var subscription = await client.SubscribeShardedAsync(["bar", "foo"]);
        var notifications = 0;
        client.Core.Cluster!.TopologyChanged += () => Interlocked.Increment(ref notifications);
        _ = await client.Core.Cluster.GetMasterConnectionsAsync(CancellationToken.None);
        await Assert.That(notifications).IsEqualTo(0);
        cluster.FirstOverride = (_, command) => command == "CLUSTER SLOTS"
            ? Encoding.ASCII.GetBytes($"*1\r\n*3\r\n:0\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{cluster.First.Port}\r\n") : null;
        _ = await client.Core.Cluster.GetMasterConnectionsAsync(CancellationToken.None);
        await Assert.That(notifications).IsEqualTo(1);
        await using var reader = subscription.GetAsyncEnumerator();
        await Assert.That(await reader.MoveNextAsync().AsTask().WaitAsync(Deadline)).IsTrue();
        await Assert.That(reader.Current.Kind).IsEqualTo(RespireMessageKind.Gap);
        await cluster.SendAsync(cluster.First, "foo", "new owner");
        await Assert.That(await reader.MoveNextAsync().AsTask().WaitAsync(Deadline)).IsTrue();
        await Assert.That(reader.Current.Text).IsEqualTo("new owner");
        await Assert.That(cluster.First.ReceivedCommands.Count(command => command == "SSUBSCRIBE bar")).IsEqualTo(1);
    }

    private static int[] ControlIds(FakeRespServer server) => server.ReceivedCommands
        .Select((command, index) => (command, index)).Where(pair => pair.command.StartsWith("SSUBSCRIBE "))
        .Select(pair => server.ReceivedConnectionIds[pair.index]).ToArray();

    private static async Task WaitAsync(Func<bool> predicate)
    {
        using var deadline = new CancellationTokenSource(Deadline);
        while (!predicate()) await Task.Delay(5, deadline.Token);
    }

    private sealed class Cluster : IAsyncDisposable
    {
        private readonly int _protocol;
        internal readonly FakeRespServer First = new(20, FakeRespServer.OkReply);
        internal readonly FakeRespServer Second = new(20, FakeRespServer.OkReply);
        internal Func<int, string, byte[]?>? FirstOverride;
        internal Func<int, string, byte[]?>? SecondOverride;

        internal Cluster(int protocol)
        {
            _protocol = protocol;
            First.ReplyOverride = (id, command) => FirstOverride?.Invoke(id, command) ?? Reply(command);
            Second.ReplyOverride = (id, command) => SecondOverride?.Invoke(id, command) ?? Reply(command);
        }
        internal RespireClient CreateClient(RespireReconnectPolicy? policy = null) => RespireClient.Create(new RespireOptions
        {
            UseCluster = true, Protocol = (RespProtocol)_protocol, Connections = 1, ReconnectPolicy = policy,
            Endpoints = { new RespireEndpoint("127.0.0.1", First.Port) }, ConnectTimeout = TimeSpan.FromSeconds(1),
        });
        private byte[] Reply(string command)
        {
            if (command.StartsWith("HELLO ")) return "%1\r\n+proto\r\n:3\r\n"u8.ToArray();
            if (command == "CLUSTER SLOTS") return Encoding.ASCII.GetBytes(
                $"*2\r\n*3\r\n:0\r\n:8191\r\n*2\r\n$9\r\n127.0.0.1\r\n:{First.Port}\r\n" +
                $"*3\r\n:8192\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{Second.Port}\r\n");
            var parts = command.Split(' ', 2);
            if (parts[0] is "SSUBSCRIBE" or "SUNSUBSCRIBE" or "SUBSCRIBE" or "UNSUBSCRIBE")
                return Confirmation(parts[0].ToLowerInvariant(), parts[1]);
            return ":1\r\n"u8.ToArray();
        }
        internal byte[] Confirmation(string verb, string channel) => Frame(Encoding.UTF8.GetBytes(verb), Encoding.UTF8.GetBytes(channel), null);
        internal byte[] Message(string channel, string text) => Frame("smessage"u8.ToArray(), Encoding.UTF8.GetBytes(channel), Encoding.UTF8.GetBytes(text));
        private byte[] Frame(byte[] verb, byte[] channel, byte[]? payload)
        {
            using var stream = new MemoryStream();
            stream.Write(Encoding.ASCII.GetBytes(_protocol == 3 ? ">3\r\n" : "*3\r\n"));
            foreach (var value in new[] { verb, channel, payload })
            {
                if (value is null) stream.Write(":1\r\n"u8);
                else
                {
                    stream.Write(Encoding.ASCII.GetBytes($"${value.Length}\r\n"));
                    stream.Write(value);
                    stream.Write("\r\n"u8);
                }
            }
            return stream.ToArray();
        }
        internal Task SendAsync(FakeRespServer server, string channel, string text)
            => server.SendRawAsync(Message(channel, text), ControlIds(server).Last());
        public async ValueTask DisposeAsync()
        {
            await First.DisposeAsync();
            await Second.DisposeAsync();
        }
    }
}
