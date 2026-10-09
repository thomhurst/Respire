using StackExchange.Redis;
#if !NET9_0_OR_GREATER
using Lock = System.Object;
#endif

// These waits implement ITransaction's synchronous Execute contract.
#pragma warning disable SER308

namespace Respire.StackExchangeCompat;

internal sealed class CompatTransaction(CompatDatabase database) : CompatDatabaseAsync(database), ITransaction
{
    private readonly Lock _gate = new();
    private readonly List<CompatBatch.IQueuedCommand> _commands = [];
    private readonly List<CompatCondition> _conditions = [];
    private Task<bool> _previous = Task.FromResult(true);
    private volatile bool _wasWatchConflict;
    private long _executionId;
    public bool WasWatchConflict => _wasWatchConflict;

    protected override Task<T> Send<T>(RespireCommand command, RedisValue[] arguments, CommandFlags flags, Func<RedisResult, T> convert)
    {
        ValidateFlags(flags);
        if ((!CompatBatch.SupportsCommand(command) && command.Name is not ("DEL" or "PUBLISH")) || command.Name == "ZSCAN")
            throw Compatibility.Unsupported($"ITransaction {command.Name}");
        var queued = new CompatBatch.QueuedCommand<T>(command, arguments, convert, DatabaseOwner.Owner.QueuedShutdown);
        lock (_gate) _commands.Add(queued);
        return queued.Task;
    }

    public ConditionResult AddCondition(Condition condition)
    {
        ArgumentNullException.ThrowIfNull(condition);
        var snapshot = CompatCondition.Create(condition);
        lock (_gate) _conditions.Add(snapshot);
        return snapshot.Result;
    }

    // IBatch.Execute has no result. Preserve its upstream fire-and-forget behavior without accepting
    // FireAndForget on the result-bearing overloads, where it would hide an abort or server error.
    void IBatch.Execute() => _ = ExecuteAsync(CommandFlags.None);
    public bool Execute(CommandFlags flags) => Wait(ExecuteAsync(flags));

    public Task<bool> ExecuteAsync(CommandFlags flags)
    {
        ValidateFlags(flags);
        lock (_gate)
        {
            var commands = _commands.ToArray();
            var conditions = _conditions.ToArray();
            _commands.Clear();
            _conditions.Clear();
            _wasWatchConflict = false;
            var executionId = ++_executionId;
            try
            {
                // Close(true) drains admitted executions, including their WATCH/read phase.
                // Transfer task lifetime before the first asynchronous setup or prior-execution wait.
                foreach (var command in commands) command.Admit();
                var previous = _previous;
                return _previous = DatabaseOwner.Owner.Run(token => ExecuteCoreAsync(previous, executionId, commands, conditions, token));
            }
            catch (Exception error)
            {
                foreach (var command in commands) command.Fail(error);
                throw;
            }
        }
    }

    private static void ValidateFlags(CommandFlags flags)
    {
        if ((flags & ~CommandFlags.DemandMaster) != 0)
            throw Compatibility.Unsupported($"ITransaction CommandFlags {flags}; use None or DemandMaster");
    }

    private async Task<bool> ExecuteCoreAsync(Task<bool> previous, long executionId, CompatBatch.IQueuedCommand[] commands,
        CompatCondition[] conditions, CancellationToken token)
    {
        try
        {
            // Each execution consumes its queue, even after failure. Concurrent executions on this
            // object keep caller order; an earlier failure must not poison a later fresh queue.
            try { await previous.ConfigureAwait(false); }
            catch { /* The earlier execution retains its own failure. */ }
            token.ThrowIfCancellationRequested();
            var client = DatabaseOwner.Client.WithReadFrom(RespireReadFrom.Primary).WithoutClientCache();
            if (commands.Length == 0 && conditions.Length == 0)
            {
                // Upstream uses PING for an empty result-bearing Execute. Preserve connection
                // failures instead of reporting success solely because there is no buffered work.
                await client.PingAsync(token).ConfigureAwait(false);
                return true;
            }
            await using var transaction = await client.CreateTransactionAsync(
                conditions.Select(static condition => condition.Key).ToArray(), token).ConfigureAwait(false);
            var satisfied = true;
            foreach (var condition in conditions)
                satisfied &= await condition.EvaluateAsync(client, token).ConfigureAwait(false);
            if (!satisfied)
            {
                foreach (var command in commands) command.Abort();
                return false;
            }
            // A local queue failure discards the entire transaction before MULTI. Never submit the
            // accepted subset of commands and accidentally weaken atomicity.
            foreach (var command in commands) command.Enqueue(transaction);
            var committed = await transaction.CommitWithWatchValidationAsync(token).ConfigureAwait(false);
            // An earlier execution can complete after a newer one is queued. Its outcome must
            // not replace the conflict status of the latest Execute call.
            lock (_gate) if (_executionId == executionId) _wasWatchConflict = !committed;
            foreach (var command in commands)
            {
                if (committed) command.Complete();
                else command.Abort();
            }
            return committed;
        }
        catch (Exception error)
        {
            foreach (var command in commands) command.Fail(error);
            throw;
        }
    }
}
