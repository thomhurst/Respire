using System.Runtime.CompilerServices;
using System.Text;
using Respire.Commands;
using Respire.Internal;
using Respire.Networking;
using Respire.Protocol;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

/// <summary>
/// Drives each branch of the pooled ready-cluster reply source with a controlled reply, so the
/// hand-written continuation is covered without depending on wire timing.
/// </summary>
[NotInParallel]
public class ClusterReadyResponseTests
{
    private static readonly int Slot = ClusterHash.GetSlot("ready");

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RetiredReplyResumesOnReplacement(bool completedBeforeCreate)
    {
        await using var harness = await Harness.StartAsync();
        harness.MoveSlotToTarget();
        var reply = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var retired = new RespireConnectionRetiredException("127.0.0.1", harness.Source.Port);
        if (completedBeforeCreate) reply.SetException(retired);
        var pending = harness.Create(new(reply.Task), new Probe());
        if (!completedBeforeCreate) reply.SetException(retired);

        await Assert.That(await pending.AsTask().WaitAsync(harness.Timeout)).IsEqualTo("value");
        await Assert.That(harness.Target.ReceivedCommands.Count(command => command == "GET ready")).IsEqualTo(1);
        await Assert.That(harness.Source.ReceivedCommands.Count(command => command == "GET ready")).IsEqualTo(0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RejectedReplyResumesOnRedirectTarget(bool completedBeforeCreate)
    {
        // A completed faulted reply is the shape of a synchronous Send failure.
        await using var harness = await Harness.StartAsync();
        var moved = new RespireServerException($"MOVED {Slot} 127.0.0.1:{harness.Target.Port}");
        var pending = completedBeforeCreate
            ? harness.Create(ValueTask.FromException<string?>(moved), new Probe())
            : Fail(harness, moved, new Probe());

        await Assert.That(await pending.AsTask().WaitAsync(harness.Timeout)).IsEqualTo("value");
        await Assert.That(harness.Target.ReceivedCommands.Count(command => command == "GET ready")).IsEqualTo(1);
    }

    [Test]
    public async Task ConverterFailureIsUnwrappedAndNeverReplayed()
    {
        await using var harness = await Harness.StartAsync();
        // Redis-shaped, so a replay would be possible if the wrapper were not recognized first.
        var inner = new RespireServerException($"MOVED {Slot} 127.0.0.1:{harness.Target.Port}");
        var pending = Fail(harness, new RespireClient.ClusterConverterException(inner), new Probe());

        var error = await Assert.That(async () => await pending.AsTask().WaitAsync(harness.Timeout)).Throws<RespireServerException>();
        await Assert.That(error).IsSameReferenceAs(inner);
        await Assert.That(harness.Target.ReceivedCommands.Any(command => command.StartsWith("GET", StringComparison.Ordinal))).IsFalse();
    }

    [Test]
    public async Task UnrecoverableReplyFailureIsPublishedWithoutReplay()
    {
        await using var harness = await Harness.StartAsync();
        var failure = new RespireServerException("ERR not recoverable");
        var pending = Fail(harness, failure, new Probe());

        var error = await Assert.That(async () => await pending.AsTask().WaitAsync(harness.Timeout)).Throws<RespireServerException>();
        await Assert.That(error).IsSameReferenceAs(failure);
        await Assert.That(harness.Target.ReceivedCommands.Any(command => command.StartsWith("GET", StringComparison.Ordinal))).IsFalse();
    }

    [Test]
    public async Task RecoveryFailureIsPublished()
    {
        await using var harness = await Harness.StartAsync();
        var previous = harness.Target.ReplyOverride!;
        harness.Target.ReplyOverride = (connection, command) =>
            command == "GET ready" ? "-ERR replay failed\r\n"u8.ToArray() : previous(connection, command);
        var probe = new Probe();
        var pending = Fail(harness, new RespireServerException($"MOVED {Slot} 127.0.0.1:{harness.Target.Port}"), probe);

        var error = await Assert.That(async () => await pending.AsTask().WaitAsync(harness.Timeout)).Throws<RespireServerException>();
        await Assert.That(error!.Message).Contains("replay failed");
        await Assert.That(probe.Conversions).IsEqualTo(0);
        await Assert.That(harness.Target.ReceivedCommands.Count(command => command == "GET ready")).IsEqualTo(1);
    }

    [Test]
    public async Task ConversionFailureAfterRecoveryIsPublishedOnce()
    {
        await using var harness = await Harness.StartAsync();
        var probe = new Probe { Failure = new InvalidOperationException("convert failed") };
        var pending = Fail(harness, new RespireServerException($"ASK {Slot} 127.0.0.1:{harness.Target.Port}"), probe);

        var error = await Assert.That(async () => await pending.AsTask().WaitAsync(harness.Timeout)).Throws<InvalidOperationException>();
        await Assert.That(error).IsSameReferenceAs(probe.Failure);
        await Assert.That(probe.Conversions).IsEqualTo(1);
        // The recovered reply was converted once and never replayed again.
        await Assert.That(harness.Target.ReceivedCommands.Count(command => command == "GET ready")).IsEqualTo(1);
    }

    [Test]
    public async Task PendingRepliesUsePooledSourcesWithoutAllocating()
    {
        const int count = 256;
        await using var harness = await Harness.StartAsync();
        var replies = new TaskCompletionSource<string?>[count];
        var responses = new ValueTask<string?>[count];
        // Fill the pool and JIT every mode before the region opens.
        for (var mode = 0; mode < 3; mode++)
        {
            Prepare(replies);
            _ = Measure(harness, replies, responses, mode);
        }

        var result = AllocationMeasurement.WithoutConcurrentGc(() =>
        {
            Prepare(replies);
            var pooled = Measure(harness, replies, responses, 0);
            Prepare(replies);
            var control = Measure(harness, replies, responses, 1);
            Prepare(replies);
            var asyncFrames = Measure(harness, replies, responses, 2);
            return (pooled, control, asyncFrames);
        });

        // Positive controls: the measurement sees deliberate arrays and the async frames this source replaces.
        await Assert.That(result.control - result.pooled).IsGreaterThanOrEqualTo(37L * count);
        await Assert.That(result.asyncFrames).IsGreaterThan(0L);
#if !DEBUG
        await Assert.That(result.pooled).IsEqualTo(0L);
#endif
    }

    private static void Prepare(TaskCompletionSource<string?>[] replies)
    {
        for (var i = 0; i < replies.Length; i++) replies[i] = new();
    }

    // Measures only issuing the pending wrappers; completion and consumption follow outside the window.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long Measure(Harness harness, TaskCompletionSource<string?>[] replies, ValueTask<string?>[] responses, int mode)
    {
        var probe = new Probe();
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < replies.Length; i++)
        {
            var reply = new ValueTask<string?>(replies[i].Task);
            responses[i] = mode == 2 ? AwaitAsync(reply) : harness.Create(reply, probe);
            if (mode == 1) GC.KeepAlive(new byte[37]);
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        for (var i = 0; i < replies.Length; i++)
        {
            replies[i].SetResult("value");
            if (responses[i].GetAwaiter().GetResult() != "value")
                throw new InvalidOperationException("The pooled cluster reply returned the wrong value.");
        }
        return allocated;
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private static async ValueTask<string?> AwaitAsync(ValueTask<string?> reply) => await reply.ConfigureAwait(false);

    private static async ValueTask<string?> Fail(Harness harness, Exception error, Probe probe)
    {
        var reply = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = harness.Create(new(reply.Task), probe);
        reply.SetException(error);
        return await pending.ConfigureAwait(false);
    }

    private sealed class Probe
    {
        public int Conversions;
        public Exception? Failure;
    }

    private readonly struct ProbeSend(Probe probe) : RespireClient.IClusterReadySend<string?>
    {
        public bool TransferOwnership => false;

        public string? Convert(in RespValue response)
        {
            probe.Conversions++;
            if (probe.Failure is { } failure) throw failure;
            return ResponseReader.StringOrNull(in response);
        }

        public ValueTask<string?> Send<TCommand>(RespireConnection connection, string operation, in TCommand command,
            CancellationToken cancellationToken, RespireTelemetry.ErrorObservation observation,
            RespireTelemetry.OperationStart durationStarted)
            where TCommand : struct, IRespCommand
            => throw new NotSupportedException("Tests supply the first reply directly.");
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly CancellationTokenSource _timeout = new(TimeSpan.FromSeconds(10));
        private RespireClient _client = null!;
        private RespireConnection _connection = null!;

        public FakeRespServer Source { get; } = Primary();
        public FakeRespServer Target { get; } = Primary();
        public CancellationToken Timeout => _timeout.Token;

        public static async Task<Harness> StartAsync()
        {
            var harness = new Harness();
            harness._client = await RespireClient.ConnectAsync(new RespireOptions
            {
                Endpoints = [new("127.0.0.1", harness.Source.Port)], UseCluster = true,
                Protocol = RespireProtocol.Resp2, Connections = 1, ThreadPoolMonitoring = false,
            });
            harness._connection = harness._client.Core.Cluster!.TryAcquireReadyConnection(Slot, default)
                ?? throw new InvalidOperationException("The cluster slot has no ready connection.");
            return harness;
        }

        public void MoveSlotToTarget()
        {
            var topology = Slots(Target.Port);
            Source.ReplyOverride = (_, command) => command == "CLUSTER SLOTS" ? topology : null;
            var router = _client.Core.Cluster!;
            router.SetSlotOwner(Slot, router.GetMultiplexer(new("127.0.0.1", Target.Port)));
        }

        public ValueTask<string?> Create(ValueTask<string?> reply, Probe probe)
            => RespireClient.ClusterReadyResponse<Cmd1, string?, ProbeSend>.Create(_client, "GET", _client.Core.Cluster!,
                new Cmd1(Verbs.Get, "ready"), Timeout, new ProbeSend(probe), default, Slot, _connection, reply);

        public async ValueTask DisposeAsync()
        {
            await _client.DisposeAsync();
            await Source.DisposeAsync();
            await Target.DisposeAsync();
            _timeout.Dispose();
        }
    }

    private static FakeRespServer Primary()
    {
        var server = new FakeRespServer(8, FakeRespServer.OkReply);
        server.ReplyOverride = (_, command) => command switch
        {
            "CLUSTER SLOTS" => Slots(server.Port),
            "GET ready" => "$5\r\nvalue\r\n"u8.ToArray(),
            "PING" => FakeRespServer.PongReply,
            _ => null,
        };
        return server;
    }

    private static byte[] Slots(int port) => Encoding.ASCII.GetBytes(
        $"*1\r\n*3\r\n:0\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{port}\r\n");
}
