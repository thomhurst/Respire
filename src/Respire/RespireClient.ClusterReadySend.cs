using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks.Sources;
using Reservoir;
using Respire.Internal;
using Respire.Networking;
using Respire.Protocol;

namespace Respire;

public sealed partial class RespireClient
{
    private bool TryGetDirectReplyCluster<TCommand>(in TCommand command,
        [NotNullWhen(true)] out ClusterRouter? cluster) where TCommand : struct, IRespCommand
    {
        cluster = _core.Cluster;
        // Cache fences and server-local cursors keep the normal route. StringOrNullAsync
        // lacks an outer streaming exclusion, so this shared cluster guard must retain it.
        // Opt-in circuits use the response path: endpoint health completes before caller
        // conversion, and every redirect or retired generation reacquires admission.
        return cluster is not null && _core.Circuits is null && _core.ClientCache is null
            && command.ReadKind != ReadCommandKind.CursorRead && command is not IStreamingRespCommand;
    }

    private ValueTask<TResult> SendOnReadyClusterAsync<TCommand, TResult, TSend>(
        string operation, ClusterRouter cluster, TCommand command, CancellationToken cancellationToken, TSend sender,
        RespireTelemetry.ErrorObservation observation)
        where TCommand : struct, IRespCommand
        where TSend : struct, IClusterReadySend<TResult>
    {
        var slot = command.TryGetClusterSlot(out var commandSlot) ? commandSlot : (int?)null;
        RespireConnection? connection;
        // Acquisition throws only for cancellation or disposal, and both persist. The async
        // path acquires again, then observes and publishes that failure exactly as before.
        try { connection = cluster.TryAcquireReadyConnection(slot, cancellationToken); }
        catch (Exception) { connection = null; }
        if (connection is null)
            return SendOnReadyClusterSlowAsync<TCommand, TResult, TSend>(operation, cluster, command, cancellationToken, sender, observation);

        ValueTask<TResult> response;
        try
        {
            response = sender.Send(connection, operation, in command, cancellationToken, observation,
                RespireTelemetry.CaptureOperationStart(operation));
        }
        catch (Exception error)
        {
            // Synchronous enqueue failures take the same recovery checks as reply failures.
            response = ValueTask.FromException<TResult>(error);
        }
        // MOVED, ASK, READONLY and retirement arrive with the reply, so this method cannot hand the
        // sender's ValueTask straight back. A pooled source observes it without an async state machine box.
        return ClusterReadyResponse<TCommand, TResult, TSend>.Create(
            this, operation, cluster, command, cancellationToken, sender, observation, slot, connection, response);
    }

    // Cold routing and acquisition failures. Ready sends use ClusterReadyResponse, which mirrors the
    // recovery below; keep the two in step.
#if NET
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
#endif
    private async ValueTask<TResult> SendOnReadyClusterSlowAsync<TCommand, TResult, TSend>(
        string operation, ClusterRouter cluster, TCommand command, CancellationToken cancellationToken, TSend sender,
        RespireTelemetry.ErrorObservation observation)
        where TCommand : struct, IRespCommand
        where TSend : struct, IClusterReadySend<TResult>
    {
        try
        {
            var slot = command.TryGetClusterSlot(out var commandSlot) ? commandSlot : (int?)null;
            var connection = cluster.TryAcquireReadyConnection(slot, cancellationToken);
            RespValue response;
            if (connection is null)
            {
                // Discovery and connection initialization borrow the same conversion owner.
                response = await SendCoreAsync(operation, command, cancellationToken, RespireCommandFlags.None,
                    allowReadFrom: true, cursorAffinity: null, observation: observation, observeErrors: false).ConfigureAwait(false);
            }
            else
            {
                try
                {
                    return await sender.Send(connection, operation, in command, cancellationToken, observation,
                        RespireTelemetry.CaptureOperationStart(operation)).ConfigureAwait(false);
                }
                catch (ClusterConverterException error)
                {
                    // A caller converter can throw a Redis-shaped exception after success.
                    // It must never be mistaken for a rejected command and replayed.
                    ExceptionDispatchInfo.Capture(error.InnerException!).Throw();
                    throw;
                }
                catch (RespireConnectionRetiredException error) when (cluster.CanRetryRetirement(0, cancellationToken))
                {
                    response = await ResumeRetiredClusterSendAsync(operation, command, connection, error,
                        RespireReadFrom.Primary, cancellationToken, observation: observation).ConfigureAwait(false);
                }
                catch (RespireServerException error) when (ClusterRouter.CanRecover(error, slot))
                {
                    response = await ResumeRejectedClusterSendAsync(operation, command, connection, error,
                        RespireReadFrom.Primary, cancellationToken, observation: observation).ConfigureAwait(false);
                }
            }

            var transferred = false;
            try
            {
                var result = sender.Convert(in response);
                transferred = sender.TransferOwnership;
                return result;
            }
            finally
            {
                if (!transferred) response.Dispose();
            }
        }
        catch (Exception error)
        {
            observation.Final(error);
            throw;
        }
    }

    /// <summary>
    /// Observes one ready cluster reply and replays it after a redirect or retirement, as the
    /// async slow path does. Pooled per command/result/sender triple; recycled after GetResult.
    /// </summary>
    private sealed class ClusterReadyResponse<TCommand, TResult, TSend> : IValueTaskSource<TResult>
        where TCommand : struct, IRespCommand
        where TSend : struct, IClusterReadySend<TResult>
    {
        private static readonly ObjectPool<ClusterReadyResponse<TCommand, TResult, TSend>, PoolPolicy> Pool = new(4096);

        private ManualResetValueTaskSourceCore<TResult> _core = new() { RunContinuationsAsynchronously = false };
        private readonly Action _complete;
        private RespireClient? _client;
        private string? _operation;
        private ClusterRouter? _cluster;
        private TCommand _command;
        private TSend _sender;
        private CancellationToken _cancellationToken;
        private RespireTelemetry.ErrorObservation _observation;
        private int? _slot;
        private RespireConnection? _connection;
        private ExecutionContext? _context;
        private ValueTask<TResult> _response;
        private ValueTask<RespValue> _recovery;
        private bool _recovering;

        private ClusterReadyResponse() => _complete = Complete;

        internal static ValueTask<TResult> Create(RespireClient client, string operation, ClusterRouter cluster,
            in TCommand command, CancellationToken cancellationToken, TSend sender,
            RespireTelemetry.ErrorObservation observation, int? slot, RespireConnection connection, ValueTask<TResult> response)
        {
            var source = Pool.Rent();
            source._client = client;
            source._operation = operation;
            source._cluster = cluster;
            source._command = command;
            source._sender = sender;
            source._cancellationToken = cancellationToken;
            source._observation = observation;
            source._slot = slot;
            source._connection = connection;
            // Recovery starts from a reply continuation. Run it in the caller's flow, as an awaiting
            // async method would. Capture returns the current context without allocating.
            source._context = ExecutionContext.Capture();
            // Take the token first: an inline completion can publish before Create returns.
            var pending = new ValueTask<TResult>(source, source._core.Version);
            source.Observe(response);
            return pending;
        }

        private void Observe(ValueTask<TResult> response)
        {
            _response = response;
            if (response.IsCompleted) Consume();
            else response.ConfigureAwait(false).GetAwaiter().UnsafeOnCompleted(_complete);
        }

        private void Recover(ValueTask<RespValue> recovery)
        {
            _recovery = recovery;
            _recovering = true;
            if (recovery.IsCompleted) Consume();
            else recovery.ConfigureAwait(false).GetAwaiter().UnsafeOnCompleted(_complete);
        }

        private void Complete()
        {
            if (_context is { } context)
                ExecutionContext.Run(context, static state => ((ClusterReadyResponse<TCommand, TResult, TSend>)state!).Consume(), this);
            else Consume();
        }

        private void Consume()
        {
            TResult result = default!;
            Exception? failure = null;
            ValueTask<RespValue> recovery = default;
            var recover = false;
            try
            {
                if (_recovering) result = ConvertRecovery();
                else
                {
                    var response = _response;
                    _response = default;
                    try { result = response.GetAwaiter().GetResult(); }
                    catch (ClusterConverterException error)
                    {
                        // A caller converter can throw a Redis-shaped exception after success.
                        // It must never be mistaken for a rejected command and replayed.
                        failure = error.InnerException!;
                    }
                    catch (RespireConnectionRetiredException error) when (_cluster!.CanRetryRetirement(0, _cancellationToken))
                    {
                        recovery = _client!.ResumeRetiredClusterSendAsync(_operation!, _command, _connection!, error,
                            RespireReadFrom.Primary, _cancellationToken, observation: _observation);
                        recover = true;
                    }
                    catch (RespireServerException error) when (ClusterRouter.CanRecover(error, _slot))
                    {
                        recovery = _client!.ResumeRejectedClusterSendAsync(_operation!, _command, _connection!, error,
                            RespireReadFrom.Primary, _cancellationToken, observation: _observation);
                        recover = true;
                    }
                }
            }
            catch (Exception error) { failure = error; }

            if (recover) Recover(recovery);
            else Publish(result, failure);
        }

        private TResult ConvertRecovery()
        {
            var recovery = _recovery;
            _recovery = default;
            _recovering = false;
            var response = recovery.GetAwaiter().GetResult();
            var transferred = false;
            try
            {
                var result = _sender.Convert(in response);
                transferred = _sender.TransferOwnership;
                return result;
            }
            finally
            {
                if (!transferred) response.Dispose();
            }
        }

        private void Publish(TResult result, Exception? failure)
        {
            if (failure is not null) _observation.Final(failure);
            // Publishing can run the caller inline, which can recycle and re-rent this instance.
            // Release every reference first, and never catch a caller's exception here.
            _client = null;
            _operation = null;
            _cluster = null;
            _command = default;
            _sender = default;
            _cancellationToken = default;
            _observation = default;
            _slot = null;
            _connection = null;
            _context = null;
            // Failures can originate in a caller's cancellation callback, so dispatch them as
            // PooledResponseSource does. Reset preserves this property across pooled reuse.
            _core.RunContinuationsAsynchronously = failure is not null;
            if (failure is null) _core.SetResult(result);
            else _core.SetException(failure);
        }

        TResult IValueTaskSource<TResult>.GetResult(short token)
        {
            if (_core.GetStatus(token) == ValueTaskSourceStatus.Pending)
                throw new InvalidOperationException("The cluster response has not completed.");
            try { return _core.GetResult(token); }
            finally { Pool.Return(this); }
        }

        ValueTaskSourceStatus IValueTaskSource<TResult>.GetStatus(short token) => _core.GetStatus(token);

        void IValueTaskSource<TResult>.OnCompleted(Action<object?> continuation, object? state, short token,
            ValueTaskSourceOnCompletedFlags flags) => _core.OnCompleted(continuation, state, token, flags);

        private readonly struct PoolPolicy : IPooledObjectPolicy<ClusterReadyResponse<TCommand, TResult, TSend>>
        {
            public ClusterReadyResponse<TCommand, TResult, TSend> Create() => new();
            public bool TryReset(ClusterReadyResponse<TCommand, TResult, TSend> source)
            {
                source._core.Reset();
                return true;
            }
        }
    }

    private interface IClusterReadySend<TResult>
    {
        ValueTask<TResult> Send<TCommand>(RespireConnection connection, string operation,
            in TCommand command, CancellationToken cancellationToken, RespireTelemetry.ErrorObservation observation,
            RespireTelemetry.OperationStart durationStarted)
            where TCommand : struct, IRespCommand;
        TResult Convert(in RespValue response);
        bool TransferOwnership { get; }
    }

    // Only this variant invokes caller code; built-in string/bytes senders need no converter exception wrapper.
    private readonly struct ClusterConvertedReadySend<TState, TResult>(
        TState state, ResponseConverter<TState, TResult> converter, bool transferOwnership) : IClusterReadySend<TResult>
    {
        public bool TransferOwnership => transferOwnership;
        public TResult Convert(in RespValue response) => converter(state, in response);

        public ValueTask<TResult> Send<TCommand>(RespireConnection connection, string operation,
            in TCommand command, CancellationToken cancellationToken, RespireTelemetry.ErrorObservation observation,
            RespireTelemetry.OperationStart durationStarted)
            where TCommand : struct, IRespCommand
            => connection.SendConvertedAsync(in command, this,
                static (ClusterConvertedReadySend<TState, TResult> sender, in RespValue response) =>
                {
                    try { return sender.Convert(in response); }
                    catch (Exception error) { throw new ClusterConverterException(error); }
                }, transferOwnership, cancellationToken, operation, observation: observation, durationStarted: durationStarted);
    }

    private sealed class ClusterConverterException(Exception error)
        : Exception("The command succeeded, but its result converter failed.", error);
}
