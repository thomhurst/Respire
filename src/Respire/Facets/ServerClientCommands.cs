using Respire.Commands;
using Respire.Internal;
using Respire.Networking;
using Respire.Protocol;

namespace Respire;

/// <summary>The client library attribute changed by CLIENT SETINFO.</summary>
public enum RespireClientInfoAttribute
{
    /// <summary>The library name.</summary>
    LibraryName,
    /// <summary>The library version.</summary>
    LibraryVersion,
}

/// <summary>Commands suspended by CLIENT PAUSE.</summary>
public enum RespireClientPauseMode
{
    /// <summary>Pause writes (Redis 6.2+).</summary>
    Write,
    /// <summary>Pause all commands.</summary>
    All,
}

/// <summary>The reply delivered to a client released by CLIENT UNBLOCK.</summary>
public enum RespireClientUnblockMode
{
    /// <summary>Complete as if its blocking timeout expired.</summary>
    Timeout,
    /// <summary>Complete with an UNBLOCKED server error.</summary>
    Error,
}

/// <summary>Owned CLIENT TRACKINGINFO data, including unrecognized flags and fields.</summary>
/// <remarks>Field results have GC-owned storage; disposal is optional. Prefix bytes are not key-prefixed.</remarks>
public sealed record RespireClientTrackingInfo(
    string[] Flags, long RedirectClientId, byte[][] Prefixes, IReadOnlyDictionary<string, RespireResult> Fields);

public partial interface IServerCommands
{
    /// <summary>Identifies and pins one existing multiplexed physical connection using CLIENT ID (Redis 5+).</summary>
    /// <remarks>The returned handle does not own or reserve the socket. It never switches to a replacement connection.</remarks>
    ValueTask<RespireServerClientConnection> GetClientConnectionAsync(CancellationToken cancellationToken = default);

    /// <summary>Lists clients separately on every discovered Cluster member, including replicas; one result on standalone.</summary>
    /// <remarks>Discovery cancellation throws. Later failures, including cancellation, are returned per endpoint.</remarks>
    ValueTask<RespireServerResult<RespireServerClientInfo[]>[]> ClientsOnAllNodesAsync(CancellationToken cancellationToken = default);
}

internal sealed partial class ServerCommands
{
    private static readonly Verb ClientIdVerb = new(-1, "CLIENT", "ID");

    public async ValueTask<RespireServerClientConnection> GetClientConnectionAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var connection = await client.AcquireConnectionAsync(cancellationToken).ConfigureAwait(false);
        using var reply = await client.SendOnConnectionAsync("CLIENT ID", connection, new Cmd(ClientIdVerb), cancellationToken).ConfigureAwait(false);
        if (reply.Type != RespDataType.Integer || reply.AsInteger() <= 0)
            throw new RespireProtocolException("CLIENT ID must return a positive integer.");
        return new RespireServerClientConnection(client, connection, reply.AsInteger());
    }

    public ValueTask<RespireServerResult<RespireServerClientInfo[]>[]> ClientsOnAllNodesAsync(CancellationToken cancellationToken = default)
        => FanOutAsync("CLIENT LIST", new Cmd(Verbs.ClientList), cancellationToken,
            static (ServerCommands _, in RespValue value) => ParseClientList(in value));

    internal static RespireServerClientInfo ParseClientInfo(in RespValue value)
    {
        if (value.Type is not (RespDataType.BulkString or RespDataType.SimpleString or RespDataType.VerbatimString))
            throw new RespireProtocolException("CLIENT INFO must return client information text.");
        var rows = ParseClientList(in value);
        if (rows.Length != 1 || rows[0].Id <= 0)
            throw new RespireProtocolException("CLIENT INFO must return exactly one identified client.");
        return rows[0];
    }
}

/// <summary>Administration of one identified physical connection and its server.</summary>
/// <remarks>
/// The parent client owns this multiplexed socket. Other application operations may share it.
/// Settings apply only to this ID until changed or disconnected; they are not client-wide defaults.
/// The handle neither reserves nor reconnects the socket, and must be reacquired after disconnection.
/// </remarks>
public sealed class RespireServerClientConnection
{
    private readonly RespireClient _client;
    private readonly RespireConnection _connection;
    private static readonly Verb ClientInfo = new(-1, "CLIENT", "INFO");
    private static readonly Verb ClientGetName = new(-1, "CLIENT", "GETNAME");
    private static readonly Verb ClientSetInfo = new(-1, "CLIENT", "SETINFO");
    private static readonly Verb ClientNoEvict = new(-1, "CLIENT", "NO-EVICT");
    private static readonly Verb ClientNoTouch = new(-1, "CLIENT", "NO-TOUCH");
    private static readonly Verb ClientPause = new(-1, "CLIENT", "PAUSE");
    private static readonly Verb ClientUnpause = new(-1, "CLIENT", "UNPAUSE");
    private static readonly Verb ClientUnblock = new(-1, "CLIENT", "UNBLOCK");
    private static readonly Verb ClientTrackingInfo = new(-1, "CLIENT", "TRACKINGINFO");
    private static readonly Verb Echo = new(-1, "ECHO");

    internal RespireServerClientConnection(RespireClient client, RespireConnection connection, long id)
    {
        _client = client;
        _connection = connection;
        Id = id;
        Endpoint = new RespireEndpoint(connection.Host, connection.Port);
    }

    /// <summary>The CLIENT ID captured when this handle was created. IDs are local to Endpoint.</summary>
    public long Id { get; }
    /// <summary>The endpoint of the pinned socket.</summary>
    public RespireEndpoint Endpoint { get; }
    /// <summary>Whether the original socket is still connected; this is a point-in-time observation.</summary>
    public bool IsConnected => !_client.Core.Disposed && _connection.IsConnected;

    /// <summary>Reads owned CLIENT INFO, preserving unknown attributes. Requires Redis 6.2+.</summary>
    public async ValueTask<RespireServerClientInfo> InfoAsync(CancellationToken cancellationToken = default)
    {
        using var reply = await SendAsync("CLIENT INFO", new Cmd(ClientInfo), cancellationToken).ConfigureAwait(false);
        return ServerCommands.ParseClientInfo(in reply);
    }

    /// <summary>Reads this connection's name, or null when unset. Redis: CLIENT GETNAME (2.6.9+).</summary>
    public async ValueTask<string?> GetNameAsync(CancellationToken cancellationToken = default)
    {
        using var reply = await SendAsync("CLIENT GETNAME", new Cmd(ClientGetName), cancellationToken).ConfigureAwait(false);
        return ResponseReader.StringOrNull(in reply);
    }

    /// <summary>Changes this connection's library metadata. Requires AllowAdmin and Redis 7.2+.</summary>
    public ValueTask SetInfoAsync(RespireClientInfoAttribute attribute, string value, CancellationToken cancellationToken = default)
    {
        EnsureAdmin("CLIENT SETINFO");
        ArgumentNullException.ThrowIfNull(value);
        var name = attribute switch
        {
            RespireClientInfoAttribute.LibraryName => "LIB-NAME",
            RespireClientInfoAttribute.LibraryVersion => "LIB-VER",
            _ => throw new ArgumentOutOfRangeException(nameof(attribute)),
        };
        return OkAsync("CLIENT SETINFO", new Cmd2(ClientSetInfo, name, value), cancellationToken);
    }

    /// <summary>Changes this connection's exemption from client eviction. Requires AllowAdmin and Redis 7+.</summary>
    public ValueTask SetNoEvictAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        EnsureAdmin("CLIENT NO-EVICT");
        return OkAsync("CLIENT NO-EVICT", new Cmd1(ClientNoEvict, enabled ? "ON" : "OFF"), cancellationToken);
    }

    /// <summary>Controls this connection's LRU/LFU touches. Requires AllowAdmin and Redis 7.2+.</summary>
    public ValueTask SetNoTouchAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        EnsureAdmin("CLIENT NO-TOUCH");
        return OkAsync("CLIENT NO-TOUCH", new Cmd1(ClientNoTouch, enabled ? "ON" : "OFF"), cancellationToken);
    }

    /// <summary>Pauses commands on this endpoint for whole milliseconds. Requires AllowAdmin and Redis 6.2+.</summary>
    /// <remarks>This affects all clients of this server. Commands queued before UNPAUSE on this socket can delay unpausing.</remarks>
    public ValueTask PauseClientsAsync(TimeSpan duration, RespireClientPauseMode mode = RespireClientPauseMode.Write,
        CancellationToken cancellationToken = default)
    {
        EnsureAdmin("CLIENT PAUSE");
        if (duration < TimeSpan.Zero || duration.Ticks % TimeSpan.TicksPerMillisecond != 0)
            throw new ArgumentOutOfRangeException(nameof(duration), "Pause duration must be nonnegative whole milliseconds.");
        var argument = mode switch
        {
            RespireClientPauseMode.Write => "WRITE", RespireClientPauseMode.All => "ALL",
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };
        return OkAsync("CLIENT PAUSE", new Cmd2(ClientPause, duration.Ticks / TimeSpan.TicksPerMillisecond, argument), cancellationToken);
    }

    /// <summary>Ends CLIENT PAUSE on this endpoint. Requires AllowAdmin and Redis 6.2+.</summary>
    public ValueTask UnpauseClientsAsync(CancellationToken cancellationToken = default)
    {
        EnsureAdmin("CLIENT UNPAUSE");
        return OkAsync("CLIENT UNPAUSE", new Cmd(ClientUnpause), cancellationToken);
    }

    /// <summary>Unblocks an ID on this endpoint; returns false if it was not blocked. Requires AllowAdmin and Redis 5+.</summary>
    public async ValueTask<bool> UnblockClientAsync(long clientId, RespireClientUnblockMode mode = RespireClientUnblockMode.Timeout,
        CancellationToken cancellationToken = default)
    {
        EnsureAdmin("CLIENT UNBLOCK");
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(clientId);
        var argument = mode switch
        {
            RespireClientUnblockMode.Timeout => "TIMEOUT", RespireClientUnblockMode.Error => "ERROR",
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };
        using var reply = await SendAsync("CLIENT UNBLOCK", new Cmd2(ClientUnblock, clientId, argument), cancellationToken).ConfigureAwait(false);
        if (reply.Type != RespDataType.Integer || reply.AsInteger() is not (0 or 1))
            throw new RespireProtocolException("CLIENT UNBLOCK must return 0 or 1.");
        return reply.AsInteger() == 1;
    }

    /// <summary>Reads this connection's tracking configuration without changing internal caching. Redis 6.2+.</summary>
    public async ValueTask<RespireClientTrackingInfo> TrackingInfoAsync(CancellationToken cancellationToken = default)
    {
        using var reply = await SendAsync("CLIENT TRACKINGINFO", new Cmd(ClientTrackingInfo), cancellationToken).ConfigureAwait(false);
        if (reply.Type is not (RespDataType.Array or RespDataType.Map))
            throw new RespireProtocolException("CLIENT TRACKINGINFO must return field/value pairs.");
        var parts = reply.AsArray();
        if (parts.Length % 2 != 0) throw new RespireProtocolException("CLIENT TRACKINGINFO contains an incomplete field.");
        var fields = new Dictionary<string, RespireResult>(StringComparer.Ordinal);
        for (var index = 0; index < parts.Length; index += 2)
            fields.Add(parts[index].AsString(), new RespireResult(parts[index + 1].ToOwned()));
        if (!fields.TryGetValue("flags", out var flags) || !fields.TryGetValue("redirect", out var redirect)
            || !fields.TryGetValue("prefixes", out var prefixes))
            throw new RespireProtocolException("CLIENT TRACKINGINFO is missing required fields.");
        if (flags.Type is not (RespDataType.Array or RespDataType.Set) || prefixes.Type is not (RespDataType.Array or RespDataType.Set)
            || redirect.Type != RespDataType.Integer)
            throw new RespireProtocolException("CLIENT TRACKINGINFO contains invalid field types.");
        return new(flags.Select(value => value.AsString()).ToArray(), redirect.AsInteger(),
            prefixes.Select(value => value.AsBytes()).ToArray(), fields);
    }

    /// <summary>Echoes binary bytes on this connection. The returned bytes are owned; no key prefix is applied.</summary>
    public async ValueTask<byte[]> EchoAsync(ReadOnlyMemory<byte> value, CancellationToken cancellationToken = default)
    {
        using var reply = await SendAsync("ECHO", new Cmd1(Echo, value), cancellationToken).ConfigureAwait(false);
        if (reply.Type != RespDataType.BulkString) throw new RespireProtocolException("ECHO must return a bulk string.");
        return reply.AsSpan().ToArray();
    }

    private ValueTask<RespValue> SendAsync<TCommand>(string operation, TCommand command, CancellationToken cancellationToken)
        where TCommand : struct, IRespCommand
    {
        ObjectDisposedException.ThrowIf(_client.Core.Disposed, _client);
        cancellationToken.ThrowIfCancellationRequested();
        return _client.SendOnConnectionAsync(operation, _connection, command, cancellationToken);
    }

    private async ValueTask OkAsync<TCommand>(string operation, TCommand command, CancellationToken cancellationToken)
        where TCommand : struct, IRespCommand
    {
        using var reply = await SendAsync(operation, command, cancellationToken).ConfigureAwait(false);
        ResponseReader.ExpectOk(in reply);
    }

    private void EnsureAdmin(string operation)
    {
        if (!_client.Core.Options.AllowAdmin)
            throw new NotSupportedException($"{operation} requires RespireOptions.AllowAdmin = true.");
    }
}

