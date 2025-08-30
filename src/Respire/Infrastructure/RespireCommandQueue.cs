using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.ObjectPool;
using Respire.Commands;
using Respire.Protocol;

namespace Respire.Infrastructure;

/// <summary>
/// Pool policy for ValueTaskCompletionSource
/// </summary>
public sealed class ValueTaskCompletionSourcePooledObjectPolicy : PooledObjectPolicy<ValueTaskCompletionSource>
{
    public override ValueTaskCompletionSource Create()
    {
        return new ValueTaskCompletionSource();
    }
    
    public override bool Return(ValueTaskCompletionSource obj)
    {
        // The object will be reset when retrieved from pool
        return true;
    }
}

/// <summary>
/// High-performance command queue that processes commands with reduced allocations
/// </summary>
public sealed class RespireCommandQueue : IRespireCommandQueue
{
    private readonly RespireConnectionMultiplexer _multiplexer;
    private readonly Channel<QueuedCommandData> _commandChannel;
    private readonly ObjectPool<ValueTaskCompletionSource> _vtcsPool;
    private readonly ILogger? _logger;
    private readonly CancellationTokenSource _cancellationTokenSource;
    private readonly Task _processingTask;
    private readonly int _maxBatchSize;
    private readonly TimeSpan _batchTimeout;
    
    private volatile bool _disposed;
    private long _totalCommandsQueued;
    private long _totalCommandsProcessed;
    private long _totalBatchesProcessed;
    
    public RespireCommandQueue(
        RespireConnectionMultiplexer multiplexer,
        int maxBatchSize = 128,
        TimeSpan batchTimeout = default,
        int tcsPoolSize = 1024,
        ILogger? logger = null)
    {
        _multiplexer = multiplexer ?? throw new ArgumentNullException(nameof(multiplexer));
        _maxBatchSize = maxBatchSize > 0 ? maxBatchSize : 128;
        _batchTimeout = batchTimeout == default ? TimeSpan.FromMilliseconds(1) : batchTimeout;
        _logger = logger;
        _cancellationTokenSource = new CancellationTokenSource();
        
        // Create unbounded channel for commands - using struct type to avoid boxing
        _commandChannel = Channel.CreateUnbounded<QueuedCommandData>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });
        
        // Create object pool for completion sources
        _vtcsPool = new DefaultObjectPool<ValueTaskCompletionSource>(
            new ValueTaskCompletionSourcePooledObjectPolicy(), 
            maximumRetained: tcsPoolSize);
        
        // Start processing task
        _processingTask = Task.Run(async () => 
        {
            try
            {
                await ProcessCommandsAsync();
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Fatal error in command processing task");
                throw;
            }
        });
    }
    
    /// <summary>
    /// Queues a command without expecting a response
    /// </summary>
    public ValueTask QueueCommandAsync(CommandData command, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        
        var queuedCommand = QueuedCommandData.CreateWithoutResponse(command);
        return _commandChannel.Writer.WriteAsync(queuedCommand, cancellationToken);
    }
    
    /// <summary>
    /// Queues a command and waits for its response
    /// </summary>
    public async ValueTask<RespireValue> QueueCommandWithResponseAsync(CommandData command, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        
        var vtcs = _vtcsPool.Get();
        vtcs.Reset();
        
        var queuedCommand = QueuedCommandData.CreateWithResponse(command, vtcs);
        await _commandChannel.Writer.WriteAsync(queuedCommand, cancellationToken).ConfigureAwait(false);
        Interlocked.Increment(ref _totalCommandsQueued);
        
        try
        {
            return await vtcs.GetValueTask().ConfigureAwait(false);
        }
        finally
        {
            _vtcsPool.Return(vtcs);
        }
    }
    
    // Legacy interface methods - these will allocate but are for backward compatibility
    public ValueTask QueueCommandAsync(Func<PipelineCommandWriter, ValueTask> commandAction, CancellationToken cancellationToken = default)
    {
        // This path still allocates - only here for compatibility
        throw new NotSupportedException("Legacy delegate-based commands are not supported. Use CommandData instead.");
    }
    
    public ValueTask<RespireValue> QueueCommandWithResponseAsync(Func<PipelineCommandWriter, ValueTask> commandAction, CancellationToken cancellationToken = default)
    {
        // This path still allocates - only here for compatibility
        throw new NotSupportedException("Legacy delegate-based commands are not supported. Use CommandData instead.");
    }
    
    private async Task ProcessCommandsAsync()
    {
        var reader = _commandChannel.Reader;
        var batch = new List<QueuedCommandData>(_maxBatchSize);
        
        _logger?.LogInformation("Command processing started");
        
        while (!_cancellationTokenSource.Token.IsCancellationRequested)
        {
            try
            {
                batch.Clear();
                
                // Wait for at least one command
                if (await reader.WaitToReadAsync(_cancellationTokenSource.Token).ConfigureAwait(false))
                {
                    // Try to read the first command
                    if (reader.TryRead(out var firstCommand))
                    {
                        batch.Add(firstCommand);
                        _logger?.LogDebug("Read first command, type: {Type}", firstCommand.Command.Type);
                        
                        // Try to batch more commands if immediately available
                        while (batch.Count < _maxBatchSize && reader.TryRead(out var nextCommand))
                        {
                            batch.Add(nextCommand);
                            _logger?.LogDebug("Batched additional command, type: {Type}", nextCommand.Command.Type);
                        }
                    }
                    
                    if (batch.Count > 0)
                    {
                        _logger?.LogDebug("Processing batch of {Count} commands", batch.Count);
                        await ProcessBatch(batch).ConfigureAwait(false);
                        Interlocked.Add(ref _totalCommandsProcessed, batch.Count);
                        Interlocked.Increment(ref _totalBatchesProcessed);
                        _logger?.LogDebug("Batch processed successfully");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error processing command batch");
            }
        }
        
        _logger?.LogInformation("Command processing stopped");
    }
    
    private async ValueTask ProcessBatch(List<QueuedCommandData> batch)
    {
        _logger?.LogDebug("Processing batch of {Count} commands", batch.Count);
        
        // Get a connection for this batch
        var (connectionIndex, connection) = await _multiplexer.GetConnectionDirectAsync().ConfigureAwait(false);
        
        try
        {
            // Get a writer from the pool
            var writerPool = _multiplexer.GetWriterPool(connectionIndex);
            var writer = writerPool.Get();
            
            try
            {
                // Write all commands to the pipeline
                foreach (var queuedCommand in batch)
                {
                    var command = queuedCommand.Command;
                    await CommandExecutor.ExecuteAsync(ref command, writer, CancellationToken.None).ConfigureAwait(false);
                }
                
                // Flush the pipeline
                await writer.FlushAsync().ConfigureAwait(false);
                
                // Read responses for commands that expect them
                foreach (var queuedCommand in batch)
                {
                    if (queuedCommand.ExpectsResponse && queuedCommand.ResponseHandler != null)
                    {
                        try
                        {
                            var response = await ReadResponseAsync(connection, CancellationToken.None).ConfigureAwait(false);
                            queuedCommand.ResponseHandler.SetResult(response);
                        }
                        catch (Exception ex)
                        {
                            queuedCommand.ResponseHandler.SetException(ex);
                        }
                    }
                }
            }
            finally
            {
                writerPool.Return(writer);
            }
        }
        finally
        {
            _multiplexer.ReleaseConnection(connectionIndex);
        }
    }
    
    private async ValueTask<RespireValue> ReadResponseAsync(PipelineConnection connection, CancellationToken cancellationToken)
    {
        while (true)
        {
            var readResult = await connection.ReadAsync(cancellationToken).ConfigureAwait(false);
            
            var reader = new RespPipelineReader(readResult.Buffer);
            
            if (reader.TryReadValue(out var value))
            {
                connection.AdvanceReader(reader.Consumed, reader.Examined);
                return value;
            }
            
            connection.AdvanceReader(reader.Consumed, reader.Examined);
            
            if (readResult.IsCompleted)
            {
                throw new InvalidOperationException("Connection closed while reading response");
            }
        }
    }
    
    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(RespireCommandQueue));
    }
    
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        
        _disposed = true;
        
        // Signal cancellation
        _cancellationTokenSource.Cancel();
        
        // Complete the channel
        _commandChannel.Writer.TryComplete();
        
        // Wait for processing to complete
        try
        {
            await _processingTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected
        }
        
        _cancellationTokenSource.Dispose();
        
        _logger?.LogInformation(
            "Command queue disposed. Queued: {Queued}, Processed: {Processed}, Batches: {Batches}",
            _totalCommandsQueued, _totalCommandsProcessed, _totalBatchesProcessed);
    }
    
    public void Dispose()
    {
        DisposeAsync().AsTask().Wait();
    }
}