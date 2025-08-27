using Keva.Core.Protocol;

namespace Keva.Core.Connection;

public enum ConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    Reconnecting,
    Failed,
    Closed
}

public interface IKevaConnection : IAsyncDisposable
{
    string Id { get; }
    ConnectionState State { get; }
    bool IsConnected { get; }
    DateTime LastActivity { get; }
    
    event EventHandler<ConnectionStateChangedEventArgs>? StateChanged;
    event EventHandler<ConnectionFailedEventArgs>? Failed;
    event EventHandler<ConnectionRestoredEventArgs>? Restored;
    
    ValueTask<bool> ConnectAsync(CancellationToken cancellationToken = default);
    ValueTask DisconnectAsync(CancellationToken cancellationToken = default);
    ValueTask<bool> IsHealthyAsync(CancellationToken cancellationToken = default);
    
    ValueTask<RespValue> ExecuteAsync(ReadOnlyMemory<byte> command, CancellationToken cancellationToken = default);
    ValueTask<RespValue> ExecuteAsync(string command, params string[] args);
    
    ValueTask<T> ExecuteAsync<T>(Func<RespValue, T> resultMapper, ReadOnlyMemory<byte> command, CancellationToken cancellationToken = default);
}

public class ConnectionStateChangedEventArgs : EventArgs
{
    public ConnectionState OldState { get; }
    public ConnectionState NewState { get; }
    public string? Reason { get; }
    
    public ConnectionStateChangedEventArgs(ConnectionState oldState, ConnectionState newState, string? reason = null)
    {
        OldState = oldState;
        NewState = newState;
        Reason = reason;
    }
}

public class ConnectionFailedEventArgs : EventArgs
{
    public Exception Exception { get; }
    public int RetryAttempt { get; }
    public TimeSpan NextRetryDelay { get; }
    
    public ConnectionFailedEventArgs(Exception exception, int retryAttempt, TimeSpan nextRetryDelay)
    {
        Exception = exception;
        RetryAttempt = retryAttempt;
        NextRetryDelay = nextRetryDelay;
    }
}

public class ConnectionRestoredEventArgs : EventArgs
{
    public TimeSpan Downtime { get; }
    public int RetryCount { get; }
    
    public ConnectionRestoredEventArgs(TimeSpan downtime, int retryCount)
    {
        Downtime = downtime;
        RetryCount = retryCount;
    }
}