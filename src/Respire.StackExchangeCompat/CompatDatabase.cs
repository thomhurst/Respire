using Respire.Internal;
using Respire.Networking;
using StackExchange.Redis;
using RedisSortedSetEntry = StackExchange.Redis.SortedSetEntry;
using RedisExpireWhen = StackExchange.Redis.ExpireWhen;

// IDatabase requires synchronous methods; these waits intentionally implement that contract.
#pragma warning disable SER308

namespace Respire.StackExchangeCompat;

internal sealed partial class CompatDatabase : CompatDatabaseAsync, IDatabase
{
    internal RespireConnectionMultiplexer Owner
    {
        get;
    }

    internal int Number
    {
        get;
    }
    private readonly RespireClient _client;

    internal RespireClient Client => _client;
    private readonly SemaphoreSlim _dispatch = new(1, 1);
    private ClusterDispatch? _clusterDispatch;

    internal CompatDatabase(RespireConnectionMultiplexer owner, RespireClient client, int number)
    {
        Owner = owner;
        Number = number;
        _client = client.WithoutAmbiguousWriteRetries();
        DatabaseOwner = this;
    }

    protected override Task<T> Send<T>(RespireCommand command, RedisValue[] arguments, CommandFlags flags, Func<RedisResult, T> convert)
    {
        if (_client.Core.Options.Connections != 1)
        {
            throw Compatibility.Unsupported("Individual commands with Connections != 1; use native batch APIs or configure Connections = 1");
        }

        if ((flags & ~(CommandFlags.NoRedirect | CommandFlags.DemandMaster | CommandFlags.PreferReplica | CommandFlags.FireAndForget)) != 0)
        {
            throw Compatibility.Unsupported($"CommandFlags {flags}");
        }

        if ((flags & (CommandFlags.NoRedirect | CommandFlags.FireAndForget)) == (CommandFlags.NoRedirect | CommandFlags.FireAndForget))
        {
            throw Compatibility.Unsupported("FireAndForget with NoRedirect");
        }

        var role = (int)flags & 12;

        if (role is 8 or 12 && !command.IsReadOnly)
        {
            throw Compatibility.Unsupported("replica routing for a write command");
        }
        // StackExchange.Redis does not submit zero integer adjustments in fire-and-forget mode.
        if ((flags & CommandFlags.FireAndForget) != 0 && (command.Name is "INCRBY" or "DECRBY") && arguments[1] == 0)
        {
            return Owner.Run(_ => Task.FromResult(default(T)!));
        }

        var client = (RespireClient)_client.WithReadFrom(role switch
        {
            4 => RespireReadFrom.Primary,
            8 when _client.Core.SupportsReplicaReads => RespireReadFrom.ReplicaPreferred,
            12 => RespireReadFrom.Replica,
            _ => RespireReadFrom.Primary,
        });
        var nativeFlags = (flags & CommandFlags.NoRedirect) != 0 ? RespireCommandFlags.NoRedirect : RespireCommandFlags.None;
        return Owner.Run(async shutdownToken =>
        {
            var timeout = client.Core.Options.CommandTimeout;
            var deadline = timeout is { } budget ? CommandDeadline.After((long)budget.TotalMilliseconds) : default;
            using var timeoutCancellation = CommandTimeoutCancellation.CreateForDeadline(shutdownToken, deadline, out var cancellationToken);
            var stage = RespireCommandStage.WaitingForCapacity;
            Task<RespireResult>? pending = null;
            var discardReply = (flags & CommandFlags.FireAndForget) != 0;

            try
            {
                // Keep the same budget through queueing, connection establishment, and dispatch.
                ClusterDispatch.Reservation? reservation = null;
                RespireValue[]? nativeArguments = null;

                if (client.Core.Cluster is null)
                {
                    await _dispatch.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    // Owner.Run starts this callback under its lock, so reservations follow call order.
                    nativeArguments = arguments.Select(static value => value.ToRespireValue()).ToArray();
                    reservation = (_clusterDispatch ??= new()).Enqueue(client, command, nativeArguments);
                }

                try
                {
                    if (reservation is { } queued)
                    {
                        await queued.Predecessors.WaitAsync(cancellationToken).ConfigureAwait(false);
                    }

                    stage = RespireCommandStage.Connecting;
                    await client.Core.EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
                    nativeArguments ??= arguments.Select(static value => value.ToRespireValue()).ToArray();

                    // A waiter can acquire the gate after its deadline but before its timer fires.
                    cancellationToken.ThrowIfCancellationRequested();

                    if (deadline.RemainingMilliseconds == 0)
                    {
                        shutdownToken.ThrowIfCancellationRequested();
                        throw new RespireTimeoutException(command.Name, timeout!.Value, null, RespireTimeoutDiagnostics.Capture(stage));
                    }

                    stage = RespireCommandStage.Unknown;

                    if (discardReply)
                    {
                        await client.ExecuteFireAndForgetAsync(command, nativeArguments, cancellationToken).ConfigureAwait(false);
                    }
                    else if (command.Name == "EVALSHA" || (client.Core.Options.EnableReplicaRedirects && client.Core.Cluster is null))
                    {
                        // Stored procedures do not expose admission. Replica redirects can move an admitted command to another FIFO.
                        pending = client.ExecuteLeaseAsync(command, nativeArguments, nativeFlags, cancellationToken).AsTask();
                        await pending.ConfigureAwait(false);
                    }
                    else
                    {
                        var admission = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        pending = client.ExecuteWithAdmissionAsync(command, nativeArguments, nativeFlags, cancellationToken, admission).AsTask();

                        if (client.Core.CommandRetries is not null || client.Core.Cluster is not null)
                        {
                            // A retry or Cluster redirect can move an accepted command to a different FIFO.
                            await pending.ConfigureAwait(false);
                        }
                        else
                        {
                            // Keep pipelining on one FIFO. Cache hits and pre-admission failures may settle without admission.
                            await Task.WhenAny(admission.Task, pending).ConfigureAwait(false);
                        }
                    }
                }
                finally
                {
                    if (reservation is { } queued)
                    {
                        _clusterDispatch!.Release(queued);
                    }
                    else
                    {
                        _dispatch.Release();
                    }
                }

                if (discardReply)
                {
                    if (typeof(T) == typeof(RedisValue[]))
                    {
                        return (T)(object)Array.Empty<RedisValue>();
                    }

                    if (typeof(T) == typeof(HashEntry[]))
                    {
                        return (T)(object)Array.Empty<HashEntry>();
                    }

                    if (typeof(T) == typeof(RedisSortedSetEntry[]))
                    {
                        return (T)(object)Array.Empty<RedisSortedSetEntry>();
                    }

                    return default(T)!;
                }

                using var result = await pending!.ConfigureAwait(false);
                return convert(result.ToStackExchangeResult());
            }
            catch (OperationCanceledException error) when (deadline.IsSet
                && RespireConnection.IsDeadlineCancellation(error, cancellationToken, shutdownToken))
            {
                throw new RespireTimeoutException(command.Name, timeout!.Value, error, RespireTimeoutDiagnostics.Capture(stage));
            }
        });
    }

    internal RespireBatch CreateNativeBatch() => _client.WithReadFrom(RespireReadFrom.Primary).CreateBatch();

    internal void DisposeDispatch() => _dispatch.Dispose();

    public IBatch CreateBatch(object? asyncState) => asyncState is null ? new CompatBatch(this) : throw Compatibility.Unsupported("asyncState");

    public new ITransaction CreateTransaction(object? asyncState) => asyncState is null ? new CompatTransaction(this) : throw Compatibility.Unsupported("asyncState");

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
