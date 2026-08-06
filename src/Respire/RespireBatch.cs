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
        => Add<Cmd1, string?>(new Cmd1(Verbs.Get, _client.Key(in key)), static (c, v) => ResponseReader.StringOrNull(in v));

    public RespirePending<T?> GetAsync<T>(RespireKey key)
        => Add<Cmd1, T?>(new Cmd1(Verbs.Get, _client.Key(in key)), static (c, v) => c.DeserializeBorrowed<T>(in v));

    public RespirePending<byte[]?> GetBytesAsync(RespireKey key)
        => Add<Cmd1, byte[]?>(new Cmd1(Verbs.Get, _client.Key(in key)), static (c, v) => ResponseReader.BytesOrNull(in v));

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

    public RespirePending<bool> ExistsAsync(RespireKey key)
        => Add<Cmd1, bool>(new Cmd1(Verbs.Exists, _client.Key(in key)), static (c, v) => ResponseReader.Flag(in v));

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

    public RespirePending<string?> HashGetAsync(RespireKey key, string field)
        => Add<Cmd2, string?>(new Cmd2(Verbs.HGet, _client.Key(in key), field), static (c, v) => ResponseReader.StringOrNull(in v));

    public RespirePending<long> ListLeftPushAsync(RespireKey key, RespireValue value)
        => Add<Cmd2, long>(new Cmd2(Verbs.LPush, _client.Key(in key), value), static (c, v) => ResponseReader.Integer(in v));

    public RespirePending<long> ListRightPushAsync(RespireKey key, RespireValue value)
        => Add<Cmd2, long>(new Cmd2(Verbs.RPush, _client.Key(in key), value), static (c, v) => ResponseReader.Integer(in v));

    public RespirePending<bool> SetAddAsync(RespireKey key, RespireValue member)
        => Add<Cmd2, bool>(new Cmd2(Verbs.SAdd, _client.Key(in key), member), static (c, v) => ResponseReader.Flag(in v));

    public RespirePending<bool> SortedSetAddAsync(RespireKey key, RespireValue member, double score)
        => Add<Cmd3, bool>(new Cmd3(Verbs.ZAdd, _client.Key(in key), score, member), static (c, v) => ResponseReader.Flag(in v));

    /// <summary>
    /// Sends every queued command in one flush and completes all pendings. Per-command server
    /// errors fault that command's pending, not this call.
    /// </summary>
    public async ValueTask SendAsync(CancellationToken cancellationToken = default)
    {
        if (_sent)
        {
            throw new InvalidOperationException("This batch has already been sent.");
        }

        _sent = true;
        if (_ops.Count == 0)
        {
            return;
        }

        var connection = await _client.AcquireConnectionAsync(cancellationToken).ConfigureAwait(false);
        var tasks = new Task[_ops.Count];
        for (var i = 0; i < _ops.Count; i++)
        {
            tasks[i] = _ops[i].RunAsync(_client, connection, cancellationToken);
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private RespirePending<T> Add<TCommand, T>(in TCommand command, Func<RespireClient, RespValue, T> convert)
        where TCommand : struct, IRespCommand
    {
        if (_sent)
        {
            throw new InvalidOperationException("This batch has already been sent.");
        }

        var pending = new RespirePending<T>();
        _ops.Add(new Op<TCommand, T>(command, pending, convert));
        return pending;
    }

    private abstract class Op
    {
        public abstract Task RunAsync(RespireClient client, RespireConnection connection, CancellationToken cancellationToken);
    }

    private sealed class Op<TCommand, T>(TCommand command, RespirePending<T> pending, Func<RespireClient, RespValue, T> convert) : Op
        where TCommand : struct, IRespCommand
    {
        public override async Task RunAsync(RespireClient client, RespireConnection connection, CancellationToken cancellationToken)
        {
            try
            {
                var value = await connection.SendAsync(in command, cancellationToken).ConfigureAwait(false);
                if (value.IsError)
                {
                    var error = ResponseReader.ServerError(in value);
                    value.Dispose();
                    pending.Fail(error);
                    return;
                }

                try
                {
                    pending.Succeed(convert(client, value));
                }
                finally
                {
                    value.Dispose();
                }
            }
            catch (Exception ex)
            {
                pending.Fail(ex);
            }
        }
    }
}
