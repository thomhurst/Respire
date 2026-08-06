using Respire.Commands;
using Respire.Internal;
using Respire.Networking;
using Respire.Protocol;

namespace Respire;

/// <summary>
/// A MULTI/EXEC transaction. Commands serialize immediately into a pooled buffer as they are
/// queued, each returning a <see cref="RespirePending{T}"/>; <see cref="CommitAsync"/> sends
/// MULTI + commands + EXEC as one atomic append so no other multiplexed command can interleave
/// into the server-side transaction, then completes every pending from EXEC's reply.
/// </summary>
/// <remarks>
/// Single-shot and not thread-safe: build, commit once, discard. When created with watch keys
/// (<see cref="RespireClient.CreateTransactionAsync"/>), the transaction owns a dedicated
/// connection and <see cref="CommitAsync"/> returns false if a watched key changed — always
/// commit or dispose so that connection is released.
/// </remarks>
public sealed class RespireTransaction : IAsyncDisposable
{
    private readonly RespireClient _client;
    private readonly RespireConnection? _watchConnection;
    private readonly WriteBuffer _buffer = new(1024);
    private readonly List<TxOp> _ops = [];
    private bool _completed;

    internal RespireTransaction(RespireClient client, RespireConnection? watchConnection)
    {
        _client = client;
        _watchConnection = watchConnection;
    }

    public int Count => _ops.Count;

    public RespirePending<string?> GetStringAsync(RespireKey key)
        => Add<Cmd1, string?>(new Cmd1(Verbs.Get, _client.Key(in key)), static (c, v) => ResponseReader.StringOrNull(in v));

    public RespirePending<T?> GetAsync<T>(RespireKey key)
        => Add<Cmd1, T?>(new Cmd1(Verbs.Get, _client.Key(in key)), static (c, v) => c.DeserializeBorrowed<T>(in v));

    public RespirePending<bool> SetAsync(
        RespireKey key, RespireValue value, TimeSpan? expiry = null, SetWhen when = SetWhen.Always, bool keepTtl = false)
        => Add<SetCommand, bool>(
            new SetCommand(_client.Key(in key), value, expiry, when, keepTtl, returnOld: false),
            static (c, v) => ResponseReader.OkOrNull(in v));

    public RespirePending<bool> SetAsync<T>(
        RespireKey key, T value, TimeSpan? expiry = null, SetWhen when = SetWhen.Always, bool keepTtl = false)
        => Add<SetCommand, bool>(
            new SetCommand(_client.Key(in key), _client.Serialize(value), expiry, when, keepTtl, returnOld: false),
            static (c, v) => ResponseReader.OkOrNull(in v));

    public RespirePending<long> DeleteAsync(RespireKey key)
        => Add<CmdN, long>(new CmdN(Verbs.Del, [_client.Key(in key)]), static (c, v) => ResponseReader.Integer(in v));

    public RespirePending<long> IncrementAsync(RespireKey key, long by = 1)
        => Add<Cmd2, long>(new Cmd2(Verbs.IncrBy, _client.Key(in key), by), static (c, v) => ResponseReader.Integer(in v));

    public RespirePending<long> DecrementAsync(RespireKey key, long by = 1)
        => Add<Cmd2, long>(new Cmd2(Verbs.DecrBy, _client.Key(in key), by), static (c, v) => ResponseReader.Integer(in v));

    public RespirePending<bool> ExpireAsync(RespireKey key, TimeSpan expiry)
        => Add<Cmd2, bool>(
            new Cmd2(Verbs.PExpire, _client.Key(in key), (long)expiry.TotalMilliseconds),
            static (c, v) => ResponseReader.Flag(in v));

    public RespirePending<bool> HashSetAsync(RespireKey key, string field, RespireValue value)
        => Add<Cmd3, bool>(new Cmd3(Verbs.HSet, _client.Key(in key), field, value), static (c, v) => ResponseReader.Flag(in v));

    public RespirePending<long> ListLeftPushAsync(RespireKey key, RespireValue value)
        => Add<Cmd2, long>(new Cmd2(Verbs.LPush, _client.Key(in key), value), static (c, v) => ResponseReader.Integer(in v));

    public RespirePending<long> ListRightPushAsync(RespireKey key, RespireValue value)
        => Add<Cmd2, long>(new Cmd2(Verbs.RPush, _client.Key(in key), value), static (c, v) => ResponseReader.Integer(in v));

    public RespirePending<bool> SetAddAsync(RespireKey key, RespireValue member)
        => Add<Cmd2, bool>(new Cmd2(Verbs.SAdd, _client.Key(in key), member), static (c, v) => ResponseReader.Flag(in v));

    public RespirePending<bool> SortedSetAddAsync(RespireKey key, RespireValue member, double score)
        => Add<Cmd3, bool>(new Cmd3(Verbs.ZAdd, _client.Key(in key), score, member), static (c, v) => ResponseReader.Flag(in v));

    /// <summary>
    /// Executes the transaction. Returns true when EXEC ran (pendings hold their results;
    /// per-command runtime errors fault only that command's pending) and false when a watched
    /// key changed and the whole transaction was discarded (pendings report aborted).
    /// </summary>
    public async ValueTask<bool> CommitAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfCompleted();
        if (_ops.Count == 0)
        {
            throw new InvalidOperationException("The transaction has no commands.");
        }

        _completed = true;
        try
        {
            var result = _watchConnection is not null
                ? await _watchConnection.SendTransactionAsync(_buffer.WrittenMemory, _ops.Count, cancellationToken)
                    .ConfigureAwait(false)
                : await _client.SendTransactionCoreAsync(_buffer.WrittenMemory, _ops.Count, cancellationToken)
                    .ConfigureAwait(false);

            if (result.IsError)
            {
                var error = ResponseReader.ServerError(in result);
                result.Dispose();
                foreach (var op in _ops)
                {
                    op.Fail(error);
                }

                throw error;
            }

            if (result.IsNull)
            {
                result.Dispose();
                foreach (var op in _ops)
                {
                    op.Abort();
                }

                return false;
            }

            var elements = result.AsArray();
            var completeCount = Math.Min(_ops.Count, elements.Length);
            for (var i = 0; i < completeCount; i++)
            {
                _ops[i].Complete(_client, in elements[i]);
            }

            if (completeCount < _ops.Count)
            {
                var mismatch = new RespireProtocolException(
                    $"EXEC returned {elements.Length} results for {_ops.Count} queued commands.");
                for (var i = completeCount; i < _ops.Count; i++)
                {
                    _ops[i].Fail(mismatch);
                }
            }

            result.Dispose();
            return true;
        }
        finally
        {
            await ReleaseAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Releases the buffer (and any watch connection) of an uncommitted transaction.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_completed)
        {
            return;
        }

        _completed = true;
        await ReleaseAsync().ConfigureAwait(false);
    }

    private async ValueTask ReleaseAsync()
    {
        _buffer.Release();
        if (_watchConnection is not null)
        {
            await _watchConnection.DisposeAsync().ConfigureAwait(false);
        }
    }

    private RespirePending<T> Add<TCommand, T>(in TCommand command, Func<RespireClient, RespValue, T> convert)
        where TCommand : struct, IRespCommand
    {
        ThrowIfCompleted();
        var writer = new RespWriter(_buffer);
        command.Write(ref writer);
        var pending = new RespirePending<T>();
        _ops.Add(new TxOp<T>(pending, convert));
        return pending;
    }

    private void ThrowIfCompleted()
    {
        if (_completed)
        {
            throw new InvalidOperationException("The transaction has already been committed or disposed.");
        }
    }

    private abstract class TxOp
    {
        public abstract void Complete(RespireClient client, in RespValue element);

        public abstract void Fail(Exception error);

        public abstract void Abort();
    }

    /// <summary>Completes from a borrowed EXEC-array element; the parent reply owns the storage.</summary>
    private sealed class TxOp<T>(RespirePending<T> pending, Func<RespireClient, RespValue, T> convert) : TxOp
    {
        public override void Complete(RespireClient client, in RespValue element)
        {
            if (element.IsError)
            {
                pending.Fail(ResponseReader.ServerError(in element));
                return;
            }

            try
            {
                pending.Succeed(convert(client, element));
            }
            catch (Exception ex)
            {
                pending.Fail(ex);
            }
        }

        public override void Fail(Exception error) => pending.Fail(error);

        public override void Abort() => pending.Abort();
    }
}
