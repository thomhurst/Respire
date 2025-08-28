using System.Runtime.CompilerServices;
using Respire.Infrastructure;

namespace Respire.Protocol;

/// <summary>
/// High-performance command writer that integrates pre-compiled commands with pipeline infrastructure
/// Combines the efficiency of pre-compiled RESP commands with System.IO.Pipelines for optimal throughput
/// </summary>
public sealed class PipelineCommandWriter : IDisposable
{
    private readonly PipelineConnection _connection;
    private readonly RespireMemoryPool _memoryPool;
    private volatile bool _disposed;
    
    public PipelineCommandWriter(PipelineConnection connection, RespireMemoryPool? memoryPool = null)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _memoryPool = memoryPool ?? RespireMemoryPool.Shared;
    }
    
    /// <summary>
    /// Writes a pre-compiled zero-argument command directly to the pipeline
    /// </summary>
    /// <param name="preCompiledCommand">Pre-compiled command bytes from RespCommands</param>
    /// <param name="cancellationToken">Cancellation token</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public async ValueTask WritePreCompiledAsync(ReadOnlyMemory<byte> preCompiledCommand, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _connection.WritePreCompiledCommandAsync(preCompiledCommand, cancellationToken).ConfigureAwait(false);
    }
    
    /// <summary>
    /// Writes PING command using pre-compiled bytes
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ValueTask WritePingAsync(CancellationToken cancellationToken = default)
    {
        return WritePreCompiledAsync(RespCommands.Ping, cancellationToken);
    }
    
    /// <summary>
    /// Writes INFO command using pre-compiled bytes
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ValueTask WriteInfoAsync(CancellationToken cancellationToken = default)
    {
        return WritePreCompiledAsync(RespCommands.Info, cancellationToken);
    }
    
    /// <summary>
    /// Writes DBSIZE command using pre-compiled bytes
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ValueTask WriteDbSizeAsync(CancellationToken cancellationToken = default)
    {
        return WritePreCompiledAsync(RespCommands.DbSize, cancellationToken);
    }
    
    /// <summary>
    /// Writes FLUSHDB command using pre-compiled bytes
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ValueTask WriteFlushDbAsync(CancellationToken cancellationToken = default)
    {
        return WritePreCompiledAsync(RespCommands.FlushDb, cancellationToken);
    }
    
    /// <summary>
    /// Writes FLUSHALL command using pre-compiled bytes
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ValueTask WriteFlushAllAsync(CancellationToken cancellationToken = default)
    {
        return WritePreCompiledAsync(RespCommands.FlushAll, cancellationToken);
    }
    
    /// <summary>
    /// Writes GET command using pre-compiled command builder with memory pooling
    /// </summary>
    /// <param name="key">Redis key to retrieve</param>
    /// <param name="cancellationToken">Cancellation token</param>
    public async ValueTask WriteGetAsync(string key, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        
        await _connection.WriteWithPooledBufferAsync(writer =>
        {
            var estimatedLength = 32 + key.Length; // Conservative estimate for GET command
            var buffer = writer.GetSpan(estimatedLength);
            var length = RespCommands.BuildGetCommand(buffer, key);
            writer.Advance(length);
        }, cancellationToken).ConfigureAwait(false);
    }
    
    /// <summary>
    /// Writes SET command using pre-compiled command builder with memory pooling
    /// </summary>
    /// <param name="key">Redis key to set</param>
    /// <param name="value">Value to set</param>
    /// <param name="cancellationToken">Cancellation token</param>
    public async ValueTask WriteSetAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        
        await _connection.WriteWithPooledBufferAsync(writer =>
        {
            var estimatedLength = 64 + key.Length + value.Length; // Conservative estimate
            var buffer = writer.GetSpan(estimatedLength);
            var length = RespCommands.BuildSetCommand(buffer, key, value);
            writer.Advance(length);
        }, cancellationToken).ConfigureAwait(false);
    }
    
    /// <summary>
    /// Writes DEL command using pre-compiled command builder with memory pooling
    /// </summary>
    /// <param name="key">Redis key to delete</param>
    /// <param name="cancellationToken">Cancellation token</param>
    public async ValueTask WriteDelAsync(string key, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        
        await _connection.WriteWithPooledBufferAsync(writer =>
        {
            var estimatedLength = 32 + key.Length; // Conservative estimate
            var buffer = writer.GetSpan(estimatedLength);
            var length = RespCommands.BuildDelCommand(buffer, key);
            writer.Advance(length);
        }, cancellationToken).ConfigureAwait(false);
    }
    
    /// <summary>
    /// Writes EXISTS command using pre-compiled command builder with memory pooling
    /// </summary>
    /// <param name="key">Redis key to check</param>
    /// <param name="cancellationToken">Cancellation token</param>
    public async ValueTask WriteExistsAsync(string key, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        
        await _connection.WriteWithPooledBufferAsync(writer =>
        {
            var estimatedLength = 32 + key.Length; // Conservative estimate
            var buffer = writer.GetSpan(estimatedLength);
            var length = RespCommands.BuildExistsCommand(buffer, key);
            writer.Advance(length);
        }, cancellationToken).ConfigureAwait(false);
    }
    
    /// <summary>
    /// Writes HGET command using pre-compiled command builder with memory pooling
    /// </summary>
    /// <param name="key">Hash key</param>
    /// <param name="field">Hash field</param>
    /// <param name="cancellationToken">Cancellation token</param>
    public async ValueTask WriteHGetAsync(string key, string field, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        
        await _connection.WriteWithPooledBufferAsync(writer =>
        {
            var estimatedLength = 64 + key.Length + field.Length; // Conservative estimate
            var buffer = writer.GetSpan(estimatedLength);
            var length = RespCommands.BuildHGetCommand(buffer, key, field);
            writer.Advance(length);
        }, cancellationToken).ConfigureAwait(false);
    }
    
    /// <summary>
    /// Writes HSET command using pre-compiled command builder with memory pooling
    /// </summary>
    /// <param name="key">Hash key</param>
    /// <param name="field">Hash field</param>
    /// <param name="value">Hash value</param>
    /// <param name="cancellationToken">Cancellation token</param>
    public async ValueTask WriteHSetAsync(string key, string field, string value, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        
        await _connection.WriteWithPooledBufferAsync(writer =>
        {
            var estimatedLength = 96 + key.Length + field.Length + value.Length; // Conservative estimate
            var buffer = writer.GetSpan(estimatedLength);
            var length = RespCommands.BuildHSetCommand(buffer, key, field, value);
            writer.Advance(length);
        }, cancellationToken).ConfigureAwait(false);
    }
    
    /// <summary>
    /// Writes LPUSH command using pre-compiled command builder with memory pooling
    /// </summary>
    /// <param name="key">List key</param>
    /// <param name="value">Value to push</param>
    /// <param name="cancellationToken">Cancellation token</param>
    public async ValueTask WriteLPushAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        
        await _connection.WriteWithPooledBufferAsync(writer =>
        {
            var estimatedLength = 64 + key.Length + value.Length; // Conservative estimate
            var buffer = writer.GetSpan(estimatedLength);
            var length = RespCommands.BuildLPushCommand(buffer, key, value);
            writer.Advance(length);
        }, cancellationToken).ConfigureAwait(false);
    }
    
    /// <summary>
    /// Writes INCR command using pre-compiled command builder with memory pooling
    /// </summary>
    /// <param name="key">Key to increment</param>
    /// <param name="cancellationToken">Cancellation token</param>
    public async ValueTask WriteIncrAsync(string key, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        
        await _connection.WriteWithPooledBufferAsync(writer =>
        {
            var estimatedLength = 32 + key.Length; // Conservative estimate
            var buffer = writer.GetSpan(estimatedLength);
            var length = RespCommands.BuildIncrCommand(buffer, key);
            writer.Advance(length);
        }, cancellationToken).ConfigureAwait(false);
    }
    
    /// <summary>
    /// Writes EXPIRE command using pre-compiled command builder with memory pooling
    /// </summary>
    /// <param name="key">Redis key to set expiration on</param>
    /// <param name="seconds">Expiration time in seconds</param>
    /// <param name="cancellationToken">Cancellation token</param>
    public async ValueTask WriteExpireAsync(string key, int seconds, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        
        await _connection.WriteWithPooledBufferAsync(writer =>
        {
            var estimatedLength = 64 + key.Length; // Conservative estimate
            var buffer = writer.GetSpan(estimatedLength);
            var length = RespCommands.BuildExpireCommand(buffer, key, seconds);
            writer.Advance(length);
        }, cancellationToken).ConfigureAwait(false);
    }
    
    /// <summary>
    /// Writes TTL command using pre-compiled command builder with memory pooling
    /// </summary>
    /// <param name="key">Redis key to get TTL for</param>
    /// <param name="cancellationToken">Cancellation token</param>
    public async ValueTask WriteTtlAsync(string key, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        
        await _connection.WriteWithPooledBufferAsync(writer =>
        {
            var estimatedLength = 32 + key.Length; // Conservative estimate
            var buffer = writer.GetSpan(estimatedLength);
            var length = RespCommands.BuildTtlCommand(buffer, key);
            writer.Advance(length);
        }, cancellationToken).ConfigureAwait(false);
    }
    
    /// <summary>
    /// Writes SADD command using pre-compiled command builder with memory pooling
    /// </summary>
    /// <param name="key">Set key</param>
    /// <param name="member">Member to add</param>
    /// <param name="cancellationToken">Cancellation token</param>
    public async ValueTask WriteSAddAsync(string key, string member, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        
        await _connection.WriteWithPooledBufferAsync(writer =>
        {
            var estimatedLength = 64 + key.Length + member.Length; // Conservative estimate
            var buffer = writer.GetSpan(estimatedLength);
            var length = RespCommands.BuildSAddCommand(buffer, key, member);
            writer.Advance(length);
        }, cancellationToken).ConfigureAwait(false);
    }
    
    /// <summary>
    /// Writes SREM command using pre-compiled command builder with memory pooling
    /// </summary>
    /// <param name="key">Set key</param>
    /// <param name="member">Member to remove</param>
    /// <param name="cancellationToken">Cancellation token</param>
    public async ValueTask WriteSRemAsync(string key, string member, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        
        await _connection.WriteWithPooledBufferAsync(writer =>
        {
            var estimatedLength = 64 + key.Length + member.Length; // Conservative estimate
            var buffer = writer.GetSpan(estimatedLength);
            var length = RespCommands.BuildSRemCommand(buffer, key, member);
            writer.Advance(length);
        }, cancellationToken).ConfigureAwait(false);
    }
    
    /// <summary>
    /// Writes RPOP command using pre-compiled command builder with memory pooling
    /// </summary>
    /// <param name="key">List key</param>
    /// <param name="cancellationToken">Cancellation token</param>
    public async ValueTask WriteRPopAsync(string key, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        
        await _connection.WriteWithPooledBufferAsync(writer =>
        {
            var estimatedLength = 32 + key.Length; // Conservative estimate
            var buffer = writer.GetSpan(estimatedLength);
            var length = RespCommands.BuildRPopCommand(buffer, key);
            writer.Advance(length);
        }, cancellationToken).ConfigureAwait(false);
    }
    
    /// <summary>
    /// Writes multiple pre-compiled commands in a batch for maximum throughput
    /// </summary>
    /// <param name="commands">Collection of pre-compiled command bytes</param>
    /// <param name="cancellationToken">Cancellation token</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public async ValueTask WriteBatchAsync(IEnumerable<ReadOnlyMemory<byte>> commands, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _connection.WriteBatchAsync(commands, cancellationToken).ConfigureAwait(false);
    }
    
    /// <summary>
    /// Executes a custom action with a pooled buffer writer for complex command construction
    /// </summary>
    /// <param name="writeAction">Action that builds the command using the writer</param>
    /// <param name="cancellationToken">Cancellation token</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public async ValueTask WriteCustomAsync(Action<PooledBufferWriter> writeAction, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _connection.WriteWithPooledBufferAsync(writeAction, cancellationToken).ConfigureAwait(false);
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(PipelineCommandWriter));
    }
    
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
    }
}