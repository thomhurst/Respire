using System.Reflection;
using System.Text;
using Respire.Commands;
using Respire.Networking;
using Respire.Protocol;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class FrameSerializationSelectionTests
{
    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task CurrentFrameBoundControlsSerializationWithoutAffectingFollowingSmallCommands(bool knownBound)
    {
        await using var server = new FakeRespServer(FakeRespServer.OkReply);
        await using var connection = await ConnectAsync(server);
        // Observe gate ownership on the producer itself, without adding transport callbacks or fields.
        var gateHeld = ObserveWriteGate(connection);
        var payload = Enumerable.Range(0, 70_000).Select(index => (byte)index).ToArray();
        var large = Serialize(new Cmd2(Verbs.Set, "large", payload));
        var observations = new List<bool>();
        var hints = 0;
        // A dedicated producer preserves thread-local scratch history across both completed sends.
        await Task.Factory.StartNew(() =>
        {
            using var first = connection.SendAsync(new ObservedCommand(large, knownBound ? large.Length : 0,
                () => hints++, () => observations.Add(gateHeld()))).AsTask().GetAwaiter().GetResult();
            using var second = connection.SendAsync(new ObservedCommand(FakeRespServer.PingFrame,
                FakeRespServer.PingFrame.Length, () => hints++,
                () => observations.Add(gateHeld()))).AsTask().GetAwaiter().GetResult();
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.That(observations.Count).IsEqualTo(2);
        await Assert.That(observations[0]).IsEqualTo(knownBound);
        await Assert.That(observations[1]).IsFalse();
        await Assert.That(hints).IsEqualTo(2);
        await Assert.That(server.ReceivedArguments.Count).IsEqualTo(2);
        await Assert.That(server.ReceivedArguments[0][0].AsSpan().SequenceEqual("SET"u8)).IsTrue();
        await Assert.That(server.ReceivedArguments[0][1].AsSpan().SequenceEqual("large"u8)).IsTrue();
        await Assert.That(server.ReceivedArguments[0][2].AsSpan().SequenceEqual(payload)).IsTrue();
        await Assert.That(server.ReceivedCommands[1]).IsEqualTo("PING");
        await Assert.That(connection.InspectForTests().Inflight.Count).IsEqualTo(0);
    }

    [Test]
    [Arguments(65_536, false)]
    [Arguments(65_537, true)]
    public async Task CompleteUpperBoundSelectsGateAtScratchRetentionBoundary(int bound, bool expectedGate)
    {
        await using var server = new FakeRespServer(FakeRespServer.PongReply);
        await using var connection = await ConnectAsync(server);
        var gateHeld = ObserveWriteGate(connection);
        var observed = false;
        using var reply = await connection.SendAsync(new ObservedCommand(FakeRespServer.PingFrame, bound,
            static () => { }, () => observed = gateHeld()));
        await Assert.That(observed).IsEqualTo(expectedGate);
        await Assert.That(reply.AsString()).IsEqualTo("PONG");
        await Assert.That(server.ReceivedCommands).IsEquivalentTo(["PING"]);
    }

    [Test]
    [Arguments(0)]
    [Arguments(65_537)]
    public async Task SerializationFailurePublishesNeitherPartialFrameNorResponseSlot(int bound)
    {
        await using var server = new FakeRespServer(FakeRespServer.PongReply);
        await using var connection = await ConnectAsync(server);
        var failure = new InvalidOperationException("Serialization rejected.");
        var error = await Assert.That(async () =>
        {
            using var reply = await connection.SendAsync(new ObservedCommand(FakeRespServer.PingFrame, bound,
                static () => { }, () => throw failure));
        }).ThrowsExactly<InvalidOperationException>();
        await Assert.That(ReferenceEquals(error, failure)).IsTrue();
        using var next = await connection.SendAsync(new RawCommand(FakeRespServer.PingFrame));
        await Assert.That(next.AsString()).IsEqualTo("PONG");
        await Assert.That(server.ReceivedCommands).IsEquivalentTo(["PING"]);
        await Assert.That(connection.InspectForTests().Inflight.Count).IsEqualTo(0);
    }

    private static Func<bool> ObserveWriteGate(RespireConnection connection)
    {
        var gate = typeof(RespireConnection).GetField("_writeGate", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(connection)!;
        // The net8.0 polyfill and net10.0 runtime Lock expose the same ownership property.
        var ownership = gate.GetType().GetProperty("IsHeldByCurrentThread")!;
        return () => (bool)ownership.GetValue(gate)!;
    }

    [Test]
    [Arguments(1, true)]
    [Arguments(1, false)]
    [Arguments(2, true)]
    [Arguments(2, false)]
    public async Task PrefixSelectionUsesAllFramesAndPropagatesUnknownBounds(int prefixCount, bool knownBound)
    {
        await using var server = new FakeRespServer(FakeRespServer.OkReply);
        await using var connection = await ConnectAsync(server);
        var gateHeld = ObserveWriteGate(connection);
        var payload = Enumerable.Repeat((byte)'a', 66_000 / (prefixCount + 1)).ToArray();
        var frame = Serialize(new Cmd1(RespireCommands.Connection.ECHO.Verb, payload));
        await Assert.That(frame.Length).IsLessThanOrEqualTo(65_536);
        await Assert.That(frame.Length * (prefixCount + 1)).IsGreaterThan(65_536);
        var observations = new List<bool>();
        var hints = 0;
        var prefix = new ObservedCommand(frame, knownBound ? frame.Length : 0,
            () => hints++, () => observations.Add(gateHeld()));
        var command = new ObservedCommand(frame, frame.Length,
            () => hints++, () => observations.Add(gateHeld()));
        using var reply = prefixCount == 1
            ? await connection.SendPrefixedAsync(prefix, command, throwOnError: true)
            : await connection.SendValidatedPrefixedAsync(prefix, command, command);
        await Assert.That(reply.AsString()).IsEqualTo("OK");
        await Assert.That(hints).IsEqualTo(prefixCount + 1);
        await Assert.That(observations.Count).IsEqualTo(prefixCount + 1);
        await Assert.That(observations.All(held => held == knownBound)).IsTrue();
        var received = server.ReceivedArguments;
        await Assert.That(received.Count).IsEqualTo(prefixCount + 1);
        foreach (var arguments in received)
        {
            await Assert.That(arguments[0].AsSpan().SequenceEqual("ECHO"u8)).IsTrue();
            await Assert.That(arguments[1].AsSpan().SequenceEqual(payload)).IsTrue();
        }
        await Assert.That(connection.InspectForTests().Inflight.Count).IsEqualTo(0);
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task TransactionBoundIncludesCommandBlockAndEveryEnvelopeFrame(bool includeMulti)
    {
        var block = Serialize(new Cmd1(RespireCommands.Connection.ECHO.Verb, new byte[65_500]));
        await Assert.That(block.Length).IsLessThanOrEqualTo(65_536);
        var type = typeof(RespireConnection).GetNestedType("TransactionCommand", BindingFlags.NonPublic)!;
        var command = (IRespCommand)Activator.CreateInstance(type,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, binder: null,
            args: [new ReadOnlyMemory<byte>(block), includeMulti, null, default(ClientSideCacheCoordinator.MutationFence)], culture: null)!;
        byte[] expected = includeMulti
            ? [.. RespCommands.Multi, .. block, .. RespCommands.Exec]
            : [.. block, .. RespCommands.Exec];
        var bound = command.GetWriteSizeHint();
        await Assert.That(bound).IsEqualTo(expected.Length);
        await Assert.That(bound).IsGreaterThan(65_536);
        var buffer = new WriteBuffer(bound);
        try
        {
            var capacity = buffer.Capacity;
            var writer = new RespWriter(buffer, bound);
            command.Write(ref writer);
            writer.Complete();
            await Assert.That(buffer.Capacity).IsEqualTo(capacity);
            await Assert.That(buffer.WrittenMemory.Span.SequenceEqual(expected)).IsTrue();
        }
        finally { buffer.Release(); }
    }

    [Test]
    public async Task MixedProducersPreserveExactPayloadsAndFifoRepliesAcrossCapacityWaits()
    {
        var full = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holdReplies = 1;
        var heldCommands = 0;
        await using var server = new FakeRespServer(FakeRespServer.OkReply)
        {
            SuppressReply = _ =>
            {
                if (Volatile.Read(ref holdReplies) == 0) return false;
                if (Interlocked.Increment(ref heldCommands) == 4) full.TrySetResult();
                return true;
            },
            ReplyOverride = (_, command) => command.StartsWith("ECHO ", StringComparison.Ordinal)
                ? Encoding.UTF8.GetBytes("+" + command[5..] + "\r\n") : null,
        };
        await using var connection = await RespireConnection.ConnectAsync("127.0.0.1", server.Port,
            new RespireConnectionOptions { Protocol = RespProtocol.Resp2, CommandTimeout = null, MaxInflightCommands = 4 });
        var binary = Enumerable.Range(0, 70_000).Select(index => (byte)index).ToArray();
        var text = new string('a', 35_000) + "é😀";
        var textBytes = Encoding.UTF8.GetBytes(text);
        var first = new Task<RespValue>[4];
        for (var worker = 0; worker < first.Length; worker++)
        {
            RespireValue payload = worker % 2 == 0 ? binary : text;
            first[worker] = connection.SendAsync(new Cmd2(Verbs.Set, $"worker-{worker}-prime", payload)).AsTask();
        }
        await full.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var waitingFrame = Serialize(new Cmd1(RespireCommands.Connection.ECHO.Verb, "capacity-wait"));
        var serialized = 0;
        var waiting = connection.SendAsync(new ObservedCommand(waitingFrame, waitingFrame.Length,
            static () => { }, () => Interlocked.Increment(ref serialized))).AsTask();
        await Assert.That(connection.InspectForTests().Inflight.Count).IsEqualTo(4);
        await Assert.That(server.CommandsSeen).IsEqualTo(4);
        await Assert.That(waiting.IsCompleted).IsFalse();
        await Assert.That(Volatile.Read(ref serialized)).IsEqualTo(0);
        Volatile.Write(ref holdReplies, 0);
        await server.SendRawAsync("+OK\r\n+OK\r\n+OK\r\n+OK\r\n"u8.ToArray());
        foreach (var pending in first)
        {
            using var reply = await pending.WaitAsync(TimeSpan.FromSeconds(10));
            await Assert.That(reply.AsString()).IsEqualTo("OK");
        }
        using (var reply = await waiting.WaitAsync(TimeSpan.FromSeconds(10)))
            await Assert.That(reply.AsString()).IsEqualTo("capacity-wait");
        await Assert.That(Volatile.Read(ref serialized)).IsEqualTo(1);

        await Task.WhenAll(Enumerable.Range(0, 8).Select(worker => Task.Run(async () =>
        {
            for (var iteration = 0; iteration < 3; iteration++)
            {
                var identity = $"worker-{worker}-{iteration}";
                RespireValue payload = worker % 2 == 0 ? binary : text;
                var stored = connection.SendAsync(new Cmd2(Verbs.Set, identity, payload)).AsTask();
                var echo = connection.SendAsync(new Cmd1(RespireCommands.Connection.ECHO.Verb, identity)).AsTask();
                using var storedReply = await stored;
                using var echoReply = await echo;
                await Assert.That(storedReply.AsString()).IsEqualTo("OK");
                await Assert.That(echoReply.AsString()).IsEqualTo(identity);
            }
        }))).WaitAsync(TimeSpan.FromSeconds(10));
        var received = server.ReceivedArguments;
        await Assert.That(received.Count).IsEqualTo(53);
        var identities = new HashSet<string>();
        foreach (var arguments in received)
        {
            if (!arguments[0].AsSpan().SequenceEqual("SET"u8)) continue;
            var identity = Encoding.UTF8.GetString(arguments[1]);
            var worker = int.Parse(identity.Split('-')[1], System.Globalization.CultureInfo.InvariantCulture);
            await Assert.That(identities.Add(identity)).IsTrue();
            await Assert.That(arguments[2].AsSpan().SequenceEqual(worker % 2 == 0 ? binary : textBytes)).IsTrue();
        }
        await Assert.That(identities.Count).IsEqualTo(28);
        await Assert.That(connection.InspectForTests().Inflight.Count).IsEqualTo(0);
    }

    private static byte[] Serialize<TCommand>(TCommand command) where TCommand : struct, IRespCommand
    {
        var buffer = new WriteBuffer(command.GetWriteSizeHint());
        try
        {
            var writer = new RespWriter(buffer, command.GetWriteSizeHint());
            command.Write(ref writer);
            writer.Complete();
            return buffer.WrittenMemory.ToArray();
        }
        finally { buffer.Release(); }
    }

    private static Task<RespireConnection> ConnectAsync(FakeRespServer server)
        => RespireConnection.ConnectAsync("127.0.0.1", server.Port,
            new RespireConnectionOptions { Protocol = RespProtocol.Resp2, CommandTimeout = null });

    private readonly struct ObservedCommand(byte[] frame, int bound, Action hint, Action written) : IRespCommand
    {
        public int GetWriteSizeHint() { hint(); return bound; }
        public ReadCommandKind ReadKind => ReadCommandKind.None;
        public void Write(ref RespWriter writer) { writer.WriteRaw(frame); written(); }
    }
}
