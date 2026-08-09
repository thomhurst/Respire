using Respire.Commands;
using Respire.Internal;
using Respire.Networking;
using Respire.Protocol;

namespace Respire;

/// <summary>
/// An explicit pipeline: queue commands, then <see cref="SendAsync"/> flushes them to one
/// connection together and completes every queued <see cref="RespirePending{T}"/>. Not atomic —
/// use <see cref="RespireTransaction"/> for MULTI/EXEC semantics. Single-shot and not
/// thread-safe: build, send once, discard.
/// </summary>
public sealed class RespireBatch
{
    private readonly RespireClient _client;
    private readonly List<Op> _ops = [];
    private bool _sent;

    internal RespireBatch(RespireClient client) => _client = client;

    public int Count => _ops.Count;

    public RespirePending<string?> GetStringAsync(RespireKey key)
        => Add<Cmd1, string?>("GET", new Cmd1(Verbs.Get, _client.Key(in key)), static (c, v) => ResponseReader.StringOrNull(in v));

    public RespirePending<T?> GetAsync<T>(RespireKey key)
        => Add<Cmd1, T?>("GET", new Cmd1(Verbs.Get, _client.Key(in key)), static (c, v) => c.DeserializeBorrowed<T>(in v));

    public RespirePending<byte[]?> GetBytesAsync(RespireKey key)
        => Add<Cmd1, byte[]?>("GET", new Cmd1(Verbs.Get, _client.Key(in key)), static (c, v) => ResponseReader.BytesOrNull(in v));

    public RespirePending<bool> SetAsync(
        RespireKey key, RespireValue value, TimeSpan? expiry = null, SetWhen when = SetWhen.Always, bool keepTtl = false)
        => Add<SetCommand, bool>(
            "SET", new SetCommand(_client.Key(in key), value, expiry, when, keepTtl, returnOld: false),
            static (c, v) => ResponseReader.OkOrNull(in v));

    public RespirePending<bool> SetAsync<T>(
        RespireKey key, T value, TimeSpan? expiry = null, SetWhen when = SetWhen.Always, bool keepTtl = false)
        => Add<SetCommand, bool>(
            "SET", new SetCommand(_client.Key(in key), _client.Serialize(value), expiry, when, keepTtl, returnOld: false),
            static (c, v) => ResponseReader.OkOrNull(in v));

    public RespirePending<long> DeleteAsync(RespireKey key)
        => Add<CmdN, long>("DEL", new CmdN(Verbs.Del, [_client.Key(in key)]), static (c, v) => ResponseReader.Integer(in v));

    public RespirePending<bool> ExistsAsync(RespireKey key)
        => Add<Cmd1, bool>("EXISTS", new Cmd1(Verbs.Exists, _client.Key(in key)), static (c, v) => ResponseReader.Flag(in v));

    public RespirePending<long> IncrementAsync(RespireKey key, long by = 1)
        => Add<IncrementCommand, long>(
            by == 1 ? "INCR" : "INCRBY", new IncrementCommand(Verbs.Incr, Verbs.IncrBy, _client.Key(in key), by),
            static (c, v) => ResponseReader.Integer(in v));

    public RespirePending<long> DecrementAsync(RespireKey key, long by = 1)
        => Add<IncrementCommand, long>(
            by == 1 ? "DECR" : "DECRBY", new IncrementCommand(Verbs.Decr, Verbs.DecrBy, _client.Key(in key), by),
            static (c, v) => ResponseReader.Integer(in v));

    public RespirePending<bool> ExpireAsync(RespireKey key, TimeSpan expiry)
        => Add<Cmd2, bool>(
            "PEXPIRE", new Cmd2(Verbs.PExpire, _client.Key(in key), (long)expiry.TotalMilliseconds),
            static (c, v) => ResponseReader.Flag(in v));

    public RespirePending<bool> HashSetAsync(RespireKey key, string field, RespireValue value)
        => Add<Cmd3, bool>("HSET", new Cmd3(Verbs.HSet, _client.Key(in key), field, value), static (c, v) => ResponseReader.Flag(in v));

    public RespirePending<string?> HashGetAsync(RespireKey key, string field)
        => Add<Cmd2, string?>("HGET", new Cmd2(Verbs.HGet, _client.Key(in key), field), static (c, v) => ResponseReader.StringOrNull(in v));

    public RespirePending<long> ListLeftPushAsync(RespireKey key, RespireValue value)
        => Add<Cmd2, long>("LPUSH", new Cmd2(Verbs.LPush, _client.Key(in key), value), static (c, v) => ResponseReader.Integer(in v));

    public RespirePending<long> ListRightPushAsync(RespireKey key, RespireValue value)
        => Add<Cmd2, long>("RPUSH", new Cmd2(Verbs.RPush, _client.Key(in key), value), static (c, v) => ResponseReader.Integer(in v));

    public RespirePending<bool> SetAddAsync(RespireKey key, RespireValue member)
        => Add<Cmd2, bool>("SADD", new Cmd2(Verbs.SAdd, _client.Key(in key), member), static (c, v) => ResponseReader.Flag(in v));

    public RespirePending<bool> SortedSetAddAsync(RespireKey key, RespireValue member, double score)
        => Add<Cmd3, bool>("ZADD", new Cmd3(Verbs.ZAdd, _client.Key(in key), score, member), static (c, v) => ResponseReader.Flag(in v));

    /// <summary>
    /// Sends every queued command in one flush and completes all pendings. Per-command failures
    /// (server errors, <see cref="RespireOptions.CommandTimeout"/> expiry) fault that command's
    /// pending, not this call; failing to obtain a connection at all faults every pending and
    /// rethrows.
    /// In cluster mode, commands are grouped by slot and each group shares one connection so its
    /// commands retain queue order. Different slot groups may run out of order, and an acquisition
    /// failure faults only its group; this method completes normally after recording the first
    /// error in telemetry.
    /// </summary>
    public async ValueTask SendAsync(CancellationToken cancellationToken = default)
    {
        if (_sent)
        {
            throw new InvalidOperationException("This batch has already been sent.");
        }

        _sent = true;
        var core = _client.Core;
        var telemetry = RespireTelemetry.StartBatchOperation(
            "PIPELINE",
            _ops,
            static op => op.Operation,
            core.Multiplexer.Host,
            core.Multiplexer.Port,
            core.Options.Database,
            out var telemetryOperation);
        if (_ops.Count == 0)
        {
            telemetry.Complete(core, telemetryOperation, batchSize: 0);
            return;
        }

        if (core.Cluster is not null)
        {
            var groups = new List<(int? Slot, List<Op> Operations)>();
            var groupIndexes = new Dictionary<int, int>();
            foreach (var op in _ops)
            {
                var groupKey = op.TryGetClusterSlot(out var slot) ? slot + 1 : 0;
                if (!groupIndexes.TryGetValue(groupKey, out var groupIndex))
                {
                    groupIndex = groups.Count;
                    groupIndexes.Add(groupKey, groupIndex);
                    groups.Add((groupKey == 0 ? null : slot, []));
                }

                groups[groupIndex].Operations.Add(op);
            }

            var clusterTasks = new Task<Exception?>[groups.Count];
            for (var i = 0; i < groups.Count; i++)
            {
                clusterTasks[i] = RunClusterGroupAsync(
                    groups[i].Slot, groups[i].Operations, cancellationToken);
            }

            var clusterErrors = await Task.WhenAll(clusterTasks).ConfigureAwait(false);
            telemetry.Complete(
                core,
                telemetryOperation,
                error: clusterErrors.FirstOrDefault(static error => error is not null),
                batchSize: _ops.Count == 1 ? null : _ops.Count);
            return;
        }

        RespireConnection? connection = null;
        try
        {
            connection = await _client.AcquireConnectionAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The batch is single-shot: without a connection nothing ran, so every pending
            // must observe the acquisition failure rather than stay unreadable forever.
            foreach (var op in _ops)
            {
                op.Fail(ex);
            }

            telemetry.Complete(
                core,
                telemetryOperation,
                error: ex,
                batchSize: _ops.Count == 1 ? null : _ops.Count);
            throw;
        }

        var timeout = _client.Core.Options.CommandTimeout;
        using var timeoutSource = timeout is null
            ? null
            : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource?.CancelAfter(timeout!.Value);
        var effectiveToken = timeoutSource?.Token ?? cancellationToken;

        var tasks = new Task<Exception?>[_ops.Count];
        for (var i = 0; i < _ops.Count; i++)
        {
            tasks[i] = _ops[i].RunAsync(_client, connection, effectiveToken, cancellationToken, timeout);
        }

        var errors = await Task.WhenAll(tasks).ConfigureAwait(false);
        telemetry.Complete(
            core,
            telemetryOperation,
            error: errors.FirstOrDefault(static error => error is not null),
            connection: connection,
            batchSize: _ops.Count == 1 ? null : _ops.Count);
    }

    private async Task<Exception?> RunClusterGroupAsync(
        int? slot,
        List<Op> operations,
        CancellationToken cancellationToken)
    {
        RespireConnection connection;
        try
        {
            connection = await _client.AcquireConnectionAsync(slot, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            foreach (var operation in operations)
            {
                operation.Fail(ex);
            }

            return ex;
        }

        var sends = new ValueTask<RespValue>[operations.Count];
        // Start every send in queue order before awaiting responses to retain pipelining.
        for (var i = 0; i < operations.Count; i++)
        {
            try
            {
                sends[i] = operations[i].StartClusterSend(_client, connection, cancellationToken);
            }
            catch (Exception ex)
            {
                sends[i] = ValueTask.FromException<RespValue>(ex);
            }
        }

        Exception? firstError = null;
        for (var i = 0; i < operations.Count; i++)
        {
            var error = await operations[i].CompleteClusterSendAsync(
                    _client, sends[i], cancellationToken)
                .ConfigureAwait(false);
            firstError ??= error;
        }

        return firstError;
    }

    private RespirePending<T> Add<TCommand, T>(string operation, in TCommand command, Func<RespireClient, RespValue, T> convert)
        where TCommand : struct, IRespCommand
    {
        if (_sent)
        {
            throw new InvalidOperationException("This batch has already been sent.");
        }

        var pending = new RespirePending<T>();
        _ops.Add(new Op<TCommand, T>(operation, command, pending, convert));
        return pending;
    }

    private abstract class Op
    {
        protected Op(string operation) => Operation = operation;

        public string Operation { get; }

        public abstract Task<Exception?> RunAsync(
            RespireClient client, RespireConnection connection, CancellationToken effectiveToken,
            CancellationToken callerToken, TimeSpan? timeout);

        public abstract bool TryGetClusterSlot(out int slot);

        public abstract ValueTask<RespValue> StartClusterSend(
            RespireClient client,
            RespireConnection connection,
            CancellationToken cancellationToken);

        public abstract Task<Exception?> CompleteClusterSendAsync(
            RespireClient client,
            ValueTask<RespValue> send,
            CancellationToken cancellationToken);

        public abstract void Fail(Exception error);
    }

    private sealed class Op<TCommand, T>(
        string operation, TCommand command, RespirePending<T> pending, Func<RespireClient, RespValue, T> convert) : Op(operation)
        where TCommand : struct, IRespCommand
    {
        public override void Fail(Exception error) => pending.Fail(error);

        public override bool TryGetClusterSlot(out int slot) => command.TryGetClusterSlot(out slot);

        public override ValueTask<RespValue> StartClusterSend(
            RespireClient client,
            RespireConnection connection,
            CancellationToken cancellationToken)
            => client.SendOnConnectionAsync(Operation, connection, command, cancellationToken);

        public override async Task<Exception?> CompleteClusterSendAsync(
            RespireClient client,
            ValueTask<RespValue> send,
            CancellationToken cancellationToken)
        {
            try
            {
                RespValue value;
                try
                {
                    value = await send.ConfigureAwait(false);
                }
                catch (RespireServerException error) when (ClusterRouter.IsRedirect(error))
                {
                    value = await client.SendAsync(Operation, command, cancellationToken).ConfigureAwait(false);
                }

                return Complete(client, value);
            }
            catch (Exception ex)
            {
                pending.Fail(ex);
                return ex;
            }
        }

        public override async Task<Exception?> RunAsync(
            RespireClient client, RespireConnection connection, CancellationToken effectiveToken,
            CancellationToken callerToken, TimeSpan? timeout)
        {
            try
            {
                var value = await connection.SendAsync(in command, effectiveToken).ConfigureAwait(false);
                return Complete(client, value);
            }
            catch (OperationCanceledException) when (timeout is { } expired && !callerToken.IsCancellationRequested)
            {
                var error = new RespireTimeoutException(Operation, expired);
                pending.Fail(error);
                return error;
            }
            catch (Exception ex)
            {
                pending.Fail(ex);
                return ex;
            }
        }

        private Exception? Complete(RespireClient client, RespValue value)
        {
            try
            {
                if (value.IsError)
                {
                    var error = ResponseReader.ServerError(in value);
                    pending.Fail(error);
                    return error;
                }

                try
                {
                    pending.Succeed(convert(client, value));
                    return null;
                }
                catch (Exception ex)
                {
                    // Conversion failed after Redis completed successfully; not a DB error.
                    pending.Fail(ex);
                    return ex;
                }
            }
            finally
            {
                value.Dispose();
            }
        }
    }
}
