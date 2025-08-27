using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Keva.Core.Protocol;

namespace Keva.Core.Infrastructure;

/// <summary>
/// High-performance command queue using System.Threading.Channels
/// Provides async command queueing with backpressure management and batch processing
/// </summary>
public sealed class KevaCommandQueue : IAsyncDisposable
{
    private readonly Channel<QueuedCommand> _commandChannel;
    private readonly ChannelWriter<QueuedCommand> _writer;
    private readonly ChannelReader<QueuedCommand> _reader;
    private readonly KevaConnectionMultiplexer _multiplexer;
    private readonly Task _processingTask;
    private readonly CancellationTokenSource _cancellationTokenSource;
    private readonly ILogger? _logger;
    
    private volatile bool _disposed;
    
    // Performance counters
    private long _totalCommandsProcessed;
    private long _totalBatchesProcessed;
    private long _totalCommandsQueued;
    
    public long TotalCommandsProcessed => _totalCommandsProcessed;
    public long TotalBatchesProcessed => _totalBatchesProcessed;
    public long TotalCommandsQueued => _totalCommandsQueued;
    public int QueuedCommandsCount => _reader.CanCount ? _reader.Count : -1;
    
    /// <summary>
    /// Configuration options for the command queue
    /// </summary>
    public readonly struct QueueOptions
    {
        public int Capacity { get; init; }
        public int BatchSize { get; init; }
        public TimeSpan BatchTimeout { get; init; }
        public BoundedChannelFullMode FullMode { get; init; }
        public bool SingleReader { get; init; }
        public bool SingleWriter { get; init; }
        
        public static QueueOptions Default => new()
        {
            Capacity = 10000,
            BatchSize = 100,
            BatchTimeout = TimeSpan.FromMilliseconds(1),
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        };
        
        public static QueueOptions HighThroughput => new()
        {
            Capacity = 100000,
            BatchSize = 1000,
            BatchTimeout = TimeSpan.FromMilliseconds(10),
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        };
        
        public static QueueOptions LowLatency => new()
        {
            Capacity = 1000,
            BatchSize = 10,
            BatchTimeout = TimeSpan.FromMicroseconds(100),
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        };
    }
    
    private readonly int _batchSize;
    private readonly TimeSpan _batchTimeout;
    
    public KevaCommandQueue(
        KevaConnectionMultiplexer multiplexer,
        QueueOptions options = default,
        ILogger? logger = null)
    {
        _multiplexer = multiplexer ?? throw new ArgumentNullException(nameof(multiplexer));
        _logger = logger;
        _cancellationTokenSource = new CancellationTokenSource();
        
        if (options.Equals(default(QueueOptions)))
            options = QueueOptions.Default;
        
        _batchSize = options.BatchSize;
        _batchTimeout = options.BatchTimeout;
        
        // Create bounded channel with specified options
        var channelOptions = new BoundedChannelOptions(options.Capacity)
        {
            FullMode = options.FullMode,
            SingleReader = options.SingleReader,
            SingleWriter = options.SingleWriter,
            AllowSynchronousContinuations = false
        };
        
        _commandChannel = Channel.CreateBounded<QueuedCommand>(channelOptions);
        _writer = _commandChannel.Writer;
        _reader = _commandChannel.Reader;
        
        // Start background processing task
        _processingTask = Task.Run(ProcessCommandsAsync);
        
        _logger?.LogInformation(
            "Created command queue with capacity {Capacity}, batch size {BatchSize}, timeout {BatchTimeout}ms",
            options.Capacity, options.BatchSize, options.BatchTimeout.TotalMilliseconds);
    }
    
    /// <summary>
    /// Queues a command for asynchronous execution
    /// </summary>
    /// <param name="commandAction">Action that writes the command</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Task that completes when the command is queued (not executed)</returns>
    public async ValueTask QueueCommandAsync(
        Func<PipelineCommandWriter, ValueTask> commandAction,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        
        var command = new QueuedCommand(commandAction, null);
        await _writer.WriteAsync(command, cancellationToken).ConfigureAwait(false);
        Interlocked.Increment(ref _totalCommandsQueued);
    }
    
    /// <summary>
    /// Queues a command with response handling
    /// </summary>
    /// <param name="commandAction">Action that writes the command</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Task that completes with the command response</returns>
    public async ValueTask<KevaValue> QueueCommandWithResponseAsync(
        Func<PipelineCommandWriter, ValueTask> commandAction,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        
        var tcs = new TaskCompletionSource<KevaValue>();
        var command = new QueuedCommand(commandAction, tcs);
        
        await _writer.WriteAsync(command, cancellationToken).ConfigureAwait(false);
        Interlocked.Increment(ref _totalCommandsQueued);
        
        return await tcs.Task.ConfigureAwait(false);
    }
    
    /// <summary>
    /// Queues multiple commands for batch execution
    /// </summary>
    /// <param name="commands">Commands to queue</param>
    /// <param name="cancellationToken">Cancellation token</param>
    public async ValueTask QueueBatchAsync(
        IEnumerable<Func<PipelineCommandWriter, ValueTask>> commands,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        
        foreach (var command in commands)
        {
            await QueueCommandAsync(command, cancellationToken).ConfigureAwait(false);
        }
    }
    
    /// <summary>
    /// Queues a pre-compiled command directly
    /// </summary>
    /// <param name="preCompiledCommand">Pre-compiled command bytes</param>
    /// <param name="cancellationToken">Cancellation token</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ValueTask QueuePreCompiledAsync(
        ReadOnlyMemory<byte> preCompiledCommand,
        CancellationToken cancellationToken = default)
    {
        return QueueCommandAsync(writer => writer.WritePreCompiledAsync(preCompiledCommand, cancellationToken), cancellationToken);
    }
    
    /// <summary>
    /// Convenience methods for common Redis commands
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ValueTask QueuePingAsync(CancellationToken cancellationToken = default)
        => QueuePreCompiledAsync(RespCommands.Ping, cancellationToken);
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ValueTask QueueInfoAsync(CancellationToken cancellationToken = default)
        => QueuePreCompiledAsync(RespCommands.Info, cancellationToken);
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ValueTask QueueDbSizeAsync(CancellationToken cancellationToken = default)
        => QueuePreCompiledAsync(RespCommands.DbSize, cancellationToken);
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ValueTask QueueGetAsync(string key, CancellationToken cancellationToken = default)
        => QueueCommandAsync(writer => writer.WriteGetAsync(key, cancellationToken), cancellationToken);
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ValueTask QueueSetAsync(string key, string value, CancellationToken cancellationToken = default)
        => QueueCommandAsync(writer => writer.WriteSetAsync(key, value, cancellationToken), cancellationToken);
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ValueTask<KevaValue> QueueGetWithResponseAsync(string key, CancellationToken cancellationToken = default)
        => QueueCommandWithResponseAsync(writer => writer.WriteGetAsync(key, cancellationToken), cancellationToken);
    
    /// <summary>
    /// Signals that no more commands will be added to the queue
    /// </summary>
    public void CompleteAdding()
    {
        _writer.Complete();
    }
    
    /// <summary>
    /// Gets queue statistics for monitoring
    /// </summary>
    public QueueStats GetStats()
    {
        return new QueueStats
        {
            TotalCommandsQueued = _totalCommandsQueued,
            TotalCommandsProcessed = _totalCommandsProcessed,
            TotalBatchesProcessed = _totalBatchesProcessed,
            QueuedCommandsCount = QueuedCommandsCount,
            IsCompleted = _reader.Completion.IsCompleted
        };
    }
    
    private async Task ProcessCommandsAsync()
    {
        var batch = new List<QueuedCommand>(_batchSize);
        
        try
        {
            while (await _reader.WaitToReadAsync(_cancellationTokenSource.Token).ConfigureAwait(false))
            {
                batch.Clear();
                
                // Collect commands for batch processing
                var deadline = DateTime.UtcNow.Add(_batchTimeout);
                
                // Get first command
                if (_reader.TryRead(out var firstCommand))
                {
                    batch.Add(firstCommand);
                }
                
                // Collect additional commands up to batch size or timeout
                while (batch.Count < _batchSize && DateTime.UtcNow < deadline)
                {
                    if (_reader.TryRead(out var command))
                    {
                        batch.Add(command);
                    }
                    else
                    {
                        // Wait a short time for more commands
                        await Task.Delay(TimeSpan.FromMicroseconds(50), _cancellationTokenSource.Token).ConfigureAwait(false);
                        break;
                    }
                }
                
                // Process the batch
                if (batch.Count > 0)
                {
                    await ProcessCommandBatch(batch).ConfigureAwait(false);
                    Interlocked.Add(ref _totalCommandsProcessed, batch.Count);
                    Interlocked.Increment(ref _totalBatchesProcessed);
                }
            }
        }
        catch (OperationCanceledException) when (_cancellationTokenSource.Token.IsCancellationRequested)
        {
            // Expected cancellation
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error in command processing loop");
        }
        
        _logger?.LogDebug("Command processing task completed");
    }
    
    private async ValueTask ProcessCommandBatch(List<QueuedCommand> batch)
    {
        try
        {
            // Group commands by whether they need responses
            var commandsWithoutResponse = new List<QueuedCommand>();
            var commandsWithResponse = new List<QueuedCommand>();
            
            foreach (var command in batch)
            {
                if (command.ResponseHandler == null)
                    commandsWithoutResponse.Add(command);
                else
                    commandsWithResponse.Add(command);
            }
            
            // Process commands without responses in parallel
            if (commandsWithoutResponse.Count > 0)
            {
                var parallelActions = commandsWithoutResponse.Select(cmd => cmd.CommandAction);
                await _multiplexer.ExecuteParallelCommandsAsync(parallelActions, _cancellationTokenSource.Token).ConfigureAwait(false);
            }
            
            // Process commands with responses sequentially (for now)
            foreach (var command in commandsWithResponse)
            {
                try
                {
                    var response = await _multiplexer.ExecuteCommandWithResponseAsync(
                        command.CommandAction, _cancellationTokenSource.Token).ConfigureAwait(false);
                    command.ResponseHandler.SetResult(response);
                }
                catch (Exception ex)
                {
                    command.ResponseHandler.SetException(ex);
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error processing command batch of size {BatchSize}", batch.Count);
            
            // Set exceptions for any commands with response handlers
            foreach (var command in batch.Where(c => c.ResponseHandler != null))
            {
                command.ResponseHandler.SetException(ex);
            }
        }
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(KevaCommandQueue));
    }
    
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        
        _disposed = true;
        
        try
        {
            // Complete the writer to stop accepting new commands
            _writer.Complete();
            
            // Cancel processing and wait for completion
            _cancellationTokenSource.Cancel();
            await _processingTask.ConfigureAwait(false);
            
            _logger?.LogInformation(
                "Disposed command queue. Processed {TotalCommands} commands in {TotalBatches} batches",
                _totalCommandsProcessed, _totalBatchesProcessed);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Error during command queue disposal");
        }
        finally
        {
            _cancellationTokenSource.Dispose();
        }
    }
    
    /// <summary>
    /// Represents a queued command with optional response handling
    /// </summary>
    private readonly struct QueuedCommand
    {
        public Func<PipelineCommandWriter, ValueTask> CommandAction { get; }
        public TaskCompletionSource<KevaValue>? ResponseHandler { get; }
        
        public QueuedCommand(
            Func<PipelineCommandWriter, ValueTask> commandAction,
            TaskCompletionSource<KevaValue>? responseHandler)
        {
            CommandAction = commandAction;
            ResponseHandler = responseHandler;
        }
    }
}

/// <summary>
/// Queue statistics for monitoring
/// </summary>
public readonly struct QueueStats
{
    public long TotalCommandsQueued { get; init; }
    public long TotalCommandsProcessed { get; init; }
    public long TotalBatchesProcessed { get; init; }
    public int QueuedCommandsCount { get; init; }
    public bool IsCompleted { get; init; }
    
    public double AverageCommandsPerBatch => TotalBatchesProcessed > 0 
        ? (double)TotalCommandsProcessed / TotalBatchesProcessed 
        : 0;
    
    public long PendingCommands => TotalCommandsQueued - TotalCommandsProcessed;
}