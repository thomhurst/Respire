using System.Diagnostics;
using Respire.Commands;
using Respire.Protocol;

namespace Respire;

/// <summary>StackExchange.Redis-style distributed lock helper commands.</summary>
public interface ILockCommands
{
    /// <summary>
    /// Acquires a lock with a generated owner token and returns a handle that releases it on
    /// disposal, or <see langword="null"/> when another owner holds it. Redis: SET ... NX PX.
    /// </summary>
    /// <example>
    /// <code>
    /// await using var mutex = await redis.Locks.AcquireAsync("job:42", TimeSpan.FromSeconds(30));
    /// if (mutex is null) return; // someone else holds it
    /// </code>
    /// </example>
    ValueTask<RespireLock?> AcquireAsync(
        RespireKey key,
        TimeSpan expiry,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Acquires a lock as <see cref="AcquireAsync(RespireKey, TimeSpan, CancellationToken)"/> does,
    /// but retries every <paramref name="retryEvery"/> until <paramref name="wait"/> elapses.
    /// Returns <see langword="null"/> when the lock was still held at the end of that budget.
    /// </summary>
    ValueTask<RespireLock?> AcquireAsync(
        RespireKey key,
        TimeSpan expiry,
        TimeSpan wait,
        TimeSpan retryEvery,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Acquires a lock when it does not already exist. The token identifies the owner and is
    /// required for later release or extension. Redis: SET ... NX PX.
    /// </summary>
    ValueTask<bool> TakeAsync(
        RespireKey key,
        RespireValue token,
        TimeSpan expiry,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Releases the lock only when its value still matches <paramref name="token"/>.
    /// Redis: EVALSHA/EVAL compare-and-DEL.
    /// </summary>
    ValueTask<bool> ReleaseAsync(
        RespireKey key,
        RespireValue token,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Extends the lock expiry only when its value still matches <paramref name="token"/>.
    /// Redis: EVALSHA/EVAL compare-and-PEXPIRE.
    /// </summary>
    ValueTask<bool> ExtendAsync(
        RespireKey key,
        RespireValue token,
        TimeSpan expiry,
        CancellationToken cancellationToken = default);

    /// <summary>Returns the lock's current token bytes, or null when missing. Redis: GET.</summary>
    ValueTask<byte[]?> QueryAsync(RespireKey key, CancellationToken cancellationToken = default);
}

internal sealed class LockCommands(RespireClient client) : ILockCommands
{
    internal static readonly RespireScript ReleaseScript = RespireScript.Create("""
        if redis.call('GET', KEYS[1]) == ARGV[1] then
          return redis.call('DEL', KEYS[1])
        end
        return 0
        """);

    internal static readonly RespireScript ExtendScript = RespireScript.Create("""
        if redis.call('GET', KEYS[1]) == ARGV[1] then
          return redis.call('PEXPIRE', KEYS[1], ARGV[2])
        end
        return 0
        """);

    public async ValueTask<RespireLock?> AcquireAsync(
        RespireKey key,
        TimeSpan expiry,
        CancellationToken cancellationToken = default)
    {
        var token = RespireLock.NewToken();
        return await TakeAsync(key, token, expiry, cancellationToken).ConfigureAwait(false)
            ? new RespireLock(this, key, token, expiry)
            : null;
    }

    public async ValueTask<RespireLock?> AcquireAsync(
        RespireKey key,
        TimeSpan expiry,
        TimeSpan wait,
        TimeSpan retryEvery,
        CancellationToken cancellationToken = default)
    {
        if (wait < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(wait), wait, "Lock wait must not be negative.");
        }

        if (retryEvery <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(retryEvery),
                retryEvery,
                "Lock retry interval must be greater than zero.");
        }

        var start = Stopwatch.GetTimestamp();
        while (true)
        {
            var acquired = await AcquireAsync(key, expiry, cancellationToken).ConfigureAwait(false);
            if (acquired is not null)
            {
                return acquired;
            }

            // Only sleep when a full interval still fits inside the budget, so the last attempt
            // happens at the deadline rather than after it.
            if (Stopwatch.GetElapsedTime(start) + retryEvery > wait)
            {
                return null;
            }

            await Task.Delay(retryEvery, cancellationToken).ConfigureAwait(false);
        }
    }

    public ValueTask<bool> TakeAsync(
        RespireKey key,
        RespireValue token,
        TimeSpan expiry,
        CancellationToken cancellationToken = default)
    {
        ValidateToken(token);
        var milliseconds = ValidateExpiry(expiry);
        return client.OkOrNullAsync(
            "SET",
            new LockTakeCommand(client.Key(in key), token, milliseconds),
            cancellationToken);
    }

    public ValueTask<bool> ReleaseAsync(
        RespireKey key,
        RespireValue token,
        CancellationToken cancellationToken = default)
    {
        ValidateToken(token);
        return ExecuteBooleanScriptAsync(ReleaseScript, key, [token], cancellationToken);
    }

    public ValueTask<bool> ExtendAsync(
        RespireKey key,
        RespireValue token,
        TimeSpan expiry,
        CancellationToken cancellationToken = default)
    {
        ValidateToken(token);
        var milliseconds = ValidateExpiry(expiry);
        return ExecuteBooleanScriptAsync(ExtendScript, key, [token, milliseconds], cancellationToken);
    }

    public ValueTask<byte[]?> QueryAsync(RespireKey key, CancellationToken cancellationToken = default)
        => client.BytesOrNullAsync("GET", new Cmd1(Verbs.Get, client.Key(in key)), cancellationToken);

    private async ValueTask<bool> ExecuteBooleanScriptAsync(
        RespireScript script,
        RespireKey key,
        RespireValue[] args,
        CancellationToken cancellationToken)
    {
        using var result = await client.ExecuteScriptAsync(
                script,
                client.BuildScriptTail([key], args),
                cancellationToken)
            .ConfigureAwait(false);
        return result.AsInteger() >= 1;
    }

    private static void ValidateToken(RespireValue token)
    {
        if (token.IsNull || token.IsEmpty)
        {
            throw new ArgumentException("Lock token must not be null or empty.", nameof(token));
        }
    }

    private static long ValidateExpiry(TimeSpan expiry)
    {
        var milliseconds = (long)expiry.TotalMilliseconds;
        if (milliseconds <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(expiry),
                expiry,
                "Lock expiry must be at least 1 millisecond.");
        }

        return milliseconds;
    }
}

/// <summary>SET key token NX PX milliseconds.</summary>
internal readonly struct LockTakeCommand(RespireValue key, RespireValue token, long milliseconds) : IRespCommand
{
    public bool TryGetClusterSlot(out int slot) => key.TryGetClusterSlot(out slot);

    public void Write(ref RespWriter writer)
    {
        writer.WriteArrayHeader(6);
        writer.WriteRaw(Verbs.Set.Bulk);
        key.WriteTo(ref writer);
        token.WriteTo(ref writer);
        writer.WriteBulkString("NX"u8);
        writer.WriteBulkString("PX"u8);
        writer.WriteBulkInteger(milliseconds);
    }
}
