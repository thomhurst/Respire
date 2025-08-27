using System.Threading.Channels;

namespace Keva.Core.Connection;

public interface IConnectionPool : IAsyncDisposable
{
    int ActiveConnections { get; }
    int AvailableConnections { get; }
    int TotalConnections { get; }
    
    ValueTask<IKevaConnection> AcquireAsync(CancellationToken cancellationToken = default);
    ValueTask ReleaseAsync(IKevaConnection connection);
    ValueTask<bool> ValidateConnectionAsync(IKevaConnection connection, CancellationToken cancellationToken = default);
}

public class ConnectionPool : IConnectionPool
{
    private readonly ConnectionPoolOptions _options;
    private readonly Channel<IKevaConnection> _availableConnections;
    private readonly SemaphoreSlim _connectionSemaphore;
    private readonly HashSet<IKevaConnection> _allConnections;
    private readonly object _lock = new();
    private int _activeConnections;
    private bool _disposed;
    
    public int ActiveConnections => _activeConnections;
    public int AvailableConnections => _availableConnections.Reader.Count;
    public int TotalConnections => _allConnections.Count;
    
    public ConnectionPool(ConnectionPoolOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _availableConnections = Channel.CreateUnbounded<IKevaConnection>();
        _connectionSemaphore = new SemaphoreSlim(_options.MaxConnections, _options.MaxConnections);
        _allConnections = new HashSet<IKevaConnection>();
        
        // Pre-create minimum connections
        _ = Task.Run(async () =>
        {
            for (int i = 0; i < _options.MinConnections; i++)
            {
                try
                {
                    var connection = await CreateConnectionAsync();
                    await _availableConnections.Writer.WriteAsync(connection);
                }
                catch
                {
                    // Log error but continue
                }
            }
        });
    }
    
    public async ValueTask<IKevaConnection> AcquireAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        
        while (true)
        {
            // Try to get an existing connection
            if (_availableConnections.Reader.TryRead(out var connection))
            {
                if (await ValidateConnectionAsync(connection, cancellationToken))
                {
                    Interlocked.Increment(ref _activeConnections);
                    return connection;
                }
                
                // Connection is unhealthy, dispose it
                await RemoveConnectionAsync(connection);
            }
            
            // Try to create a new connection if under limit
            if (TotalConnections < _options.MaxConnections)
            {
                await _connectionSemaphore.WaitAsync(cancellationToken);
                try
                {
                    if (TotalConnections < _options.MaxConnections)
                    {
                        connection = await CreateConnectionAsync(cancellationToken);
                        Interlocked.Increment(ref _activeConnections);
                        return connection;
                    }
                }
                finally
                {
                    _connectionSemaphore.Release();
                }
            }
            
            // Wait for an available connection
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(_options.AcquireTimeout);
            
            try
            {
                connection = await _availableConnections.Reader.ReadAsync(cts.Token);
                if (await ValidateConnectionAsync(connection, cancellationToken))
                {
                    Interlocked.Increment(ref _activeConnections);
                    return connection;
                }
                
                await RemoveConnectionAsync(connection);
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException($"Failed to acquire connection within {_options.AcquireTimeout}");
            }
        }
    }
    
    public async ValueTask ReleaseAsync(IKevaConnection connection)
    {
        if (connection == null)
        {
            throw new ArgumentNullException(nameof(connection));
        }

        ThrowIfDisposed();
        
        Interlocked.Decrement(ref _activeConnections);
        
        if (!connection.IsConnected || !await ValidateConnectionAsync(connection))
        {
            await RemoveConnectionAsync(connection);
            return;
        }
        
        // Check if connection has been idle too long
        if (_options.ConnectionIdleTimeout.HasValue && 
            DateTime.UtcNow - connection.LastActivity > _options.ConnectionIdleTimeout.Value)
        {
            await RemoveConnectionAsync(connection);
            return;
        }
        
        // Return to pool
        await _availableConnections.Writer.WriteAsync(connection);
    }
    
    public async ValueTask<bool> ValidateConnectionAsync(IKevaConnection connection, CancellationToken cancellationToken = default)
    {
        if (connection == null || !connection.IsConnected)
        {
            return false;
        }

        try
        {
            return await connection.IsHealthyAsync(cancellationToken);
        }
        catch
        {
            return false;
        }
    }
    
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        
        _availableConnections.Writer.TryComplete();
        
        List<IKevaConnection> connections;
        lock (_lock)
        {
            connections = _allConnections.ToList();
            _allConnections.Clear();
        }
        
        var disposeTasks = connections.Select(c => c.DisposeAsync().AsTask());
        await Task.WhenAll(disposeTasks);
        
        _connectionSemaphore.Dispose();
    }
    
    private async ValueTask<IKevaConnection> CreateConnectionAsync(CancellationToken cancellationToken = default)
    {
        var connection = new RespConnection(_options.ConnectionOptions);
        
        if (!await connection.ConnectAsync(cancellationToken))
        {
            throw new InvalidOperationException("Failed to establish connection");
        }
        
        lock (_lock)
        {
            _allConnections.Add(connection);
        }
        
        // Subscribe to connection events
        connection.StateChanged += OnConnectionStateChanged;
        connection.Failed += OnConnectionFailed;
        
        return connection;
    }
    
    private async ValueTask RemoveConnectionAsync(IKevaConnection connection)
    {
        lock (_lock)
        {
            _allConnections.Remove(connection);
        }
        
        connection.StateChanged -= OnConnectionStateChanged;
        connection.Failed -= OnConnectionFailed;
        
        await connection.DisposeAsync();
    }
    
    private void OnConnectionStateChanged(object? sender, ConnectionStateChangedEventArgs e)
    {
        // Handle connection state changes
        if (e.NewState == ConnectionState.Failed || e.NewState == ConnectionState.Closed)
        {
            if (sender is IKevaConnection connection)
            {
                _ = Task.Run(() => RemoveConnectionAsync(connection));
            }
        }
    }
    
    private void OnConnectionFailed(object? sender, ConnectionFailedEventArgs e)
    {
        // Log failure
        // In production, we'd use ILogger here
    }
    
    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(ConnectionPool));
        }
    }
}

public class ConnectionPoolOptions
{
    public int MinConnections { get; set; } = 2;
    public int MaxConnections { get; set; } = 10;
    public TimeSpan AcquireTimeout { get; set; } = TimeSpan.FromSeconds(5);
    public TimeSpan? ConnectionIdleTimeout { get; set; } = TimeSpan.FromMinutes(10);
    public ConnectionOptions ConnectionOptions { get; set; } = new();
}