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
    public async Task MixedProducersPreserveExactPayloadsAndFifoRepliesAcrossCapacityWaits()
    {
        await using var server = new FakeRespServer(FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => command.StartsWith("ECHO ", StringComparison.Ordinal)
                ? Encoding.UTF8.GetBytes("+" + command[5..] + "\r\n") : null,
        };
        await using var connection = await RespireConnection.ConnectAsync("127.0.0.1", server.Port,
            new RespireConnectionOptions { Protocol = RespProtocol.Resp2, CommandTimeout = null, MaxInflightCommands = 4 });
        var binary = Enumerable.Range(0, 70_000).Select(index => (byte)index).ToArray();
        var text = new string('a', 35_000) + "é😀";
        var textBytes = Encoding.UTF8.GetBytes(text);
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
        await Assert.That(received.Count).IsEqualTo(48);
        var identities = new HashSet<string>();
        foreach (var arguments in received)
        {
            if (!arguments[0].AsSpan().SequenceEqual("SET"u8)) continue;
            var identity = Encoding.UTF8.GetString(arguments[1]);
            var worker = int.Parse(identity.Split('-')[1], System.Globalization.CultureInfo.InvariantCulture);
            await Assert.That(identities.Add(identity)).IsTrue();
            await Assert.That(arguments[2].AsSpan().SequenceEqual(worker % 2 == 0 ? binary : textBytes)).IsTrue();
        }
        await Assert.That(identities.Count).IsEqualTo(24);
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
