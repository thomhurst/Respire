using StackExchange.Redis;

namespace Respire.StackExchangeCompat;

internal sealed class CompatBatch(CompatDatabase database) : CompatDatabaseAsync(database), IBatch
{
    private readonly object _gate = new();
    private readonly List<IQueuedCommand> _pending = [];

    protected override Task<T> Send<T>(RespireCommand command, RedisValue[] arguments, CommandFlags flags, Func<RedisResult, T> convert)
    {
        if (command.Name is not ("HSET" or "HSETNX" or "HDEL" or "PEXPIRE" or "PEXPIREAT" or "PERSIST"))
            throw Compatibility.Unsupported($"IBatch {command.Name}");
        if ((flags & ~CommandFlags.DemandMaster) != 0) throw Compatibility.Unsupported($"IBatch CommandFlags {flags}");
        var queued = new QueuedCommand<T>(command, arguments, convert, DatabaseOwner.Owner.QueuedShutdown);
        lock (_gate) _pending.Add(queued);
        return queued.Task;
    }

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
            foreach (var command in commands) command.Enqueue(batch);
            await batch.TryExecuteAsync(cancellationToken).ConfigureAwait(false);
            foreach (var command in commands) command.Complete();
        }
        catch (Exception error) { foreach (var command in commands) command.Fail(error); }
        return true;
    }

    private interface IQueuedCommand
    {
        void Enqueue(RespireBatch batch);
        void Complete();
        void Fail(Exception error);
    }

    private sealed class QueuedCommand<T> : IQueuedCommand
    {
        private readonly RespireCommand _command;
        private readonly RedisValue[] _arguments;
        private readonly Func<RedisResult, T> _convert;
        private readonly TaskCompletionSource<T> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationTokenRegistration _registration;
        private RespirePending<RespireResult>? _pending;

        internal QueuedCommand(RespireCommand command, RedisValue[] arguments, Func<RedisResult, T> convert, CancellationToken shutdown)
        {
            _command = command;
            _arguments = arguments;
            _convert = convert;
            _registration = shutdown.Register(() => _completion.TrySetCanceled(shutdown));
        }

        internal Task<T> Task => _completion.Task;

        public void Enqueue(RespireBatch batch)
        {
            _registration.Dispose();
            if (!_completion.Task.IsCompleted)
                _pending = batch.Execute(_command, _arguments.Select(static argument => argument.ToRespireValue()).ToArray());
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
        }

        public void Fail(Exception error)
        {
            _registration.Dispose();
            if (error is OperationCanceledException canceled) _completion.TrySetCanceled(canceled.CancellationToken);
            else _completion.TrySetException(error);
        }
    }
}
