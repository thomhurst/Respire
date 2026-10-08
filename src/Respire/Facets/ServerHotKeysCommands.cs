using Respire.Commands;
using Respire.Internal;
using Respire.Networking;
using Respire.Protocol;

namespace Respire;

public partial interface IServerCommands
{
    /// <summary>Pins an existing connection for Redis 8.6+ node-local HOTKEYS operations; does not start tracking.</summary>
    /// <remarks>The handle never switches connections or nodes. Reacquire explicitly after disconnection.</remarks>
    ValueTask<RespireHotKeysTracker> GetHotKeysTrackerAsync(CancellationToken cancellationToken = default);
    /// <summary>Starts tracking separately on every currently discovered node. Requires AllowAdmin; true means OK.</summary>
    /// <remarks>Includes replicas. Sessions are shared with other users, not owned by this client. Partial success is possible.</remarks>
    ValueTask<RespireServerResult<bool>[]> StartHotKeysOnAllNodesAsync(RespireHotKeysOptions options, CancellationToken cancellationToken = default);
    /// <summary>Returns each currently discovered node's owned snapshots; null means no session on that node.</summary>
    /// <remarks>Does not require AllowAdmin; server ACLs still apply. Discovery can change between calls.
    /// Compare endpoints; results are not a global ranking. Snapshot arrays preserve the server's outer array
    /// without assuming that future servers always return exactly one element.</remarks>
    ValueTask<RespireServerResult<RespireHotKeysSnapshot[]?>[]> GetHotKeysOnAllNodesAsync(CancellationToken cancellationToken = default);
    /// <summary>Stops tracking on every currently discovered node. Requires AllowAdmin; false means no active session.</summary>
    ValueTask<RespireServerResult<bool>[]> StopHotKeysOnAllNodesAsync(CancellationToken cancellationToken = default);
    /// <summary>Releases stopped tracking data on every currently discovered node. Requires AllowAdmin; true means OK.</summary>
    /// <remarks>Active sessions fail on the server. No rollback, replication, or automatic stop is performed.</remarks>
    ValueTask<RespireServerResult<bool>[]> ResetHotKeysOnAllNodesAsync(CancellationToken cancellationToken = default);
}

internal sealed partial class ServerCommands
{
    public async ValueTask<RespireHotKeysTracker> GetHotKeysTrackerAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var connection = await client.AcquireConnectionAsync(cancellationToken).ConfigureAwait(false);
        return new(client, connection);
    }

    public ValueTask<RespireServerResult<bool>[]> StartHotKeysOnAllNodesAsync(RespireHotKeysOptions options, CancellationToken cancellationToken = default)
    {
        EnsureAdminAllowed("HOTKEYS START");
        ArgumentNullException.ThrowIfNull(options);
        var command = options.BuildCommand();
        cancellationToken.ThrowIfCancellationRequested();
        return FanOutAsync("HOTKEYS START", command, cancellationToken,
            static (ServerCommands _, in RespValue value) => HotKeysParser.Ok(in value));
    }

    public ValueTask<RespireServerResult<RespireHotKeysSnapshot[]?>[]> GetHotKeysOnAllNodesAsync(CancellationToken cancellationToken = default)
        => FanOutAsync("HOTKEYS GET", new Cmd(HotKeysCommands.Get), cancellationToken,
            static (ServerCommands _, in RespValue value) => HotKeysParser.Parse(in value));

    public ValueTask<RespireServerResult<bool>[]> StopHotKeysOnAllNodesAsync(CancellationToken cancellationToken = default)
        => MutateHotKeysOnAllNodesAsync("HOTKEYS STOP", new Cmd(HotKeysCommands.Stop), cancellationToken,
            static (ServerCommands _, in RespValue value) => HotKeysParser.Stopped(in value));

    public ValueTask<RespireServerResult<bool>[]> ResetHotKeysOnAllNodesAsync(CancellationToken cancellationToken = default)
        => MutateHotKeysOnAllNodesAsync("HOTKEYS RESET", new Cmd(HotKeysCommands.Reset), cancellationToken,
            static (ServerCommands _, in RespValue value) => HotKeysParser.Ok(in value));

    private ValueTask<RespireServerResult<bool>[]> MutateHotKeysOnAllNodesAsync<TCommand>(string operation,
        TCommand command, CancellationToken cancellationToken, ResponseConverter<ServerCommands, bool> convert)
        where TCommand : struct, IRespCommand
    {
        EnsureAdminAllowed(operation);
        cancellationToken.ThrowIfCancellationRequested();
        return FanOutAsync(operation, command, cancellationToken, convert);
    }
}

/// <summary>A pinned connection to one server's shared HOTKEYS tracker. Requires Redis 8.6+.</summary>
/// <remarks>This handle neither owns the socket nor reserves the server's tracking session. Other clients can
/// stop, reset, or replace that session. The connection remains shared with ordinary client commands;
/// holding a tracker does not reserve pool capacity. It never reconnects, reroutes, retries writes,
/// or automatically stops tracking.</remarks>
public sealed class RespireHotKeysTracker
{
    private readonly RespireClient _client;
    private readonly RespireConnection _connection;

    internal RespireHotKeysTracker(RespireClient client, RespireConnection connection)
    {
        _client = client;
        _connection = connection;
        Endpoint = new(connection.Host, connection.Port);
    }

    /// <summary>The endpoint of the pinned physical connection.</summary>
    public RespireEndpoint Endpoint { get; }
    /// <summary>A point-in-time observation of the original connection, not a guarantee of future availability.</summary>
    public bool IsConnected => !_client.Core.Disposed && _connection.IsConnected;

    /// <summary>Starts a shared node-local session. Requires AllowAdmin; an active session fails on the server.</summary>
    public async ValueTask StartAsync(RespireHotKeysOptions options, CancellationToken cancellationToken = default)
    {
        // The owner covers handle preflight and transfers into conversion.
        var observation = RespireTelemetry.ErrorObservation.Rent(force: true);
        CmdN command;
        try
        {
            EnsureAdmin("HOTKEYS START");
            ArgumentNullException.ThrowIfNull(options);
            command = options.BuildCommand();
        }
        catch (Exception error) { observation.Final(error); observation.Dispose(); throw; }
        _ = await ConvertAsync("HOTKEYS START", command, cancellationToken,
            static (RespireHotKeysTracker _, in RespValue reply) => HotKeysParser.Ok(in reply), observation).ConfigureAwait(false);
    }

    /// <summary>Returns owned snapshot maps in server order, or null when no session exists. Does not stop tracking.</summary>
    /// <remarks>Does not require AllowAdmin; server ACLs still apply. The array preserves the server's outer
    /// reply without assuming that future servers always return exactly one snapshot.</remarks>
    public async ValueTask<RespireHotKeysSnapshot[]?> GetAsync(CancellationToken cancellationToken = default)
        => await ConvertAsync("HOTKEYS GET", new Cmd(HotKeysCommands.Get), cancellationToken,
            static (RespireHotKeysTracker _, in RespValue reply) => HotKeysParser.Parse(in reply)).ConfigureAwait(false);

    /// <summary>Stops collection, retaining its data. Requires AllowAdmin; false means no active session.</summary>
    public async ValueTask<bool> StopAsync(CancellationToken cancellationToken = default)
    {
        var observation = AdminObservation("HOTKEYS STOP");
        return await ConvertAsync("HOTKEYS STOP", new Cmd(HotKeysCommands.Stop), cancellationToken,
            static (RespireHotKeysTracker _, in RespValue reply) => HotKeysParser.Stopped(in reply), observation).ConfigureAwait(false);
    }

    /// <summary>Releases stopped session data. Requires AllowAdmin; an active session fails on the server.</summary>
    public async ValueTask ResetAsync(CancellationToken cancellationToken = default)
    {
        var observation = AdminObservation("HOTKEYS RESET");
        _ = await ConvertAsync("HOTKEYS RESET", new Cmd(HotKeysCommands.Reset), cancellationToken,
            static (RespireHotKeysTracker _, in RespValue reply) => HotKeysParser.Ok(in reply), observation).ConfigureAwait(false);
    }

    // Starts the final owner before AllowAdmin preflight; conversion takes ownership on success.
    private RespireTelemetry.ErrorObservation AdminObservation(string operation)
    {
        var observation = RespireTelemetry.ErrorObservation.Rent(force: true);
        try { EnsureAdmin(operation); }
        catch (Exception error) { observation.Final(error); observation.Dispose(); throw; }
        return observation;
    }

    private ValueTask<TResult> ConvertAsync<TCommand, TResult>(string operation, TCommand command,
        CancellationToken cancellationToken, ResponseConverter<RespireHotKeysTracker, TResult> converter,
        RespireTelemetry.ErrorObservation observation = default)
        where TCommand : struct, IRespCommand
    {
        return _client.ConvertOnConnectionAsync(operation, _connection, command, cancellationToken,
            this, converter, pinToConnection: false, observation: observation);
    }

    private void EnsureAdmin(string operation)
    {
        if (!_client.Core.Options.AllowAdmin)
            throw new NotSupportedException($"{operation} requires RespireOptions.AllowAdmin = true.");
    }
}
