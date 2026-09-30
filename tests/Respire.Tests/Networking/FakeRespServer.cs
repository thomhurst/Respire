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
    /// <summary>Frames shared by the wire tests.</summary>
    public static readonly byte[] PingFrame = "*1\r\n$4\r\nPING\r\n"u8.ToArray();
    public static readonly byte[] OkReply = "+OK\r\n"u8.ToArray();
    public static readonly byte[] PongReply = "+PONG\r\n"u8.ToArray();

    private readonly TcpListener _listener;
    private readonly byte[][] _replies;
    private readonly Dictionary<int, int> _replyDelays = [];
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

    public int Port { get; }
    /// <summary>Completes on peer EOF or reset, before server teardown.</summary>
    public Task PeerClosed => _peerClosed.Task;
    public int CommandsSeen => Volatile.Read(ref _commandsSeen);

    /// <summary>
    /// Holds scripted replies until this many commands have arrived. Tests use this to prove
    /// commands were pipelined instead of waiting for each preceding response.
    /// </summary>
    public int MinimumCommandsBeforeReply { get; set; } = 1;

    /// <summary>Closes the connection after receiving this many commands, without sending a reply.</summary>
    public int? CloseConnectionAfterCommand { get; set; }

    /// <summary>
    /// Suppresses replies for matching commands, allowing cancellation tests to park a connection.
    /// </summary>
    public Func<string, bool>? SuppressReply { get; set; }

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
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptTask = Task.Run(async () =>
        {
            await acceptGate.WaitAsync(_cts.Token);
            await RunAsync(maxConnections);
        });
    }

    /// <summary>
    /// Delays the reply at the given script index, for tests that need client-side work to
    /// happen while a command's confirmation is still in flight. Call before the command is sent.
    /// </summary>
    public void DelayReply(int replyIndex, int milliseconds) => _replyDelays[replyIndex] = milliseconds;

    /// <summary>Injects a server-initiated frame (e.g. a pub/sub message) onto the wire.</summary>
    public async Task SendRawAsync(byte[] frame)
    {
        var socket = await _clientSocket.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await socket.SendAsync(frame, SocketFlags.None);
    }

    /// <summary>Injects a reply on a specific accepted connection, identified by ReceivedConnectionIds.</summary>
    public async Task SendRawAsync(byte[] frame, int connectionId)
    {
        Socket socket;
        lock (_receivedCommands) socket = _clientSockets[connectionId];
        await socket.SendAsync(frame, SocketFlags.None);
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

        await Task.WhenAll(connections);
    }

    private async Task HandleConnectionAsync(Socket socket, int connectionId)
    {
        using (socket)
        {
            var buffer = new byte[1 << 20];
            var pendingReplies = new List<byte[]>();
            var end = 0;
            var replyIndex = 0;

            while (!_cts.IsCancellationRequested)
            {
                int read;
                try { read = await socket.ReceiveAsync(buffer.AsMemory(end), SocketFlags.None, _cts.Token); }
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
                while (RespParser.TryParseValue(buffer.AsSpan(0, end), ref pos, out var command) == RespParseStatus.Done)
                {
                    var commandText = RecordCommand(in command, connectionId);
                    command.Dispose();
                    var commandsSeen = Interlocked.Increment(ref _commandsSeen);
                    if (commandsSeen == CloseConnectionAfterCommand)
                    {
                        return;
                    }

                    if (_replyDelays.TryGetValue(replyIndex, out var delay))
                    {
                        await Task.Delay(delay, _cts.Token);
                    }

                    if (SuppressReply?.Invoke(commandText) != true)
                    {
                        var reply = ReplyOverride?.Invoke(connectionId, commandText)
                            ?? _replies[Math.Min(replyIndex, _replies.Length - 1)];
                        pendingReplies.Add(reply);
                        replyIndex++;
                    }
                }

                if (pendingReplies.Count >= MinimumCommandsBeforeReply)
                {
                    foreach (var reply in pendingReplies)
                    {
                        await socket.SendAsync(reply, SocketFlags.None, _cts.Token);
                    }

                    pendingReplies.Clear();
                }

                Buffer.BlockCopy(buffer, pos, buffer, 0, end - pos);
                end -= pos;
            }
        }
    }

    private string RecordCommand(in RespValue command, int connectionId)
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

        var arguments = new byte[elements.Length][];
        for (var i = 0; i < elements.Length; i++) arguments[i] = elements[i].AsSpan().ToArray();
        var commandText = builder.ToString();
        lock (_receivedCommands)
        {
            _receivedCommands.Add(commandText);
            _receivedArguments.Add(arguments);
            _receivedConnectionIds.Add(connectionId);
        }

        return commandText;
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
