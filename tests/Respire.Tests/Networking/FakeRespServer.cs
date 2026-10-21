using System.Net;
using System.Net.Sockets;
using System.Text;
using Respire.Protocol;

namespace Respire.Tests.Networking;

/// <summary>
/// Minimal in-process RESP server for wire tests. Parses inbound command frames with
/// RespParser, records each command as a space-joined string, and answers with the next
/// scripted reply (cycling the last reply once the script runs out). Server-initiated frames
/// (pub/sub messages, RESP3 pushes) can be injected with <see cref="SendRawAsync"/>.
/// </summary>
internal sealed class FakeRespServer : IAsyncDisposable
{
    private static readonly object s_portGate = new();
    private static readonly HashSet<int> s_usedPorts = [];

    /// <summary>Frames shared by the wire tests.</summary>
    public static readonly byte[] PingFrame = "*1\r\n$4\r\nPING\r\n"u8.ToArray();
    public static readonly byte[] OkReply = "+OK\r\n"u8.ToArray();
    public static readonly byte[] PongReply = "+PONG\r\n"u8.ToArray();

    private readonly TcpListener _listener;
    private readonly byte[][] _replies;
    private readonly Dictionary<int, int> _replyDelays = [];
    private volatile (string Prefix, int Milliseconds)[] _commandDelays = [];
    private volatile (string Prefix, Task Gate, Action<int>? OnReceived)[] _commandGates = [];
    private readonly Task _acceptTask;
    private readonly CancellationTokenSource _cts = new();
    private readonly TaskCompletionSource<Socket> _clientSocket = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _peerClosed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<string> _receivedCommands = [];
    private readonly List<byte[][]> _receivedArguments = [];
    private readonly List<int> _receivedConnectionIds = [];
    private readonly List<Socket> _clientSockets = [];
    private int _commandsSeen;
    private int _disposed;
    private volatile Task? _readGate;

    public int Port { get; }
    /// <summary>Completes after the first socket is recorded, even before a full command arrives.</summary>
    public Task ConnectionAccepted => _clientSocket.Task;
    /// <summary>Completes on peer EOF or reset, before server teardown.</summary>
    public Task PeerClosed => _peerClosed.Task;
    public int CommandsSeen => Volatile.Read(ref _commandsSeen);

    /// <summary>
    /// Holds scripted replies until this many commands have arrived. Tests use this to prove
    /// commands were pipelined instead of waiting for each preceding response.
    /// </summary>
    public int MinimumCommandsBeforeReply { get; set; } = 1;

    /// <summary>Bounds each socket read to force command fragmentation independently of TCP coalescing.</summary>
    public int MaximumBytesPerRead { get; init; } = int.MaxValue;

    /// <summary>
    /// Answers initial CLIENT SETINFO without consuming or recording scripted application commands.
    /// Disable for wire tests that explicitly script the library metadata handshake.
    /// </summary>
    public bool HandleLibraryInfo { get; set; } = true;

    /// <summary>Closes the connection after receiving this many commands, without sending a reply.</summary>
    public int? CloseConnectionAfterCommand { get; set; }

    /// <summary>
    /// Suppresses replies for matching commands, allowing cancellation tests to park a connection.
    /// </summary>
    public Func<string, bool>? SuppressReply { get; set; }

    /// <summary>Pauses socket reads so tests can separate local writes from peer receipt.</summary>
    public Task? ReadGate
    {
        get => _readGate;
        set => _readGate = value;
    }

    /// <summary>Overrides a command's scripted reply by accepted connection ID; null keeps the script.</summary>
    public Func<int, string, byte[]?>? ReplyOverride { get; set; }

    public IReadOnlyList<string> ReceivedCommands
    {
        get
        {
            lock (_receivedCommands)
            {
                return _receivedCommands.ToArray();
            }
        }
    }

    public IReadOnlyList<byte[][]> ReceivedArguments
    {
        get
        {
            lock (_receivedCommands) return _receivedArguments.ToArray();
        }
    }

    public IReadOnlyList<int> ReceivedConnectionIds
    {
        get
        {
            lock (_receivedCommands)
            {
                return _receivedConnectionIds.ToArray();
            }
        }
    }

    /// <summary>
    /// Connects a single-connection client to a fake server's port. One connection keeps command
    /// order deterministic, so tests can assert on <see cref="ReceivedCommands"/> by index.
    /// </summary>
    public static ValueTask<RespireClient> ConnectClientAsync(int port)
        => RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespireProtocol.Resp2, // Scripted fixtures describe commands after protocol setup.
            Endpoints = { new RespireEndpoint("127.0.0.1", port) },
            Connections = 1,
        });

    public FakeRespServer(params byte[][] replies) : this(1, replies)
    {
    }

    public FakeRespServer(int maxConnections, params byte[][] replies)
        : this(maxConnections, Task.CompletedTask, replies)
    {
    }

    public FakeRespServer(int maxConnections, Task acceptGate, params byte[][] replies)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxConnections);
        _replies = replies;
        _listener = StartListener();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptTask = Task.Run(async () =>
        {
            await acceptGate.WaitAsync(_cts.Token);
            await RunAsync(maxConnections);
        });
    }

    internal static TcpListener StartListener(Func<TcpListener>? createListener = null)
    {
        // Failover tests stop a server while its client still knows that endpoint. Reusing
        // the port can let that client consume another fixture's only accepted connection
        // or read its unrelated topology. Keep endpoint identities unique for this process.
        lock (s_portGate)
        {
            List<TcpListener>? rejected = null;
            try
            {
                while (true)
                {
                    var listener = createListener?.Invoke() ?? new TcpListener(IPAddress.Loopback, 0);
                    try { listener.Start(); }
                    catch { listener.Stop(); throw; }
                    if (s_usedPorts.Add(((IPEndPoint)listener.LocalEndpoint).Port)) return listener;

                    // Keep rejected ports bound until allocation succeeds so the OS cannot
                    // repeatedly return the same retired endpoint. Release rejected listeners
                    // before returning the fresh listener or propagating an allocation failure.
                    (rejected ??= []).Add(listener);
                }
            }
            finally
            {
                if (rejected is not null)
                    foreach (var listener in rejected) listener.Stop();
            }
        }
    }

    /// <summary>
    /// Delays the reply at the given script index, for tests that need client-side work to
    /// happen while a command's confirmation is still in flight. Call before the command is sent.
    /// </summary>
    public void DelayReply(int replyIndex, int milliseconds) => _replyDelays[replyIndex] = milliseconds;

    /// <summary>
    /// Delays the reply to every command whose text starts with <paramref name="commandPrefix"/>.
    /// Unlike <see cref="DelayReply"/>, it does not depend on how many setup commands precede
    /// the target. The delay also holds back later commands on the same connection.
    /// </summary>
    public void DelayCommand(string commandPrefix, int milliseconds)
        => _commandDelays = [.. _commandDelays, (commandPrefix, milliseconds)];

    /// <summary>
    /// Holds the reply to every command whose text starts with <paramref name="commandPrefix"/>
    /// until <paramref name="gate"/> completes. The command is recorded first, so a test can see
    /// it arrive and act while its reply is still guaranteed to be pending. Like
    /// <see cref="DelayCommand"/>, the gate also holds back later commands on the same connection.
    /// The optional callback signals arrival before waiting on the gate.
    /// </summary>
    public void GateCommand(string commandPrefix, Task gate, Action? onReceived = null)
        => GateCommand(commandPrefix, gate, _ => onReceived?.Invoke());

    /// <summary>
    /// Gates a command and supplies its own accepted connection ID to the arrival callback.
    /// Unlike the last entry in ReceivedConnectionIds, this remains the command's socket
    /// even when another connection records a command before the callback runs.
    /// </summary>
    public void GateCommand(string commandPrefix, Task gate, Action<int> onReceived)
        => _commandGates = [.. _commandGates, (commandPrefix, gate, onReceived)];

    /// <summary>Injects a server-initiated frame (e.g. a pub/sub message) onto the wire.</summary>
    public async Task SendRawAsync(byte[] frame)
    {
        var socket = await _clientSocket.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await SendAllAsync(socket, frame);
    }

    /// <summary>Injects a reply on a specific accepted connection, identified by ReceivedConnectionIds.</summary>
    public async Task SendRawAsync(byte[] frame, int connectionId)
    {
        Socket socket;
        lock (_receivedCommands) socket = _clientSockets[connectionId];
        await SendAllAsync(socket, frame);
    }

    public void CloseConnections()
    {
        Socket[] sockets;
        lock (_receivedCommands) sockets = _clientSockets.ToArray();
        foreach (var socket in sockets)
        {
            try { socket.Close(0); }
            catch (SocketException) { }
            catch (ObjectDisposedException) { }
        }
    }

    private static async Task SendAllAsync(Socket socket, ReadOnlyMemory<byte> frame, CancellationToken cancellationToken = default)
    {
        while (!frame.IsEmpty)
        {
            var sent = await socket.SendAsync(frame, SocketFlags.None, cancellationToken);
            if (sent == 0) throw new IOException("Socket closed during raw frame send.");
            frame = frame[sent..];
        }
    }

    public void CloseConnection(int connectionId)
    {
        Socket socket;
        lock (_receivedCommands) socket = _clientSockets[connectionId];
        socket.LingerState = new LingerOption(true, 0);
        socket.Close();
    }

    private async Task RunAsync(int maxConnections)
    {
        var connections = new List<Task>(maxConnections);
        try
        {
            for (var i = 0; i < maxConnections;)
            {
                Socket socket;
                try { socket = await _listener.AcceptSocketAsync(_cts.Token); }
                catch (SocketException error) when (!_cts.IsCancellationRequested
                    && error.SocketErrorCode is SocketError.ConnectionReset or SocketError.ConnectionAborted)
                {
                    // Windows can report a peer reset before returning the accepted socket.
                    // This peer consumes no accepted-connection slot; keep serving replacements.
                    _peerClosed.TrySetResult();
                    continue;
                }
                socket.NoDelay = true;
                lock (_receivedCommands) _clientSockets.Add(socket);
                _clientSocket.TrySetResult(socket);
                connections.Add(HandleConnectionAsync(socket, i++));
            }
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            // Test teardown before every optional connection was opened.
        }
        finally
        {
            // Stop can race AcceptSocketAsync with a socket/disposal failure instead of
            // cancellation. Every handler already started still belongs to this loop.
            await Task.WhenAll(connections);
        }
    }

    private async Task HandleConnectionAsync(Socket socket, int connectionId)
    {
        using (socket)
        {
            var buffer = new byte[1 << 20];
            var pendingReplies = new List<byte[]>();
            var end = 0;
            var replyIndex = 0;
            var inHandshake = true;

            while (!_cts.IsCancellationRequested)
            {
                int read;
                try
                {
                    if (ReadGate is { } gate) await gate.WaitAsync(_cts.Token);
                    // Large binary wire tests retain one complete frame for exact argument checks.
                    // Keep this fixture bounded rather than treating a full parse buffer as EOF.
                    if (end == buffer.Length)
                    {
                        if (buffer.Length == 8 * 1024 * 1024)
                            throw new InvalidDataException("Fake RESP frame exceeds the 8 MiB fixture limit.");
                        Array.Resize(ref buffer, buffer.Length * 2);
                    }
                    read = await socket.ReceiveAsync(
                        buffer.AsMemory(end, Math.Min(buffer.Length - end, MaximumBytesPerRead)),
                        SocketFlags.None, _cts.Token);
                }
                catch (SocketException error) when (!_cts.IsCancellationRequested
                    && error.SocketErrorCode is SocketError.ConnectionReset or SocketError.ConnectionAborted)
                {
                    // Respire deliberately uses Close(0) for abortive disposal, which is RST on Linux.
                    _peerClosed.TrySetResult();
                    return;
                }
                if (read == 0)
                {
                    _peerClosed.TrySetResult();
                    return;
                }

                end += read;

                var pos = 0;
                var hasLibraryInfoReply = false;
                while (RespParser.TryParseValue(buffer.AsSpan(0, end), ref pos, out var command) == RespParseStatus.Done)
                {
                    var commandText = FormatCommand(in command, out var arguments);
                    command.Dispose();
                    if (HandleLibraryInfo && inHandshake && commandText.StartsWith("CLIENT SETINFO LIB-", StringComparison.Ordinal))
                    {
                        pendingReplies.Add(OkReply);
                        hasLibraryInfoReply = true;
                        continue;
                    }
                    if (!(commandText.StartsWith("AUTH ", StringComparison.Ordinal)
                        || commandText.StartsWith("HELLO ", StringComparison.Ordinal)
                        || commandText.StartsWith("CLIENT SETNAME ", StringComparison.Ordinal)
                        || commandText.StartsWith("CLIENT TRACKING ", StringComparison.Ordinal)
                        || commandText.StartsWith("SELECT ", StringComparison.Ordinal)
                        || commandText is "READONLY" or "INFO SERVER"))
                        inHandshake = false;
                    if (SuppressReply is not null && pendingReplies.Count > 0 && MinimumCommandsBeforeReply <= 1)
                    {
                        // Tests park a suppressed command and inject its reply once it is recorded.
                        // Send replies owed to earlier pipelined commands (such as CLIENT CACHING YES)
                        // before recording, so an injected frame cannot overtake them.
                        foreach (var reply in pendingReplies)
                        {
                            await SendAllAsync(socket, reply, _cts.Token);
                        }

                        pendingReplies.Clear();
                    }

                    RecordCommand(commandText, arguments, connectionId);
                    var commandsSeen = Interlocked.Increment(ref _commandsSeen);
                    if (commandsSeen == CloseConnectionAfterCommand)
                    {
                        return;
                    }

                    if (_replyDelays.TryGetValue(replyIndex, out var delay))
                    {
                        await Task.Delay(delay, _cts.Token);
                    }

                    foreach (var (prefix, milliseconds) in _commandDelays)
                    {
                        if (commandText.StartsWith(prefix, StringComparison.Ordinal))
                        {
                            await Task.Delay(milliseconds, _cts.Token);
                        }
                    }

                    foreach (var (prefix, gate, onReceived) in _commandGates)
                    {
                        if (commandText.StartsWith(prefix, StringComparison.Ordinal))
                        {
                            onReceived?.Invoke(connectionId);
                            await gate.WaitAsync(_cts.Token);
                        }
                    }

                    if (SuppressReply?.Invoke(commandText) != true)
                    {
                        var reply = ReplyOverride?.Invoke(connectionId, commandText)
                            ?? _replies[Math.Min(replyIndex, _replies.Length - 1)];
                        pendingReplies.Add(reply);
                        replyIndex++;
                    }
                }

                // Automatic setup must complete before callers can send the application batch.
                // Flush all earlier replies with metadata so AUTH/HELLO retain their FIFO position.
                if (hasLibraryInfoReply || pendingReplies.Count >= MinimumCommandsBeforeReply)
                {
                    try
                    {
                        foreach (var reply in pendingReplies)
                        {
                            await SendAllAsync(socket, reply, _cts.Token);
                        }
                    }
                    catch (SocketException error) when (!_cts.IsCancellationRequested
                        && error.SocketErrorCode is SocketError.ConnectionReset or SocketError.ConnectionAborted)
                    {
                        // The peer closed while a (possibly delayed) reply was pending.
                        _peerClosed.TrySetResult();
                        return;
                    }

                    pendingReplies.Clear();
                }

                Buffer.BlockCopy(buffer, pos, buffer, 0, end - pos);
                end -= pos;
            }
        }
    }

    private static string FormatCommand(in RespValue command, out byte[][] arguments)
    {
        var elements = command.AsArray();
        var builder = new StringBuilder();
        for (var i = 0; i < elements.Length; i++)
        {
            if (i > 0)
            {
                builder.Append(' ');
            }

            builder.Append(elements[i].AsString());
        }

        arguments = new byte[elements.Length][];
        for (var i = 0; i < elements.Length; i++) arguments[i] = elements[i].AsSpan().ToArray();
        return builder.ToString();
    }

    private void RecordCommand(string commandText, byte[][] arguments, int connectionId)
    {
        lock (_receivedCommands)
        {
            _receivedCommands.Add(commandText);
            _receivedArguments.Add(arguments);
            _receivedConnectionIds.Add(connectionId);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _cts.Cancel();
        _listener.Stop();
        try
        {
            await _acceptTask;
        }
        catch
        {
            // Ignore teardown races.
        }

        _cts.Dispose();
    }
}
