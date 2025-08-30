using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Respire.Infrastructure;
using Respire.Protocol;
using CommandType = Respire.Commands.CommandType;
using CommandData = Respire.Commands.CommandData;

namespace Respire.FastClient;

/// <summary>
/// High-performance Redis client using enum-based commands to reduce allocations
/// </summary>
public sealed class RespireClient : IAsyncDisposable
{
    private readonly RespireConnectionMultiplexer _multiplexer;
    private readonly RespireCommandQueue _commandQueue;
    private readonly ILogger? _logger;
    private volatile bool _disposed;
    
    public string Host => _multiplexer.Host;
    public int Port => _multiplexer.Port;
    public bool IsConnected => _multiplexer.IsConnected && !_disposed;
    
    private RespireClient(
        RespireConnectionMultiplexer multiplexer,
        RespireCommandQueue commandQueue,
        ILogger? logger)
    {
        _multiplexer = multiplexer;
        _commandQueue = commandQueue;
        _logger = logger;
    }
    
    /// <summary>
    /// Creates a high-performance Redis client
    /// </summary>
    public static async Task<RespireClient> CreateAsync(
        string host,
        int port = 6379,
        int connectionCount = 0,
        ILogger? logger = null)
    {
        var multiplexer = await RespireConnectionMultiplexer.CreateAsync(
            host, port, connectionCount, logger: logger).ConfigureAwait(false);
        
        var commandQueue = new RespireCommandQueue(
            multiplexer, 
            maxBatchSize: 128, 
            batchTimeout: TimeSpan.FromMilliseconds(1),
            tcsPoolSize: 1024,
            logger);
        
        var client = new RespireClient(multiplexer, commandQueue, logger);
        
        logger?.LogInformation(
            "Created RespireClient for {Host}:{Port} with {ConnectionCount} connections",
            host, port, multiplexer.ConnectionCount);
        
        return client;
    }
    
    // String Commands
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public async ValueTask<RespireValue> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var command = new CommandData(CommandType.Get, key);
        return await _commandQueue.QueueCommandWithResponseAsync(command, cancellationToken).ConfigureAwait(false);
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public async ValueTask SetAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var command = new CommandData(CommandType.Set, key, value);
        var response = await _commandQueue.QueueCommandWithResponseAsync(command, cancellationToken).ConfigureAwait(false);
        
        // Verify we got "OK" response
        if (!response.Type.Equals(RespDataType.SimpleString) || !response.AsString().Equals("OK", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"SET command failed. Expected 'OK' but got: {response}");
        }
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public async ValueTask<long> DelAsync(string key, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var command = new CommandData(CommandType.Del, key);
        var response = await _commandQueue.QueueCommandWithResponseAsync(command, cancellationToken).ConfigureAwait(false);
        return response.AsInteger();
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public async ValueTask<bool> ExistsAsync(string key, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var command = new CommandData(CommandType.Exists, key);
        var response = await _commandQueue.QueueCommandWithResponseAsync(command, cancellationToken).ConfigureAwait(false);
        return response.AsInteger() > 0;
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public async ValueTask<long> IncrAsync(string key, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var command = new CommandData(CommandType.Incr, key);
        var response = await _commandQueue.QueueCommandWithResponseAsync(command, cancellationToken).ConfigureAwait(false);
        return response.AsInteger();
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public async ValueTask<long> DecrAsync(string key, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var command = new CommandData(CommandType.Decr, key);
        var response = await _commandQueue.QueueCommandWithResponseAsync(command, cancellationToken).ConfigureAwait(false);
        return response.AsInteger();
    }
    
    // [MethodImpl(MethodImplOptions.AggressiveInlining)]
    // public async ValueTask<long> IncrByAsync(string key, long value, CancellationToken cancellationToken = default)
    // {
    //     ThrowIfDisposed();
    //     var command = new CommandData(Commands.CommandType.IncrBy, key, value);
    //     var response = await _commandQueue.QueueCommandWithResponseAsync(command, cancellationToken).ConfigureAwait(false);
    //     return response.AsInteger();
    // }
    
    // [MethodImpl(MethodImplOptions.AggressiveInlining)]
    // public async ValueTask<long> DecrByAsync(string key, long value, CancellationToken cancellationToken = default)
    // {
    //     ThrowIfDisposed();
    //     var command = new CommandData(Commands.CommandType.DecrBy, key, value);
    //     var response = await _commandQueue.QueueCommandWithResponseAsync(command, cancellationToken).ConfigureAwait(false);
    //     return response.AsInteger();
    // }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public async ValueTask<long> AppendAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var command = new CommandData(CommandType.Append, key, value);
        var response = await _commandQueue.QueueCommandWithResponseAsync(command, cancellationToken).ConfigureAwait(false);
        return response.AsInteger();
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public async ValueTask<long> StrLenAsync(string key, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var command = new CommandData(CommandType.StrLen, key);
        var response = await _commandQueue.QueueCommandWithResponseAsync(command, cancellationToken).ConfigureAwait(false);
        return response.AsInteger();
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public async ValueTask<RespireValue> TtlAsync(string key, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var command = new CommandData(CommandType.Ttl, key);
        return await _commandQueue.QueueCommandWithResponseAsync(command, cancellationToken).ConfigureAwait(false);
    }
    
    // Hash Commands
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public async ValueTask<RespireValue> HGetAsync(string key, string field, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var command = new CommandData(CommandType.HGet, key, field);
        return await _commandQueue.QueueCommandWithResponseAsync(command, cancellationToken).ConfigureAwait(false);
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public async ValueTask<long> HSetAsync(string key, string field, string value, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var command = new CommandData(CommandType.HSet, key, field, value);
        var response = await _commandQueue.QueueCommandWithResponseAsync(command, cancellationToken).ConfigureAwait(false);
        return response.AsInteger();
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public async ValueTask<long> HDelAsync(string key, string field, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var command = new CommandData(CommandType.HDel, key, field);
        var response = await _commandQueue.QueueCommandWithResponseAsync(command, cancellationToken).ConfigureAwait(false);
        return response.AsInteger();
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public async ValueTask<bool> HExistsAsync(string key, string field, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var command = new CommandData(CommandType.HExists, key, field);
        var response = await _commandQueue.QueueCommandWithResponseAsync(command, cancellationToken).ConfigureAwait(false);
        return response.AsInteger() > 0;
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public async ValueTask<long> HLenAsync(string key, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var command = new CommandData(CommandType.HLen, key);
        var response = await _commandQueue.QueueCommandWithResponseAsync(command, cancellationToken).ConfigureAwait(false);
        return response.AsInteger();
    }
    
    // List Commands
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public async ValueTask<long> LPushAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var command = new CommandData(CommandType.LPush, key, value);
        var response = await _commandQueue.QueueCommandWithResponseAsync(command, cancellationToken).ConfigureAwait(false);
        return response.AsInteger();
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public async ValueTask<long> RPushAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var command = new CommandData(CommandType.RPush, key, value);
        var response = await _commandQueue.QueueCommandWithResponseAsync(command, cancellationToken).ConfigureAwait(false);
        return response.AsInteger();
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public async ValueTask<RespireValue> LPopAsync(string key, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var command = new CommandData(CommandType.LPop, key);
        return await _commandQueue.QueueCommandWithResponseAsync(command, cancellationToken).ConfigureAwait(false);
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public async ValueTask<RespireValue> RPopAsync(string key, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var command = new CommandData(CommandType.RPop, key);
        return await _commandQueue.QueueCommandWithResponseAsync(command, cancellationToken).ConfigureAwait(false);
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public async ValueTask<long> LLenAsync(string key, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var command = new CommandData(CommandType.LLen, key);
        var response = await _commandQueue.QueueCommandWithResponseAsync(command, cancellationToken).ConfigureAwait(false);
        return response.AsInteger();
    }
    
    // Set Commands
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public async ValueTask<long> SAddAsync(string key, string member, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var command = new CommandData(CommandType.SAdd, key, member);
        var response = await _commandQueue.QueueCommandWithResponseAsync(command, cancellationToken).ConfigureAwait(false);
        return response.AsInteger();
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public async ValueTask<long> SRemAsync(string key, string member, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var command = new CommandData(CommandType.SRem, key, member);
        var response = await _commandQueue.QueueCommandWithResponseAsync(command, cancellationToken).ConfigureAwait(false);
        return response.AsInteger();
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public async ValueTask<bool> SIsMemberAsync(string key, string member, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var command = new CommandData(CommandType.SIsMember, key, member);
        var response = await _commandQueue.QueueCommandWithResponseAsync(command, cancellationToken).ConfigureAwait(false);
        return response.AsInteger() > 0;
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public async ValueTask<long> SCardAsync(string key, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var command = new CommandData(CommandType.SCard, key);
        var response = await _commandQueue.QueueCommandWithResponseAsync(command, cancellationToken).ConfigureAwait(false);
        return response.AsInteger();
    }
    
    // Connection Commands
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public async ValueTask<string> PingAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var command = new CommandData(CommandType.Ping, string.Empty);
        var response = await _commandQueue.QueueCommandWithResponseAsync(command, cancellationToken).ConfigureAwait(false);
        return response.AsString();
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public async ValueTask<RespireValue> PingWithResponseAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var command = new CommandData(CommandType.Ping, string.Empty);
        return await _commandQueue.QueueCommandWithResponseAsync(command, cancellationToken).ConfigureAwait(false);
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public async ValueTask<string> EchoAsync(string message, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var command = new CommandData(CommandType.Echo, message);
        var response = await _commandQueue.QueueCommandWithResponseAsync(command, cancellationToken).ConfigureAwait(false);
        return response.AsString();
    }
    
    // Server Commands
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public async ValueTask FlushDbAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var command = new CommandData(CommandType.FlushDb, string.Empty);
        await _commandQueue.QueueCommandWithResponseAsync(command, cancellationToken).ConfigureAwait(false);
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public async ValueTask FlushAllAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var command = new CommandData(CommandType.FlushAll, string.Empty);
        await _commandQueue.QueueCommandWithResponseAsync(command, cancellationToken).ConfigureAwait(false);
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public async ValueTask<long> DbSizeAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var command = new CommandData(CommandType.DbSize, string.Empty);
        var response = await _commandQueue.QueueCommandWithResponseAsync(command, cancellationToken).ConfigureAwait(false);
        return response.AsInteger();
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(RespireClient));
    }
    
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        
        _disposed = true;
        
        await _commandQueue.DisposeAsync().ConfigureAwait(false);
        await _multiplexer.DisposeAsync().ConfigureAwait(false);
        
        _logger?.LogInformation("RespireClient disposed");
    }
    
    public void Dispose()
    {
        DisposeAsync().AsTask().Wait();
    }
}