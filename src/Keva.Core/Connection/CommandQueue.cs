using System.Threading.Channels;
using Keva.Core.Protocol;

namespace Keva.Core.Connection;

public interface ICommandQueue
{
    int QueuedCount { get; }
    bool IsQueueingEnabled { get; set; }
    
    ValueTask<bool> EnqueueAsync(QueuedCommand command, CancellationToken cancellationToken = default);
    ValueTask<QueuedCommand?> DequeueAsync(CancellationToken cancellationToken = default);
    void CompleteCommand(QueuedCommand command, RespValue response);
    void FailCommand(QueuedCommand command, Exception exception);
    void FailAll(Exception exception);
    void Clear();
}

public sealed class CommandQueue : ICommandQueue, IDisposable
{
    private readonly Channel<QueuedCommand> _queue;
    private readonly int _maxQueueSize;
    private readonly TimeSpan _defaultTimeout;
    private readonly object _lock = new();
    private readonly Dictionary<string, QueuedCommand> _pendingCommands;
    private int _queuedCount;
    private bool _isQueueingEnabled;
    
    public int QueuedCount => _queuedCount;
    public bool IsQueueingEnabled
    {
        get => _isQueueingEnabled;
        set => _isQueueingEnabled = value;
    }
    
    public CommandQueue(int maxQueueSize, TimeSpan defaultTimeout)
    {
        _maxQueueSize = maxQueueSize;
        _defaultTimeout = defaultTimeout;
        _queue = Channel.CreateBounded<QueuedCommand>(new BoundedChannelOptions(maxQueueSize)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false
        });
        _pendingCommands = new Dictionary<string, QueuedCommand>();
        _isQueueingEnabled = true;
    }
    
    public async ValueTask<bool> EnqueueAsync(QueuedCommand command, CancellationToken cancellationToken = default)
    {
        if (!_isQueueingEnabled)
            return false;
            
        if (_queuedCount >= _maxQueueSize)
            return false;
        
        command.EnqueuedAt = DateTime.UtcNow;
        command.TimeoutAt = DateTime.UtcNow.Add(command.Timeout ?? _defaultTimeout);
        
        try
        {
            await _queue.Writer.WriteAsync(command, cancellationToken);
            Interlocked.Increment(ref _queuedCount);
            
            lock (_lock)
            {
                _pendingCommands[command.Id] = command;
            }
            
            // Set up timeout
            _ = Task.Run(async () =>
            {
                await Task.Delay(command.Timeout ?? _defaultTimeout);
                if (!command.IsCompleted)
                {
                    FailCommand(command, new TimeoutException("Command timed out in queue"));
                }
            });
            
            return true;
        }
        catch
        {
            return false;
        }
    }
    
    public async ValueTask<QueuedCommand?> DequeueAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var command = await _queue.Reader.ReadAsync(cancellationToken);
            Interlocked.Decrement(ref _queuedCount);
            
            // Check if command has timed out
            if (DateTime.UtcNow >= command.TimeoutAt)
            {
                FailCommand(command, new TimeoutException("Command timed out before execution"));
                return null;
            }
            
            return command;
        }
        catch (ChannelClosedException)
        {
            return null;
        }
    }
    
    public void CompleteCommand(QueuedCommand command, RespValue response)
    {
        if (command.IsCompleted)
            return;
            
        command.IsCompleted = true;
        command.CompletedAt = DateTime.UtcNow;
        
        lock (_lock)
        {
            _pendingCommands.Remove(command.Id);
        }
        
        command.ResponseCompletionSource.TrySetResult(response);
    }
    
    public void FailCommand(QueuedCommand command, Exception exception)
    {
        if (command.IsCompleted)
            return;
            
        command.IsCompleted = true;
        command.CompletedAt = DateTime.UtcNow;
        
        lock (_lock)
        {
            _pendingCommands.Remove(command.Id);
        }
        
        command.ResponseCompletionSource.TrySetException(exception);
    }
    
    public void FailAll(Exception exception)
    {
        List<QueuedCommand> commands;
        
        lock (_lock)
        {
            commands = _pendingCommands.Values.ToList();
            _pendingCommands.Clear();
        }
        
        foreach (var command in commands)
        {
            FailCommand(command, exception);
        }
        
        // Clear the queue
        while (_queue.Reader.TryRead(out var command))
        {
            FailCommand(command, exception);
            Interlocked.Decrement(ref _queuedCount);
        }
    }
    
    public void Clear()
    {
        while (_queue.Reader.TryRead(out _))
        {
            Interlocked.Decrement(ref _queuedCount);
        }
        
        lock (_lock)
        {
            _pendingCommands.Clear();
        }
    }
    
    public void Dispose()
    {
        _queue.Writer.TryComplete();
        FailAll(new ObjectDisposedException("CommandQueue has been disposed"));
    }
}

public sealed class QueuedCommand
{
    public string Id { get; }
    public ReadOnlyMemory<byte> Command { get; }
    public string? CommandName { get; }
    public TaskCompletionSource<RespValue> ResponseCompletionSource { get; }
    public DateTime EnqueuedAt { get; set; }
    public DateTime TimeoutAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public TimeSpan? Timeout { get; set; }
    public bool IsCompleted { get; set; }
    public int RetryCount { get; set; }
    public object? Context { get; set; }
    
    public QueuedCommand(ReadOnlyMemory<byte> command, string? commandName = null, TimeSpan? timeout = null)
    {
        Id = Guid.NewGuid().ToString("N");
        Command = command;
        CommandName = commandName;
        Timeout = timeout;
        ResponseCompletionSource = new TaskCompletionSource<RespValue>();
    }
    
    public TimeSpan QueueDuration => (CompletedAt ?? DateTime.UtcNow) - EnqueuedAt;
}