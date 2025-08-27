using System.Buffers;
using System.Runtime.CompilerServices;
using Keva.Core.Infrastructure;
using Keva.Core.Protocol;

namespace Keva.Core.FastClient;

/// <summary>
/// High-performance async pipeline for batching Redis operations with zero-allocation optimizations
/// Combines the efficiency of buffer pooling with modern async patterns and connection multiplexing
/// </summary>
public sealed class KevaPipeline : IDisposable
{
    private readonly KevaClient _client;
    private readonly KevaCommandQueue _commandQueue;
    private readonly List<Func<PipelineCommandWriter, ValueTask>> _commands;
    private readonly List<ValueTask<KevaValue>> _responseTasks;
    private bool _disposed;
    
    internal KevaPipeline(KevaClient client, KevaCommandQueue commandQueue)
    {
        _client = client;
        _commandQueue = commandQueue;
        _commands = new List<Func<PipelineCommandWriter, ValueTask>>();
        _responseTasks = new List<ValueTask<KevaValue>>();
    }
    
    /// <summary>
    /// Adds a SET command to the pipeline (fire-and-forget)
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public KevaPipeline Set(string key, string value)
    {
        ThrowIfDisposed();
        _commands.Add(writer => writer.WriteSetAsync(key, value));
        return this;
    }
    
    /// <summary>
    /// Adds a GET command to the pipeline (with response tracking)
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public KevaPipeline Get(string key, out ValueTask<KevaValue> responseTask)
    {
        ThrowIfDisposed();
        responseTask = _client.GetAsync(key);
        _responseTasks.Add(responseTask);
        return this;
    }
    
    /// <summary>
    /// Adds a DEL command to the pipeline (fire-and-forget)
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public KevaPipeline Del(string key)
    {
        ThrowIfDisposed();
        _commands.Add(writer => writer.WriteDelAsync(key));
        return this;
    }
    
    /// <summary>
    /// Adds an EXISTS command to the pipeline (with response tracking)
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public KevaPipeline Exists(string key, out ValueTask<KevaValue> responseTask)
    {
        ThrowIfDisposed();
        responseTask = _client.ExistsAsync(key);
        _responseTasks.Add(responseTask);
        return this;
    }
    
    /// <summary>
    /// Adds an INCR command to the pipeline (fire-and-forget)
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public KevaPipeline Incr(string key)
    {
        ThrowIfDisposed();
        _commands.Add(writer => writer.WriteIncrAsync(key));
        return this;
    }
    
    /// <summary>
    /// Adds an INCR command to the pipeline (with response tracking)
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public KevaPipeline IncrWithResponse(string key, out ValueTask<KevaValue> responseTask)
    {
        ThrowIfDisposed();
        responseTask = _client.IncrWithResponseAsync(key);
        _responseTasks.Add(responseTask);
        return this;
    }
    
    /// <summary>
    /// Adds an EXPIRE command to the pipeline (fire-and-forget)
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public KevaPipeline Expire(string key, int seconds)
    {
        ThrowIfDisposed();
        _commands.Add(writer => writer.WriteExpireAsync(key, seconds));
        return this;
    }
    
    /// <summary>
    /// Adds a TTL command to the pipeline (with response tracking)
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public KevaPipeline Ttl(string key, out ValueTask<KevaValue> responseTask)
    {
        ThrowIfDisposed();
        responseTask = _client.TtlAsync(key);
        _responseTasks.Add(responseTask);
        return this;
    }
    
    /// <summary>
    /// Adds an HSET command to the pipeline (fire-and-forget)
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public KevaPipeline HSet(string key, string field, string value)
    {
        ThrowIfDisposed();
        _commands.Add(writer => writer.WriteHSetAsync(key, field, value));
        return this;
    }
    
    /// <summary>
    /// Adds an HGET command to the pipeline (with response tracking)
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public KevaPipeline HGet(string key, string field, out ValueTask<KevaValue> responseTask)
    {
        ThrowIfDisposed();
        responseTask = _client.HGetAsync(key, field);
        _responseTasks.Add(responseTask);
        return this;
    }
    
    /// <summary>
    /// Adds an LPUSH command to the pipeline (fire-and-forget)
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public KevaPipeline LPush(string key, string value)
    {
        ThrowIfDisposed();
        _commands.Add(writer => writer.WriteLPushAsync(key, value));
        return this;
    }
    
    /// <summary>
    /// Adds an RPOP command to the pipeline (with response tracking)
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public KevaPipeline RPop(string key, out ValueTask<KevaValue> responseTask)
    {
        ThrowIfDisposed();
        responseTask = _client.RPopAsync(key);
        _responseTasks.Add(responseTask);
        return this;
    }
    
    /// <summary>
    /// Adds a SADD command to the pipeline (fire-and-forget)
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public KevaPipeline SAdd(string key, string member)
    {
        ThrowIfDisposed();
        _commands.Add(writer => writer.WriteSAddAsync(key, member));
        return this;
    }
    
    /// <summary>
    /// Adds a PING command to the pipeline (fire-and-forget)
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public KevaPipeline Ping()
    {
        ThrowIfDisposed();
        _commands.Add(writer => writer.WritePingAsync());
        return this;
    }
    
    /// <summary>
    /// Adds a PING command to the pipeline (with response tracking)
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public KevaPipeline PingWithResponse(out ValueTask<KevaValue> responseTask)
    {
        ThrowIfDisposed();
        responseTask = _client.PingWithResponseAsync();
        _responseTasks.Add(responseTask);
        return this;
    }
    
    /// <summary>
    /// Adds a custom command to the pipeline (fire-and-forget)
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public KevaPipeline Custom(Func<PipelineCommandWriter, ValueTask> commandAction)
    {
        ThrowIfDisposed();
        _commands.Add(commandAction);
        return this;
    }
    
    /// <summary>
    /// Adds a pre-compiled command to the pipeline (fire-and-forget)
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public KevaPipeline PreCompiled(ReadOnlyMemory<byte> preCompiledCommand)
    {
        ThrowIfDisposed();
        _commands.Add(writer => writer.WritePreCompiledAsync(preCompiledCommand));
        return this;
    }
    
    /// <summary>
    /// Executes all queued commands in the pipeline with optimal batching and parallelization
    /// </summary>
    public async ValueTask ExecuteAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        
        if (_commands.Count == 0 && _responseTasks.Count == 0)
        {
            return; // Nothing to execute
        }
        
        try
        {
            // Execute fire-and-forget commands in batch
            if (_commands.Count > 0)
            {
                await _commandQueue.QueueBatchAsync(_commands, cancellationToken).ConfigureAwait(false);
            }
            
            // Wait for all response tasks (these were already queued when added to pipeline)
            if (_responseTasks.Count > 0)
            {
                await Task.WhenAll(_responseTasks.Select(t => t.AsTask())).ConfigureAwait(false);
            }
        }
        finally
        {
            // Clear commands and responses for potential reuse
            _commands.Clear();
            _responseTasks.Clear();
        }
    }
    
    /// <summary>
    /// Gets all response results (only works after ExecuteAsync completes)
    /// </summary>
    public async ValueTask<KevaValue[]> GetAllResponsesAsync()
    {
        ThrowIfDisposed();
        
        if (_responseTasks.Count == 0)
        {
            return Array.Empty<KevaValue>();
        }
        
        var results = new KevaValue[_responseTasks.Count];
        for (int i = 0; i < _responseTasks.Count; i++)
        {
            results[i] = await _responseTasks[i].ConfigureAwait(false);
        }
        
        return results;
    }
    
    /// <summary>
    /// Gets the number of commands in the pipeline
    /// </summary>
    public int CommandCount => _commands.Count + _responseTasks.Count;
    
    /// <summary>
    /// Gets the number of commands expecting responses
    /// </summary>
    public int ResponseCount => _responseTasks.Count;
    
    /// <summary>
    /// Clears all queued commands and responses
    /// </summary>
    public void Clear()
    {
        ThrowIfDisposed();
        _commands.Clear();
        _responseTasks.Clear();
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(KevaPipeline));
    }
    
    public void Dispose()
    {
        if (_disposed) return;
        
        _disposed = true;
        _commands.Clear();
        _responseTasks.Clear();
    }
}