using Keva.Core.Connection;
using Keva.Core.Pipeline;
using Keva.Core.Protocol;

namespace Keva.Client;

public interface IKevaClient : IAsyncDisposable
{
    bool IsConnected { get; }
    
    // Basic commands
    ValueTask<string?> GetAsync(string key, CancellationToken cancellationToken = default);
    ValueTask<bool> SetAsync(string key, string value, TimeSpan? expiry = null, CancellationToken cancellationToken = default);
    ValueTask<bool> DeleteAsync(string key, CancellationToken cancellationToken = default);
    ValueTask<bool> ExistsAsync(string key, CancellationToken cancellationToken = default);
    ValueTask<long> IncrementAsync(string key, long by = 1, CancellationToken cancellationToken = default);
    ValueTask<long> DecrementAsync(string key, long by = 1, CancellationToken cancellationToken = default);
    ValueTask<long> IncrAsync(string key, CancellationToken cancellationToken = default);
    ValueTask<string?[]> MGetAsync(params string[] keys);
    ValueTask<bool> DelAsync(string key, CancellationToken cancellationToken = default);
    ValueTask<bool> PingAsync(CancellationToken cancellationToken = default);
    
    // Generic command execution
    ValueTask<RespValue> ExecuteAsync(string command, params string[] args);
    ValueTask<RespValue> ExecuteAsync(ReadOnlyMemory<byte> command, CancellationToken cancellationToken = default);
    ValueTask<T> ExecuteAsync<T>(string command, Func<RespValue, T> resultMapper, params string[] args);
}

public class KevaClient : IKevaClient
{
    private readonly IConnectionPool _connectionPool;
    private readonly InterceptorChain _interceptorChain;
    private readonly KevaClientOptions _options;
    private bool _disposed;
    
    public bool IsConnected => _connectionPool.ActiveConnections > 0;
    
    internal KevaClient(
        IConnectionPool connectionPool,
        InterceptorChain interceptorChain,
        KevaClientOptions options)
    {
        _connectionPool = connectionPool ?? throw new ArgumentNullException(nameof(connectionPool));
        _interceptorChain = interceptorChain ?? throw new ArgumentNullException(nameof(interceptorChain));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }
    
    public async ValueTask<string?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(key))
            throw new ArgumentNullException(nameof(key));
        
        var response = await ExecuteAsync("GET", key);
        
        if (response.IsNull)
            return null;
            
        return response.AsString();
    }
    
    public async ValueTask<bool> SetAsync(string key, string value, TimeSpan? expiry = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(key))
            throw new ArgumentNullException(nameof(key));
        
        RespValue response;
        
        if (expiry.HasValue)
        {
            var milliseconds = (long)expiry.Value.TotalMilliseconds;
            response = await ExecuteAsync("SET", key, value, "PX", milliseconds.ToString());
        }
        else
        {
            response = await ExecuteAsync("SET", key, value);
        }
        
        return response.Type == RespDataType.SimpleString && response.AsString() == "OK";
    }
    
    public async ValueTask<bool> DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(key))
            throw new ArgumentNullException(nameof(key));
        
        var response = await ExecuteAsync("DEL", key);
        return response.Type == RespDataType.Integer && response.AsInteger() > 0;
    }
    
    public async ValueTask<bool> ExistsAsync(string key, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(key))
            throw new ArgumentNullException(nameof(key));
        
        var response = await ExecuteAsync("EXISTS", key);
        return response.Type == RespDataType.Integer && response.AsInteger() > 0;
    }
    
    public async ValueTask<long> IncrementAsync(string key, long by = 1, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(key))
            throw new ArgumentNullException(nameof(key));
        
        RespValue response;
        
        if (by == 1)
        {
            response = await ExecuteAsync("INCR", key);
        }
        else
        {
            response = await ExecuteAsync("INCRBY", key, by.ToString());
        }
        
        return response.AsInteger();
    }
    
    public async ValueTask<long> DecrementAsync(string key, long by = 1, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(key))
            throw new ArgumentNullException(nameof(key));
        
        RespValue response;
        
        if (by == 1)
        {
            response = await ExecuteAsync("DECR", key);
        }
        else
        {
            response = await ExecuteAsync("DECRBY", key, by.ToString());
        }
        
        return response.AsInteger();
    }
    
    public async ValueTask<long> IncrAsync(string key, CancellationToken cancellationToken = default)
    {
        return await IncrementAsync(key, 1, cancellationToken);
    }
    
    public async ValueTask<string?[]> MGetAsync(params string[] keys)
    {
        if (keys == null || keys.Length == 0)
            throw new ArgumentException("At least one key must be provided", nameof(keys));
        
        var response = await ExecuteAsync("MGET", keys);
        
        if (response.Type != RespDataType.Array)
            throw new InvalidOperationException($"Expected array response, got {response.Type}");
        
        var array = response.AsArray();
        var results = new string?[array.Length];
        
        for (int i = 0; i < array.Length; i++)
        {
            if (array.Span[i].IsNull)
            {
                results[i] = null;
            }
            else
            {
                results[i] = array.Span[i].AsString();
            }
        }
        
        return results;
    }
    
    public async ValueTask<bool> DelAsync(string key, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(key))
            throw new ArgumentNullException(nameof(key));
        
        var response = await ExecuteAsync("DEL", key);
        return response.AsInteger() > 0;
    }
    
    public async ValueTask<bool> PingAsync(CancellationToken cancellationToken = default)
    {
        var response = await ExecuteAsync("PING");
        return response.Type == RespDataType.SimpleString && response.AsString() == "PONG";
    }
    
    public async ValueTask<RespValue> ExecuteAsync(string command, params string[] args)
    {
        ThrowIfDisposed();
        
        var commandInfo = new CommandInfo(command, args);
        
        // Execute through interceptor chain
        return await _interceptorChain.ExecuteAsync(commandInfo, CancellationToken.None);
    }
    
    public async ValueTask<RespValue> ExecuteAsync(ReadOnlyMemory<byte> command, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        
        // For raw command execution, we need to parse it first (this is for backward compatibility)
        // In practice, this method should rarely be used
        var commandName = command.Span.GetCommandName();
        var commandInfo = new CommandInfo(commandName);
        
        return await _interceptorChain.ExecuteAsync(commandInfo, cancellationToken);
    }
    
    public async ValueTask<T> ExecuteAsync<T>(string command, Func<RespValue, T> resultMapper, params string[] args)
    {
        var response = await ExecuteAsync(command, args);
        return resultMapper(response);
    }
    
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
            
        _disposed = true;
        
        await _connectionPool.DisposeAsync();
    }
    
    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(KevaClient));
    }
}

public class KevaClientOptions
{
    public ConnectionPoolOptions ConnectionPool { get; set; } = new();
    public TimeSpan DefaultCommandTimeout { get; set; } = TimeSpan.FromSeconds(30);
    public bool ThrowOnError { get; set; } = true;
}