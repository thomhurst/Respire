using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Diagnostics;
using Respire.Commands;
using Respire.Internal;
using Respire.Protocol;

namespace Respire.Networking;

internal sealed partial class RespireConnection
{
    /// <summary>
    /// Executes one logical send without retrying a transport failure. The owner receives transport
    /// evidence separately from the original result or exception. Multi-reply frames are one unit.
    /// </summary>
    /// <remarks>
    /// An unwritten streamed request does not prove that a caller's source is replayable or that an
    /// asynchronous source read has ended. Write evidence never transfers payload ownership.
    /// Single-reply error translation follows <paramref name="throwOnError"/>; multi-reply owners
    /// retain the raw envelope, including transaction queue errors, as on the existing send path.
    /// </remarks>
#if NET
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
#endif
    internal async ValueTask<CommandAttemptResult> SendAttemptAsync<TCommand>(
        TCommand command, CancellationToken cancellationToken = default, string? commandName = null,
        CommandDeadline commandDeadline = default, int repliesBeforeFinal = 0, int firstQueueReply = 0,
        bool pinToConnection = true, DedicatedStreamRoute streamingRoute = default, bool throwOnError = true)
        where TCommand : struct, IRespCommand
    {
        var observation = new CommandWriteObservation();
        try
        {
            ArgumentOutOfRangeException.ThrowIfNegative(repliesBeforeFinal);
            if (repliesBeforeFinal >= _inflight.Capacity)
                throw new InvalidOperationException("The command frame needs more in-flight slots than this connection permits.");
            var pending = repliesBeforeFinal == 0
                ? SendCoreAsync(in command, discardRepliesBefore: 0, throwOnError, cancellationToken,
                    commandName, commandDeadline: commandDeadline, pinToConnection: pinToConnection,
                    streamingRoute: streamingRoute, writeObservation: observation)
                : SendMultiReplyCoreAsync(in command, repliesBeforeFinal, firstQueueReply,
                    cancellationToken, commandName ?? "(command)", commandDeadline: commandDeadline,
                    pinToConnection: pinToConnection, writeObservation: observation);
            var response = await pending.ConfigureAwait(false);
            return CaptureAttemptResult(observation, response);
        }
        catch (Exception error)
        {
            return CaptureAttemptResult(observation, failure: error);
        }
    }

    /// <summary>Observes an ASKING prelude and its streamed SET as one logical attempt.</summary>
#if NET
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
#endif
    internal async ValueTask<CommandAttemptResult> SendAskingStreamAttemptAsync(
        ProtocolCommand<RawCommand> asking, StreamedSetCommand command, CancellationToken cancellationToken,
        CommandDeadline commandDeadline, DedicatedStreamRoute streamingRoute)
    {
        var observation = new CommandWriteObservation();
        try
        {
            var response = await SendStreamedSetAsync(command, cancellationToken, commandDeadline,
                streamingRoute, asking, observation).ConfigureAwait(false);
            return CaptureAttemptResult(observation, response);
        }
        catch (Exception error)
        {
            return CaptureAttemptResult(observation, failure: error);
        }
    }

    private static CommandAttemptResult CaptureAttemptResult(CommandWriteObservation observation,
        RespValue response = default, Exception? failure = null)
        => new(response, observation.CaptureCompletedOutcome(),
            failure is null ? null : ExceptionDispatchInfo.Capture(failure));

    // One observation belongs to exactly one logical attempt and is never pooled or reset. It
    // stores immutable frame coordinates, not a borrowed PendingResponse or its short task token.
    // Thus a consumed, retired or recycled source cannot change another attempt's evidence.
    private sealed class CommandWriteObservation
    {
        private RespireConnection? _connection;
        private long _start;
        private long _end;
        private bool _multipleFrames;

        // Called under the connection's write gate, before the reply slot or flush is published.
        internal void RecordQueued(RespireConnection connection, long start, long end)
        {
            if (_connection is not null)
            {
                Debug.Assert(ReferenceEquals(_connection, connection), "A logical attempt is bound to one connection.");
                _multipleFrames = true;
                return;
            }
            _connection = connection;
            _start = start;
            _end = end;
        }

        // Only the logical owner calls this, after its send has completed. A cancellation can
        // leave the frame queued: an open connection must therefore remain ambiguous.
        internal CommandWriteOutcome CaptureCompletedOutcome()
        {
            if (_connection is null) return CommandWriteOutcome.DefinitelyUnwritten;
            // An ASKING upload spans independently scheduled frames. Keep the entire logical
            // attempt conservative instead of attributing only its first frame.
            if (_multipleFrames) return CommandWriteOutcome.EffectMayHaveReachedServer;
            return _connection.CaptureWriteOutcome(_start, _end);
        }
    }

    private CommandWriteOutcome CaptureWriteOutcome(long start, long end)
    {
        lock (_writeGate)
        {
            var claimed = _flushProgress.ClaimedWriteEnd;
            if (start < 0 || end <= start || claimed < 0)
                return CommandWriteOutcome.Unknown;
            // Abort and sender selection share this gate. An unselected active buffer cannot
            // be sent after Abort. A selected buffer may already have entered an ambiguous
            // socket/TLS write, even when its successful-byte counter has not advanced.
            return _dead && claimed <= start
                ? CommandWriteOutcome.DefinitelyUnwritten
                : CommandWriteOutcome.EffectMayHaveReachedServer;
        }
    }
}
