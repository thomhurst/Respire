using System.IO.Pipelines;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;

namespace Respire.Infrastructure;

/// <summary>
/// High-performance pipeline-based Redis connection
/// Combines System.IO.Pipelines with pre-compiled commands for maximum throughput
/// </summary>
public sealed class PipelineConnection : IAsyncDisposable
{
    private readonly Socket _socket;
    private readonly IDuplexPipe _pipe;
    private readonly PipeReader _reader;
    private readonly PipeWriter _writer;
    private readonly SemaphoreSlim _writeSemaphore;
    private readonly CancellationTokenSource _cancellationTokenSource;
    private readonly ILogger? _logger;
    
    private volatile bool _disposed;
    private volatile bool _connected;
    
    public bool IsConnected => _connected && !_disposed;
    public string Host { get; }
    public int Port { get; }
    
    private PipelineConnection(Socket socket, IDuplexPipe pipe, string host, int port, ILogger? logger = null)
    {
        _socket = socket;
        _pipe = pipe;
        _reader = pipe.Input;
        _writer = pipe.Output;
        _writeSemaphore = new SemaphoreSlim(1, 1);
        _cancellationTokenSource = new CancellationTokenSource();
        _logger = logger;
        Host = host;
        Port = port;
        _connected = true;
    }
    
    /// <summary>
    /// Creates a new pipeline connection to Redis server
    /// </summary>
    /// <param name="host">Redis server host</param>
    /// <param name="port">Redis server port</param>
    /// <param name="connectTimeout">Connection timeout</param>
    /// <param name="logger">Optional logger</param>
    /// <returns>Connected pipeline connection</returns>
    public static async Task<PipelineConnection> ConnectAsync(
        string host, 
        int port, 
        TimeSpan connectTimeout = default,
        ILogger? logger = null)
    {
        if (connectTimeout == default)
            connectTimeout = TimeSpan.FromSeconds(30);
        
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
        {
            NoDelay = true,
            ReceiveBufferSize = 65536,
            SendBufferSize = 65536
        };
        
        try
        {
            using var cts = new CancellationTokenSource(connectTimeout);
            await socket.ConnectAsync(host, port, cts.Token).ConfigureAwait(false);
            
            var pipe = CreatePipeFromSocket(socket);
            var connection = new PipelineConnection(socket, pipe, host, port, logger);
            
            logger?.LogInformation("Connected to Redis at {Host}:{Port}", host, port);
            return connection;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
    
    /// <summary>
    /// Writes pre-compiled command bytes directly to the pipeline
    /// </summary>
    /// <param name="commandBytes">Pre-compiled command bytes</param>
    /// <param name="cancellationToken">Cancellation token</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ValueTask WritePreCompiledCommandAsync(ReadOnlyMemory<byte> commandBytes, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        
        // Try to acquire semaphore synchronously for fast path
        if (_writeSemaphore.Wait(0))
        {
            try
            {
                var memory = _writer.GetMemory(commandBytes.Length);
                commandBytes.CopyTo(memory);
                _writer.Advance(commandBytes.Length);
                
                var flushTask = _writer.FlushAsync(cancellationToken);
                
                // If flush completes synchronously, we can return synchronously
                if (flushTask.IsCompletedSuccessfully)
                {
                    _writeSemaphore.Release();
                    return default; // Completed synchronously
                }
                
                // Flush didn't complete synchronously, need async path
                return WritePreCompiledCommandAsyncSlow(flushTask);
            }
            catch
            {
                _writeSemaphore.Release();
                throw;
            }
        }
        
        // Semaphore not available, use async path
        return WritePreCompiledCommandAsyncCore(commandBytes, cancellationToken);
    }
    
    private async ValueTask WritePreCompiledCommandAsyncSlow(ValueTask<FlushResult> flushTask)
    {
        try
        {
            await flushTask.ConfigureAwait(false);
        }
        finally
        {
            _writeSemaphore.Release();
        }
    }
    
    private async ValueTask WritePreCompiledCommandAsyncCore(ReadOnlyMemory<byte> commandBytes, CancellationToken cancellationToken)
    {
        await _writeSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var memory = _writer.GetMemory(commandBytes.Length);
            commandBytes.CopyTo(memory);
            _writer.Advance(commandBytes.Length);
            
            await _writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeSemaphore.Release();
        }
    }
    
    /// <summary>
    /// Writes multiple pre-compiled commands in a batch for maximum throughput
    /// </summary>
    /// <param name="commands">Collection of pre-compiled command bytes</param>
    /// <param name="cancellationToken">Cancellation token</param>
    public async ValueTask WriteBatchAsync(IEnumerable<ReadOnlyMemory<byte>> commands, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        
        await _writeSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var command in commands)
            {
                var memory = _writer.GetMemory(command.Length);
                command.CopyTo(memory);
                _writer.Advance(command.Length);
            }
            
            await _writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeSemaphore.Release();
        }
    }
    
    /// <summary>
    /// Writes using a pooled buffer writer for complex commands
    /// Now with C# 13, we can use ref structs in async methods!
    /// </summary>
    /// <param name="writeAction">Action that writes to the buffer</param>
    /// <param name="cancellationToken">Cancellation token</param>
    public async ValueTask WriteWithPooledBufferAsync(
        Action<PooledBufferWriter> writeAction, 
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        
        using var bufferWriter = RespireMemoryPool.Shared.CreateBufferWriter();
        writeAction(bufferWriter);
        
        if (bufferWriter.WrittenCount > 0)
        {
            await WritePreCompiledCommandAsync(bufferWriter.WrittenMemory, cancellationToken).ConfigureAwait(false);
        }
    }
    
    /// <summary>
    /// Reads response data from the pipeline
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Read result containing response data</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ValueTask<ReadResult> ReadAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        
        // Try to read synchronously if data is already available
        var readTask = _reader.ReadAsync(cancellationToken);
        if (readTask.IsCompletedSuccessfully)
        {
            return new ValueTask<ReadResult>(readTask.Result);
        }
        
        // Data not immediately available, return the async task
        return readTask;
    }
    
    /// <summary>
    /// Advances the reader past consumed data
    /// </summary>
    /// <param name="consumed">Position of consumed data</param>
    /// <param name="examined">Position of examined data</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AdvanceReader(SequencePosition consumed, SequencePosition examined)
    {
        _reader.AdvanceTo(consumed, examined);
    }
    
    /// <summary>
    /// Cancels all pending operations
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void CancelPendingRead()
    {
        _reader.CancelPendingRead();
    }
    
    private static IDuplexPipe CreatePipeFromSocket(Socket socket)
    {
        var stream = new NetworkStream(socket, ownsSocket: false);
        var reader = PipeReader.Create(stream, new StreamPipeReaderOptions(
            bufferSize: 65536,
            minimumReadSize: 1024,
            leaveOpen: false
        ));
        var writer = PipeWriter.Create(stream, new StreamPipeWriterOptions(
            minimumBufferSize: 1024,
            leaveOpen: false
        ));
        return new SimpleDuplexPipe(reader, writer);
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(PipelineConnection));
    }
    
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        
        _disposed = true;
        _connected = false;
        
        try
        {
            _cancellationTokenSource.Cancel();
            
            // Complete the writer to signal end of writes
            await _writer.CompleteAsync().ConfigureAwait(false);
            
            // Complete the reader
            await _reader.CompleteAsync().ConfigureAwait(false);
            
            _logger?.LogDebug("Disconnected from Redis at {Host}:{Port}", Host, Port);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Error during connection cleanup for {Host}:{Port}", Host, Port);
        }
        finally
        {
            _writeSemaphore.Dispose();
            _cancellationTokenSource.Dispose();
            _socket.Dispose();
        }
    }
}

/// <summary>
/// Connection factory for creating pipeline connections with various options
/// </summary>
public static class PipelineConnectionFactory
{
    /// <summary>
    /// Creates a connection with default high-performance settings
    /// </summary>
    public static Task<PipelineConnection> CreateHighPerformanceConnectionAsync(
        string host, 
        int port = 6379, 
        ILogger? logger = null)
    {
        return PipelineConnection.ConnectAsync(host, port, TimeSpan.FromSeconds(30), logger);
    }
    
    /// <summary>
    /// Creates multiple connections for load balancing
    /// </summary>
    public static async Task<PipelineConnection[]> CreateConnectionPoolAsync(
        string host, 
        int port = 6379, 
        int poolSize = 0,
        ILogger? logger = null)
    {
        if (poolSize <= 0)
            poolSize = Environment.ProcessorCount;
            
        var connections = new PipelineConnection[poolSize];
        var tasks = new Task<PipelineConnection>[poolSize];
        
        for (var i = 0; i < poolSize; i++)
        {
            tasks[i] = CreateHighPerformanceConnectionAsync(host, port, logger);
        }
        
        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        Array.Copy(results, connections, poolSize);
        
        return connections;
    }
}

/// <summary>
/// Simple implementation of IDuplexPipe for combining PipeReader and PipeWriter
/// </summary>
internal sealed class SimpleDuplexPipe : IDuplexPipe
{
    public PipeReader Input { get; }
    public PipeWriter Output { get; }
    
    public SimpleDuplexPipe(PipeReader input, PipeWriter output)
    {
        Input = input;
        Output = output;
    }
}