using System.Buffers;
using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.ObjectPool;
using Respire.Protocol;

namespace Respire.Infrastructure;

/// <summary>
/// High-performance command queue with proper Redis pipelining
/// Batches multiple commands into single request/response cycles
/// </summary>
public sealed class RespireCommandQueuePipelined : IRespireCommandQueue
{
    private readonly RespireConnectionMultiplexer _multiplexer;
    private readonly Channel<QueuedCommand> _commandChannel;
    private readonly ILogger? _logger;
    private readonly CancellationTokenSource _cancellationTokenSource;
    private readonly Task _processingTask;
    private readonly ObjectPool<ValueTaskCompletionSource> _vtcsPool;
    private readonly int _maxBatchSize;
    private readonly TimeSpan _batchTimeout;
    private readonly int _tcsPoolSize;
    
    private volatile bool _disposed;
    private long _totalCommandsQueued;
    private long _totalCommandsProcessed;
    private long _totalBatchesProcessed;
    
    public RespireCommandQueuePipelined(
        RespireConnectionMultiplexer multiplexer,
        int maxBatchSize = 100,
        TimeSpan? batchTimeout = null,
        int tcsPoolSize = 100,
        ILogger? logger = null)
    {
        _multiplexer = multiplexer ?? throw new ArgumentNullException(nameof(multiplexer));
        _maxBatchSize = maxBatchSize;
        _batchTimeout = batchTimeout ?? TimeSpan.FromMilliseconds(1);
        _tcsPoolSize = tcsPoolSize;
        _logger = logger;
        _cancellationTokenSource = new CancellationTokenSource();
        
        // Create unbounded channel for commands
        _commandChannel = Channel.CreateUnbounded<QueuedCommand>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });
        
        // Create object pool for completion sources
        _vtcsPool = new DefaultObjectPool<ValueTaskCompletionSource>(
            new ValueTaskCompletionSourcePooledObjectPolicy(), 
            maximumRetained: _tcsPoolSize);
        
        // Start processing task
        _processingTask = ProcessCommandsAsync();
    }
    
    /// <summary>
    /// Queues a command for execution
    /// </summary>
    public async ValueTask QueueCommandAsync(
        Func<PipelineCommandWriter, ValueTask> commandAction,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        
        var command = new QueuedCommand(commandAction, null);
        await _commandChannel.Writer.WriteAsync(command, cancellationToken).ConfigureAwait(false);
        Interlocked.Increment(ref _totalCommandsQueued);
    }
    
    /// <summary>
    /// Queues a command and waits for its response
    /// </summary>
    public async ValueTask<RespireValue> QueueCommandWithResponseAsync(
        Func<PipelineCommandWriter, ValueTask> commandAction,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        
        var vtcs = _vtcsPool.Get();
        vtcs.Reset();
        var command = new QueuedCommand(commandAction, vtcs);
        
        await _commandChannel.Writer.WriteAsync(command, cancellationToken).ConfigureAwait(false);
        Interlocked.Increment(ref _totalCommandsQueued);
        
        try
        {
            var result = await vtcs.GetValueTask().ConfigureAwait(false);
            return result;
        }
        finally
        {
            _vtcsPool.Return(vtcs);
        }
    }
    
    private async Task ProcessCommandsAsync()
    {
        var reader = _commandChannel.Reader;
        var batch = new List<QueuedCommand>(_maxBatchSize);
        
        _logger?.LogInformation("Command processing task started");
        
        while (!_cancellationTokenSource.Token.IsCancellationRequested)
        {
            try
            {
                batch.Clear();
                
                // Wait for at least one command
                bool hasData;
                try
                {
                    hasData = await reader.WaitToReadAsync(_cancellationTokenSource.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break; // Shutdown requested
                }
                
                if (!hasData)
                {
                    // Channel is completed
                    _logger?.LogInformation("Command channel completed");
                    break;
                }
                
                // Read first command
                if (!reader.TryRead(out var firstCommand))
                {
                    continue; // Spurious wakeup, try again
                }
                
                batch.Add(firstCommand);
                
                // Try to batch more commands that are immediately available
                while (batch.Count < _maxBatchSize && reader.TryRead(out var command))
                {
                    batch.Add(command);
                }
                
                // If we got multiple commands immediately, wait a bit for more to maximize batching
                // But if we only have one command, process it immediately to minimize latency
                if (batch.Count > 1 && batch.Count < _maxBatchSize)
                {
                    var batchDeadline = Environment.TickCount64 + (long)_batchTimeout.TotalMilliseconds;
                    
                    while (batch.Count < _maxBatchSize && 
                           Environment.TickCount64 < batchDeadline)
                    {
                        if (reader.TryRead(out var command))
                        {
                            batch.Add(command);
                        }
                        else
                        {
                            // No more commands immediately available
                            break; // Exit the wait loop
                        }
                    }
                }
                
                // Process the batch
                if (batch.Count > 0)
                {
                    _logger?.LogDebug("Processing batch with {Count} commands", batch.Count);
                    try
                    {
                        await ProcessBatchPipelined(batch).ConfigureAwait(false);
                        Interlocked.Add(ref _totalCommandsProcessed, batch.Count);
                        Interlocked.Increment(ref _totalBatchesProcessed);
                        _logger?.LogDebug("Batch processed successfully");
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogError(ex, "Error processing batch of {Count} commands", batch.Count);
                        // Set exception on all pending commands
                        foreach (var cmd in batch)
                        {
                            cmd.ResponseHandler?.TrySetException(ex);
                        }
                    }
                    finally
                    {
                        // Clear the batch for next iteration
                        batch.Clear();
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Unexpected error in command processing loop");
                // Continue processing after error
            }
        }
        
        _logger?.LogInformation("Command processing task stopped");
    }
    
    private async ValueTask ProcessBatchPipelined(List<QueuedCommand> batch)
    {
        _logger?.LogDebug("Processing batch of {Count} commands", batch.Count);
        
        if (batch.Count == 1)
        {
            // Single command - no pipelining benefit
            var command = batch[0];
            try
            {
                _logger?.LogDebug("Executing single command");
                if (command.ResponseHandler != null)
                {
                    var response = await _multiplexer.ExecuteCommandWithResponseAsync(
                        command.CommandAction, _cancellationTokenSource.Token).ConfigureAwait(false);
                    command.ResponseHandler.SetResult(response);
                    _logger?.LogDebug("Single command completed with response");
                }
                else
                {
                    await _multiplexer.ExecuteCommandAsync(
                        command.CommandAction, _cancellationTokenSource.Token).ConfigureAwait(false);
                    _logger?.LogDebug("Single command completed (no response)");
                }
            }
            catch (Exception ex)
            {
                command.ResponseHandler?.SetException(ex);
                if (command.ResponseHandler == null)
                    _logger?.LogError(ex, "Error executing command");
            }
            return;
        }
        
        // Multiple commands - pipeline them!
        _logger?.LogDebug("Pipelining {Count} commands", batch.Count);
        using var handle = await _multiplexer.GetConnectionHandleAsync(_cancellationTokenSource.Token).ConfigureAwait(false);
        
        try
        {
            // Write ALL commands in the batch
            using var writer = new PipelineCommandWriter(handle.Connection);
            
            // Enter batch mode to buffer all commands without flushing
            writer.BeginBatch();
            
            _logger?.LogDebug("Writing {Count} commands to pipeline", batch.Count);
            foreach (var command in batch)
            {
                try
                {
                    await command.CommandAction(writer).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // If write fails, set exception and skip reading response for this command
                    command.ResponseHandler?.SetException(ex);
                    _logger?.LogError(ex, "Error writing command in pipeline");
                    throw; // Abort the whole batch
                }
            }
            
            // Now flush all commands at once
            await writer.EndBatchAsync(_cancellationTokenSource.Token).ConfigureAwait(false);
            
            _logger?.LogDebug("Finished writing and flushing commands, now reading {Count} responses", batch.Count);
            // Now read ALL responses in order
            foreach (var command in batch)
            {
                try
                {
                    // Read response (even for fire-and-forget to keep protocol in sync)
                    var response = await ReadResponseAsync(handle.Connection, _cancellationTokenSource.Token).ConfigureAwait(false);
                    
                    // Set the response if there's a handler waiting
                    command.ResponseHandler?.SetResult(response);
                    _logger?.LogDebug("Read response: {Type}", response.Type);
                }
                catch (Exception ex)
                {
                    command.ResponseHandler?.SetException(ex);
                    if (command.ResponseHandler == null)
                        _logger?.LogError(ex, "Error reading response in pipeline");
                }
            }
            _logger?.LogDebug("Successfully processed pipelined batch");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error processing pipelined batch of size {BatchSize}", batch.Count);
            
            // Set exception for any remaining commands
            foreach (var command in batch.Where(c => c.ResponseHandler != null))
            {
                command.ResponseHandler?.TrySetException(ex);
            }
            throw; // Re-throw to let the processing loop handle it
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
            
            if (readResult.IsCompleted)
            {
                connection.AdvanceReader(readResult.Buffer.Start, readResult.Buffer.End);
                throw new InvalidOperationException("Connection closed before complete response received");
            }
            
            // Not enough data yet, keep buffer and try again
            connection.AdvanceReader(readResult.Buffer.Start, readResult.Buffer.End);
        }
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(RespireCommandQueuePipelined));
    }
    
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        
        // Signal shutdown
        _cancellationTokenSource.Cancel();
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
    
    private record struct QueuedCommand(
        Func<PipelineCommandWriter, ValueTask> CommandAction,
        ValueTaskCompletionSource? ResponseHandler);
}