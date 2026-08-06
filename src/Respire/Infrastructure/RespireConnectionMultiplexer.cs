using Microsoft.Extensions.Logging;
using Respire.Networking;
using Respire.Protocol;

namespace Respire.Infrastructure;

/// <summary>
/// Round-robins commands across a fixed set of fully multiplexed <see cref="RespireConnection"/>s.
/// Every connection pipelines concurrent commands, so there is no per-command checkout — a dead
/// connection is skipped and replaced in the background.
/// </summary>
public sealed class RespireConnectionMultiplexer : IAsyncDisposable
{
    private readonly RespireConnection[] _connections;
    private readonly int[] _reconnecting;
    private readonly RespireConnectionOptions _options;
    private readonly ILogger? _logger;
    private uint _next;
    private int _disposed;

    public string Host { get; }
    public int Port { get; }
    public int ConnectionCount => _connections.Length;

    /// <summary>The options every connection (and any subscriber) is built from.</summary>
    public RespireConnectionOptions Options => _options;

    public bool IsConnected
    {
        get
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return false;
            }

            foreach (var connection in _connections)
            {
                if (connection.IsConnected)
                {
                    return true;
                }
            }

            return false;
        }
    }

    private RespireConnectionMultiplexer(
        RespireConnection[] connections, string host, int port, RespireConnectionOptions options, ILogger? logger)
    {
        _connections = connections;
        Host = host;
        Port = port;
        _options = options;
        _logger = logger;
        _reconnecting = new int[connections.Length];
    }

    public static async Task<RespireConnectionMultiplexer> CreateAsync(
        string host,
        int port = 6379,
        int connectionCount = 0,
        RespireConnectionOptions? options = null,
        ILogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        options ??= RespireConnectionOptions.Default;
        if (connectionCount <= 0)
        {
            connectionCount = Math.Clamp(Environment.ProcessorCount / 2, 1, 8);
        }

        var connectTasks = new Task<RespireConnection>[connectionCount];
        for (var i = 0; i < connectionCount; i++)
        {
            connectTasks[i] = RespireConnection.ConnectAsync(host, port, options, logger, cancellationToken);
        }

        try
        {
            var connections = await Task.WhenAll(connectTasks).ConfigureAwait(false);
            return new RespireConnectionMultiplexer(connections, host, port, options, logger);
        }
        catch
        {
            foreach (var task in connectTasks)
            {
                if (task.IsCompletedSuccessfully)
                {
                    await task.Result.DisposeAsync().ConfigureAwait(false);
                }
            }

            throw;
        }
    }

    /// <summary>Returns the next healthy connection, scheduling replacement of any dead ones seen.</summary>
    public RespireConnection GetConnection()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        var count = _connections.Length;
        var startIndex = Interlocked.Increment(ref _next);
        for (var i = 0; i < count; i++)
        {
            var slot = (int)((startIndex + (uint)i) % (uint)count);
            var connection = Volatile.Read(ref _connections[slot]);
            if (connection.IsConnected)
            {
                return connection;
            }

            ScheduleReconnect(slot);
        }

        throw new RespireConnectionException($"No healthy connections to {Host}:{Port}.");
    }

    public ValueTask<RespireValue> SendAsync<TCommand>(in TCommand command, CancellationToken cancellationToken = default)
        where TCommand : struct, IRespCommand
        => GetConnection().SendAsync(in command, cancellationToken);

    public ValueTask SendFireAndForgetAsync<TCommand>(in TCommand command, CancellationToken cancellationToken = default)
        where TCommand : struct, IRespCommand
        => GetConnection().SendFireAndForgetAsync(in command, cancellationToken);

    /// <summary>Runs a MULTI/EXEC block on one connection; see <see cref="RespireConnection.SendTransactionAsync"/>.</summary>
    public ValueTask<RespireValue> SendTransactionAsync(
        ReadOnlyMemory<byte> serializedCommands, int commandCount, CancellationToken cancellationToken = default)
        => GetConnection().SendTransactionAsync(serializedCommands, commandCount, cancellationToken);

    private void ScheduleReconnect(int slot)
    {
        if (Interlocked.CompareExchange(ref _reconnecting[slot], 1, 0) != 0)
        {
            return;
        }

        _ = ReconnectAsync(slot);
    }

    private async Task ReconnectAsync(int slot)
    {
        try
        {
            var replacement = await RespireConnection.ConnectAsync(Host, Port, _options, _logger).ConfigureAwait(false);
            if (Volatile.Read(ref _disposed) != 0)
            {
                await replacement.DisposeAsync().ConfigureAwait(false);
                return;
            }

            var old = Interlocked.Exchange(ref _connections[slot], replacement);
            _logger?.LogInformation("Replaced dead connection {Slot} to {Host}:{Port}", slot, Host, Port);
            await old.DisposeAsync().ConfigureAwait(false);

            // Disposal may have swept the array between the pre-check above and the exchange,
            // missing the just-published replacement. DisposeAsync sets _disposed before it
            // sweeps, so if the flag is still clear here the sweep is guaranteed to see the
            // replacement; otherwise take it back out and dispose it ourselves (double
            // dispose is idempotent).
            if (Volatile.Read(ref _disposed) != 0)
            {
                Interlocked.Exchange(ref _connections[slot], old);
                await replacement.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Reconnect to {Host}:{Port} failed; will retry on next use", Host, Port);
        }
        finally
        {
            Volatile.Write(ref _reconnecting[slot], 0);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        foreach (var connection in _connections)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }
    }
}
