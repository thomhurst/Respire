using System.Globalization;
using Respire.Internal;
using Respire.Protocol;

namespace Respire;

/// <summary>An owned snapshot of a primary monitored by Sentinel.</summary>
public sealed record RespireSentinelPrimary(string Name, RespireEndpoint Endpoint, string Flags,
    long ConfigurationEpoch, int Quorum, IReadOnlyDictionary<string, string> Attributes);

/// <summary>An owned snapshot of a replica, including down and disconnected replicas. Primary is null before INFO establishes it.</summary>
public sealed record RespireSentinelReplica(string Name, RespireEndpoint Endpoint, string Flags,
    RespireEndpoint? Primary, long ReplicationOffset, int Priority, IReadOnlyDictionary<string, string> Attributes);

/// <summary>An owned snapshot of another Sentinel monitoring a primary.</summary>
public sealed record RespireSentinelPeer(string Name, RespireEndpoint Endpoint, string Flags,
    string RunId, IReadOnlyDictionary<string, string> Attributes);

/// <summary>One cached INFO response. Age is measured in milliseconds by Sentinel.</summary>
public sealed record RespireSentinelInfo(string PrimaryName, long AgeMilliseconds, string? Info);

/// <summary>A pending script and its scheduling state. Time is runtime or delay in milliseconds.</summary>
public sealed record RespireSentinelScript(string[] Arguments, string Flags, long ProcessId,
    long TimeMilliseconds, long RetryCount);

/// <summary>A Sentinel down-state observation and its vote for a failover leader.</summary>
public readonly record struct RespireSentinelDownState(bool IsDown, string LeaderId, long LeaderEpoch);

/// <summary>Crash simulation flags for a Sentinel node.</summary>
[Flags]
public enum RespireSentinelFailure
{
    /// <summary>Clear previously configured simulation flags.</summary>
    None = 0,
    /// <summary>Crash after winning an election.</summary>
    CrashAfterElection = 1,
    /// <summary>Crash after promoting a replica.</summary>
    CrashAfterPromotion = 2,
}

/// <summary>Typed administration of one explicitly selected Sentinel, independent of data routing.</summary>
/// <remarks>Uses Sentinel credentials and TLS from RespireOptions. Mutations require AllowAdmin.
/// Commands affect this node only; configuration is not broadcast to other Sentinels.
/// Dispose this client independently of any data client. Accepted commands are never replayed.</remarks>
public sealed class RespireSentinelClient : IAsyncDisposable
{
    private readonly RespireClient _client;
    private readonly SentinelServerProfile _profile;

    /// <summary>Owns an established connection and its detected wire capabilities.</summary>
    private RespireSentinelClient(RespireClient client, SentinelServerProfile profile)
        => (_client, _profile) = (client, profile);

    /// <summary>The Sentinel endpoint selected at connection time.</summary>
    public RespireEndpoint Endpoint => _client.Endpoint;

    /// <summary>Connects directly to one Sentinel. Does not discover or connect to a data primary.</summary>
    /// <remarks>When INFO SERVER is denied by ACL, uses legacy command names and disables multi-option CONFIG SET.</remarks>
    public static async ValueTask<RespireSentinelClient> ConnectAsync(RespireEndpoint endpoint,
        RespireOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new RespireOptions();
        var sentinelOptions = SentinelMonitoring.CreateOptions(options, endpoint) with
        {
            AllowAdmin = options.AllowAdmin,
            ReconnectTelemetryScope = "sentinel-admin",
        };
        var client = await RespireClient.ConnectAsync(sentinelOptions, cancellationToken).ConfigureAwait(false);
        try
        {
            RespireResult reply;
            try
            {
                reply = await client.ExecuteAsync("INFO SERVER", [], cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (RespireServerException exception) when (exception.Code == RespireErrorCodes.NoPerm)
            {
                return new(client, SentinelServerProfile.Legacy);
            }
            using var lease = reply;
            var info = SentinelReply.Text(reply);
            return new(client, SentinelServerProfile.FromInfo(info));
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Lists all monitored primaries, retaining every reported state.</summary>
    public ValueTask<RespireSentinelPrimary[]> PrimariesAsync(CancellationToken cancellationToken = default)
        => ReadAsync(_profile.PrimariesCommand, [], SentinelReply.Primaries, cancellationToken);

    /// <summary>Returns the named primary. Unknown names produce the original server error.</summary>
    public ValueTask<RespireSentinelPrimary> PrimaryAsync(string name, CancellationToken cancellationToken = default)
        => ReadAsync(_profile.PrimaryCommand, [Name(name)], SentinelReply.Primary, cancellationToken);

    /// <summary>Returns replicas, including replicas flagged down or disconnected.</summary>
    public ValueTask<RespireSentinelReplica[]> ReplicasAsync(string name, CancellationToken cancellationToken = default)
        => ReadAsync(_profile.ReplicasCommand, [Name(name)], SentinelReply.Replicas, cancellationToken);

    /// <summary>Returns other Sentinels monitoring the named primary.</summary>
    public ValueTask<RespireSentinelPeer[]> SentinelsAsync(string name, CancellationToken cancellationToken = default)
        => ReadAsync("SENTINELS", [Name(name)], SentinelReply.Peers, cancellationToken);

    /// <summary>Checks quorum and majority. Unreachable quorum throws the original server error.</summary>
    public ValueTask<string> CheckQuorumAsync(string name, CancellationToken cancellationToken = default)
        => ReadAsync("CKQUORUM", [Name(name)], SentinelReply.Text, cancellationToken);

    /// <summary>Returns the node ID. Redis 6.2+.</summary>
    public ValueTask<string> MyIdAsync(CancellationToken cancellationToken = default)
        => ReadAsync("MYID", [], SentinelReply.Text, cancellationToken);

    /// <summary>Returns cached INFO from all primaries and replicas, or only the requested primaries. Redis 3.2+.</summary>
    public ValueTask<RespireSentinelInfo[]> InfoCacheAsync(string[]? names = null, CancellationToken cancellationToken = default)
    {
        RespireValue[] arguments = names is null ? [] : new RespireValue[names.Length];
        for (var index = 0; index < arguments.Length; index++) arguments[index] = Name(names![index]);
        return ReadAsync("INFO-CACHE", arguments, SentinelReply.InfoCache, cancellationToken);
    }

    /// <summary>Returns pending scripts with owned argument strings.</summary>
    public ValueTask<RespireSentinelScript[]> PendingScriptsAsync(CancellationToken cancellationToken = default)
        => ReadAsync("PENDING-SCRIPTS", [], SentinelReply.Scripts, cancellationToken);

    /// <summary>Reads global configuration matching a pattern. Redis 6.2+.</summary>
    public ValueTask<IReadOnlyDictionary<string, string>> ConfigGetAsync(string pattern, CancellationToken cancellationToken = default)
        => ReadAsync("CONFIG GET", [Name(pattern)], SentinelReply.Fields, cancellationToken);

    /// <summary>Observes down state or requests a leader vote. Use runId="*" for observation only.</summary>
    /// <remarks>A non-* runId requires AllowAdmin because it can change the election state.</remarks>
    public ValueTask<RespireSentinelDownState> IsPrimaryDownByAddressAsync(RespireEndpoint endpoint,
        long currentEpoch = 0, string runId = "*", CancellationToken cancellationToken = default)
    {
        ValidateEndpoint(endpoint);
        ArgumentOutOfRangeException.ThrowIfNegative(currentEpoch);
        Name(runId);
        if (runId != "*") EnsureAdmin();
        return ReadAsync(_profile.DownStateCommand,
            [endpoint.Host, endpoint.Port, currentEpoch, runId], SentinelReply.DownState, cancellationToken);
    }

    /// <summary>Forces failover without quorum agreement. Requires AllowAdmin.</summary>
    public ValueTask FailoverAsync(string name, CancellationToken cancellationToken = default)
        => MutateAsync("FAILOVER", [Name(name)], cancellationToken);

    /// <summary>Resets matching monitored primaries and returns the number reset. Requires AllowAdmin.</summary>
    public ValueTask<long> ResetAsync(string pattern, CancellationToken cancellationToken = default)
    {
        EnsureAdmin();
        return ReadAsync("RESET", [Name(pattern)], SentinelReply.NonnegativeInteger, cancellationToken);
    }

    /// <summary>Sets ordered monitoring options for one primary. Requires AllowAdmin.</summary>
    /// <remarks>Preserves enumeration order and duplicate options. Use a list or array when order matters.</remarks>
    public ValueTask SetAsync(string name, IEnumerable<KeyValuePair<string, string>> options, CancellationToken cancellationToken = default)
        => MutateAsync("SET", [Name(name), .. Options(options)], cancellationToken);

    /// <summary>Sets global configuration. Requires AllowAdmin. Redis 6.2+; multiple pairs require Redis 7.2+ or Valkey 8+.</summary>
    public ValueTask ConfigSetAsync(IReadOnlyDictionary<string, string> options, CancellationToken cancellationToken = default)
    {
        EnsureAdmin();
        var arguments = Options(options);
        if (arguments.Length > 2 && !_profile.SupportsMultiOptionConfig)
            throw new NotSupportedException("Multiple Sentinel CONFIG SET options require Redis 7.2+ or Valkey 8+.");
        return MutateAsync("CONFIG SET", arguments, cancellationToken);
    }

    /// <summary>Rewrites this Sentinel's configuration file. Requires AllowAdmin.</summary>
    public ValueTask FlushConfigAsync(CancellationToken cancellationToken = default)
        => MutateAsync("FLUSHCONFIG", [], cancellationToken);

    /// <summary>Stops monitoring a primary on this node. Requires AllowAdmin.</summary>
    public ValueTask RemoveAsync(string name, CancellationToken cancellationToken = default)
        => MutateAsync("REMOVE", [Name(name)], cancellationToken);

    /// <summary>Starts monitoring a primary on this node. Requires AllowAdmin.</summary>
    public ValueTask MonitorAsync(string name, RespireEndpoint endpoint, int quorum, CancellationToken cancellationToken = default)
    {
        ValidateEndpoint(endpoint);
        ArgumentOutOfRangeException.ThrowIfLessThan(quorum, 1);
        return MutateAsync("MONITOR", [Name(name), endpoint.Host, endpoint.Port, quorum], cancellationToken);
    }

    /// <summary>Configures crash simulation; None clears it. Requires AllowAdmin. Redis 3.2+.</summary>
    public async ValueTask SimulateFailureAsync(RespireSentinelFailure failure, CancellationToken cancellationToken = default)
    {
        if ((failure & ~(RespireSentinelFailure.CrashAfterElection | RespireSentinelFailure.CrashAfterPromotion)) != 0)
            throw new ArgumentOutOfRangeException(nameof(failure));
        EnsureAdmin();
        if (failure == RespireSentinelFailure.None)
        {
            // HELP resets flags before returning its array; a command without flags fails arity validation.
            await ReadAsync("SIMULATE-FAILURE", ["help"], SentinelReply.FailureHelp, cancellationToken).ConfigureAwait(false);
            return;
        }
        List<RespireValue> arguments = [];
        if (failure.HasFlag(RespireSentinelFailure.CrashAfterElection)) arguments.Add("crash-after-election");
        if (failure.HasFlag(RespireSentinelFailure.CrashAfterPromotion)) arguments.Add("crash-after-promotion");
        await MutateAsync("SIMULATE-FAILURE", arguments.ToArray(), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Parses an owned result before returning the pooled reply lease.</summary>
    private async ValueTask<T> ReadAsync<T>(string command, RespireValue[] arguments, Func<RespireResult, T> parse,
        CancellationToken cancellationToken)
    {
        using var reply = await _client.ExecuteAsync("SENTINEL " + command, arguments, cancellationToken: cancellationToken).ConfigureAwait(false);
        return parse(reply);
    }

    /// <summary>Checks administrative opt-in and requires an OK acknowledgement.</summary>
    private async ValueTask MutateAsync(string command, RespireValue[] arguments, CancellationToken cancellationToken)
    {
        EnsureAdmin();
        await ReadAsync(command, arguments, SentinelReply.Ok, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Rejects writes and election votes before sending command bytes when administration is disabled.</summary>
    private void EnsureAdmin()
    {
        if (!_client.Core.Options.AllowAdmin) throw new NotSupportedException("Sentinel mutations require RespireOptions.AllowAdmin=true.");
    }

    /// <summary>Rejects missing names and option keys without splitting embedded whitespace.</summary>
    private static string Name(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return value;
    }

    /// <summary>Requires a nonblank host and a usable TCP port for monitoring and voting commands.</summary>
    private static void ValidateEndpoint(RespireEndpoint endpoint)
    {
        Name(endpoint.Host);
        if (endpoint.Port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(endpoint));
    }

    /// <summary>Validates and materializes option pairs in exactly their supplied enumeration order.</summary>
    private static RespireValue[] Options(IEnumerable<KeyValuePair<string, string>> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        List<RespireValue> arguments = [];
        foreach (var pair in options)
        {
            arguments.Add(Name(pair.Key));
            ArgumentNullException.ThrowIfNull(pair.Value);
            arguments.Add(pair.Value);
        }
        if (arguments.Count == 0) throw new ArgumentException("At least one option is required.", nameof(options));
        return arguments.ToArray();
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync() => _client.DisposeAsync();
}

/// <summary>Copies Sentinel reply shapes into managed snapshots and rejects malformed fields.</summary>
internal static class SentinelReply
{
    /// <summary>Validates the HELP acknowledgement used to clear crash simulation flags.</summary>
    internal static bool FailureHelp(RespireResult value)
    {
        Array(value);
        if (value.Count != 2 || Text(value[0]) != "crash-after-election" || Text(value[1]) != "crash-after-promotion")
            throw Invalid();
        return true;
    }

    /// <summary>Copies a textual reply without coercing integers, nulls, or aggregates.</summary>
    internal static string Text(RespireResult value)
    {
        if (value.Type is not (RespDataType.SimpleString or RespDataType.BulkString or RespDataType.VerbatimString))
            throw Invalid();
        return value.AsString();
    }

    /// <summary>Requires the exact successful mutation acknowledgement.</summary>
    internal static bool Ok(RespireResult value)
        => Text(value) == "OK" ? true : throw Invalid();

    /// <summary>Reads an integer count or epoch and rejects negative values.</summary>
    internal static long NonnegativeInteger(RespireResult value)
    {
        if (value.Type != RespDataType.Integer || value.AsInteger() < 0) throw Invalid();
        return value.AsInteger();
    }

    /// <summary>Requires positional array framing for row-oriented replies.</summary>
    private static void Array(RespireResult value)
    {
        if (value.Type != RespDataType.Array) throw Invalid();
    }

    /// <summary>Copies RESP2 pairs or RESP3 map entries, rejecting odd lengths and duplicate keys.</summary>
    internal static IReadOnlyDictionary<string, string> Fields(RespireResult value)
    {
        if (value.Type is not (RespDataType.Array or RespDataType.Map) || value.Count % 2 != 0) throw Invalid();
        Dictionary<string, string> fields = new(StringComparer.Ordinal);
        for (var index = 0; index < value.Count; index += 2)
            if (!fields.TryAdd(Text(value[index]), Text(value[index + 1]))) throw Invalid();
        return fields;
    }

    /// <summary>Reads a mandatory field without replacing missing server state with defaults.</summary>
    private static string Required(IReadOnlyDictionary<string, string> fields, string name)
        => fields.TryGetValue(name, out var value) ? value : throw Invalid();

    /// <summary>Parses a nonnegative scalar, accepting the legacy field name when its replacement is absent.</summary>
    private static long Number(IReadOnlyDictionary<string, string> fields, string name, string? legacy = null)
    {
        var text = fields.TryGetValue(name, out var value) ? value : Required(fields, legacy ?? name);
        return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) && number >= 0
            ? number : throw Invalid();
    }

    /// <summary>Reads a numeric field and rejects values outside the Int32 range.</summary>
    private static int IntNumber(IReadOnlyDictionary<string, string> fields, string name, string? legacy = null)
    {
        var number = Number(fields, name, legacy);
        return number <= int.MaxValue ? (int)number : throw Invalid();
    }

    /// <summary>Builds a reported endpoint only when its address and TCP port are valid.</summary>
    private static RespireEndpoint Endpoint(IReadOnlyDictionary<string, string> fields, string host = "ip", string port = "port")
    {
        var address = Required(fields, host);
        var number = IntNumber(fields, port);
        if (string.IsNullOrWhiteSpace(address) || number is < 1 or > 65535) throw Invalid();
        return new(address, number);
    }

    /// <summary>Copies primary identity, configuration, flags, and all scalar fields.</summary>
    internal static RespireSentinelPrimary Primary(RespireResult value)
    {
        var fields = Fields(value);
        return new(Required(fields, "name"), Endpoint(fields), Required(fields, "flags"),
            Number(fields, "config-epoch"), IntNumber(fields, "quorum"), fields);
    }

    /// <summary>Copies every primary row, including unhealthy primaries.</summary>
    internal static RespireSentinelPrimary[] Primaries(RespireResult value) => Rows(value, Primary);
    /// <summary>Copies every replica row, including disconnected replicas.</summary>
    internal static RespireSentinelReplica[] Replicas(RespireResult value) => Rows(value, Replica);
    /// <summary>Copies the Sentinel peers reported by this node.</summary>
    internal static RespireSentinelPeer[] Peers(RespireResult value) => Rows(value, Peer);

    /// <summary>Preserves replica state and represents an undiscovered upstream address as null.</summary>
    private static RespireSentinelReplica Replica(RespireResult value)
    {
        var fields = Fields(value);
        var primaryHost = fields.ContainsKey("primary-host") ? "primary-host" : "master-host";
        var primaryPort = fields.ContainsKey("primary-port") ? "primary-port" : "master-port";
        var host = Required(fields, primaryHost);
        var port = IntNumber(fields, primaryPort);
        if (string.IsNullOrWhiteSpace(host) || port > 65535) throw Invalid();
        RespireEndpoint? primary = host == "?" || port == 0 ? null : new(host, port);
        return new(Required(fields, "name"), Endpoint(fields), Required(fields, "flags"), primary,
            Number(fields, "replica-repl-offset", "slave-repl-offset"), IntNumber(fields, "replica-priority", "slave-priority"), fields);
    }

    /// <summary>Copies a peer endpoint, run ID, flags, and scalar attributes.</summary>
    private static RespireSentinelPeer Peer(RespireResult value)
    {
        var fields = Fields(value);
        return new(Required(fields, "name"), Endpoint(fields), Required(fields, "flags"), Required(fields, "runid"), fields);
    }

    /// <summary>Materializes an array of owned values using the command-specific row parser.</summary>
    private static T[] Rows<T>(RespireResult value, Func<RespireResult, T> parse)
    {
        Array(value);
        var result = new T[value.Count];
        for (var index = 0; index < result.Length; index++) result[index] = parse(value[index]);
        return result;
    }

    /// <summary>Flattens primary and replica INFO caches while preserving nullable text and server-reported ages.</summary>
    internal static RespireSentinelInfo[] InfoCache(RespireResult value)
    {
        if (value.Type is not (RespDataType.Array or RespDataType.Map) || value.Count % 2 != 0) throw Invalid();
        List<RespireSentinelInfo> result = [];
        for (var index = 0; index < value.Count; index += 2)
        {
            var name = Text(value[index]);
            var rows = value[index + 1];
            Array(rows);
            for (var row = 0; row < rows.Count; row++)
            {
                var item = rows[row];
                Array(item);
                if (item.Count != 2) throw Invalid();
                result.Add(new(name, NonnegativeInteger(item[0]), item[1].IsNull ? null : Text(item[1])));
            }
        }
        return result.ToArray();
    }

    /// <summary>Copies pending script rows and their owned argument arrays.</summary>
    internal static RespireSentinelScript[] Scripts(RespireResult value) => Rows(value, Script);

    /// <summary>Reads script scheduling fields, selecting runtime or delay according to the reported flags.</summary>
    private static RespireSentinelScript Script(RespireResult value)
    {
        if (value.Type is not (RespDataType.Array or RespDataType.Map) || value.Count % 2 != 0) throw Invalid();
        Dictionary<string, string> fields = new(StringComparer.Ordinal);
        string[]? arguments = null;
        for (var index = 0; index < value.Count; index += 2)
        {
            var name = Text(value[index]);
            if (name == "argv")
            {
                if (arguments is not null) throw Invalid();
                arguments = Rows(value[index + 1], Text);
            }
            else if (!fields.TryAdd(name, Text(value[index + 1]))) throw Invalid();
        }
        var flags = Required(fields, "flags");
        if (flags is not ("running" or "scheduled") || arguments is null) throw Invalid();
        return new(arguments, flags, Number(fields, "pid"), Number(fields, flags == "running" ? "run-time" : "run-delay"),
            Number(fields, "retry-num"));
    }

    /// <summary>Validates the down-state bit and copies the election leader and epoch.</summary>
    internal static RespireSentinelDownState DownState(RespireResult value)
    {
        Array(value);
        if (value.Count != 3) throw Invalid();
        var down = NonnegativeInteger(value[0]);
        if (down > 1) throw Invalid();
        return new(down == 1, Text(value[1]), NonnegativeInteger(value[2]));
    }

    /// <summary>Creates the shared protocol error for invalid Sentinel reply shapes.</summary>
    private static RespireProtocolException Invalid() => new("Malformed Sentinel reply.");
}
