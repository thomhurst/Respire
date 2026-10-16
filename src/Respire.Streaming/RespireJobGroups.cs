using System.Buffers;
using System.Text;

namespace Respire.Streaming;

/// <summary>A read-only application group snapshot from the Redis server clock.</summary>
public sealed class RespireJobGroupInfo
{
    internal RespireJobGroupInfo(string name, long queued, long ready, long active, bool paused,
        bool rateLimited, int? maximum, RespireJobRateLimit? rate, TimeSpan leaseDuration)
    {
        Name = name;
        QueuedCount = queued;
        ReadyCount = ready;
        ActiveCount = active;
        IsPaused = paused;
        IsRateLimited = rateLimited;
        MaxConcurrency = maximum;
        RateLimit = rate;
        LeaseDuration = leaseDuration;
    }

    /// <summary>The application group identity, not a Redis consumer group.</summary>
    public string Name { get; }
    /// <summary>Staged jobs, including delayed jobs. Expired entries remain counted until scheduler cleanup.</summary>
    public long QueuedCount { get; }
    /// <summary>Staged jobs whose due time has arrived, before expiry cleanup or admission checks.</summary>
    public long ReadyCount { get; }
    /// <summary>Unexpired delivery leases, not all pending consumer-group entries.</summary>
    public long ActiveCount { get; }
    /// <summary>Whether this application group is paused.</summary>
    public bool IsPaused { get; }
    /// <summary>Whether this group's rate policy currently denies another admission.</summary>
    public bool IsRateLimited { get; }
    /// <summary>Whether live leases exhaust the configured concurrency limit.</summary>
    public bool IsConcurrencyLimited => MaxConcurrency is { } maximum && ActiveCount >= maximum;
    /// <summary>Whether due work passes group admission. Queue-wide controls and job expiry still apply.</summary>
    public bool IsEligible => ReadyCount > 0 && !IsPaused && !IsRateLimited && !IsConcurrencyLimited;
    /// <summary>The configured concurrency limit, or null for unlimited.</summary>
    public int? MaxConcurrency { get; }
    /// <summary>The configured rate policy, or null for unlimited.</summary>
    public RespireJobRateLimit? RateLimit { get; }
    /// <summary>The configured delivery lease duration, defaulting to one minute.</summary>
    public TimeSpan LeaseDuration { get; }
}

/// <summary>A bounded page of application groups with queued jobs or retained lease entries.</summary>
public sealed class RespireJobGroupPage
{
    internal RespireJobGroupPage(IReadOnlyList<RespireJobGroupInfo> groups, string? nextCursor)
    {
        Groups = groups;
        NextCursor = nextCursor;
    }

    /// <summary>The groups on this page, ordered by their encoded identities.</summary>
    public IReadOnlyList<RespireJobGroupInfo> Groups { get; }
    /// <summary>An opaque continuation cursor, or null at the end. Concurrent changes can affect later pages.</summary>
    public string? NextCursor { get; }
}

/// <summary>Bounded inspection and shared pause controls for application job groups.</summary>
public static class RespireJobGroups
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly SearchValues<char> CursorCharacters = SearchValues.Create("0123456789abcdef");

    /// <summary>Reads up to 200 groups with staged work or retained leases, without scanning job payloads.</summary>
    /// <remarks>Idle policy-only groups can be inspected with GetAsync. Expired lease entries may keep a group
    /// visible with zero active deliveries. This read does not clean up or mutate scheduler state.
    /// The script blocks Redis while it runs; cost grows with the page size, not the job backlog.</remarks>
    public static ValueTask<RespireJobGroupPage> ReadAsync(IRespireClient client, string streamKey,
        int count = 50, string? cursor = null, CancellationToken cancellationToken = default)
    {
        if (count is < 1 or > 200) throw new ArgumentOutOfRangeException(nameof(count));
        if (cursor is not null && (cursor.Length < 3 || cursor[^1] != '|' || cursor.Length % 2 != 1
            || cursor.AsSpan(0, cursor.Length - 1).ContainsAnyExcept(CursorCharacters)))
            throw new ArgumentException("Invalid group cursor.", nameof(cursor));
        return ReadCoreAsync(client, streamKey, count, cursor ?? "", false, cancellationToken);
    }

    /// <summary>Inspects one named group, including idle policy-only groups. Missing state means zero counts and unlimited admission.</summary>
    public static async ValueTask<RespireJobGroupInfo> GetAsync(IRespireClient client, string streamKey,
        string group, CancellationToken cancellationToken = default)
        => (await ReadCoreAsync(client, streamKey, 1, Prefix(group), true, cancellationToken).ConfigureAwait(false)).Groups[0];

    /// <summary>Stops new delivery attempts for one application group. In-flight leases continue.</summary>
    public static ValueTask<bool> PauseAsync(IRespireClient client, string streamKey, string group,
        CancellationToken cancellationToken = default) => SetPausedAsync(client, streamKey, group, true, cancellationToken);

    /// <summary>Resumes one application group, subject to its shared admission policy.</summary>
    public static ValueTask<bool> ResumeAsync(IRespireClient client, string streamKey, string group,
        CancellationToken cancellationToken = default) => SetPausedAsync(client, streamKey, group, false, cancellationToken);

    private static async ValueTask<bool> SetPausedAsync(IRespireClient client, string streamKey, string group,
        bool paused, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(streamKey);
        var prefix = Prefix(group);
        var keys = JobGroupScheduling.Keys(streamKey);
        JobQueueScheduling.ValidateKeys(client, keys);
        return await client.Scripts.ExecuteIntegerAsync(JobGroupControls.Pause, [.. keys],
            [prefix, paused ? 1 : 0], cancellationToken).ConfigureAwait(false) == 1;
    }

    private static async ValueTask<RespireJobGroupPage> ReadCoreAsync(IRespireClient client, string streamKey,
        int count, string prefix, bool single, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(streamKey);
        var schedulingKeys = JobGroupScheduling.Keys(streamKey);
        // Project the grouped-jobs, leases and policy slots from the scheduler's fixed key layout.
        string[] keys = [schedulingKeys[4], schedulingKeys[7], schedulingKeys[6]];
        JobQueueScheduling.ValidateKeys(client, keys);
        using var reply = await client.Scripts.ExecuteAsync(JobGroupInspection.Read, [.. keys],
            [count, prefix, single ? 1 : 0], cancellationToken).ConfigureAwait(false);
        var rows = reply[0];
        var groups = new RespireJobGroupInfo[rows.Count];
        for (var i = 0; i < groups.Length; i++)
        {
            var row = rows[i];
            var encoded = row[0].AsString();
            var maximum = checked((int)row[6].AsInteger());
            var burst = checked((int)row[7].AsInteger());
            groups[i] = new(StrictUtf8.GetString(Convert.FromHexString(encoded[..^1])),
                row[1].AsInteger(), row[2].AsInteger(), row[3].AsInteger(), row[4].AsInteger() == 1,
                row[5].AsInteger() == 1, maximum == 0 ? null : maximum,
                burst == 0 ? null : new() { PermitLimit = burst, Window = TimeSpan.FromMilliseconds(row[8].AsInteger()) },
                TimeSpan.FromMilliseconds(row[9].AsInteger()));
        }
        var next = reply[1].AsString();
        return new(groups, next.Length == 0 ? null : next);
    }

    internal static string Prefix(string group)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(group);
        return Convert.ToHexString(StrictUtf8.GetBytes(group)).ToLowerInvariant() + "|";
    }
}

internal static class JobGroupInspection
{
    internal static readonly RespireScript Read = RespireScript.Create("""
        local time = redis.call('TIME')
        local clock = tonumber(time[1])*1000 + math.floor(tonumber(time[2])/1000)
        local suffix = string.format('%016.0f', clock) .. '|' .. string.char(255)
        local function nextGroup(after)
            local lower = after == '' and '-' or '(' .. after .. string.char(255)
            local queued = redis.call('ZRANGEBYLEX', KEYS[1], lower, '+', 'LIMIT', 0, 1)
            local leases = redis.call('ZRANGEBYLEX', KEYS[2], lower, '+', 'LIMIT', 0, 1)
            local a = queued[1] and string.match(queued[1], '^([^|]+|)')
            local b = leases[1] and string.match(leases[1], '^([^|]+|)')
            -- Redis uses the C locale, matching byte ordering for these hex-encoded prefixes.
            if a and b then return a < b and a or b end
            return a or b
        end
        local function inspect(prefix)
            local upper = '[' .. prefix .. string.char(255)
            local queued = redis.call('ZLEXCOUNT', KEYS[1], '[' .. prefix, upper)
            local ready = redis.call('ZLEXCOUNT', KEYS[1], '[' .. prefix, '[' .. prefix .. suffix)
            local active = redis.call('ZLEXCOUNT', KEYS[2], '(' .. prefix .. suffix, upper)
            local policy = redis.call('HMGET', KEYS[3], prefix .. 'paused', prefix .. 'max', prefix .. 'burst',
                prefix .. 'window', prefix .. 'lease', prefix .. 'tat')
            local burst = tonumber(policy[3] or '0')
            local window = tonumber(policy[4] or '0')
            local limited = burst > 0 and math.ceil(tonumber(policy[6] or '0') - (burst-1)*math.ceil(window/burst)) > clock
            return {prefix, queued, ready, active, tonumber(policy[1] or '0'), limited and 1 or 0,
                tonumber(policy[2] or '0'), burst, window, tonumber(policy[5] or '60000')}
        end
        if ARGV[3] == '1' then return {{inspect(ARGV[2])}, ''} end
        local result, last = {}, ARGV[2]
        for i = 1, tonumber(ARGV[1]) do
            local prefix = nextGroup(last)
            if not prefix then return {result, ''} end
            table.insert(result, inspect(prefix))
            last = prefix
        end
        return {result, nextGroup(last) and last or ''}
        """);
}
