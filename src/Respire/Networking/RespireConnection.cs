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
    private static readonly TimeSpan MaxWatchdogSleep = TimeSpan.FromDays(1);

    private readonly Socket _socket;
    private readonly SslStream? _tlsStream;
    private readonly object _writeGate = new();
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
    private readonly object _receiveDeadlineGate = new();
    private readonly AsyncFlushSignal _flushSignal = new();
    private readonly AsyncCapacitySignal _capacitySignal = new();

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
        _receiveTask = Task.Run(ReceiveLoopAsync);
        _flushTask = Task.Run(FlushLoopAsync);
        if (_responseTimeout is { } responseTimeout)
        {
            _watchdogCancellation = new CancellationTokenSource();
            _watchdogTask = WatchReceiveAsync(responseTimeout, _watchdogCancellation.Token);
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
        if (options.ResponseTimeout is { } invalidTimeout && invalidTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "ResponseTimeout must be positive.");
        }

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
                if (failure is null && reply.IsError)
                {
                    failure = CreateHandshakeException(in reply, step);
                }

                reply.Dispose();
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
            throw new RespireServerException(message);
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

    /// <summary>Sends a command and translates a RESP error reply when its result is consumed.</summary>
    internal ValueTask<RespValue> SendCheckedAsync<TCommand>(
        in TCommand command, CancellationToken cancellationToken = default)
        where TCommand : struct, IRespCommand
        => SendCoreAsync(in command, discardRepliesBefore: 0, throwOnError: true, cancellationToken);

    /// <summary>
    /// Sends a command through a typed in-flight source, avoiding intermediate async state
    /// machines. Conversion occurs when caller consumes result, never on receive loop.
    /// </summary>
    internal ValueTask<TResult> SendConvertedAsync<TCommand, TState, TResult>(
        in TCommand command,
        TState state,
        ResponseConverter<TState, TResult> converter,
        bool transferOwnership,
        CancellationToken cancellationToken = default)
        where TCommand : struct, IRespCommand
    {
        var source = ConvertedPendingResponseSource<TState, TResult>.Rent(state, converter, transferOwnership);
        bool enqueued;
        try
        {
            enqueued = TryEnqueue(in command, source);
        }
        catch
        {
            ReclaimUnpublished(source);
            throw;
        }

        if (enqueued)
        {
            source.RegisterCancellation(cancellationToken);
            ScheduleFlush();
            return source.Task;
        }

        return SendConvertedSlowAsync(command, source, cancellationToken);
    }

    /// <summary>
    /// Sends MULTI + pre-serialized commands + EXEC as one atomic append, so no other
    /// multiplexed command can interleave into the server-side transaction state. MULTI's +OK
    /// and each +QUEUED reply are consumed and discarded; the returned task completes with
    /// EXEC's reply — an array of per-command results, or an error (e.g. EXECABORT when a
    /// command failed to queue).
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

        return SendCoreAsync(
            new TransactionCommand(serializedCommands), discardRepliesBefore: commandCount + 1,
            throwOnError: false, cancellationToken);
    }

    private ValueTask<RespValue> SendCoreAsync<TCommand>(
        in TCommand command, int discardRepliesBefore, bool throwOnError, CancellationToken cancellationToken)
        where TCommand : struct, IRespCommand
    {
        var source = _sourcePool.Rent(throwOnError);
        bool enqueued;
        try
        {
            enqueued = TryEnqueue(in command, source, discardRepliesBefore);
        }
        catch
        {
            ReclaimUnpublished(source);
            throw;
        }

        if (enqueued)
        {
            source.RegisterCancellation(cancellationToken);
            ScheduleFlush();
            return source.Task;
        }

        return SendSlowAsync(command, source, discardRepliesBefore, cancellationToken);
    }

    /// <summary>
    /// Sends a command whose response is read from the wire but discarded. Completes once the
    /// command is queued for sending.
    /// </summary>
    public ValueTask SendFireAndForgetAsync<TCommand>(in TCommand command, CancellationToken cancellationToken = default)
        where TCommand : struct, IRespCommand
    {
        if (TryEnqueue(in command, InflightRing.DiscardSentinel))
        {
            ScheduleFlush();
            return ValueTask.CompletedTask;
        }

        return SendFireAndForgetSlowAsync(command, cancellationToken);
    }

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
    /// A transaction reserves <paramref name="discardRepliesBefore"/> discard slots ahead of
    /// its real source, one per reply that is consumed but not delivered.
    /// Serialization runs outside the write gate into a per-thread scratch buffer, so
    /// concurrent callers contend only for a memcpy and the ring enqueue — not for UTF-8
    /// encoding their payloads.
    /// </summary>
    private bool TryEnqueue<TCommand>(in TCommand command, PendingResponse source, int discardRepliesBefore = 0)
        where TCommand : struct, IRespCommand
    {
        // Racy pre-check; the authoritative one runs under the gate below. This keeps the
        // ring-full retry loop from re-serializing the frame on every attempt.
        if (_inflight.Capacity - _inflight.Count < discardRepliesBefore + 1)
        {
            return false;
        }

        if (_directPathBudget > 0)
        {
            _directPathBudget--;
            return TryEnqueueDirect(in command, source, discardRepliesBefore);
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

                _activeBuffer.Append(frame);
                if (_responseTimeout is not null)
                {
                    _activeReplyCount += discardRepliesBefore + 1;
                }

                for (var i = 0; i < discardRepliesBefore; i++)
                {
                    _inflight.TryEnqueue(InflightRing.DiscardSentinel);
                }

                _inflight.TryEnqueue(source);
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
    private bool TryEnqueueDirect<TCommand>(in TCommand command, PendingResponse source, int discardRepliesBefore)
        where TCommand : struct, IRespCommand
    {
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
                _inflight.TryEnqueue(InflightRing.DiscardSentinel);
            }

            _inflight.TryEnqueue(source);
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
        await WaitForInflightCapacityAsync(command, source, discardRepliesBefore, cancellationToken).ConfigureAwait(false);
        source.RegisterCancellation(cancellationToken);
        ScheduleFlush();
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
        await WaitForInflightCapacityAsync(command, source, 0, cancellationToken).ConfigureAwait(false);
        source.RegisterCancellation(cancellationToken);
        ScheduleFlush();
        return await source.Task.ConfigureAwait(false);
    }

#if NET
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
#endif
    private async ValueTask SendFireAndForgetSlowAsync<TCommand>(TCommand command, CancellationToken cancellationToken)
        where TCommand : struct, IRespCommand
    {
        await WaitForInflightCapacityAsync(command, InflightRing.DiscardSentinel, 0, cancellationToken).ConfigureAwait(false);
        ScheduleFlush();
    }

    /// <summary>In-flight ring full: flush, then park until the receive loop frees capacity.</summary>
    private async ValueTask WaitForInflightCapacityAsync<TCommand>(
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
                if (TryEnqueue(in command, source, discardRepliesBefore))
                {
                    return;
                }

                ScheduleFlush();
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

        source.ReleaseRef();
        source.ReleaseRef();
    }

    private void ScheduleFlush() => _flushSignal.Signal();

    /// <summary>
    /// The connection's single persistent sender. Parks on <see cref="_flushSignal"/> between
    /// batches instead of spawning a Task per flush, then drains whatever has coalesced into
    /// the active buffer — many pipelined commands per syscall.
    /// </summary>
    private async Task FlushLoopAsync()
    {
        try
        {
            while (true)
            {
                await _flushSignal.WaitAsync().ConfigureAwait(false);

                while (true)
                {
                    WriteBuffer sending;
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
                            var sent = await _socket.SendAsync(memory, SocketFlags.None).ConfigureAwait(false);
                            memory = memory[sent..];
                        }
                    }
                    else
                    {
                        await _tlsStream.WriteAsync(memory).ConfigureAwait(false);
                    }

                    MarkRepliesSent(sendingReplyCount);

                    sending.Reset();
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Send failed for {Host}:{Port}; aborting connection", Host, Port);
            Abort();
        }
    }

    private async Task ReceiveLoopAsync()
    {
        var buffer = RespirePools.ResponsePayloads.Rent(_receiveBufferSize);
        var start = 0;
        var end = 0;
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
                    var hasBulkHeader = RespParser.TryPeekBulkHeader(
                        bufferedData, start, out var bulkType, out var bulkLength, out var headerEnd);
                    if (hasBulkHeader && bulkLength >= DirectFillThreshold)
                    {
                        if (bulkLength > MaxResponseSize)
                        {
                            throw new RespireProtocolException($"Response of {bulkLength} bytes exceeds the {MaxResponseSize} byte limit.");
                        }

                        (start, end) = await ReceiveLargeBulkAsync(buffer, headerEnd, end, bulkType, (int)bulkLength).ConfigureAwait(false);
                        continue;
                    }

                    var pos = start;
                    var status = hasBulkHeader
                        ? RespParser.TryParseBulkValue(bufferedData, ref pos, bulkType, bulkLength, headerEnd, out var value)
                        : RespParser.TryParseValue(bufferedData, ref pos, out value);
                    switch (status)
                    {
                        case RespParseStatus.Done:
                            start = pos;
                            CompleteResponse(in value);
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
            RespirePools.ResponsePayloads.Return(buffer);
            Abort();
            FailAllPending(Volatile.Read(ref _abortReason)
                ?? fault
                ?? new RespireConnectionException($"Connection to {Host}:{Port} closed."));
        }
    }

    /// <summary>
    /// Receives a large bulk payload straight into its pooled array — one user-space copy for
    /// the part already buffered, zero for the remainder. Returns the new (start, end) cursors.
    /// </summary>
#if NET
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
#endif
    private async ValueTask<(int Start, int End)> ReceiveLargeBulkAsync(
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
            CompleteResponse(in value);
            return (start, end);
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

        if (!source.TrySetResult(in value))
        {
            // Caller already cancelled; the response still had to be consumed from the wire.
            value.Dispose();
        }

        source.ReleaseRef();
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
        lock (_writeGate)
        {
            if (_dead)
            {
                return;
            }

            _dead = true;
            _abortReason = reason;
        }

        _watchdogCancellation?.Cancel();
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

    /// <summary>Kernel socket receive buffer size; 0 keeps the OS default.</summary>
    public int SocketReceiveBufferSize { get; init; } = 64 * 1024;

    /// <summary>Kernel socket send buffer size; 0 keeps the OS default.</summary>
    public int SocketSendBufferSize { get; init; } = 64 * 1024;

    /// <summary>Maximum commands awaiting responses on one connection (rounded up to a power of two).</summary>
    public int MaxInflightCommands { get; init; } = 16 * 1024;

    /// <summary>Maximum pooled completion sources kept per connection.</summary>
    public int CompletionSourcePoolSize { get; init; } = 4096;
}
