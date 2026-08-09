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
    private readonly List<string> _receivedCommands = [];
    private int _commandsSeen;

    public int Port { get; }
    public int CommandsSeen => Volatile.Read(ref _commandsSeen);

    /// <summary>Holds replies until this many commands have arrived.</summary>
    public int MinimumCommandsBeforeReply { get; set; } = 1;

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
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxConnections);
        _replies = replies;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptTask = Task.Run(() => RunAsync(maxConnections));
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

    private async Task RunAsync(int maxConnections)
    {
        var connections = new List<Task>(maxConnections);
        try
        {
            for (var i = 0; i < maxConnections; i++)
            {
                var socket = await _listener.AcceptSocketAsync(_cts.Token);
                socket.NoDelay = true;
                _clientSocket.TrySetResult(socket);
                connections.Add(HandleConnectionAsync(socket));
            }
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            // Test teardown before every optional connection was opened.
        }

        await Task.WhenAll(connections);
    }

    private async Task HandleConnectionAsync(Socket socket)
    {
        using (socket)
        {
            var buffer = new byte[1 << 20];
            var pendingReplies = new List<int>();
            var end = 0;
            var replyIndex = 0;

            while (!_cts.IsCancellationRequested)
            {
                var read = await socket.ReceiveAsync(buffer.AsMemory(end), SocketFlags.None, _cts.Token);
                if (read == 0)
                {
                    return;
                }

                end += read;

                var pos = 0;
                while (RespParser.TryParseValue(buffer.AsSpan(0, end), ref pos, out var command) == RespParseStatus.Done)
                {
                    RecordCommand(in command);
                    command.Dispose();
                    Interlocked.Increment(ref _commandsSeen);
                    if (_replyDelays.TryGetValue(replyIndex, out var delay))
                    {
                        await Task.Delay(delay, _cts.Token);
                    }

                    pendingReplies.Add(replyIndex++);
                }

                if (CommandsSeen >= MinimumCommandsBeforeReply)
                {
                    foreach (var pendingReply in pendingReplies)
                    {
                        var reply = _replies[Math.Min(pendingReply, _replies.Length - 1)];
                        await socket.SendAsync(reply, SocketFlags.None, _cts.Token);
                    }

                    pendingReplies.Clear();
                }

                Buffer.BlockCopy(buffer, pos, buffer, 0, end - pos);
                end -= pos;
            }
        }
    }

    private void RecordCommand(in RespValue command)
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

        lock (_receivedCommands)
        {
            _receivedCommands.Add(builder.ToString());
        }
    }

    public async ValueTask DisposeAsync()
    {
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
