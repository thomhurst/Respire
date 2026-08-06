using Respire.Commands;
using Respire.Infrastructure;
using Respire.Networking;
using Respire.Protocol;

namespace Respire.FastClient;

/// <summary>
/// A MULTI/EXEC transaction builder. Commands serialize immediately into a pooled buffer as
/// they are added; <see cref="ExecuteAsync"/> sends MULTI + all commands + EXEC as one atomic
/// append on a single connection, so no other multiplexed command can interleave into the
/// server-side transaction state.
/// </summary>
/// <remarks>
/// Single-shot and not thread-safe: build, execute once, discard. The result is EXEC's reply —
/// an array with one element per queued command, in order. Dispose it when done. If any
/// command fails to queue (e.g. bad arity), Redis aborts the whole transaction and
/// <see cref="ExecuteAsync"/> throws <see cref="RespireServerException"/> (EXECABORT).
/// Executing releases the internal buffer; call <see cref="Dispose"/> only when abandoning a
/// transaction without executing it.
/// </remarks>
public sealed class RespireTransaction : IDisposable
{
    private readonly RespireConnectionMultiplexer _multiplexer;
    private readonly WriteBuffer _buffer = new(1024);
    private int _commandCount;
    private bool _completed;

    internal RespireTransaction(RespireConnectionMultiplexer multiplexer)
    {
        _multiplexer = multiplexer;
    }

    public int CommandCount => _commandCount;

    /// <summary>Appends any command to the transaction.</summary>
    public RespireTransaction Add<TCommand>(in TCommand command) where TCommand : struct, IRespCommand
    {
        ThrowIfCompleted();
        var writer = new RespWriter(_buffer);
        command.Write(ref writer);
        _commandCount++;
        return this;
    }

    public RespireTransaction Get(string key)
        => Add(new KeyCommand(CommandPrefixes.Get, key));

    public RespireTransaction Set(string key, string value)
        => Add(new KeyValueCommand(CommandPrefixes.Set, key, value));

    public RespireTransaction Del(string key)
        => Add(new KeyCommand(CommandPrefixes.Del, key));

    public RespireTransaction Incr(string key)
        => Add(new KeyCommand(CommandPrefixes.Incr, key));

    public RespireTransaction Decr(string key)
        => Add(new KeyCommand(CommandPrefixes.Decr, key));

    public RespireTransaction Expire(string key, int seconds)
        => Add(new KeyIntegerCommand(CommandPrefixes.Expire, key, seconds));

    public RespireTransaction HSet(string key, string field, string value)
        => Add(new KeyFieldValueCommand(CommandPrefixes.HSet, key, field, value));

    public RespireTransaction LPush(string key, string value)
        => Add(new KeyValueCommand(CommandPrefixes.LPush, key, value));

    public RespireTransaction RPush(string key, string value)
        => Add(new KeyValueCommand(CommandPrefixes.RPush, key, value));

    public RespireTransaction SAdd(string key, string member)
        => Add(new KeyValueCommand(CommandPrefixes.SAdd, key, member));

    /// <summary>
    /// Executes the transaction and returns EXEC's reply: an array with one result per
    /// command, in the order they were added. Dispose the returned value when done.
    /// </summary>
    public async ValueTask<RespireValue> ExecuteAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfCompleted();
        if (_commandCount == 0)
        {
            throw new InvalidOperationException("Transaction has no commands.");
        }

        _completed = true;
        try
        {
            var result = await _multiplexer
                .SendTransactionAsync(_buffer.WrittenMemory, _commandCount, cancellationToken).ConfigureAwait(false);
            result.ThrowIfError();
            return result;
        }
        finally
        {
            _buffer.Release();
        }
    }

    /// <summary>Returns the pooled buffer of a transaction that was built but never executed.</summary>
    public void Dispose()
    {
        _completed = true;
        _buffer.Release();
    }

    private void ThrowIfCompleted()
    {
        if (_completed)
        {
            throw new InvalidOperationException("Transaction has already been executed or disposed.");
        }
    }
}
