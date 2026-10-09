using StackExchange.Redis;
using RedisExpireWhen = StackExchange.Redis.ExpireWhen;

// IDatabase requires synchronous methods; these waits intentionally implement that contract.
#pragma warning disable SER308

namespace Respire.StackExchangeCompat;

internal sealed partial class CompatDatabase : CompatDatabaseAsync, IDatabase
{
    internal RespireConnectionMultiplexer Owner { get; }
    internal int Number { get; }
    private readonly RespireClient _client;
    private readonly SemaphoreSlim _dispatch = new(1, 1);
    internal CompatDatabase(RespireConnectionMultiplexer owner, RespireClient client, int number)
    {
        Owner = owner;
        Number = number;
        _client = client;
        DatabaseOwner = this;
    }

    protected override Task<T> Send<T>(RespireCommand command, RedisValue[] arguments, CommandFlags flags, Func<RedisResult, T> convert)
    {
        if (_client.Core.Options.Connections != 1)
            throw Compatibility.Unsupported("Individual commands with Connections != 1; use native batch APIs or configure Connections = 1");
        if ((flags & ~(CommandFlags.NoRedirect | CommandFlags.DemandMaster | CommandFlags.PreferReplica | CommandFlags.FireAndForget)) != 0)
            throw Compatibility.Unsupported($"CommandFlags {flags}");
        if ((flags & (CommandFlags.NoRedirect | CommandFlags.FireAndForget)) == (CommandFlags.NoRedirect | CommandFlags.FireAndForget))
            throw Compatibility.Unsupported("FireAndForget with NoRedirect");
        var role = (int)flags & 12;
        if (role is 8 or 12 && !command.IsReadOnly) throw Compatibility.Unsupported("replica routing for a write command");
        var client = _client.WithReadFrom(role switch
        {
            4 => RespireReadFrom.Primary,
            8 => RespireReadFrom.ReplicaPreferred,
            12 => RespireReadFrom.Replica,
            _ => RespireReadFrom.Primary,
        });
        var nativeFlags = (flags & CommandFlags.NoRedirect) != 0 ? RespireCommandFlags.NoRedirect : RespireCommandFlags.None;
        return Owner.Run(async cancellationToken =>
        {
            ValueTask<RedisResult> pending = default;
            ValueTask fireAndForget = default;
            var discardReply = (flags & CommandFlags.FireAndForget) != 0;
            // Initialize and enqueue in caller order, then release before awaiting replies.
            // This preserves pipelining while preventing cold connection establishment from reordering commands.
            await _dispatch.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await _client.Core.EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
                if (discardReply)
                {
                    var nativeArguments = arguments.Select(static value => value.ToRespireValue()).ToArray();
                    fireAndForget = client.ExecuteFireAndForgetAsync(command, nativeArguments, cancellationToken);
                }
                else pending = client.ExecuteStackExchangeAsync(command, arguments, nativeFlags, cancellationToken);
            }
            finally { _dispatch.Release(); }
            if (discardReply)
            {
                await fireAndForget.ConfigureAwait(false);
                if (typeof(T) == typeof(RedisValue[])) return (T)(object)Array.Empty<RedisValue>();
                return default(T)!;
            }
            return convert(await pending.ConfigureAwait(false));
        });
    }

    internal RespireBatch CreateNativeBatch() => _client.WithReadFrom(RespireReadFrom.Primary).CreateBatch();
    internal void DisposeDispatch() => _dispatch.Dispose();
    public IBatch CreateBatch(object? asyncState) => asyncState is null ? new CompatBatch(this) : throw Compatibility.Unsupported("asyncState");
    public RedisValue HashGet(RedisKey key, RedisValue hashField, CommandFlags flags) => Wait(HashGetAsync(key, hashField, flags));
    public RedisValue[] HashGet(RedisKey key, RedisValue[] hashFields, CommandFlags flags) => Wait(HashGetAsync(key, hashFields, flags));
    public Lease<byte>? HashGetLease(RedisKey key, RedisValue hashField, CommandFlags flags) => Wait(HashGetLeaseAsync(key, hashField, flags));
    public void HashSet(RedisKey key, HashEntry[] hashFields, CommandFlags flags) => Wait(HashSetAsync(key, hashFields, flags));
    public bool HashSet(RedisKey key, RedisValue hashField, RedisValue value, When when, CommandFlags flags) => Wait(HashSetAsync(key, hashField, value, when, flags));
    public bool KeyDelete(RedisKey key, CommandFlags flags) => Wait(KeyDeleteAsync(key, flags));
    public long KeyDelete(RedisKey[] keys, CommandFlags flags) => Wait(KeyDeleteAsync(keys, flags));
    public bool KeyExpire(RedisKey key, TimeSpan? expiry, CommandFlags flags) => Wait(KeyExpireAsync(key, expiry, flags));
    public bool KeyExpire(RedisKey key, TimeSpan? expiry, RedisExpireWhen when, CommandFlags flags) => Wait(KeyExpireAsync(key, expiry, when, flags));
    public bool KeyExpire(RedisKey key, DateTime? expiry, CommandFlags flags) => Wait(KeyExpireAsync(key, expiry, flags));
    public bool KeyExpire(RedisKey key, DateTime? expiry, RedisExpireWhen when, CommandFlags flags) => Wait(KeyExpireAsync(key, expiry, when, flags));
    public RedisValue[] ListRange(RedisKey key, long start, long stop, CommandFlags flags) => Wait(ListRangeAsync(key, start, stop, flags));
    public long ListRightPush(RedisKey key, RedisValue value, When when, CommandFlags flags) => Wait(ListRightPushAsync(key, value, when, flags));
    public long ListRightPush(RedisKey key, RedisValue[] values, CommandFlags flags) => Wait(ListRightPushAsync(key, values, flags));
    public long ListRightPush(RedisKey key, RedisValue[] values, When when, CommandFlags flags) => Wait(ListRightPushAsync(key, values, when, flags));
}
