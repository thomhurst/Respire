using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using Microsoft.Extensions.Logging;
using Respire.Internal;
using Respire.Protocol;

namespace Respire.Networking;

/// <summary>
/// A single multiplexed RESP connection: one socket, a coalescing write path, a dedicated
/// receive loop, and a FIFO in-flight queue pairing pipelined commands with their responses.
/// </summary>
/// <remarks>
/// <para>
/// <b>Write path.</b> Callers serialize their command into the active write buffer and enqueue
/// a pooled completion source into the in-flight ring under one short lock, then a single
/// flush loop (started on demand, never more than one) swaps the double buffers and sends the
/// coalesced bytes — many pipelined commands per syscall. Socket sends are never cancelled:
/// aborting a partially sent frame would desynchronize the protocol stream permanently, so
/// failures abort the whole connection instead.
/// </para>
/// <para>
/// <b>Read path.</b> One receive loop reads straight from the socket into a pooled contiguous
/// buffer (no pipe — that costs a second full-payload copy) and incrementally parses RESP
/// values, copying each payload exactly once into pooled storage owned by the completed
/// <see cref="RespValue"/>. Bulk payloads at or above <see cref="DirectFillThreshold"/> are
/// received directly into their pooled payload array. Because RESP has no correlation ids,
/// responses complete in-flight sources strictly in FIFO order.
/// </para>
/// <para>
/// <b>Failure.</b> Any socket fault marks the connection dead, wakes the receive loop by
/// closing the socket, and fails every in-flight command. Dead connections are replaced by the
/// multiplexer, never revived in place.
/// </para>
/// </remarks>
public sealed class RespireConnection : IAsyncDisposable
{
    private const int DirectFillThreshold = 4 * 1024;
    private const int MaxResponseSize = 512 * 1024 * 1024;
    private static readonly TimeSpan MinWatchdogDelay = TimeSpan.FromMilliseconds(1);
    private static readonly TimeSpan MaxWatchdogSleep = TimeSpan.FromDays(1);

    private readonly Socket _socket;
    private readonly SslStream? _tlsStream;
    private readonly Lock _writeGate = new();
    private readonly InflightRing _inflight;
    private readonly PendingResponsePool _sourcePool;
    private readonly int _receiveBufferSize;
    private readonly string? _networkPeerAddress;
    private readonly int? _networkPeerPort;
    private readonly ILogger? _logger;
    private readonly RespirePushHandler? _pushHandler;
    private readonly Task _receiveTask;
    private readonly Task _flushTask;
    private readonly Task? _watchdogTask;
    private readonly CancellationTokenSource? _watchdogCancellation;
    private readonly TimeSpan? _responseTimeout;
    // Sent/received counters and the deadline are one state transition: a reply must not clear
    // a deadline concurrently armed for a later batch.
    private readonly Lock _receiveDeadlineGate = new();
    private readonly AsyncFlushSignal _flushSignal = new();
    private readonly AsyncCapacitySignal _capacitySignal = new();
    private readonly CompletionScheduler _completions = new();

    private WriteBuffer _activeBuffer;
    private WriteBuffer _spareBuffer;
    private int _activeReplyCount;
    private bool _dead;
    private int _disposed;
    private long _serverClientId;
    private long _sentReplyCount;
    private long _receivedReplyCount;
    private long _receiveDeadlineTimestamp;
    private int _responseTimeoutSuppressions;
    private Exception? _abortReason;

    public string Host { get; }
    public int Port { get; }
    public bool IsConnected => !Volatile.Read(ref _dead);
    internal string? NetworkPeerAddress => _networkPeerAddress;
    internal int? NetworkPeerPort => _networkPeerPort;

    /// <summary>
    /// Completes when the connection dies for any reason (fault, remote close, disposal). Never
    /// faults itself — used to observe connection lifetime (e.g. pub/sub auto-resubscribe).
    /// </summary>
    internal Task Closed => _receiveTask;

    /// <summary>
    /// Redis's connection ID, populated only when reliable cross-connection correction ordering
    /// is enabled. Zero means it has not been requested.
    /// </summary>
    internal long ServerClientId => Volatile.Read(ref _serverClientId);

    private RespireConnection(
        Socket socket, SslStream? tlsStream, string host, int port, RespireConnectionOptions options, ILogger? logger)
    {
        _socket = socket;
        _tlsStream = tlsStream;
        if (socket.RemoteEndPoint is IPEndPoint remoteEndpoint)
        {
            var address = remoteEndpoint.Address;
            _networkPeerAddress = (address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).ToString();
            _networkPeerPort = remoteEndpoint.Port;
        }

        Host = host;
        Port = port;
        _logger = logger;
        _pushHandler = options.PushHandler;
        _receiveBufferSize = options.ReceiveBufferSize;
        _inflight = new InflightRing(options.MaxInflightCommands);
        _sourcePool = new PendingResponsePool(options.CompletionSourcePoolSize);
        _activeBuffer = new WriteBuffer(options.WriteBufferSize);
        _spareBuffer = new WriteBuffer(options.WriteBufferSize);
        _responseTimeout = options.ResponseTimeout;
        if (_responseTimeout is not null)
        {
            _watchdogCancellation = new CancellationTokenSource();
        }

        _receiveTask = Task.Run(ReceiveLoopAsync);
        _flushTask = Task.Run(FlushLoopAsync);
        if (_responseTimeout is { } responseTimeout)
        {
            _watchdogTask = WatchReceiveAsync(responseTimeout, _watchdogCancellation!.Token);
        }
    }

    public static async Task<RespireConnection> ConnectAsync(
        string host,
        int port,
        RespireConnectionOptions? options = null,
        ILogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        options ??= RespireConnectionOptions.Default;
        if (options.ResponseTimeout is { } invalidTimeout && invalidTimeout < MinWatchdogDelay)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options), "ResponseTimeout must be at least one millisecond.");
        }

        ValidateTcpKeepAlive(options);

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp)
        {
            NoDelay = true,
        };

        if (options.SocketReceiveBufferSize > 0)
        {
            socket.ReceiveBufferSize = options.SocketReceiveBufferSize;
        }

        if (options.SocketSendBufferSize > 0)
        {
            socket.SendBufferSize = options.SocketSendBufferSize;
        }

        SslStream? tlsStream = null;
        try
        {
            ApplyTcpKeepAlive(socket, options);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(options.ConnectTimeout);
            await socket.ConnectAsync(host, port, timeoutCts.Token).ConfigureAwait(false);

            if (options.UseTls)
            {
                tlsStream = new SslStream(new NetworkStream(socket, ownsSocket: false));
                var tlsOptions = CreateTlsOptions(options.TlsOptions, host);
                await tlsStream.AuthenticateAsClientAsync(tlsOptions, timeoutCts.Token).ConfigureAwait(false);
            }
        }
        catch
        {
            tlsStream?.Dispose();
            socket.Dispose();
            throw;
        }

        logger?.LogDebug("Connected to {Host}:{Port}", host, port);
        var connection = new RespireConnection(socket, tlsStream, host, port, options, logger);
        try
        {
            await connection.HandshakeAsync(options, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return connection;
    }

    private static SslClientAuthenticationOptions CreateTlsOptions(
        SslClientAuthenticationOptions? configured,
        string host)
    {
        if (configured is null)
        {
            return new SslClientAuthenticationOptions { TargetHost = host };
        }

        if (!string.IsNullOrWhiteSpace(configured.TargetHost))
        {
            return configured;
        }

        // Do not mutate a caller-owned options instance: one instance can configure several
        // concurrent connections, including connections to different cluster nodes.
        var copy = new SslClientAuthenticationOptions
        {
            AllowRenegotiation = configured.AllowRenegotiation,
            AllowTlsResume = configured.AllowTlsResume,
            ApplicationProtocols = configured.ApplicationProtocols,
            CertificateChainPolicy = configured.CertificateChainPolicy,
            CertificateRevocationCheckMode = configured.CertificateRevocationCheckMode,
            CipherSuitesPolicy = configured.CipherSuitesPolicy,
            ClientCertificateContext = configured.ClientCertificateContext,
            ClientCertificates = configured.ClientCertificates,
            EnabledSslProtocols = configured.EnabledSslProtocols,
            EncryptionPolicy = configured.EncryptionPolicy,
            LocalCertificateSelectionCallback = configured.LocalCertificateSelectionCallback,
            RemoteCertificateValidationCallback = configured.RemoteCertificateValidationCallback,
            TargetHost = host,
        };
#if NET10_0_OR_GREATER
        if (OperatingSystem.IsLinux() || OperatingSystem.IsWindows())
        {
            copy.AllowRsaPkcs1Padding = configured.AllowRsaPkcs1Padding;
            copy.AllowRsaPssPadding = configured.AllowRsaPssPadding;
        }
#endif
        return copy;
    }

    private static void ValidateTcpKeepAlive(RespireConnectionOptions options)
    {
        // The socket options carry whole seconds; sub-second values would silently truncate to 0.
        if (options.TcpKeepAliveTime is { } time && time < TimeSpan.FromSeconds(1))
        {
            throw new ArgumentOutOfRangeException(
                nameof(options), "TcpKeepAliveTime must be at least one second.");
        }

        if (options.TcpKeepAliveInterval is { } interval && interval < TimeSpan.FromSeconds(1))
        {
            throw new ArgumentOutOfRangeException(
                nameof(options), "TcpKeepAliveInterval must be at least one second.");
        }

        if (options.TcpKeepAliveRetryCount is { } retryCount && retryCount < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options), "TcpKeepAliveRetryCount must be at least 1.");
        }

        if (options.TcpKeepAliveTime is null
            && (options.TcpKeepAliveInterval is not null || options.TcpKeepAliveRetryCount is not null))
        {
            throw new ArgumentException(
                $"{nameof(RespireConnectionOptions.TcpKeepAliveInterval)} and {nameof(RespireConnectionOptions.TcpKeepAliveRetryCount)} require {nameof(RespireConnectionOptions.TcpKeepAliveTime)} to be set.",
                nameof(options));
        }
    }

    internal static void ApplyTcpKeepAlive(Socket socket, RespireConnectionOptions options)
    {
        if (options.TcpKeepAliveTime is not { } time)
        {
            return;
        }

        socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
        socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, (int)time.TotalSeconds);
        if (options.TcpKeepAliveInterval is { } interval)
        {
            socket.SetSocketOption(
                SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, (int)interval.TotalSeconds);
        }

        if (options.TcpKeepAliveRetryCount is { } retryCount)
        {
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, retryCount);
        }
    }

    /// <summary>
    /// Runs HELLO/AUTH/CLIENT SETNAME through the normal send path before the connection is
    /// handed out, so every later command runs on an authenticated, protocol-negotiated stream.
    /// </summary>
    private async Task HandshakeAsync(RespireConnectionOptions options, CancellationToken cancellationToken)
    {
        List<(string Step, ValueTask<RespValue> Reply)>? pending = null;
        if (options.UseResp3)
        {
            (pending ??= new(3)).Add(("HELLO", SendAsync(
                new Commands.HelloCommand(options.Username, options.Password), cancellationToken)));
        }
        else if (options.Password is not null)
        {
            (pending ??= new(3)).Add(("AUTH", SendAsync(
                new Commands.AuthCommand(options.Username, options.Password), cancellationToken)));
        }

        if (options.ClientName is not null)
        {
            (pending ??= new(3)).Add(("CLIENT SETNAME", SendAsync(
                new Commands.ClientSetNameCommand(options.ClientName), cancellationToken)));
        }

        if (options.Database != 0)
        {
            (pending ??= new(3)).Add(("SELECT", SendAsync(
                new Commands.SelectCommand(options.Database), cancellationToken)));
        }

        if (pending is null)
        {
            return;
        }

        Exception? failure = null;
        foreach (var (step, pendingReply) in pending)
        {
            try
            {
                var reply = await pendingReply.ConfigureAwait(false);
                try
                {
                    if (failure is null && reply.IsError)
                    {
                        failure = CreateHandshakeException(in reply, step);
                    }
                    else if (failure is null && step == "HELLO")
                    {
                        ValidateHelloProtocol(in reply);
                    }
                }
                finally
                {
                    reply.Dispose();
                }
            }
            catch (Exception ex)
            {
                // Observe every pipelined reply, but preserve the first failure. A later transport
                // fault must not replace the useful AUTH/HELLO error that caused the handshake to fail.
                failure ??= ex;
            }
        }

        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private RespireConnectionException CreateHandshakeException(in RespValue reply, string step)
        => new($"{step} failed for {Host}:{Port}: {reply.GetErrorMessage()}");

    private void ValidateHelloProtocol(in RespValue reply)
    {
        if (reply.Type == RespDataType.Map)
        {
            var fields = reply.AsArray();
            for (var i = 0; i + 1 < fields.Length; i += 2)
            {
                if (fields[i].AsSpan().SequenceEqual("proto"u8)
                    && fields[i + 1].Type == RespDataType.Integer
                    && fields[i + 1].AsInteger() == 3)
                {
                    return;
                }
            }
        }

        throw new RespireConnectionException(
            $"HELLO 3 failed for {Host}:{Port}: server did not confirm RESP3 (expected map field 'proto' = 3).");
    }

    /// <summary>
    /// Captures Redis's connection ID. Corrections use it to kill a locally dead connection on
    /// the server before claiming that a command previously flushed on that socket is harmless.
    /// </summary>
    internal async ValueTask<long> EnsureServerClientIdAsync(CancellationToken cancellationToken = default)
    {
        var existing = ServerClientId;
        if (existing != 0)
        {
            return existing;
        }

        var reply = await SendAsync(new Commands.ClientIdCommand(), cancellationToken).ConfigureAwait(false);
        if (reply.IsError)
        {
            var message = reply.GetErrorMessage();
            reply.Dispose();
            throw new RespireServerException(message, "CLIENT ID");
        }

        var id = reply.AsInteger();
        reply.Dispose();
        Interlocked.CompareExchange(ref _serverClientId, id, 0);
        return ServerClientId;
    }

    /// <summary>
    /// Serializes the command into the coalescing write buffer and returns a task that
    /// completes with its response.
    /// </summary>
    public ValueTask<RespValue> SendAsync<TCommand>(in TCommand command, CancellationToken cancellationToken = default)
        where TCommand : struct, IRespCommand
        => SendCoreAsync(in command, discardRepliesBefore: 0, throwOnError: false, cancellationToken);

    /// <summary>Sends an intentionally blocking command without applying the receive watchdog.</summary>
    internal async ValueTask<RespValue> SendWithoutResponseTimeoutAsync<TCommand>(
        TCommand command, CancellationToken cancellationToken = default)
        where TCommand : struct, IRespCommand
    {
        Interlocked.Increment(ref _responseTimeoutSuppressions);
        try
        {
            return await SendAsync(in command, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref _responseTimeoutSuppressions);
        }
    }

    /// <summary>Sends a prefixed blocking command without applying the receive watchdog.</summary>
    internal async ValueTask<RespValue> SendPrefixedWithoutResponseTimeoutAsync<TPrefix, TCommand>(
        TPrefix prefix,
        TCommand command,
        bool throwOnError,
        CancellationToken cancellationToken = default)
        where TPrefix : struct, IRespCommand
        where TCommand : struct, IRespCommand
    {
        Interlocked.Increment(ref _responseTimeoutSuppressions);
        try
        {
            return await SendPrefixedAsync(
                    in prefix, in command, throwOnError, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref _responseTimeoutSuppressions);
        }
    }

    /// <summary>Sends a command and translates a RESP error reply when its result is consumed.</summary>
    internal ValueTask<RespValue> SendCheckedAsync<TCommand>(
        in TCommand command,
        CancellationToken cancellationToken = default,
        string? commandName = null)
        where TCommand : struct, IRespCommand
        => SendCoreAsync(
            in command, discardRepliesBefore: 0, throwOnError: true, cancellationToken, commandName);

    /// <summary>
    /// Sends a command through a typed in-flight source, avoiding intermediate async state
    /// machines. Conversion occurs when caller consumes result, never on receive loop.
    /// </summary>
    internal ValueTask<TResult> SendConvertedAsync<TCommand, TState, TResult>(
        in TCommand command,
        TState state,
        ResponseConverter<TState, TResult> converter,
        bool transferOwnership,
        CancellationToken cancellationToken = default,
        string? commandName = null)
        where TCommand : struct, IRespCommand
    {
        var source = ConvertedPendingResponseSource<TState, TResult>.Rent(
            state, converter, transferOwnership, commandName);
        bool enqueued;
        bool startedBatch;
        try
        {
            enqueued = TryEnqueue(in command, source, out startedBatch);
        }
        catch
        {
            ReclaimUnpublished(source);
            throw;
        }

        if (enqueued)
        {
            source.RegisterCancellation(cancellationToken);
            ScheduleFlush(startedBatch);
            return source.Task;
        }

        return SendConvertedSlowAsync(command, source, cancellationToken);
    }

    /// <summary>
    /// Sends MULTI + pre-serialized commands + EXEC as one atomic append, so no other
    /// multiplexed command can interleave into the server-side transaction state. MULTI's +OK
    /// and each queue reply are drained; the returned task completes with the first queue
    /// error when present, otherwise EXEC's reply. Retaining queue errors is required because
    /// Redis Cluster can report MOVED/ASK there and then return only EXECABORT from EXEC.
    /// </summary>
    public ValueTask<RespValue> SendTransactionAsync(
        ReadOnlyMemory<byte> serializedCommands, int commandCount, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(commandCount);

        // MULTI's +OK plus one +QUEUED per command precede the EXEC reply. A transaction
        // needing more slots than the ring holds could never enqueue and would spin in the
        // slow path forever — reject it up front.
        var slotsNeeded = commandCount + 2;
        if (slotsNeeded > _inflight.Capacity)
        {
            throw new ArgumentOutOfRangeException(
                nameof(commandCount),
                $"A transaction with {commandCount} commands needs {slotsNeeded} in-flight slots, but this connection allows {_inflight.Capacity} (see {nameof(RespireConnectionOptions)}.{nameof(RespireConnectionOptions.MaxInflightCommands)}).");
        }

        return SendTransactionCoreAsync(
            new TransactionCommand(serializedCommands), repliesBeforeFinal: commandCount + 1,
            firstQueueReply: 1, cancellationToken);
    }

    /// <summary>
    /// Appends a one-shot connection prelude and its command under the same write-gate hold.
    /// The prelude reply is consumed; the returned task completes with the command reply.
    /// </summary>
    internal ValueTask<RespValue> SendPrefixedCheckedAsync<TPrefix, TCommand>(
        in TPrefix prefix,
        in TCommand command,
        CancellationToken cancellationToken = default,
        string? commandName = null)
        where TPrefix : struct, IRespCommand
        where TCommand : struct, IRespCommand
        => SendPrefixedAsync(
            in prefix, in command, throwOnError: true, cancellationToken, commandName);

    internal ValueTask<RespValue> SendPrefixedAsync<TPrefix, TCommand>(
        in TPrefix prefix,
        in TCommand command,
        bool throwOnError,
        CancellationToken cancellationToken = default,
        string? commandName = null)
        where TPrefix : struct, IRespCommand
        where TCommand : struct, IRespCommand
    {
        if (_inflight.Capacity < 2)
        {
            throw new InvalidOperationException(
                $"A prefixed command needs 2 in-flight slots, but this connection allows {_inflight.Capacity}.");
        }

        return SendCoreAsync(
            new PrefixedCommand<TPrefix, TCommand>(prefix, command),
            discardRepliesBefore: 1,
            throwOnError,
            cancellationToken,
            commandName);
    }

    private ValueTask<RespValue> SendTransactionCoreAsync<TCommand>(
        in TCommand command,
        int repliesBeforeFinal,
        int firstQueueReply,
        CancellationToken cancellationToken)
        where TCommand : struct, IRespCommand
    {
        var replyCount = repliesBeforeFinal + 1;
        var source = TransactionPendingResponseSource.Rent(replyCount, firstQueueReply);

        bool enqueued;
        bool startedBatch;
        try
        {
            enqueued = TryEnqueue(
                in command, source, out startedBatch, repliesBeforeFinal, retainRepliesBefore: true);
        }
        catch
        {
            ReclaimUnpublished(source, replyCount + 1);
            throw;
        }

        if (!enqueued)
        {
            return SendTransactionSlowAsync(
                command, source, repliesBeforeFinal, replyCount, cancellationToken);
        }

        source.RegisterCancellation(cancellationToken);
        ScheduleFlush(startedBatch);
        return source.Task;
    }

    private ValueTask<RespValue> SendCoreAsync<TCommand>(
        in TCommand command,
        int discardRepliesBefore,
        bool throwOnError,
        CancellationToken cancellationToken,
        string? commandName = null)
        where TCommand : struct, IRespCommand
    {
        var source = _sourcePool.Rent(throwOnError, commandName);
        bool enqueued;
        bool startedBatch;
        try
        {
            enqueued = TryEnqueue(in command, source, out startedBatch, discardRepliesBefore);
        }
        catch
        {
            ReclaimUnpublished(source);
            throw;
        }

        if (enqueued)
        {
            source.RegisterCancellation(cancellationToken);
            ScheduleFlush(startedBatch);
            return source.Task;
        }

        return SendSlowAsync(command, source, discardRepliesBefore, cancellationToken);
    }

    /// <summary>
    /// Sends a command whose response is read from the wire but discarded. Completes once the
    /// command has been written to the socket.
    /// </summary>
    public ValueTask SendFireAndForgetAsync<TCommand>(in TCommand command, CancellationToken cancellationToken = default)
        where TCommand : struct, IRespCommand
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (TryEnqueueForWrite(in command, out var startedBatch, out var writeTask))
        {
            ScheduleFlush(startedBatch);
            return WaitForWriteAsync(writeTask, cancellationToken);
        }

        return SendFireAndForgetSlowAsync(command, cancellationToken);
    }

    private static ValueTask WaitForWriteAsync(Task writeTask, CancellationToken cancellationToken)
        => cancellationToken.CanBeCanceled
            ? new ValueTask(writeTask.WaitAsync(cancellationToken))
            : new ValueTask(writeTask);

    [ThreadStatic]
    private static WriteBuffer? _serializeScratch;

    /// <summary>
    /// Positive after a frame outgrew <see cref="ScratchRetainLimit"/>: the thread's next
    /// commands serialize directly into the active buffer under the gate, whose storage
    /// persists across commands. Any further large frame refreshes the budget, so workloads
    /// mixing large writes with small commands stay on the direct path instead of renting,
    /// copying, and discarding an oversized scratch buffer per large command (above the
    /// pool's limit that is an LOH allocation each time). Sustained small traffic decays the
    /// budget and returns to the scratch path.
    /// </summary>
    [ThreadStatic]
    private static int _directPathBudget;

    private const int ScratchInitialSize = 4 * 1024;
    private const int ScratchRetainLimit = 64 * 1024;
    private const int DirectPathBudgetAfterLargeFrame = 64;

    /// <summary>
    /// Appends the command and its pending source atomically: buffer byte order must exactly
    /// match ring order, or every later response on this connection answers the wrong command.
    /// A composite command reserves <paramref name="discardRepliesBefore"/> slots ahead of
    /// its final reply. Ordinary prefixes discard them; transactions retain them in their
    /// multi-reply source so queue errors remain observable.
    /// Serialization runs outside the write gate into a per-thread scratch buffer, so
    /// concurrent callers contend only for a memcpy and the ring enqueue — not for UTF-8
    /// encoding their payloads.
    /// </summary>
    private bool TryEnqueue<TCommand>(
        in TCommand command,
        PendingResponse source,
        out bool startedBatch,
        int discardRepliesBefore = 0,
        bool retainRepliesBefore = false)
        where TCommand : struct, IRespCommand
        => TryEnqueue(
            in command,
            source,
            out startedBatch,
            out _,
            trackWrite: false,
            discardRepliesBefore,
            retainRepliesBefore);

    private bool TryEnqueueForWrite<TCommand>(
        in TCommand command,
        out bool startedBatch,
        out Task writeTask)
        where TCommand : struct, IRespCommand
    {
        var enqueued = TryEnqueue(
            in command,
            InflightRing.DiscardSentinel,
            out startedBatch,
            out var trackedWrite,
            trackWrite: true);
        writeTask = trackedWrite ?? Task.CompletedTask;
        return enqueued;
    }

    private bool TryEnqueue<TCommand>(
        in TCommand command,
        PendingResponse source,
        out bool startedBatch,
        out Task? writeTask,
        bool trackWrite,
        int discardRepliesBefore = 0,
        bool retainRepliesBefore = false)
        where TCommand : struct, IRespCommand
    {
        startedBatch = false;
        writeTask = null;

        // Racy pre-check; the authoritative one runs under the gate below. This keeps the
        // ring-full retry loop from re-serializing the frame on every attempt.
        if (_inflight.Capacity - _inflight.Count < discardRepliesBefore + 1)
        {
            return false;
        }

        if (_directPathBudget > 0)
        {
            _directPathBudget--;
            return TryEnqueueDirect(
                in command,
                source,
                out startedBatch,
                out writeTask,
                trackWrite,
                discardRepliesBefore,
                retainRepliesBefore);
        }

        var scratch = _serializeScratch ??= new WriteBuffer(ScratchInitialSize);
        try
        {
            scratch.Reset();
            var writer = new RespWriter(scratch);
            command.Write(ref writer);
            var frame = scratch.WrittenMemory.Span;

            lock (_writeGate)
            {
                if (_dead)
                {
                    throw new RespireConnectionException($"Connection to {Host}:{Port} is closed.");
                }

                if (_inflight.Capacity - _inflight.Count < discardRepliesBefore + 1)
                {
                    return false;
                }

                // Inline wake-up is worth it only on an otherwise idle connection: nothing
                // buffered and nothing awaiting a reply. With responses still in flight the
                // flush loop is already cycling, and stealing the producer thread for the
                // send costs more than the dispatch it saves.
                startedBatch = _activeBuffer.Count == 0 && _inflight.Count == 0;
                _activeBuffer.Append(frame);
                if (_responseTimeout is not null)
                {
                    _activeReplyCount += discardRepliesBefore + 1;
                }

                for (var i = 0; i < discardRepliesBefore; i++)
                {
                    _inflight.TryEnqueue(retainRepliesBefore ? source : InflightRing.DiscardSentinel);
                }

                _inflight.TryEnqueue(source);
                if (trackWrite)
                {
                    writeTask = _activeBuffer.WriteCompletion;
                }

                return true;
            }
        }
        finally
        {
            // An oversized frame (a multi-megabyte SET, a large transaction block) must not
            // stay pinned to this thread for the rest of its life.
            if (scratch.Capacity > ScratchRetainLimit)
            {
                _serializeScratch = null;
                scratch.Release();
                _directPathBudget = DirectPathBudgetAfterLargeFrame;
            }
        }
    }

    /// <summary>
    /// The pre-scratch path: serializes under the gate straight into the active buffer, whose
    /// storage persists across commands. Used while the thread's direct-path budget lasts so
    /// large-write workloads reuse the active buffer's growth instead of churning scratch.
    /// </summary>
    private bool TryEnqueueDirect<TCommand>(
        in TCommand command,
        PendingResponse source,
        out bool startedBatch,
        out Task? writeTask,
        bool trackWrite,
        int discardRepliesBefore,
        bool retainRepliesBefore)
        where TCommand : struct, IRespCommand
    {
        startedBatch = false;
        writeTask = null;
        lock (_writeGate)
        {
            if (_dead)
            {
                throw new RespireConnectionException($"Connection to {Host}:{Port} is closed.");
            }

            if (_inflight.Capacity - _inflight.Count < discardRepliesBefore + 1)
            {
                return false;
            }

            var mark = _activeBuffer.Count;
            startedBatch = mark == 0 && _inflight.Count == 0;
            try
            {
                var writer = new RespWriter(_activeBuffer);
                command.Write(ref writer);
            }
            catch
            {
                _activeBuffer.TruncateTo(mark);
                throw;
            }

            if (_activeBuffer.Count - mark > ScratchRetainLimit)
            {
                _directPathBudget = DirectPathBudgetAfterLargeFrame;
            }

            if (_responseTimeout is not null)
            {
                _activeReplyCount += discardRepliesBefore + 1;
            }

            for (var i = 0; i < discardRepliesBefore; i++)
            {
                _inflight.TryEnqueue(retainRepliesBefore ? source : InflightRing.DiscardSentinel);
            }

            _inflight.TryEnqueue(source);
            if (trackWrite)
            {
                writeTask = _activeBuffer.WriteCompletion;
            }

            return true;
        }
    }

#if NET
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
#endif
    private async ValueTask<RespValue> SendSlowAsync<TCommand>(
        TCommand command, PendingResponseSource source, int discardRepliesBefore, CancellationToken cancellationToken)
        where TCommand : struct, IRespCommand
    {
        var startedBatch = await WaitForInflightCapacityAsync(command, source, discardRepliesBefore, cancellationToken).ConfigureAwait(false);
        source.RegisterCancellation(cancellationToken);
        ScheduleFlush(startedBatch);
        return await source.Task.ConfigureAwait(false);
    }

#if NET
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
#endif
    private async ValueTask<RespValue> SendTransactionSlowAsync<TCommand>(
        TCommand command,
        TransactionPendingResponseSource source,
        int repliesBeforeFinal,
        int replyCount,
        CancellationToken cancellationToken)
        where TCommand : struct, IRespCommand
    {
        bool startedBatch;
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var capacityAvailable = _capacitySignal.WaitAsync(cancellationToken);
                if (TryEnqueue(
                    in command, source, out startedBatch, repliesBeforeFinal, retainRepliesBefore: true))
                {
                    break;
                }

                ScheduleFlush(startedBatch: false);
                await capacityAvailable.ConfigureAwait(false);
            }
        }
        catch
        {
            ReclaimUnpublished(source, replyCount + 1);
            throw;
        }

        source.RegisterCancellation(cancellationToken);
        ScheduleFlush(startedBatch);
        return await source.Task.ConfigureAwait(false);
    }

#if NET
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
#endif
    private async ValueTask<TResult> SendConvertedSlowAsync<TCommand, TState, TResult>(
        TCommand command,
        ConvertedPendingResponseSource<TState, TResult> source,
        CancellationToken cancellationToken)
        where TCommand : struct, IRespCommand
    {
        var startedBatch = await WaitForInflightCapacityAsync(command, source, 0, cancellationToken).ConfigureAwait(false);
        source.RegisterCancellation(cancellationToken);
        ScheduleFlush(startedBatch);
        return await source.Task.ConfigureAwait(false);
    }

#if NET
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
#endif
    private async ValueTask SendFireAndForgetSlowAsync<TCommand>(TCommand command, CancellationToken cancellationToken)
        where TCommand : struct, IRespCommand
    {
        bool startedBatch;
        Task writeTask;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var capacityAvailable = _capacitySignal.WaitAsync(cancellationToken);
            if (TryEnqueueForWrite(in command, out startedBatch, out writeTask))
            {
                break;
            }

            ScheduleFlush(startedBatch: false);
            await capacityAvailable.ConfigureAwait(false);
        }

        ScheduleFlush(startedBatch);
        await WaitForWriteAsync(writeTask, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// In-flight ring full: flush, then park until the receive loop frees capacity. Returns
    /// whether the eventual enqueue started a new write batch.
    /// </summary>
    private async ValueTask<bool> WaitForInflightCapacityAsync<TCommand>(
        TCommand command, PendingResponse source, int discardRepliesBefore, CancellationToken cancellationToken)
        where TCommand : struct, IRespCommand
    {
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var capacityAvailable = _capacitySignal.WaitAsync(cancellationToken);

                // Arm before retrying so a concurrent dequeue cannot pulse between the
                // failed enqueue and waiter registration.
                if (TryEnqueue(in command, source, out var startedBatch, discardRepliesBefore))
                {
                    return startedBatch;
                }

                ScheduleFlush(startedBatch: false);
                await capacityAvailable.ConfigureAwait(false);
            }
        }
        catch
        {
            ReclaimUnpublished(source);
            throw;
        }
    }

    /// <summary>Returns a rented source that was never enqueued or exposed to a caller.</summary>
    private static void ReclaimUnpublished(PendingResponse source)
    {
        if (ReferenceEquals(source, InflightRing.DiscardSentinel))
        {
            return;
        }

        ReclaimUnpublished(source, referenceCount: 2);
    }

    private static void ReclaimUnpublished(PendingResponse source, int referenceCount)
    {
        for (var i = 0; i < referenceCount; i++)
        {
            source.ReleaseRef();
        }
    }

    /// <summary>
    /// Wakes the flush loop. A command written to an otherwise idle connection wakes it
    /// inline so the send begins without a thread-pool hop; when other commands are buffered
    /// or awaiting replies the wake dispatches asynchronously — the flush loop is already
    /// cycling, and capturing producer threads would not deepen batches.
    /// </summary>
    private void ScheduleFlush(bool startedBatch) => _flushSignal.Signal(preferInline: startedBatch);

    /// <summary>
    /// Caps how many consecutive batches the flush loop sends without ever suspending before
    /// it forces a yield to the thread pool. <see cref="AsyncFlushSignal"/> resumes the loop
    /// inline on the signaling thread, and on platforms where socket sends complete
    /// synchronously that thread could otherwise be captured for as long as producers keep
    /// the buffer non-empty.
    /// </summary>
    private const int MaxSynchronousBatchesBeforeYield = 8;

    /// <summary>
    /// The connection's single persistent sender. Parks on <see cref="_flushSignal"/> between
    /// batches instead of spawning a Task per flush, then drains whatever has coalesced into
    /// the active buffer — many pipelined commands per syscall. Waking is inline: the first
    /// command into an empty buffer runs this loop on its own thread up to the first true
    /// suspension, so the send syscall starts without a thread-pool hop.
    /// </summary>
    private async Task FlushLoopAsync()
    {
        WriteBuffer? sending = null;
        try
        {
            var synchronousBatches = 0;
            while (true)
            {
                await _flushSignal.WaitAsync().ConfigureAwait(false);

                while (true)
                {
                    int sendingReplyCount;
                    lock (_writeGate)
                    {
                        if (_dead)
                        {
                            return;
                        }

                        if (_activeBuffer.Count == 0)
                        {
                            break;
                        }

                        sending = _activeBuffer;
                        _activeBuffer = _spareBuffer;
                        _spareBuffer = sending;
                        sendingReplyCount = _activeReplyCount;
                        _activeReplyCount = 0;
                    }

                    // Never cancelled: a partial RESP frame on the wire is unrecoverable.
                    var memory = sending.WrittenMemory;
                    if (_tlsStream is null)
                    {
                        while (memory.Length > 0)
                        {
                            var pending = _socket.SendAsync(memory, SocketFlags.None);
                            int sent;
                            if (pending.IsCompletedSuccessfully)
                            {
                                sent = pending.Result;
                            }
                            else
                            {
                                synchronousBatches = -1;
                                sent = await pending.ConfigureAwait(false);
                            }

                            memory = memory[sent..];
                        }
                    }
                    else
                    {
                        var pending = _tlsStream.WriteAsync(memory);
                        if (!pending.IsCompletedSuccessfully)
                        {
                            synchronousBatches = -1;
                        }

                        await pending.ConfigureAwait(false);
                    }

                    MarkRepliesSent(sendingReplyCount);

                    sending.CompleteWrite();
                    sending.Reset();
                    sending = null;

                    if (++synchronousBatches >= MaxSynchronousBatchesBeforeYield)
                    {
                        synchronousBatches = 0;
                        await Task.Yield();
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Send failed for {Host}:{Port}; aborting connection", Host, Port);
            var failure = new RespireConnectionException($"Send failed for {Host}:{Port}: {ex.Message}", ex);
            sending?.FailWrite(failure);
            Abort(failure);
        }
    }

    private async Task ReceiveLoopAsync()
    {
        var buffer = RespirePools.ResponsePayloads.Rent(_receiveBufferSize);
        var parser = new RespParseState(DirectFillThreshold);
        var start = 0;
        var end = 0;
        long responseBytes = 0;
        Exception? fault = null;

        try
        {
            while (true)
            {
                // Drain every complete value currently in the buffer.
                var progressing = true;
                while (progressing && start < end)
                {
                    var bufferedData = buffer.AsSpan(0, end);
                    var previousStart = start;
                    RespParseStatus status;
                    RespValue value;
                    RespDirectFillRequest directFill = default;
                    if (parser.IsIdle)
                    {
                        var hasBulkHeader = RespParser.TryPeekBulkHeader(
                            bufferedData, start, out var bulkType, out var bulkLength, out var headerEnd);
                        if (hasBulkHeader && bulkLength >= DirectFillThreshold)
                        {
                            if (bulkLength > int.MaxValue - 2)
                            {
                                throw new RespireProtocolException(
                                    $"Response exceeds the {MaxResponseSize} byte limit.");
                            }

                            start = headerEnd;
                            value = default;
                            directFill = new RespDirectFillRequest(bulkType, (int)bulkLength);
                            status = RespParseStatus.NeedDirectFill;
                        }
                        else
                        {
                            var pos = start;
                            status = hasBulkHeader
                                ? RespParser.TryParseBulkValue(
                                    bufferedData, ref pos, bulkType, bulkLength, headerEnd, out value)
                                : RespParser.TryParseValue(bufferedData, ref pos, out value);
                            if (status == RespParseStatus.Done)
                            {
                                start = pos;
                            }
                            else if (status == RespParseStatus.NeedMoreData && hasBulkHeader)
                            {
                                start = headerEnd;
                                parser.PrepareBulk(bulkType, (int)bulkLength);
                            }
                            else if (status == RespParseStatus.NeedMoreData)
                            {
                                status = parser.TryParseResumable(
                                    bufferedData, ref start, out value, out directFill);
                            }
                        }
                    }
                    else
                    {
                        status = parser.TryParseResumable(
                            bufferedData, ref start, out value, out directFill);
                    }

                    responseBytes += start - previousStart;
                    if (responseBytes > MaxResponseSize)
                    {
                        throw new RespireProtocolException($"Response exceeds the {MaxResponseSize} byte limit.");
                    }

                    switch (status)
                    {
                        case RespParseStatus.Done:
                            responseBytes = 0;
                            CompleteResponse(in value);
                            break;
                        case RespParseStatus.NeedDirectFill:
                            if (responseBytes + directFill.PayloadLength + 2 > MaxResponseSize)
                            {
                                throw new RespireProtocolException(
                                    $"Response exceeds the {MaxResponseSize} byte limit.");
                            }

                            // Flush before awaiting so already-parsed replies don't wait on
                            // the rest of a large frame.
                            _completions.Flush();
                            var filled = await ReceiveLargeBulkAsync(
                                    buffer, start, end, directFill.Type, directFill.PayloadLength)
                                .ConfigureAwait(false);
                            start = filled.Start;
                            end = filled.End;
                            responseBytes += directFill.PayloadLength + 2L;
                            if (parser.SupplyDirectFill(in filled.Value, out value))
                            {
                                responseBytes = 0;
                                CompleteResponse(in value);
                            }

                            break;
                        case RespParseStatus.InvalidData:
                            throw new RespireProtocolException($"Malformed RESP data from {Host}:{Port} (leading byte 0x{buffer[start]:X2}).");
                        default:
                            progressing = false;
                            break;
                    }
                }

                // Make room, then receive.
                if (start == end)
                {
                    start = 0;
                    end = 0;
                }
                else if (end == buffer.Length)
                {
                    if (start > 0)
                    {
                        Buffer.BlockCopy(buffer, start, buffer, 0, end - start);
                        end -= start;
                        start = 0;
                    }
                    else
                    {
                        // One value larger than the whole buffer (e.g. a big nested array).
                        if (buffer.Length >= MaxResponseSize)
                        {
                            throw new RespireProtocolException($"Response exceeds the {MaxResponseSize} byte limit.");
                        }

                        var bigger = RespirePools.ResponsePayloads.Rent(buffer.Length * 2);
                        buffer.AsSpan(0, end).CopyTo(bigger);
                        RespirePools.ResponsePayloads.Return(buffer);
                        buffer = bigger;
                    }
                }

                _completions.Flush();
                var received = await ReceiveAsync(buffer.AsMemory(end)).ConfigureAwait(false);
                if (received == 0)
                {
                    fault = new RespireConnectionException($"Connection to {Host}:{Port} closed by remote peer.");
                    break;
                }

                ResetReceiveDeadline();
                end += received;
            }
        }
        catch (Exception ex)
        {
            fault = TranslateReceiveFault(ex);
        }
        finally
        {
            parser.Dispose();
            RespirePools.ResponsePayloads.Return(buffer);
            // Replies parsed before the fault still complete normally.
            _completions.Flush();
            Abort();
            FailAllPending(Volatile.Read(ref _abortReason)
                ?? fault
                ?? new RespireConnectionException($"Connection to {Host}:{Port} closed."));
        }
    }

    /// <summary>
    /// Receives a large bulk payload straight into its pooled array — one user-space copy for
    /// the part already buffered, zero for the remainder. Returns the new cursors and value;
    /// the resumable parser decides whether it completes a top-level or nested aggregate.
    /// </summary>
#if NET
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
#endif
    private async ValueTask<(int Start, int End, RespValue Value)> ReceiveLargeBulkAsync(
        byte[] buffer, int start, int end, RespDataType type, int payloadLength)
    {
        var payload = RespirePools.ResponsePayloads.Rent(payloadLength);
        try
        {
            var buffered = Math.Min(payloadLength, end - start);
            buffer.AsSpan(start, buffered).CopyTo(payload);
            start += buffered;
            var filled = buffered;

            while (filled < payloadLength)
            {
                var read = await ReceiveAsync(payload.AsMemory(filled, payloadLength - filled)).ConfigureAwait(false);
                if (read == 0)
                {
                    throw new RespireConnectionException($"Connection to {Host}:{Port} closed mid-frame.");
                }

                ResetReceiveDeadline();
                filled += read;
            }

            // Consume the trailing CRLF through the buffered path.
            while (end - start < 2)
            {
                if (start == end)
                {
                    start = 0;
                    end = 0;
                }
                else if (end == buffer.Length)
                {
                    Buffer.BlockCopy(buffer, start, buffer, 0, end - start);
                    end -= start;
                    start = 0;
                }

                var read = await ReceiveAsync(buffer.AsMemory(end)).ConfigureAwait(false);
                if (read == 0)
                {
                    throw new RespireConnectionException($"Connection to {Host}:{Port} closed mid-frame.");
                }

                ResetReceiveDeadline();
                end += read;
            }

            if (buffer[start] != RespConstants.CarriageReturn || buffer[start + 1] != RespConstants.LineFeed)
            {
                throw new RespireProtocolException($"Bulk payload from {Host}:{Port} not terminated by CRLF.");
            }

            start += 2;

            var value = RespValue.PooledString(type, payload, payloadLength);
            payload = null;
            return (start, end, value);
        }
        finally
        {
            if (payload is not null)
            {
                RespirePools.ResponsePayloads.Return(payload);
            }
        }
    }

    private void CompleteResponse(in RespValue value)
    {
        if (TryRoutePush(in value))
        {
            return;
        }

        if (!_inflight.TryDequeue(out var source))
        {
            value.Dispose();
            throw new RespireProtocolException($"Unsolicited response from {Host}:{Port} with no command in flight.");
        }

        if (_responseTimeout is not null)
        {
            lock (_receiveDeadlineGate)
            {
                _receivedReplyCount++;
                if (_receivedReplyCount >= _sentReplyCount)
                {
                    _receiveDeadlineTimestamp = 0;
                }
            }
        }

        _capacitySignal.Signal();

        if (ReferenceEquals(source, InflightRing.DiscardSentinel))
        {
            value.Dispose();
            return;
        }

        // Deferred: the scheduler runs TrySetResult + ReleaseRef on a pool thread, one work
        // item per receive drain rather than one per reply.
        _completions.Add(source, in value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ValueTask<int> ReceiveAsync(Memory<byte> buffer)
        => _tlsStream is null
            ? _socket.ReceiveAsync(buffer, SocketFlags.None)
            : _tlsStream.ReadAsync(buffer);

    /// <summary>
    /// Routes out-of-band frames to the push handler. RESP3 delivers them as Push frames; on a
    /// RESP2 subscriber connection (one constructed with a push handler) they arrive as plain
    /// arrays. Subscribe-family confirmations are NOT pushes — both protocols deliver them
    /// out of the reply stream, but they answer the pending SUBSCRIBE/UNSUBSCRIBE command, so
    /// they fall through to normal FIFO completion.
    /// </summary>
    private bool TryRoutePush(in RespValue value)
    {
        var isPushFrame = value.Type == RespDataType.Push;
        if (!isPushFrame && (value.Type != RespDataType.Array || _pushHandler is null))
        {
            return false;
        }

        var elements = value.AsArray();
        if (elements.Length > 0)
        {
            var kind = elements[0].AsSpan();
            if (kind.SequenceEqual("message"u8)
                || kind.SequenceEqual("pmessage"u8)
                || kind.SequenceEqual("smessage"u8))
            {
                DeliverPush(in value);
                return true;
            }

            if (kind.SequenceEqual("subscribe"u8)
                || kind.SequenceEqual("unsubscribe"u8)
                || kind.SequenceEqual("psubscribe"u8)
                || kind.SequenceEqual("punsubscribe"u8)
                || kind.SequenceEqual("ssubscribe"u8)
                || kind.SequenceEqual("sunsubscribe"u8))
            {
                return false;
            }
        }

        if (isPushFrame)
        {
            // Other pushes (e.g. client-side caching invalidation) never answer a command.
            DeliverPush(in value);
            return true;
        }

        return false;
    }

    /// <summary>Runs on the receive loop; the value is only valid during the callback.</summary>
    private void DeliverPush(in RespValue value)
    {
        try
        {
            _pushHandler?.Invoke(in value);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Push handler threw for {Host}:{Port}; message dropped", Host, Port);
        }
        finally
        {
            value.Dispose();
        }
    }

    /// <summary>Writes MULTI + a pre-serialized command block + EXEC as one frame sequence.</summary>
    private readonly struct TransactionCommand(ReadOnlyMemory<byte> serializedCommands) : IRespCommand
    {
        public void Write(ref RespWriter writer)
        {
            writer.WriteRaw(RespCommands.Multi);
            writer.WriteRaw(serializedCommands.Span);
            writer.WriteRaw(RespCommands.Exec);
        }
    }

    /// <summary>Writes two RESP commands as one buffer append.</summary>
    private readonly struct PrefixedCommand<TPrefix, TCommand>(TPrefix prefix, TCommand command) : IRespCommand
        where TPrefix : struct, IRespCommand
        where TCommand : struct, IRespCommand
    {
        public void Write(ref RespWriter writer)
        {
            prefix.Write(ref writer);
            command.Write(ref writer);
        }
    }

    private static Exception TranslateReceiveFault(Exception ex)
        => ex switch
        {
            ObjectDisposedException or SocketException { SocketErrorCode: SocketError.OperationAborted } =>
                new RespireConnectionException("Connection closed."),
            RespireException => ex,
            _ => new RespireConnectionException($"Connection failed: {ex.Message}", ex),
        };

    /// <summary>
    /// Marks the connection dead and closes the socket, waking any blocked receive. Idempotent.
    /// The receive loop's exit path fails all in-flight commands — it is the ring's only consumer.
    /// </summary>
    private async Task WatchReceiveAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                if (Volatile.Read(ref _responseTimeoutSuppressions) != 0)
                {
                    await DelayWatchdogAsync(timeout, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                var deadlineStart = Volatile.Read(ref _receiveDeadlineTimestamp);
                if (deadlineStart == 0)
                {
                    await DelayWatchdogAsync(timeout, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                var elapsed = Stopwatch.GetElapsedTime(deadlineStart);
                if (elapsed < timeout)
                {
                    await DelayWatchdogAsync(timeout - elapsed, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                lock (_receiveDeadlineGate)
                {
                    if (deadlineStart != _receiveDeadlineTimestamp
                        || _sentReplyCount <= _receivedReplyCount
                        || Volatile.Read(ref _responseTimeoutSuppressions) != 0)
                    {
                        continue;
                    }

                    Abort(new RespireConnectionException(
                        $"Connection to {Host}:{Port} received no data for {timeout} while responses were pending."));
                }

                return;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal connection teardown.
        }
    }

    private static Task DelayWatchdogAsync(TimeSpan delay, CancellationToken cancellationToken)
        => Task.Delay(delay > MaxWatchdogSleep ? MaxWatchdogSleep : delay, cancellationToken);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ResetReceiveDeadline()
    {
        if (_responseTimeout is null)
        {
            return;
        }

        lock (_receiveDeadlineGate)
        {
            if (_sentReplyCount > _receivedReplyCount)
            {
                _receiveDeadlineTimestamp = Stopwatch.GetTimestamp();
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void MarkRepliesSent(int count)
    {
        if (_responseTimeout is null || count == 0)
        {
            return;
        }

        lock (_receiveDeadlineGate)
        {
            _sentReplyCount += count;
            if (_sentReplyCount > _receivedReplyCount && _receiveDeadlineTimestamp == 0)
            {
                _receiveDeadlineTimestamp = Stopwatch.GetTimestamp();
            }
        }
    }

    private void Abort(Exception? reason = null)
    {
        _watchdogCancellation?.Cancel();
        lock (_writeGate)
        {
            if (_dead)
            {
                return;
            }

            _dead = true;
            _abortReason = reason;
            var writeFailure = reason
                ?? new RespireConnectionException($"Connection to {Host}:{Port} closed before writing completed.");
            _activeBuffer.FailWrite(writeFailure);
        }

        try
        {
            _tlsStream?.Dispose();
        }
        catch
        {
            // Already closed or faulted.
        }

        try
        {
            _socket.Close(0);
        }
        catch
        {
            // Already closed.
        }

        // Wake the parked flush loop so it can observe the dead flag and exit.
        _flushSignal.Signal();
    }

    /// <summary>
    /// Called only from the receive loop's exit path, after <see cref="Abort"/> has set the
    /// dead flag under the write gate — no producer can enqueue afterwards, so draining here
    /// is single-consumer and race-free.
    /// </summary>
    private void FailAllPending(Exception exception)
    {
        var failed = 0;
        while (_inflight.TryDequeue(out var source))
        {
            if (ReferenceEquals(source, InflightRing.DiscardSentinel))
            {
                continue;
            }

            source.TrySetException(exception);
            source.ReleaseRef();
            failed++;
        }

        _capacitySignal.Signal();

        if (failed > 0)
        {
            _logger?.LogDebug("Failed {Count} in-flight commands on {Host}:{Port}: {Reason}", failed, Host, Port, exception.Message);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Abort();
        await _receiveTask.ConfigureAwait(false);
        await _flushTask.ConfigureAwait(false);
        if (_watchdogTask is not null)
        {
            await _watchdogTask.ConfigureAwait(false);
        }

        lock (_writeGate)
        {
            _activeBuffer.Release();
            _spareBuffer.Release();
        }

        _tlsStream?.Dispose();
        _socket.Dispose();
        _watchdogCancellation?.Dispose();
        _logger?.LogDebug("Disconnected from {Host}:{Port}", Host, Port);
    }
}

/// <summary>
/// Receives out-of-band frames (pub/sub messages, RESP3 pushes) on the connection's receive
/// loop. The value is only valid for the duration of the callback — copy what you need; the
/// connection disposes it afterwards. Do not block.
/// </summary>
public delegate void RespirePushHandler(in RespValue value);

/// <summary>Tuning options for a single RESP connection.</summary>
public sealed record RespireConnectionOptions
{
    public static readonly RespireConnectionOptions Default = new();

    /// <summary>
    /// Receives out-of-band frames on connections built from these options (see
    /// <see cref="RespirePushHandler"/>). Set by the client's pub/sub hub.
    /// </summary>
    public RespirePushHandler? PushHandler { get; init; }

    /// <summary>Timeout for the initial TCP connect.</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Aborts the connection when responses are pending and no bytes arrive within this period.
    /// Null disables the watchdog.
    /// </summary>
    public TimeSpan? ResponseTimeout { get; init; }

    /// <summary>Wrap the connected socket in TLS before the RESP handshake.</summary>
    public bool UseTls { get; init; }

    /// <summary>
    /// TLS client settings. When null, the host name is used with platform certificate
    /// validation defaults. Set <see cref="SslClientAuthenticationOptions.TargetHost"/> when
    /// supplying custom settings.
    /// </summary>
    public SslClientAuthenticationOptions? TlsOptions { get; init; }

    /// <summary>ACL username for AUTH/HELLO. Defaults to Redis's "default" user when only a password is set.</summary>
    public string? Username { get; init; }

    /// <summary>Password for AUTH (RESP2) or HELLO AUTH (RESP3). Null skips authentication.</summary>
    public string? Password { get; init; }

    /// <summary>When set, CLIENT SETNAME runs during the handshake.</summary>
    public string? ClientName { get; init; }

    /// <summary>Logical database SELECTed during the handshake; 0 skips the SELECT.</summary>
    public int Database { get; init; }

    /// <summary>Negotiate RESP3 via HELLO 3 during the handshake. Requires Redis 6+.</summary>
    public bool UseResp3 { get; init; }

    /// <summary>Initial size of the pooled parse buffer the receive loop reads into.</summary>
    public int ReceiveBufferSize { get; init; } = 64 * 1024;

    /// <summary>Initial size of each of the two coalescing write buffers.</summary>
    public int WriteBufferSize { get; init; } = 64 * 1024;

    /// <summary>
    /// Enables TCP keepalive and sets how long the connection may sit idle before the kernel
    /// starts probing. Whole seconds, minimum one second. Null (the default) leaves keepalive
    /// off. Recommended for connections idling behind NATs or load balancers that silently
    /// drop stale flows; the receive watchdog only detects dead peers while replies are pending.
    /// </summary>
    public TimeSpan? TcpKeepAliveTime { get; init; }

    /// <summary>
    /// Interval between keepalive probes once <see cref="TcpKeepAliveTime"/> has elapsed
    /// without traffic. Whole seconds, minimum one second. Null keeps the OS default; requires
    /// <see cref="TcpKeepAliveTime"/>.
    /// </summary>
    public TimeSpan? TcpKeepAliveInterval { get; init; }

    /// <summary>
    /// Unanswered keepalive probes before the kernel declares the connection dead. Null keeps
    /// the OS default; requires <see cref="TcpKeepAliveTime"/>.
    /// </summary>
    public int? TcpKeepAliveRetryCount { get; init; }

    /// <summary>
    /// Kernel socket receive buffer size; 0 (the default) keeps the OS default. Setting an
    /// explicit size disables Linux receive-window autotuning, capping single-connection
    /// throughput on high-latency links, so only pin a size when profiling demands it.
    /// </summary>
    public int SocketReceiveBufferSize { get; init; }

    /// <summary>Kernel socket send buffer size; 0 (the default) keeps the OS default.</summary>
    public int SocketSendBufferSize { get; init; }

    /// <summary>Maximum commands awaiting responses on one connection (rounded up to a power of two).</summary>
    public int MaxInflightCommands { get; init; } = 16 * 1024;

    /// <summary>Maximum pooled completion sources kept per connection.</summary>
    public int CompletionSourcePoolSize { get; init; } = 4096;
}
