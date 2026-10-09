using System.Runtime.CompilerServices;
using StackExchange.Redis;
#if !NET9_0_OR_GREATER
using Lock = System.Object;
#endif

namespace Respire.StackExchangeCompat;

internal sealed class CompatBatch(CompatDatabase database) : CompatDatabaseAsync(database), IBatch
{
    private readonly Lock _gate = new();
    private readonly List<IQueuedCommand> _pending = [];

    protected override Task<T> Send<T>(RespireCommand command, RedisValue[] arguments, CommandFlags flags, Func<RedisResult, T> convert)
    {
        if (!SupportsCommand(command))
            throw Compatibility.Unsupported($"IBatch {command.Name}");
        if ((flags & ~CommandFlags.DemandMaster) != 0) throw Compatibility.Unsupported($"IBatch CommandFlags {flags}");
        var queued = new QueuedCommand<T>(command, arguments, convert, DatabaseOwner.Owner.QueuedShutdown);
        lock (_gate) _pending.Add(queued);
        return queued.Task;
    }

    internal static bool SupportsCommand(RespireCommand command) => command.Name is
        "HGET" or "HMGET" or "HGETALL" or "HLEN" or "HSET" or "HSETNX" or "HDEL"
        or "LLEN" or "LINDEX" or "LRANGE" or "LPUSH" or "LPUSHX" or "RPUSH" or "RPUSHX"
        or "LREM" or "LTRIM" or "RPOPLPUSH" or "PEXPIRE" or "PEXPIREAT" or "PERSIST"
        or "EXISTS" or "PTTL" or "GET" or "MGET" or "INCR" or "DECR" or "INCRBY" or "DECRBY"
        or "SADD" or "SREM" or "SMEMBERS" or "SCARD" or "ZADD" or "ZREM" or "ZCARD" or "ZCOUNT"
        or "ZRANGE" or "ZREVRANGE" or "ZRANGEBYSCORE" or "ZREVRANGEBYSCORE" or "ZSCAN";

    public void Execute()
    {
        lock (_gate)
        {
            if (_pending.Count == 0) return;
            var commands = _pending.ToArray();
            _pending.Clear();
            try { _ = DatabaseOwner.Owner.Run(token => ExecuteAsync(commands, token)); }
            catch (Exception error) { foreach (var command in commands) command.Fail(error); }
        }
    }

    private async Task<bool> ExecuteAsync(IQueuedCommand[] commands, CancellationToken cancellationToken)
    {
        try
        {
            using var batch = DatabaseOwner.CreateNativeBatch();
            foreach (var command in commands)
            {
                try { command.Enqueue(batch); }
                catch (Exception error) { command.Fail(error); }
            }
            await batch.TryExecuteAsync(cancellationToken).ConfigureAwait(false);
            foreach (var command in commands) command.Complete();
        }
        catch (Exception error) { foreach (var command in commands) command.Fail(error); }
        return true;
    }

    internal interface IQueuedCommand
    {
        void Admit();
        void Enqueue(IRespireCommandQueue batch);
        void Complete();
        void Fail(Exception error);
        void Abort();
    }

    internal sealed class QueuedCommand<T> : IQueuedCommand
    {
        private static readonly ConditionalWeakTable<Task<T>, QueuedCommand<T>> RetainedTasks = new();
        private readonly RespireCommand _command;
        private RedisValue[] _arguments;
        private readonly Func<RedisResult, T> _convert;
        private readonly TaskCompletionSource<T> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationTokenRegistration _registration;
        private RespirePending<RespireResult>? _pending;

        internal QueuedCommand(RespireCommand command, RedisValue[] arguments, Func<RedisResult, T> convert, CancellationToken shutdown)
        {
            _command = command;
            _arguments = arguments;
            _convert = convert;
            // A retained task keeps its command alive, but the connection must not root an abandoned batch's buffers.
            RetainedTasks.Add(_completion.Task, this);
            _registration = shutdown.Register(static (state, token) =>
            {
                if (((WeakReference<QueuedCommand<T>>)state!).TryGetTarget(out var queued))
                {
                    queued._arguments = [];
                    queued._completion.TrySetCanceled(token);
                    RetainedTasks.Remove(queued._completion.Task);
                }
            }, new WeakReference<QueuedCommand<T>>(this));
        }

        internal Task<T> Task => _completion.Task;

        public void Admit()
        {
            _registration.Dispose();
            RetainedTasks.Remove(_completion.Task);
        }

        public void Enqueue(IRespireCommandQueue batch)
        {
            Admit();
            if (!_completion.Task.IsCompleted)
                _pending = batch.Execute(_command, _arguments.Select(static argument => argument.ToRespireValue()).ToArray());
            _arguments = [];
        }

        public void Complete()
        {
            if (_pending is null) return;
            try
            {
                using var result = _pending.Result;
                _completion.TrySetResult(_convert(result.ToStackExchangeResult()));
            }
            catch (Exception error) { Fail(error); }
            finally { _pending = null; }
        }

        public void Fail(Exception error)
        {
            _registration.Dispose();
            RetainedTasks.Remove(_completion.Task);
            _arguments = [];
            _pending = null;
            if (error is OperationCanceledException canceled) _completion.TrySetCanceled(canceled.CancellationToken);
            else _completion.TrySetException(error);
        }

        public void Abort() => Fail(new OperationCanceledException("The Redis transaction was aborted."));
    }
}
