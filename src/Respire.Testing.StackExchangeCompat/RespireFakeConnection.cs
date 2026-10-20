using Respire.StackExchangeCompat;
using StackExchange.Redis;

[assembly: System.Diagnostics.CodeAnalysis.Experimental("RSP0006", UrlFormat = "https://respire.tomlonghurst.dev/docs/diagnostics/{0}")]

namespace Respire.Testing.StackExchangeCompat;

/// <summary>Runs the supported StackExchange.Redis compatibility subset against an in-memory fake server.</summary>
/// <remarks>No sockets or containers are used. Only database zero is supported. Dispose this fixture
/// before a caller-supplied server. The fixture owns its client and multiplexer, but never a supplied server.</remarks>
public sealed class RespireFakeConnection : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly RespireConnectionMultiplexer _connection;
    private readonly bool _ownsServer;
    private Task? _disposeTask;

    /// <summary>Creates a connection with its own isolated fake server.</summary>
    public RespireFakeConnection() : this(new RespireFakeServer(), ownsServer: true) { }

    /// <summary>Creates a connection sharing a caller-owned server, including its clock, data and fault configuration.</summary>
    /// <param name="server">The server to borrow. It is not disposed by this fixture.</param>
    public RespireFakeConnection(RespireFakeServer server) : this(server, ownsServer: false) { }

    private RespireFakeConnection(RespireFakeServer server, bool ownsServer)
    {
        ArgumentNullException.ThrowIfNull(server);
        Server = server;
        _ownsServer = ownsServer;
        // Wrapping a database-zero client rejects other database selections immediately.
        // Create(options) would instead create another client and fail at SELECT during connection.
        var client = RespireClient.Create(server.CreateOptions());
        _connection = RespireConnectionMultiplexer.Wrap(client, ownsClient: true);
    }

    /// <summary>The fake server, available for deterministic clock, expectation and fault configuration.</summary>
    public RespireFakeServer Server { get; }

    /// <summary>The fixture-owned compatibility connection. It supports only database zero.</summary>
    public IConnectionMultiplexer Connection => _connection;

    /// <summary>Gets database zero. Throws after the connection has been disposed.</summary>
    public IDatabase Database => _connection.GetDatabase();

    /// <summary>Disposes the multiplexer and its clients, then the server if this fixture created it.</summary>
    /// <remarks>Repeated and concurrent calls join the same disposal operation.</remarks>
    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            return new(_disposeTask ??= DisposeCoreAsync());
        }
    }

    private async Task DisposeCoreAsync()
    {
        try { await _connection.DisposeAsync().ConfigureAwait(false); }
        finally
        {
            if (_ownsServer) await Server.DisposeAsync().ConfigureAwait(false);
        }
    }
}
