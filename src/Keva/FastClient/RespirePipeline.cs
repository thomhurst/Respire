using System.Runtime.CompilerServices;
using Respire.Infrastructure;
using Respire.Protocol;

namespace Respire.FastClient;

/// <summary>
/// High-performance async pipeline for batching Redis operations with zero-allocation optimizations
/// Combines the efficiency of buffer pooling with modern async patterns and connection multiplexing
/// </summary>
public sealed class RespirePipeline : IDisposable
{
    private readonly RespireClient _client;
    private readonly RespireCommandQueue _commandQueue;
    private readonly List<Func<PipelineCommandWriter, ValueTask>> _commands;
    private readonly List<ValueTask<RespireValue>> _responseTasks;
    private bool _disposed;
    
    internal RespirePipeline(RespireClient client, RespireCommandQueue commandQueue)
    {
        _client = client;
        _commandQueue = commandQueue;
        _commands = new List<Func<PipelineCommandWriter, ValueTask>>();
        _responseTasks = new List<ValueTask<RespireValue>>();
    }
    
    /// <summary>
    /// Adds a SET command to the pipeline (fire-and-forget)
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public RespirePipeline Set(string key, string value)
    {
        ThrowIfDisposed();
        _commands.Add(writer => writer.WriteSetAsync(key, value));
        return this;
    }
    
    /// <summary>
    /// Adds a GET command to the pipeline (with response tracking)
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public RespirePipeline Get(string key, out ValueTask<RespireValue> responseTask)
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
    public RespirePipeline Del(string key)
    {
        ThrowIfDisposed();
        _commands.Add(writer => writer.WriteDelAsync(key));
        return this;
    }
    
    /// <summary>
    /// Adds an EXISTS command to the pipeline (with response tracking)
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public RespirePipeline Exists(string key, out ValueTask<RespireValue> responseTask)
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
    public RespirePipeline Incr(string key)
    {
        ThrowIfDisposed();
        _commands.Add(writer => writer.WriteIncrAsync(key));
        return this;
    }
    
    /// <summary>
    /// Adds an INCR command to the pipeline (with response tracking)
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public RespirePipeline IncrWithResponse(string key, out ValueTask<RespireValue> responseTask)
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
    public RespirePipeline Expire(string key, int seconds)
    {
        ThrowIfDisposed();
        _commands.Add(writer => writer.WriteExpireAsync(key, seconds));
        return this;
    }
    
    /// <summary>
    /// Adds a TTL command to the pipeline (with response tracking)
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public RespirePipeline Ttl(string key, out ValueTask<RespireValue> responseTask)
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
    public RespirePipeline HSet(string key, string field, string value)
    {
        ThrowIfDisposed();
        _commands.Add(writer => writer.WriteHSetAsync(key, field, value));
        return this;
    }
    
    /// <summary>
    /// Adds an HGET command to the pipeline (with response tracking)
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public RespirePipeline HGet(string key, string field, out ValueTask<RespireValue> responseTask)
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
    public RespirePipeline LPush(string key, string value)
    {
        ThrowIfDisposed();
        _commands.Add(writer => writer.WriteLPushAsync(key, value));
        return this;
    }
    
    /// <summary>
    /// Adds an RPOP command to the pipeline (with response tracking)
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public RespirePipeline RPop(string key, out ValueTask<RespireValue> responseTask)
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
    public RespirePipeline SAdd(string key, string member)
    {
        ThrowIfDisposed();
        _commands.Add(writer => writer.WriteSAddAsync(key, member));
        return this;
    }
    
    /// <summary>
    /// Adds a PING command to the pipeline (fire-and-forget)
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public RespirePipeline Ping()
    {
        ThrowIfDisposed();
        _commands.Add(writer => writer.WritePingAsync());
        return this;
    }
    
    /// <summary>
    /// Adds a PING command to the pipeline (with response tracking)
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public RespirePipeline PingWithResponse(out ValueTask<RespireValue> responseTask)
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
    public RespirePipeline Custom(Func<PipelineCommandWriter, ValueTask> commandAction)
    {
        ThrowIfDisposed();
        _commands.Add(commandAction);
        return this;
    }
    
    /// <summary>
    /// Adds a pre-compiled command to the pipeline (fire-and-forget)
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public RespirePipeline PreCompiled(ReadOnlyMemory<byte> preCompiledCommand)
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
    public async ValueTask<RespireValue[]> GetAllResponsesAsync()
    {
        ThrowIfDisposed();
        
        if (_responseTasks.Count == 0)
        {
            return Array.Empty<RespireValue>();
        }
        
        var results = new RespireValue[_responseTasks.Count];
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
        if (_disposed) throw new ObjectDisposedException(nameof(RespirePipeline));
    }
    
    public void Dispose()
    {
        if (_disposed) return;
        
        _disposed = true;
        _commands.Clear();
        _responseTasks.Clear();
    }
}