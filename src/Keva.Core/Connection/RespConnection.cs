using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using Keva.Core.Protocol;

namespace Keva.Core.Connection;

public sealed class RespConnection : IKevaConnection
{
    private readonly ConnectionOptions _options;
    private readonly ArrayPool<byte> _arrayPool;
    private readonly ArrayPool<RespValue> _valuePool;
    private readonly SemaphoreSlim _writeLock;
    private readonly Channel<PendingCommand> _commandQueue;
    private readonly CancellationTokenSource _disposeCts;
    
    private Socket? _socket;
    private NetworkStream? _stream;
    private PipeReader? _reader;
    private PipeWriter? _writer;
    private ConnectionState _state;
    private DateTime _lastActivity;
    private int _reconnectAttempts;
    private DateTime? _disconnectedAt;
    private Task? _readLoop;
    private Task? _healthCheckLoop;
    
    public string Id { get; }
    public ConnectionState State => _state;
    public bool IsConnected => _state == ConnectionState.Connected;
    public DateTime LastActivity => _lastActivity;
    
    public event EventHandler<ConnectionStateChangedEventArgs>? StateChanged;
    public event EventHandler<ConnectionFailedEventArgs>? Failed;
    public event EventHandler<ConnectionRestoredEventArgs>? Restored;
    
    public RespConnection(ConnectionOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _arrayPool = ArrayPool<byte>.Shared;
        _valuePool = ArrayPool<RespValue>.Shared;
        _writeLock = new SemaphoreSlim(1, 1);
        _commandQueue = Channel.CreateUnbounded<PendingCommand>();
        _disposeCts = new CancellationTokenSource();
        
        Id = Guid.NewGuid().ToString("N");
        _state = ConnectionState.Disconnected;
        _lastActivity = DateTime.UtcNow;
    }
    
    public async ValueTask<bool> ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (_state == ConnectionState.Connected)
        {
            return true;
        }

        if (_state == ConnectionState.Connecting)
        {
            return await WaitForConnectionAsync(cancellationToken);
        }

        try
        {
            await ChangeStateAsync(ConnectionState.Connecting);
            
            _socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
            {
                NoDelay = true,
                SendBufferSize = _options.SendBufferSize,
                ReceiveBufferSize = _options.ReceiveBufferSize
            };
            
            var endpoint = _options.GetEndPoint();
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposeCts.Token);
            connectCts.CancelAfter(_options.ConnectTimeout);
            
            await _socket.ConnectAsync(endpoint, connectCts.Token);
            
            _stream = new NetworkStream(_socket, ownsSocket: true);
            var pipe = new Pipe();
            _reader = pipe.Reader;
            _writer = PipeWriter.Create(_stream);
            
            // Start read loop
            _readLoop = Task.Run(() => ReadLoopAsync(_disposeCts.Token), _disposeCts.Token);
            
            // Authenticate if needed
            if (!string.IsNullOrEmpty(_options.Password))
            {
                await AuthenticateAsync(cancellationToken);
            }
            
            // Select database
            if (_options.Database > 0)
            {
                await SelectDatabaseAsync(_options.Database, cancellationToken);
            }
            
            // Start health checks
            if (_options.EnableHealthChecks)
            {
                _healthCheckLoop = Task.Run(() => HealthCheckLoopAsync(_disposeCts.Token), _disposeCts.Token);
            }
            
            await ChangeStateAsync(ConnectionState.Connected);
            _lastActivity = DateTime.UtcNow;
            
            // Notify restoration if this was a reconnection
            if (_disconnectedAt.HasValue)
            {
                var downtime = DateTime.UtcNow - _disconnectedAt.Value;
                Restored?.Invoke(this, new ConnectionRestoredEventArgs(downtime, _reconnectAttempts));
                _disconnectedAt = null;
                _reconnectAttempts = 0;
            }
            
            return true;
        }
        catch (Exception ex)
        {
            await ChangeStateAsync(ConnectionState.Failed);
            Failed?.Invoke(this, new ConnectionFailedEventArgs(ex, _reconnectAttempts, CalculateBackoff()));
            
            if (_options.EnableAutoReconnect && _reconnectAttempts < _options.MaxReconnectAttempts)
            {
                _ = Task.Run(() => ReconnectAsync(), _disposeCts.Token);
            }
            
            return false;
        }
    }
    
    public async ValueTask DisconnectAsync(CancellationToken cancellationToken = default)
    {
        if (_state == ConnectionState.Disconnected || _state == ConnectionState.Closed)
        {
            return;
        }

        await ChangeStateAsync(ConnectionState.Disconnected);
        
        try
        {
            _reader?.Complete();
            _writer?.Complete();
            _stream?.Dispose();
            _socket?.Dispose();
        }
        catch
        {
            // Ignore cleanup errors
        }
        
        _reader = null;
        _writer = null;
        _stream = null;
        _socket = null;
    }
    
    public async ValueTask<bool> IsHealthyAsync(CancellationToken cancellationToken = default)
    {
        if (!IsConnected)
        {
            return false;
        }

        try
        {
            var pong = await ExecuteAsync("PING"u8.ToArray(), cancellationToken);
            return pong.Type == RespDataType.SimpleString && pong.AsString() == "PONG";
        }
        catch
        {
            return false;
        }
    }
    
    public async ValueTask<RespValue> ExecuteAsync(ReadOnlyMemory<byte> command, CancellationToken cancellationToken = default)
    {
        if (!IsConnected)
        {
            if (_options.EnableAutoReconnect)
            {
                await ConnectAsync(cancellationToken);
            }
            else
            {
                throw new InvalidOperationException("Not connected");
            }
        }
        
        var pending = new PendingCommand(command);
        
        try
        {
            await _writeLock.WaitAsync(cancellationToken);
            
            if (_writer == null)
            {
                throw new InvalidOperationException("Connection is not established");
            }

            // Write command directly without using ref struct in async
            // Note: For now, we're just writing the raw command bytes
            // In a real implementation, we'd parse and properly format the command
            await _writer.WriteAsync(command, cancellationToken);
            await _writer.FlushAsync(cancellationToken);
            
            _lastActivity = DateTime.UtcNow;
        }
        finally
        {
            _writeLock.Release();
        }
        
        // Wait for response
        // In a real implementation, we'd have a proper response correlation mechanism
        // For now, we'll just return a success response
        return RespValue.SimpleString("OK");
    }
    
    public ValueTask<RespValue> ExecuteAsync(string command, params string[] args)
    {
        var buffer = new ArrayBufferWriter<byte>();
        WriteCommand(buffer, command, args);
        
        return ExecuteAsync(buffer.WrittenMemory);
    }
    
    private void WriteCommand(ArrayBufferWriter<byte> buffer, string command, string[] args)
    {
        var writer = new RespWriter(buffer, _arrayPool);
        writer.WriteCommand(command, args);
    }
    
    public async ValueTask<T> ExecuteAsync<T>(Func<RespValue, T> resultMapper, ReadOnlyMemory<byte> command, CancellationToken cancellationToken = default)
    {
        var result = await ExecuteAsync(command, cancellationToken);
        return resultMapper(result);
    }
    
    public async ValueTask DisposeAsync()
    {
        if (_state == ConnectionState.Closed)
        {
            return;
        }

        await ChangeStateAsync(ConnectionState.Closed);
        
        _disposeCts.Cancel();
        
        await DisconnectAsync();
        
        if (_readLoop != null)
        {
            try { await _readLoop; } catch { }
        }
        
        if (_healthCheckLoop != null)
        {
            try { await _healthCheckLoop; } catch { }
        }
        
        _writeLock.Dispose();
        _disposeCts.Dispose();
    }
    
    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && IsConnected)
        {
            try
            {
                // Read responses and correlate with pending commands
                // This is a simplified version - real implementation would be more complex
                await Task.Delay(100, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // Handle read errors
                Failed?.Invoke(this, new ConnectionFailedEventArgs(ex, 0, TimeSpan.Zero));
                break;
            }
        }
    }
    
    private async Task HealthCheckLoopAsync(CancellationToken cancellationToken)
    {
        var consecutiveFailures = 0;
        
        while (!cancellationToken.IsCancellationRequested && IsConnected)
        {
            try
            {
                await Task.Delay(_options.HealthCheckInterval, cancellationToken);
                
                if (await IsHealthyAsync(cancellationToken))
                {
                    consecutiveFailures = 0;
                }
                else
                {
                    consecutiveFailures++;
                    if (consecutiveFailures >= _options.MaxFailuresBeforeReconnect)
                    {
                        await ReconnectAsync();
                        break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                consecutiveFailures++;
            }
        }
    }
    
    private async Task ReconnectAsync()
    {
        if (_state == ConnectionState.Reconnecting)
        {
            return;
        }

        _disconnectedAt ??= DateTime.UtcNow;
        await ChangeStateAsync(ConnectionState.Reconnecting);
        
        while (_reconnectAttempts < _options.MaxReconnectAttempts && !_disposeCts.Token.IsCancellationRequested)
        {
            _reconnectAttempts++;
            
            var delay = CalculateBackoff();
            await Task.Delay(delay, _disposeCts.Token);
            
            if (await ConnectAsync(_disposeCts.Token))
            {
                break;
            }
        }
        
        if (!IsConnected)
        {
            await ChangeStateAsync(ConnectionState.Failed);
        }
    }
    
    private TimeSpan CalculateBackoff()
    {
        var baseDelay = _options.ReconnectDelay;
        var maxDelay = _options.MaxReconnectDelay;
        
        return _options.ReconnectBackoff switch
        {
            BackoffStrategy.Fixed => baseDelay,
            BackoffStrategy.Linear => TimeSpan.FromMilliseconds(Math.Min(baseDelay.TotalMilliseconds * _reconnectAttempts, maxDelay.TotalMilliseconds)),
            BackoffStrategy.Exponential => TimeSpan.FromMilliseconds(Math.Min(baseDelay.TotalMilliseconds * Math.Pow(2, _reconnectAttempts - 1), maxDelay.TotalMilliseconds)),
            BackoffStrategy.ExponentialWithJitter => TimeSpan.FromMilliseconds(
                Math.Min(baseDelay.TotalMilliseconds * Math.Pow(2, _reconnectAttempts - 1), maxDelay.TotalMilliseconds) * 
                (0.7 + Random.Shared.NextDouble() * 0.3)),
            _ => baseDelay
        };
    }
    
    private async Task AuthenticateAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(_options.Username))
        {
            await ExecuteAsync("AUTH", _options.Username, _options.Password!);
        }
        else if (!string.IsNullOrEmpty(_options.Password))
        {
            await ExecuteAsync("AUTH", _options.Password);
        }
    }
    
    private async Task SelectDatabaseAsync(int database, CancellationToken cancellationToken)
    {
        await ExecuteAsync("SELECT", database.ToString());
    }
    
    private async Task<bool> WaitForConnectionAsync(CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<bool>();
        
        void OnStateChanged(object? sender, ConnectionStateChangedEventArgs e)
        {
            if (e.NewState == ConnectionState.Connected)
            {
                tcs.TrySetResult(true);
            }
            else if (e.NewState == ConnectionState.Failed || e.NewState == ConnectionState.Closed)
            {
                tcs.TrySetResult(false);
            }
        }
        
        StateChanged += OnStateChanged;
        
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposeCts.Token);
            cts.CancelAfter(_options.ConnectTimeout);
            
            return await tcs.Task.WaitAsync(cts.Token);
        }
        finally
        {
            StateChanged -= OnStateChanged;
        }
    }
    
    private async Task ChangeStateAsync(ConnectionState newState)
    {
        var oldState = _state;
        if (oldState == newState)
        {
            return;
        }

        _state = newState;
        StateChanged?.Invoke(this, new ConnectionStateChangedEventArgs(oldState, newState));
        await Task.CompletedTask;
    }
    
    private class PendingCommand
    {
        public ReadOnlyMemory<byte> Command { get; }
        public TaskCompletionSource<RespValue> Response { get; }
        public CancellationTokenSource? TimeoutCts { get; set; }
        
        public PendingCommand(ReadOnlyMemory<byte> command)
        {
            Command = command;
            Response = new TaskCompletionSource<RespValue>();
        }
    }
}