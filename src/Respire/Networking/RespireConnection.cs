using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipelines;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using Microsoft.Extensions.Logging;
using Respire.Commands;
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
internal sealed partial class RespireConnection : IAsyncDisposable
{
    private const int DirectFillThreshold = 4 * 1024;
    private const int MaxResponseSize = 512 * 1024 * 1024;
    private const long StreamTimeoutTimerSliceMilliseconds = 30L * 24 * 60 * 60 * 1000;
    private static readonly TimeSpan MinWatchdogDelay = TimeSpan.FromMilliseconds(1);
    private static readonly TimeSpan MaxWatchdogSleep = TimeSpan.FromDays(1);

    private sealed class StreamDeadlineCancellation : IDisposable
    {
        private readonly long _deadline;
        private readonly CancellationTokenSource _source = new();
        private readonly Timer _timer;
        private int _disposed;

        internal StreamDeadlineCancellation(long deadline)
        {
            _deadline = deadline;
            _timer = new Timer(static state => ((StreamDeadlineCancellation)state!).Schedule(),
                this, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }

        internal CancellationToken Token => _source.Token;

        internal void Start() => Schedule();

        private void Schedule()
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            var remaining = _deadline - Environment.TickCount64;
            if (remaining <= 0)
            {
                try { _source.Cancel(); }
                catch (ObjectDisposedException) { }
                catch (AggregateException) { }
                return;
            }

            var delay = TimeSpan.FromMilliseconds(Math.Min(remaining, StreamTimeoutTimerSliceMilliseconds));
            try { _timer.Change(delay, Timeout.InfiniteTimeSpan); }
            catch (ObjectDisposedException) { }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _timer.Dispose();
            _source.Dispose();
        }
    }

    private readonly Socket? _socket;
    private readonly Stream? _stream;
    private readonly Lock _writeGate = new();
    private readonly SemaphoreSlim _streamingGate = new(1, 1);
    // Cancelled by Abort so a streamed SET blocked on its source or on a stalled socket write
    // observes the closed connection. Never disposed: a racing streamed SET may still link to it.
    private readonly CancellationTokenSource _closedCancellation = new();
    private readonly TaskCompletionSource _retiredSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly InflightRing _inflight;
    private readonly PendingResponsePool _sourcePool;
    private readonly int _receiveBufferSize;
    private readonly string? _networkPeerAddress;
    private readonly int? _networkPeerPort;
    private readonly ILogger? _logger;
    private readonly RespirePushHandler? _pushHandler;
    private readonly RespirePushFilter? _subscriptionPushFilter;
    private readonly RespirePushHandler? _subscriptionConfirmationHandler;
    private readonly Task _receiveTask;
    private readonly Task _flushTask;
    private readonly Task? _watchdogTask;
    private readonly Task? _deadlineSweepTask;
    private readonly CancellationTokenSource? _watchdogCancellation;
    private readonly TimeSpan? _responseTimeout;
    private readonly TimeSpan? _commandTimeout;
    private readonly long _commandTimeoutMilliseconds;
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
    private bool _retired;
    private bool _sending;
    private bool _streamingActive;
    private TaskCompletionSource? _retirementCompletion;
    private TaskCompletionSource? _disposeCompletion;
    private bool _drainedSuccessfully;
    private long _serverClientId;
    private static long _nextDiagnosticId;
    private readonly long _diagnosticId = Interlocked.Increment(ref _nextDiagnosticId);
    private long _enqueuedBytes;
    private long _sentBytes;
    private long _lastReadTimestamp;
    private long _lastWriteTimestamp;
    private long _sentReplyCount;
    private long _receivedReplyCount;
    private long _receiveDeadlineTimestamp;
    private int _responseTimeoutSuppressions;
    private Exception? _abortReason;
    private readonly IConnectionGeneration? _generation;
    private BulkStreamPendingResponseSource? _activeBulkStreamSource;

    // Set by the multiplexer before publication; endpoint aliases may later change owners.
    private Respire.Infrastructure.RespireConnectionMultiplexer? _multiplexer;
    internal Respire.Infrastructure.RespireConnectionMultiplexer? Multiplexer
    {
        get => Volatile.Read(ref _multiplexer);
        set => Volatile.Write(ref _multiplexer, value);
    }
    internal int MultiplexerSlot { get; set; }
    internal long MovingPublicationGeneration;
    internal long LastQueuedMovingSequence = long.MinValue;

    public string Host { get; }
    public int Port { get; }
    public bool IsConnected => !Volatile.Read(ref _dead);
    internal bool IsAcceptingCommands => IsConnected && !Volatile.Read(ref _retired) && _generation?.IsRetired != true;
    internal int WriteBufferCapacity => Math.Max(_activeBuffer.Capacity, _spareBuffer.Capacity);
    internal bool DrainedSuccessfully => Volatile.Read(ref _drainedSuccessfully);

    /// <summary>
    /// Picks the socket that takes a send this socket rejected before admission because a MOVING
    /// handoff retired it, and the deadline the send carries there. Returns false, so the
    /// retirement surfaces to the caller, when the send is pinned to this socket, when a retired
    /// Sentinel generation or multiplexer routes the command instead, or when the multiplexer has
    /// no other unretired socket to offer.
    /// </summary>
    /// <remarks>
    /// <para>Only sends that were never admitted get here. <see cref="RespireConnectionRetiredException"/>
    /// is thrown only by the pre-admission check, and every caller catches it around enqueue and
    /// capacity waits only, never around an admitted command's reply, so nothing is sent twice.</para>
    /// <para>Pinned sends are connection-scoped: CLIENT ID, CLIENT KILL probes, FIFO ordering
    /// barriers and credential renewal AUTH mean nothing on another socket.</para>
    /// <para>Each hop goes to a different socket that was not retired when it was selected. A
    /// further hop therefore needs another handoff to retire that socket before the send is
    /// admitted, so the chain is bounded by handoff publications and cannot loop.</para>
    /// </remarks>
    private bool TryReroute(bool pinToConnection, CommandDeadline deadline, out RespireConnection target,
        out CommandDeadline reroutedDeadline)
    {
        target = null!;
        reroutedDeadline = default;
        if (pinToConnection || _generation?.IsRetired == true || Multiplexer is not { IsRetired: false } multiplexer)
            return false;
        var selected = multiplexer.GetConnection();
        if (ReferenceEquals(selected, this) || Volatile.Read(ref selected._retired)) return false;
        target = selected;
        reroutedDeadline = GetReroutedCommandDeadline(deadline);
        return true;
    }

    /// <summary>The physical peer, which MOVING sequence IDs belong to.</summary>
    internal (string Host, int Port) PeerKey => (NetworkPeerAddress ?? Host, NetworkPeerPort ?? Port);
    internal string? NetworkPeerAddress => _networkPeerAddress;
    internal int? NetworkPeerPort => _networkPeerPort;

    /// <summary>
    /// Completes when the connection dies for any reason (fault, remote close, disposal). Never
    /// faults itself — used to observe connection lifetime (e.g. pub/sub auto-resubscribe).
    /// </summary>
    internal Task Closed => _receiveTask;
    internal Exception? CloseError => Volatile.Read(ref _abortReason);

    /// <summary>
    /// Raised after the connection is marked dead but before in-flight callers are failed.
    /// Multiplexers use this ordering point to publish continuity loss first.
    /// </summary>
    internal event Action? PendingCommandsFailing;

    /// <summary>
    /// Redis's connection ID, populated only when reliable cross-connection correction ordering
    /// is enabled. Zero means it has not been requested.
    /// </summary>
    internal long ServerClientId => Volatile.Read(ref _serverClientId);

    private RespireConnection(
        Socket? socket, Stream? stream, string host, int port, RespireConnectionOptions options, ILogger? logger)
    {
        _socket = socket;
        _stream = stream;
        if (socket?.RemoteEndPoint is IPEndPoint remoteEndpoint)
        {
            var address = remoteEndpoint.Address;
            _networkPeerAddress = (address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).ToString();
            _networkPeerPort = remoteEndpoint.Port;
        }

        Host = host;
        Port = port;
        _logger = logger;
        _generation = options.Generation;
        _pushHandler = options.PushHandler;
        _subscriptionPushFilter = options.SubscriptionPushFilter;
        _subscriptionConfirmationHandler = options.SubscriptionConfirmationHandler;
        _maintenanceOptions = options.MaintenanceNotifications == RespireMaintenanceNotificationMode.Disabled ? null : options;
        _receiveBufferSize = options.ReceiveBufferSize;
        _inflight = new InflightRing(options.MaxInflightCommands);
        _sourcePool = new PendingResponsePool(options.CompletionSourcePoolSize);
        _activeBuffer = new WriteBuffer(options.WriteBufferSize);
        _spareBuffer = new WriteBuffer(options.WriteBufferSize);
        _responseTimeout = options.ResponseTimeout;
        _commandTimeout = options.CommandTimeout;
        if (_commandTimeout is { } commandTimeoutValue)
        {
            _commandTimeoutMilliseconds = Math.Max(1L, (long)commandTimeoutValue.TotalMilliseconds);
        }

        if (_responseTimeout is not null || _commandTimeout is not null)
        {
            _watchdogCancellation = new CancellationTokenSource();
        }

        _receiveTask = Task.Run(ReceiveLoopAsync);
        _flushTask = Task.Run(FlushLoopAsync);
        if (_responseTimeout is { } responseTimeout)
        {
            _watchdogTask = WatchReceiveAsync(responseTimeout, _watchdogCancellation!.Token);
        }

        if (_commandTimeout is { } commandTimeout)
        {
            _deadlineSweepTask = SweepCommandDeadlinesAsync(commandTimeout, _watchdogCancellation!.Token);
        }
    }

    public static async Task<RespireConnection> ConnectAsync(
        string host,
        int port,
        RespireConnectionOptions? options = null,
        ILogger? logger = null,
        CancellationToken cancellationToken = default,
        bool armHandshakeDeadline = true)
    {
        options ??= RespireConnectionOptions.Default;
        if (options.ResponseTimeout is { } invalidTimeout && invalidTimeout < MinWatchdogDelay)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options), "ResponseTimeout must be at least one millisecond.");
        }

        if (options.CommandTimeout is { } invalidCommandTimeout && invalidCommandTimeout < MinWatchdogDelay)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options), "CommandTimeout must be at least one millisecond.");
        }

        ValidateTcpKeepAlive(options);

        options = await ResolveCredentialsAsync(host, port, options, cancellationToken).ConfigureAwait(false);

        if (options.TestingStreamFactory is not null)
            return await ConnectTestingStreamAsync(host, port, options, logger, cancellationToken, armHandshakeDeadline).ConfigureAwait(false);

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

            using var timeoutCts = CommandTimeoutCancellation.Create(
                cancellationToken,
                options.ConnectTimeout);
            try
            {
                await socket.ConnectAsync(host, port, timeoutCts.Token).ConfigureAwait(false);
                timeoutCts.Token.ThrowIfCancellationRequested();

                if (options.UseTls)
                {
                    try
                    {
                        tlsStream = new SslStream(new NetworkStream(socket, ownsSocket: false));
                    }
                    catch (IOException error) when (timeoutCts.IsCancellationRequested)
                    {
                        // TCP cancellation can close the socket after connect completes but before
                        // NetworkStream takes it, especially on .NET 8. Keep our cancellation identity.
                        throw new OperationCanceledException(error.Message, error, timeoutCts.Token);
                    }
                    var tlsOptions = CreateTlsOptions(options.TlsOptions, host);
                    await tlsStream.AuthenticateAsClientAsync(tlsOptions, timeoutCts.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException error) when (CommandTimeoutCancellation.IsFromLinkedToken(
                error, cancellationToken, timeoutCts.Token))
            {
                // Preserve the initiating token across our private connect-timeout link.
                // An independent connect timeout or unrelated cancellation keeps its own token.
                throw new OperationCanceledException(error.Message, error, cancellationToken);
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
            await connection.HandshakeAsync(options, cancellationToken, armHandshakeDeadline).ConfigureAwait(false);
            if (options.Generation is { } generation)
                await generation.ValidateAsync(connection, cancellationToken).ConfigureAwait(false);
            connection.StartCredentialRefresh(options);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return connection;
    }

    private static async Task<RespireConnection> ConnectTestingStreamAsync(
        string host, int port, RespireConnectionOptions options, ILogger? logger,
        CancellationToken cancellationToken, bool armHandshakeDeadline)
    {
        if (options.UseTls) throw new NotSupportedException("In-memory testing connections do not support TLS.");
        using var timeout = CommandTimeoutCancellation.Create(cancellationToken, options.ConnectTimeout);
        Stream? stream = null;
        try
        {
            try
            {
                stream = await options.TestingStreamFactory!(host, port, timeout.Token).ConfigureAwait(false);
                // A factory can return after cancellation instead of observing its token.
                timeout.Token.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException error) when (CommandTimeoutCancellation.IsFromLinkedToken(error, cancellationToken, timeout.Token))
            {
                throw new OperationCanceledException(error.Message, error, cancellationToken);
            }
        }
        catch
        {
            stream?.Dispose();
            throw;
        }
        var connection = new RespireConnection(null, stream, host, port, options, logger);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await connection.HandshakeAsync(options, cancellationToken, armHandshakeDeadline).ConfigureAwait(false);
            if (options.Generation is { } generation)
                await generation.ValidateAsync(connection, cancellationToken).ConfigureAwait(false);
            connection.StartCredentialRefresh(options);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    internal static SslClientAuthenticationOptions CreateTlsOptions(
        SslClientAuthenticationOptions? configured,
        string host,
        bool overrideTargetHost = false)
    {
        if (configured is null)
        {
            return new SslClientAuthenticationOptions { TargetHost = host };
        }

        if (!overrideTargetHost && !string.IsNullOrWhiteSpace(configured.TargetHost))
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

    private async ValueTask<RespProtocol> NegotiatePreferredProtocolAsync(RespireConnectionOptions options,
        CancellationToken cancellationToken, bool armCommandDeadline)
    {
        using var hello = await SendAsync(new Commands.HelloCommand(options.Username, options.Password),
            cancellationToken, armCommandDeadline: armCommandDeadline).ConfigureAwait(false);
        if (hello.IsError)
        {
            var message = hello.GetErrorMessage();
            var kind = ClassifyHelloError(message.AsSpan());
            if (kind != HelloErrorKind.Unsupported)
            {
                // Provider-backed HELLO includes a secret; server text may echo it.
                if (options.CredentialProvider is not null)
                    throw new RespireAuthenticationException($"HELLO authentication failed for {Host}:{Port}.");
                var hint = kind == HelloErrorKind.Other && message.StartsWith("ERR ", StringComparison.OrdinalIgnoreCase)
                    ? " If this endpoint does not support HELLO, explicitly set protocol=2."
                    : null;
                throw CreateHandshakeException(in hello, "HELLO", hint);
            }
            _logger?.LogInformation("HELLO 3 is unsupported by {Host}:{Port}; using RESP2 on this connection", Host, Port);
            return RespProtocol.Resp2;
        }
        ValidateHelloProtocol(in hello);
        _logger?.LogDebug("Negotiated RESP3 with {Host}:{Port}", Host, Port);
        return RespProtocol.Resp3;
    }

    /// <summary>
    /// Runs HELLO/AUTH/CLIENT SETNAME through the normal send path before the connection is
    /// handed out, so every later command runs on an authenticated, protocol-negotiated stream.
    /// </summary>
    private async Task HandshakeAsync(RespireConnectionOptions options, CancellationToken cancellationToken,
        bool armCommandDeadline)
    {
        // Automatic negotiation must finish before setup commands: an unsupported HELLO
        // may require RESP2 AUTH before SELECT, SETNAME, or capability discovery can succeed.
        var requestedProtocol = options.Protocol == RespProtocol.Auto && options.EnableClientTracking
            ? RespProtocol.Resp3 : options.Protocol;
        var negotiatedProtocol = requestedProtocol == RespProtocol.Auto
            ? await NegotiatePreferredProtocolAsync(options, cancellationToken, armCommandDeadline).ConfigureAwait(false)
            : requestedProtocol;

        List<(string Step, ValueTask<RespValue> Reply)>? pending = null;
        // Auto already sent HELLO above; only explicitly requested RESP3 enters this branch.
        if (requestedProtocol == RespProtocol.Resp3)
        {
            (pending ??= new(3)).Add(("HELLO", SendAsync(
                new Commands.HelloCommand(options.Username, options.Password), cancellationToken, armCommandDeadline: armCommandDeadline)));
        }
        else if (negotiatedProtocol == RespProtocol.Resp2 && options.Password is not null)
        {
            (pending ??= new(3)).Add(("AUTH", SendAsync(
                new Commands.AuthCommand(options.Username, options.Password), cancellationToken, armCommandDeadline: armCommandDeadline)));
        }

        if (options.ClientName is not null)
        {
            (pending ??= new(3)).Add(("CLIENT SETNAME", SendAsync(
                new Commands.ClientSetNameCommand(options.ClientName), cancellationToken, armCommandDeadline: armCommandDeadline)));
        }

        if (options.RequireClusterDatabaseSupport)
        {
            // HELLO reports a Redis compatibility version on Valkey. INFO identifies the
            // actual implementation/version, independently for every new physical socket.
            (pending ??= new(3)).Add(("INFO SERVER", SendAsync(
                new Commands.Cmd1(Commands.Verbs.Info, "SERVER"), cancellationToken, armCommandDeadline: armCommandDeadline)));
        }
        else if (options.Database != 0)
        {
            (pending ??= new(3)).Add(("SELECT", SendAsync(
                new Commands.SelectCommand(options.Database), cancellationToken, armCommandDeadline: armCommandDeadline)));
        }

        if (options.EnableClientTracking && !options.RequireClusterDatabaseSupport)
        {
            (pending ??= new(4)).Add(("CLIENT TRACKING", SendAsync(
                new Commands.ClientTrackingCommand(options.ClientTrackingOptions), cancellationToken, armCommandDeadline: armCommandDeadline)));
        }

        if (pending is null)
        {
            await NegotiateMaintenanceAsync(options, negotiatedProtocol, cancellationToken, armCommandDeadline).ConfigureAwait(false);
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
                        // Provider credentials may be echoed by arbitrary proxy/server error codes.
                        failure = options.CredentialProvider is not null && step is ("AUTH" or "HELLO")
                            ? new RespireAuthenticationException($"Credential authentication failed for {Host}:{Port}.")
                            : CreateHandshakeException(in reply, step);
                    }
                    else if (failure is null && step == "HELLO")
                    {
                        ValidateHelloProtocol(in reply);
                    }
                    else if (failure is null && step == "INFO SERVER")
                    {
                        ValidateClusterDatabaseSupport(in reply);
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

        if (options.RequireClusterDatabaseSupport)
        {
            // Do not select a database or publish this connection until capability validation
            // succeeds. SELECT also verifies the configured database range and ACL permission.
            // Tracking must follow SELECT so its initial registration uses the selected database.
            await CompleteHandshakeStepAsync("SELECT", new Commands.SelectCommand(options.Database),
                cancellationToken, armCommandDeadline).ConfigureAwait(false);
            if (options.EnableClientTracking)
            {
                await CompleteHandshakeStepAsync("CLIENT TRACKING", new Commands.ClientTrackingCommand(options.ClientTrackingOptions),
                    cancellationToken, armCommandDeadline).ConfigureAwait(false);
            }
        }
        await NegotiateMaintenanceAsync(options, negotiatedProtocol, cancellationToken, armCommandDeadline).ConfigureAwait(false);
    }

    private async ValueTask CompleteHandshakeStepAsync<TCommand>(string step, TCommand command,
        CancellationToken cancellationToken, bool armCommandDeadline) where TCommand : struct, IRespCommand
    {
        using var reply = await SendAsync(command, cancellationToken, armCommandDeadline: armCommandDeadline)
            .ConfigureAwait(false);
        if (reply.IsError) throw CreateHandshakeException(in reply, step);
    }

    private void ValidateClusterDatabaseSupport(in RespValue reply)
    {
        string? server = null, version = null, mode = null;
        var remaining = reply.AsString().AsSpan();
        while (!remaining.IsEmpty)
        {
            var end = remaining.IndexOf('\n');
            var line = (end < 0 ? remaining : remaining[..end]).Trim();
            remaining = end < 0 ? default : remaining[(end + 1)..];
            if (line.StartsWith("server_name:", StringComparison.Ordinal)) server = line[12..].ToString();
            else if (line.StartsWith("valkey_version:", StringComparison.Ordinal)) version = line[15..].ToString();
            else if (line.StartsWith("server_mode:", StringComparison.Ordinal)) mode = line[12..].ToString();
        }

        // Valkey uses an unsigned major followed by a dotted version or prerelease suffix.
        var majorText = version.AsSpan();
        var separator = majorText.IndexOfAny('.', '-');
        if (separator >= 0) majorText = majorText[..separator];
        if (string.Equals(server, "valkey", StringComparison.OrdinalIgnoreCase)
            && string.Equals(mode, "cluster", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(majorText, NumberStyles.None, CultureInfo.InvariantCulture, out var major) && major >= 9)
        {
            return;
        }

        throw new RespireConfigurationException(
            $"Redis Cluster supports database 0 only. Non-zero Cluster databases require Valkey 9+; " +
            $"INFO SERVER from {Host}:{Port} did not confirm a compatible Valkey cluster " +
            $"(server_name={server ?? "<missing>"}, valkey_version={version ?? "<missing>"}, server_mode={mode ?? "<missing>"}).");
    }

    private RespireConnectionException CreateHandshakeException(in RespValue reply, string step, string? hint = null)
        => new($"{step} failed for {Host}:{Port}: {reply.GetErrorMessage()}{hint}", ResponseReader.ServerError(in reply, step));

    private enum HelloErrorKind { Unsupported, Authentication, Other }
    private static readonly string[] HelloCredentialWording = ["auth", "password", "credential", "permission", "ACL"];

    private static HelloErrorKind ClassifyHelloError(ReadOnlySpan<char> message)
    {
        if (message.Equals("NOPROTO", StringComparison.OrdinalIgnoreCase)
            || message.StartsWith("NOPROTO ", StringComparison.OrdinalIgnoreCase)
            || message.StartsWith("ERR unknown command \"HELLO\"", StringComparison.OrdinalIgnoreCase)
            || message.StartsWith("ERR unknown command 'HELLO'", StringComparison.OrdinalIgnoreCase)
            || message.StartsWith("ERR unknown command `HELLO`", StringComparison.OrdinalIgnoreCase))
            return HelloErrorKind.Unsupported;

        // ERR has no structured subcode. Suppress compatibility advice conservatively
        // for credential/ACL wording; these failures must never suggest a downgrade.
        if (message.StartsWith("WRONGPASS", StringComparison.OrdinalIgnoreCase)
            || message.StartsWith("NOPERM", StringComparison.OrdinalIgnoreCase))
            return HelloErrorKind.Authentication;
        foreach (var wording in HelloCredentialWording)
            if (message.Contains(wording, StringComparison.OrdinalIgnoreCase)) return HelloErrorKind.Authentication;
        return HelloErrorKind.Other;
    }

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

        var message = $"HELLO 3 failed for {Host}:{Port}: server did not confirm RESP3 (expected map field 'proto' = 3).";
        // Preserve the connection exception contract while exposing the permanent protocol
        // failure to acquisition retry classification.
        throw new RespireConnectionException(message, new RespireProtocolException(message));
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

        // Identity setup is bounded by its callers' own timeout tokens (relabeled as
        // "CLIENT ID / CLIENT KILL"), not the per-command deadline.
        var reply = await SendCoreAsync(
                new Commands.ClientIdCommand(), discardRepliesBefore: 0, throwOnError: false,
                cancellationToken, commandName: "CLIENT ID", armCommandDeadline: false,
                pinToConnection: true) // The ID belongs to this socket.
            .ConfigureAwait(false);
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
    /// completes with its response. With <paramref name="pinToConnection"/>, a send rejected
    /// because a MOVING handoff retired this socket surfaces that retirement instead of moving
    /// to another socket.
    /// </summary>
    public ValueTask<RespValue> SendAsync<TCommand>(
        in TCommand command,
        CancellationToken cancellationToken = default,
        bool armCommandDeadline = true,
        string? commandName = null,
        bool pinToConnection = false)
        where TCommand : struct, IRespCommand
        => SendCoreAsync(
            in command, discardRepliesBefore: 0, throwOnError: false, cancellationToken,
            commandName, armCommandDeadline, pinToConnection: pinToConnection);

    /// <summary>Sends an intentionally blocking command without applying the receive watchdog
    /// or the command deadline (a BLPOP-style wait may legitimately outlast both).</summary>
    internal async ValueTask<RespValue> SendWithoutResponseTimeoutAsync<TCommand>(
        TCommand command, CancellationToken cancellationToken = default)
        where TCommand : struct, IRespCommand
    {
        Interlocked.Increment(ref _responseTimeoutSuppressions);
        try
        {
            return await SendCoreAsync(
                    in command, discardRepliesBefore: 0, throwOnError: false, cancellationToken,
                    commandName: null, armCommandDeadline: false)
                .ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref _responseTimeoutSuppressions);
        }
    }

    /// <summary>Sends a prefixed blocking command without applying the receive watchdog
    /// or the command deadline (a BLPOP-style wait may legitimately outlast both).</summary>
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
                    in prefix, in command, throwOnError, cancellationToken,
                    commandName: null, armCommandDeadline: false)
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
        string? commandName = null,
        CommandDeadline commandDeadline = default)
        where TCommand : struct, IRespCommand
    {
        if (!commandDeadline.IsSet) commandDeadline = CommandDeadline.After(_commandTimeoutMilliseconds);
        var source = ConvertedPendingResponseSource<TState, TResult>.Rent(
            state, converter, transferOwnership, commandName);
        bool enqueued;
        bool startedBatch;
        try
        {
            enqueued = TryEnqueue(in command, source, out startedBatch);
        }
        catch (RespireConnectionRetiredException) when (TryReroute(pinToConnection: false, commandDeadline, out var target, out var rerouted))
        {
            ReclaimUnpublished(source);
            return target.SendConvertedAsync(in command, state, converter,
                transferOwnership, cancellationToken, commandName, rerouted);
        }
        catch
        {
            ReclaimUnpublished(source);
            throw;
        }

        if (enqueued)
        {
            ClampDeadline(source, commandDeadline);
            source.RegisterCancellation(cancellationToken);
            ScheduleFlush(startedBatch);
            return source.Task;
        }

        return SendConvertedSlowAsync(command, source, state, converter, transferOwnership,
            cancellationToken, commandName, commandDeadline);
    }

    /// <summary>
    /// Sends a command whose reply shape is <c>bulk string | null | error</c> through a
    /// specialized source. The receive loop decodes small fully buffered bulk replies straight
    /// into the final <see cref="string"/> (see <see cref="StringPendingResponseSource"/>);
    /// anything else falls back to the general <see cref="RespValue"/> conversion path.
    /// </summary>
    internal ValueTask<string?> SendStringAsync<TCommand>(
        in TCommand command,
        CancellationToken cancellationToken = default,
        string? commandName = null,
        CommandDeadline commandDeadline = default)
        where TCommand : struct, IRespCommand
    {
        if (!commandDeadline.IsSet) commandDeadline = CommandDeadline.After(_commandTimeoutMilliseconds);
        var source = StringPendingResponseSource.Rent(commandName);
        bool enqueued;
        bool startedBatch;
        try
        {
            enqueued = TryEnqueue(in command, source, out startedBatch);
        }
        catch (RespireConnectionRetiredException) when (TryReroute(pinToConnection: false, commandDeadline, out var target, out var rerouted))
        {
            ReclaimUnpublished(source);
            return target.SendStringAsync(in command, cancellationToken, commandName,
                rerouted);
        }
        catch
        {
            ReclaimUnpublished(source);
            throw;
        }

        if (enqueued)
        {
            ClampDeadline(source, commandDeadline);
            source.RegisterCancellation(cancellationToken);
            ScheduleFlush(startedBatch);
            return source.Task;
        }

        return SendStringSlowAsync(command, source, cancellationToken, commandName, commandDeadline);
    }

    /// <summary>Sends a command whose reply must be a bulk string or null without retaining its payload.</summary>
    internal ValueTask<Stream?> SendBulkStreamAsync<TCommand>(
        in TCommand command,
        CancellationToken cancellationToken = default,
        string? commandName = null,
        Action<Exception?>? onFrameCompleted = null)
        where TCommand : struct, IRespCommand
        => SendBulkStreamCoreAsync(command,
            new BulkStreamPendingResponseSource(commandName, hasPrefixReply: false, onFrameCompleted),
            discardRepliesBefore: 0, retainRepliesBefore: false, cancellationToken);

    /// <summary>Atomically sends a checked prefix and a streaming command, as required for ASK redirects.</summary>
    internal ValueTask<Stream?> SendPrefixedBulkStreamAsync<TPrefix, TCommand>(
        in TPrefix prefix,
        in TCommand command,
        CancellationToken cancellationToken = default,
        string? commandName = null,
        Action<Exception?>? onFrameCompleted = null)
        where TPrefix : struct, IRespCommand
        where TCommand : struct, IRespCommand
        => SendBulkStreamCoreAsync(
            new PrefixedCommand<TPrefix, TCommand>(prefix, command),
            new BulkStreamPendingResponseSource(commandName, hasPrefixReply: true, onFrameCompleted),
            discardRepliesBefore: 1, retainRepliesBefore: true, cancellationToken);

    private ValueTask<Stream?> SendBulkStreamCoreAsync<TCommand>(
        TCommand command,
        BulkStreamPendingResponseSource source,
        int discardRepliesBefore,
        bool retainRepliesBefore,
        CancellationToken cancellationToken,
        CommandDeadline commandDeadline = default)
        where TCommand : struct, IRespCommand
    {
        if (!commandDeadline.IsSet) commandDeadline = CommandDeadline.After(_commandTimeoutMilliseconds);
        bool enqueued;
        bool startedBatch;
        try
        {
            enqueued = TryEnqueue(in command, source, out startedBatch,
                discardRepliesBefore, retainRepliesBefore);
        }
        catch (RespireConnectionRetiredException) when (TryReroute(pinToConnection: false, commandDeadline, out var target, out var rerouted))
        {
            ReclaimUnpublished(source, discardRepliesBefore + 2);
            return target.SendBulkStreamCoreAsync(command,
                new BulkStreamPendingResponseSource(source.CommandName, source.HasPrefixReply, source.OnFrameCompleted),
                discardRepliesBefore, retainRepliesBefore, cancellationToken, rerouted);
        }
        catch
        {
            ReclaimUnpublished(source, discardRepliesBefore + 2);
            throw;
        }

        if (enqueued)
        {
            ClampDeadline(source, commandDeadline);
            source.RegisterCancellation(cancellationToken);
            ScheduleFlush(startedBatch);
            return source.Task;
        }

        return SendBulkStreamSlowAsync(command, source, discardRepliesBefore,
            retainRepliesBefore, cancellationToken, commandDeadline);
    }

#if NET
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
#endif
    private async ValueTask<Stream?> SendBulkStreamSlowAsync<TCommand>(
        TCommand command,
        BulkStreamPendingResponseSource source,
        int discardRepliesBefore,
        bool retainRepliesBefore,
        CancellationToken cancellationToken,
        CommandDeadline commandDeadline)
        where TCommand : struct, IRespCommand
    {
        bool startedBatch;
        try
        {
            startedBatch = await WaitForInflightCapacityAsync(
                command, source, discardRepliesBefore, cancellationToken,
                retainRepliesBefore: retainRepliesBefore, commandDeadline: commandDeadline).ConfigureAwait(false);
        }
        catch (RespireConnectionRetiredException) when (TryReroute(pinToConnection: false, commandDeadline, out var target, out var rerouted))
        {
            // The capacity wait reclaimed the unadmitted source; the target needs a fresh one.
            return await target.SendBulkStreamCoreAsync(command,
                new BulkStreamPendingResponseSource(source.CommandName, source.HasPrefixReply, source.OnFrameCompleted),
                discardRepliesBefore, retainRepliesBefore, cancellationToken, rerouted).ConfigureAwait(false);
        }

        source.RegisterCancellation(cancellationToken);
        ScheduleFlush(startedBatch);
        return await source.Task.ConfigureAwait(false);
    }

#if NET
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
#endif
    private async ValueTask<string?> SendStringSlowAsync<TCommand>(
        TCommand command, StringPendingResponseSource source, CancellationToken cancellationToken,
        string? commandName, CommandDeadline commandDeadline)
        where TCommand : struct, IRespCommand
    {
        bool startedBatch;
        try
        {
            startedBatch = await WaitForInflightCapacityAsync(
                command, source, 0, cancellationToken, commandDeadline: commandDeadline).ConfigureAwait(false);
        }
        catch (RespireConnectionRetiredException) when (TryReroute(pinToConnection: false, commandDeadline, out var target, out var rerouted))
        {
            return await target.SendStringAsync(in command, cancellationToken, commandName, rerouted).ConfigureAwait(false);
        }

        source.RegisterCancellation(cancellationToken);
        ScheduleFlush(startedBatch);
        return await source.Task.ConfigureAwait(false);
    }

    /// <summary>
    /// Sends MULTI + pre-serialized commands + EXEC as one atomic append, so no other
    /// multiplexed command can interleave into the server-side transaction state. MULTI's +OK
    /// and each queue reply are drained; the returned task completes with the first queue
    /// error when present, otherwise EXEC's reply. Retaining queue errors is required because
    /// Redis Cluster can report MOVED/ASK there and then return only EXECABORT from EXEC.
    /// </summary>
    public ValueTask<RespValue> SendTransactionAsync(
        ReadOnlyMemory<byte> serializedCommands, int commandCount, CancellationToken cancellationToken = default,
        TimeSpan? cancellationTimeout = null, CancellationToken callerCancellationToken = default)
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

        return SendMultiReplyCoreAsync(
            new TransactionCommand(serializedCommands), repliesBeforeFinal: commandCount + 1,
            firstQueueReply: 1, cancellationToken, commandName: "MULTI/EXEC", cancellationTimeout, callerCancellationToken);
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
        string? commandName = null,
        bool armCommandDeadline = true,
        bool pinToConnection = false)
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
            commandName,
            armCommandDeadline,
            pinToConnection: pinToConnection);
    }

    /// <summary>
    /// Appends a one-shot prelude and command atomically, retaining a prelude error instead of
    /// discarding it. Both replies are drained before the returned operation completes.
    /// </summary>
    internal ValueTask<RespValue> SendValidatedPrefixedAsync<TPrefix, TCommand>(
        in TPrefix prefix,
        in TCommand command,
        CancellationToken cancellationToken = default,
        string commandName = "(command)")
        where TPrefix : struct, IRespCommand
        where TCommand : struct, IRespCommand
    {
        if (_inflight.Capacity < 2)
        {
            throw new InvalidOperationException(
                $"A validated prefixed command needs 2 in-flight slots, but this connection allows {_inflight.Capacity}.");
        }

        return SendMultiReplyCoreAsync(
            new PrefixedCommand<TPrefix, TCommand>(prefix, command),
            repliesBeforeFinal: 1,
            firstQueueReply: 0,
            cancellationToken,
            commandName);
    }

    /// <summary>Appends two one-shot preludes and a command atomically.</summary>
    internal ValueTask<RespValue> SendValidatedPrefixedAsync<TFirstPrefix, TSecondPrefix, TCommand>(
        in TFirstPrefix firstPrefix,
        in TSecondPrefix secondPrefix,
        in TCommand command,
        CancellationToken cancellationToken = default,
        string commandName = "(command)")
        where TFirstPrefix : struct, IRespCommand
        where TSecondPrefix : struct, IRespCommand
        where TCommand : struct, IRespCommand
    {
        if (_inflight.Capacity < 3)
        {
            throw new InvalidOperationException(
                $"A doubly prefixed command needs 3 in-flight slots, but this connection allows {_inflight.Capacity}.");
        }

        return SendMultiReplyCoreAsync(
            new PrefixedCommand<TFirstPrefix, PrefixedCommand<TSecondPrefix, TCommand>>(
                firstPrefix, new PrefixedCommand<TSecondPrefix, TCommand>(secondPrefix, command)),
            repliesBeforeFinal: 2,
            firstQueueReply: 0,
            cancellationToken,
            commandName);
    }

    private ValueTask<RespValue> SendMultiReplyCoreAsync<TCommand>(
        in TCommand command,
        int repliesBeforeFinal,
        int firstQueueReply,
        CancellationToken cancellationToken,
        string commandName,
        TimeSpan? cancellationTimeout = null, CancellationToken callerCancellationToken = default,
        CommandDeadline commandDeadline = default)
        where TCommand : struct, IRespCommand
    {
        if (!commandDeadline.IsSet) commandDeadline = CommandDeadline.After(_commandTimeoutMilliseconds);
        var replyCount = repliesBeforeFinal + 1;
        var source = MultiReplyPendingResponseSource.Rent(replyCount, firstQueueReply, commandName);
        source.ConfigureTimeout(this, cancellationTimeout, callerCancellationToken, cancellationToken);

        bool enqueued;
        bool startedBatch;
        try
        {
            enqueued = TryEnqueue(
                in command, source, out startedBatch, repliesBeforeFinal, retainRepliesBefore: true);
        }
        catch (RespireConnectionRetiredException) when (TryReroute(pinToConnection: false, commandDeadline, out var target, out var rerouted))
        {
            ReclaimUnpublished(source, replyCount + 1);
            return target.SendMultiReplyCoreAsync(in command, repliesBeforeFinal,
                firstQueueReply, cancellationToken, commandName, cancellationTimeout, callerCancellationToken,
                rerouted);
        }
        catch
        {
            ReclaimUnpublished(source, replyCount + 1);
            throw;
        }

        if (!enqueued)
        {
            return SendMultiReplySlowAsync(
                command, source, repliesBeforeFinal, firstQueueReply, replyCount, cancellationToken,
                commandName, cancellationTimeout, callerCancellationToken, commandDeadline);
        }

        ClampDeadline(source, commandDeadline);
        source.RegisterCancellation(cancellationToken);
        ScheduleFlush(startedBatch);
        return source.Task;
    }

    internal static bool IsDeadlineCancellation(OperationCanceledException error,
        CancellationToken deadlineToken, CancellationToken callerToken)
        => !callerToken.IsCancellationRequested && deadlineToken.IsCancellationRequested
            && error.CancellationToken == deadlineToken;

    private ValueTask<RespValue> SendCoreAsync<TCommand>(
        in TCommand command,
        int discardRepliesBefore,
        bool throwOnError,
        CancellationToken cancellationToken,
        string? commandName = null,
        bool armCommandDeadline = true,
        CommandDeadline commandDeadline = default,
        bool pinToConnection = false)
        where TCommand : struct, IRespCommand
    {
        if (!commandDeadline.IsSet && armCommandDeadline) commandDeadline = CommandDeadline.After(_commandTimeoutMilliseconds);
        // TCommand is always a struct, so the JIT specializes this method per command type and
        // folds the type test to a constant; it costs nothing on the ordinary command hot path.
        if (command is StreamedSetCommand streamedSet)
        {
            return SendStreamedSetAsync(streamedSet, cancellationToken, armCommandDeadline);
        }

        var source = _sourcePool.Rent(throwOnError, commandName);
        bool enqueued;
        bool startedBatch;
        try
        {
            enqueued = TryEnqueue(
                in command, source, out startedBatch, discardRepliesBefore,
                retainRepliesBefore: false, armCommandDeadline);
        }
        catch (RespireConnectionRetiredException) when (TryReroute(pinToConnection, commandDeadline, out var target, out var rerouted))
        {
            ReclaimUnpublished(source);
            return target.SendCoreAsync(in command, discardRepliesBefore, throwOnError,
                cancellationToken, commandName, armCommandDeadline, rerouted);
        }
        catch
        {
            ReclaimUnpublished(source);
            throw;
        }

        if (enqueued)
        {
            ClampDeadline(source, commandDeadline);
            source.RegisterCancellation(cancellationToken);
            ScheduleFlush(startedBatch);
            return source.Task;
        }

        return SendSlowAsync(command, source, discardRepliesBefore, cancellationToken, throwOnError,
            commandName, armCommandDeadline, commandDeadline, pinToConnection);
    }

    private async ValueTask<RespValue> SendStreamedSetAsync(
        StreamedSetCommand command, CancellationToken cancellationToken, bool armCommandDeadline)
    {
        var deadline = armCommandDeadline && _commandTimeoutMilliseconds != 0
            ? Environment.TickCount64 + _commandTimeoutMilliseconds
            : 0;
        using var timeoutCancellation = deadline == 0 ? null : new StreamDeadlineCancellation(deadline);
        timeoutCancellation?.Start();
        // Streamed SETs are rare and large, so one linked source per call is cheap. It observes the
        // caller, the command deadline and a connection abort; the reply is not yet published to
        // _inflight while the frame is written, so nothing else could complete this call.
        using var linkedCancellation = timeoutCancellation is null
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _closedCancellation.Token)
            : CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, timeoutCancellation.Token, _closedCancellation.Token);
        var effectiveCancellation = linkedCancellation.Token;
        try
        {
            await WaitForStreamingGateAsync(effectiveCancellation).ConfigureAwait(false);
        }
        catch (OperationCanceledException error) when (IsClosedCancellation(error, effectiveCancellation, cancellationToken))
        {
            throw ClosedDuringStreamedSet(error);
        }
        catch (OperationCanceledException error) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(error.Message, error, cancellationToken);
        }
        catch (OperationCanceledException error) when (timeoutCancellation is not null
            && IsDeadlineCancellation(error, effectiveCancellation, cancellationToken))
        {
            // Another streamed SET held this connection's frame for the whole command timeout.
            throw new RespireTimeoutException("SET", _commandTimeout!.Value, error,
                CaptureTimeoutDiagnostics(stage: RespireCommandStage.WaitingForCapacity));
        }

        PendingResponseSource source;
        try
        {
            source = _sourcePool.Rent(throwOnError: true, commandName: "SET");
        }
        catch
        {
            _streamingGate.Release();
            throw;
        }
        var streamingStarted = false;
        var requestStarted = false;
        var requestQueued = false;
        var requestFrameWritten = false;
        var queuedBatchStarted = false;
        try
        {
            lock (_writeGate)
            {
                ThrowIfRetired();
                if (_dead) throw new RespireConnectionException($"Connection to {Host}:{Port} is closed.");
                _streamingActive = true;
                streamingStarted = true;
            }

            await DrainBufferedWritesAsync(effectiveCancellation).ConfigureAwait(false);
            while (true)
            {
                effectiveCancellation.ThrowIfCancellationRequested();
                var capacityAvailable = _capacitySignal.WaitAsync(effectiveCancellation);
                lock (_writeGate)
                {
                    ThrowIfRetired();
                    if (_dead) throw new RespireConnectionException($"Connection to {Host}:{Port} is closed.");
                    if (_inflight.Capacity - _inflight.Count > 0) break;
                }

                await capacityAvailable.ConfigureAwait(false);
            }

            source.Deadline = deadline;

            // AppendStreamingStart rejects a retired or closed connection before writing any
            // bytes, so the request (and the caller's stream) stays untouched and retryable.
            var write = AppendStreamingStart(command, out queuedBatchStarted, out var requestWriteStart);
            requestStarted = true;
            ScheduleFlush(queuedBatchStarted);
            // A peer that stops reading stalls the socket write; bound every wait by the caller,
            // the deadline and connection abort so the catch below can close the partial frame.
            await write.WaitAsync(effectiveCancellation).ConfigureAwait(false);

            var chunk = ArrayPool<byte>.Shared.Rent(32 * 1024);
            try
            {
                var remaining = command.Length;
                while (remaining > 0)
                {
                    var pendingRead = command.Source.ReadAsync(
                        chunk.AsMemory(0, (int)Math.Min(chunk.Length, remaining)), effectiveCancellation).AsTask();
                    int read;
                    try
                    {
                        // WaitAsync also bounds streams that ignore their cancellation token.
                        read = await pendingRead.WaitAsync(effectiveCancellation).ConfigureAwait(false);
                    }
                    catch
                    {
                        // The read may still be writing into this pooled memory. Retain it until
                        // that read finishes instead of returning it while the source can mutate it.
                        _ = ReturnChunkAfterReadAsync(pendingRead, chunk);
                        chunk = null!;
                        throw;
                    }
                    if (read == 0) throw new EndOfStreamException("Stream ended before its declared SET length.");
                    remaining -= read;
                    write = AppendStreamingBytes(chunk.AsSpan(0, read));
                    ScheduleFlush(startedBatch: false);
                    await write.WaitAsync(effectiveCancellation).ConfigureAwait(false);
                }
            }
            finally
            {
                if (chunk is not null) ArrayPool<byte>.Shared.Return(chunk);
            }

            var finalWrite = AppendStreamingEnd(command, source, requestWriteStart, out queuedBatchStarted);
            requestQueued = true;
            source.RegisterCancellation(cancellationToken);
            ScheduleFlush(queuedBatchStarted);
            await finalWrite.WaitAsync(effectiveCancellation).ConfigureAwait(false);
            requestFrameWritten = true;
        }
        catch (OperationCanceledException error) when (IsClosedCancellation(error, effectiveCancellation, cancellationToken))
        {
            // The connection is already dead, so no partial frame can be followed by other bytes.
            if (!requestQueued) ReclaimUnpublished(source);
            else await ObserveStreamedSetResponseAsync(source).ConfigureAwait(false);
            throw ClosedDuringStreamedSet(error);
        }
        catch (OperationCanceledException error) when (timeoutCancellation is not null
            && IsDeadlineCancellation(error, effectiveCancellation, cancellationToken))
        {
            if (requestStarted && !requestFrameWritten)
                Abort(new RespireConnectionException(
                    $"Streamed SET on {Host}:{Port} timed out before its RESP frame completed.", error));
            if (!requestQueued) ReclaimUnpublished(source);
            else await ObserveStreamedSetResponseAsync(source).ConfigureAwait(false);
            throw new RespireTimeoutException("SET", _commandTimeout!.Value, error,
                CaptureTimeoutDiagnostics(stage: requestStarted
                    ? RespireCommandStage.Writing
                    : RespireCommandStage.WaitingForCapacity));
        }
        catch (OperationCanceledException error) when (cancellationToken.IsCancellationRequested)
        {
            if (requestStarted && !requestFrameWritten)
                Abort(new RespireConnectionException(
                    $"Streamed SET on {Host}:{Port} did not complete; connection was closed to preserve RESP framing.", error));
            if (!requestQueued) ReclaimUnpublished(source);
            else await ObserveStreamedSetResponseAsync(source).ConfigureAwait(false);
            throw new OperationCanceledException(error.Message, error, cancellationToken);
        }
        catch (Exception error)
        {
            if (requestStarted && !requestFrameWritten)
            {
                Abort(new RespireConnectionException(
                    $"Streamed SET on {Host}:{Port} did not complete; connection was closed to preserve RESP framing.", error));
            }
            if (!requestQueued) ReclaimUnpublished(source);
            else await ObserveStreamedSetResponseAsync(source).ConfigureAwait(false);
            throw;
        }
        finally
        {
            if (streamingStarted)
            {
                lock (_writeGate) _streamingActive = false;
                _capacitySignal.Signal();
            }
            _streamingGate.Release();
        }

        return await source.Task.ConfigureAwait(false);
    }

    private async ValueTask WaitForStreamingGateAsync(CancellationToken cancellationToken)
    {
        using var gateCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var gateWait = _streamingGate.WaitAsync(gateCancellation.Token);
        if (await Task.WhenAny(gateWait, _retiredSignal.Task).ConfigureAwait(false) == _retiredSignal.Task)
        {
            gateCancellation.Cancel();
            try
            {
                await gateWait.ConfigureAwait(false);
                _streamingGate.Release();
            }
            catch (OperationCanceledException error)
            {
                if (cancellationToken.IsCancellationRequested)
                    throw new OperationCanceledException(error.Message, error, cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfRetired();
        }

        try { await gateWait.ConfigureAwait(false); }
        catch (OperationCanceledException error) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(error.Message, error, cancellationToken);
        }
    }

    private static async ValueTask ObserveStreamedSetResponseAsync(PendingResponseSource source)
    {
        try
        {
            using var response = await source.Task.ConfigureAwait(false);
        }
        catch
        {
            // Aborted or cancelled commands still need GetResult to release the caller reference.
        }
    }

    private static async Task ReturnChunkAfterReadAsync(Task<int> pendingRead, byte[] chunk)
    {
        try { _ = await pendingRead.ConfigureAwait(false); }
        catch { /* The original streamed SET owns its failure. */ }
        finally { ArrayPool<byte>.Shared.Return(chunk); }
    }

    private bool IsClosedCancellation(
        OperationCanceledException error, CancellationToken effectiveCancellation, CancellationToken callerToken)
        => _closedCancellation.IsCancellationRequested && !callerToken.IsCancellationRequested
            && error.CancellationToken == effectiveCancellation;

    private RespireConnectionException ClosedDuringStreamedSet(OperationCanceledException error)
        => new($"Connection to {Host}:{Port} closed before the streamed SET completed.",
            Volatile.Read(ref _abortReason) ?? error);

    private async Task DrainBufferedWritesAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            Task? write = null;
            var schedule = false;
            lock (_writeGate)
            {
                ThrowIfRetired();
                if (_dead) throw new RespireConnectionException($"Connection to {Host}:{Port} is closed.");
                if (_activeBuffer.Count > 0)
                {
                    write = _activeBuffer.WriteCompletion;
                    schedule = true;
                }
                else if (Volatile.Read(ref _sending))
                {
                    write = _spareBuffer.WriteCompletion;
                    // The flush loop clears _sending outside this lock before completing the
                    // buffer. If it did so while this waiter was created, its completion may
                    // already have run; re-check instead of waiting for the buffer's next send.
                    Interlocked.MemoryBarrier();
                    if (!Volatile.Read(ref _sending)) continue;
                }
                else
                {
                    return;
                }
            }

            if (schedule) ScheduleFlush(startedBatch: false);
            if (write is not null) await write.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private Task AppendStreamingStart(
        StreamedSetCommand command, out bool startedBatch, out long requestWriteStart)
    {
        lock (_writeGate)
        {
            ThrowIfRetired();
            if (_dead) throw new RespireConnectionException($"Connection to {Host}:{Port} is closed.");
            var start = _activeBuffer.Count;
            startedBatch = start == 0 && _inflight.Count == 0;
            requestWriteStart = _enqueuedBytes;
            var writer = new RespWriter(_activeBuffer);
            command.WriteStart(ref writer);
            Volatile.Write(ref _enqueuedBytes, _enqueuedBytes + _activeBuffer.Count - start);
            return _activeBuffer.WriteCompletion;
        }
    }

    private Task AppendStreamingBytes(ReadOnlySpan<byte> bytes)
    {
        lock (_writeGate)
        {
            // The header is already queued, so the command was accepted before any retirement.
            // Retirement drains accepted work; _streamingActive keeps the drain pending.
            if (_dead) throw new RespireConnectionException($"Connection to {Host}:{Port} is closed.");
            _activeBuffer.Append(bytes);
            Volatile.Write(ref _enqueuedBytes, _enqueuedBytes + bytes.Length);
            return _activeBuffer.WriteCompletion;
        }
    }

    private Task AppendStreamingEnd(
        StreamedSetCommand command, PendingResponse source, long requestWriteStart, out bool startedBatch)
    {
        lock (_writeGate)
        {
            // Accepted before any retirement (see AppendStreamingBytes); finish the frame.
            if (_dead) throw new RespireConnectionException($"Connection to {Host}:{Port} is closed.");
            var start = _activeBuffer.Count;
            startedBatch = start == 0 && _inflight.Count == 0;
            var writer = new RespWriter(_activeBuffer);
            command.WriteEnd(ref writer);
            var length = _activeBuffer.Count - start;
            var requestWriteEnd = _enqueuedBytes + length;
            source.WriteStart = requestWriteStart;
            source.WriteEnd = requestWriteEnd;
            Volatile.Write(ref _enqueuedBytes, requestWriteEnd);
            if (_responseTimeout is not null) _activeReplyCount++;
            if (!_inflight.TryEnqueue(source, requestWriteEnd))
                throw new InvalidOperationException("No in-flight slot remained for streamed SET response.");
            return _activeBuffer.WriteCompletion;
        }
    }

    /// <summary>
    /// Sends a command whose response is read from the wire but discarded. Completes once the
    /// command has been written to the socket.
    /// </summary>
    public ValueTask SendFireAndForgetAsync<TCommand>(in TCommand command, CancellationToken cancellationToken = default,
        string? commandName = null, CommandDeadline capacityDeadline = default)
        where TCommand : struct, IRespCommand
    {
        cancellationToken.ThrowIfCancellationRequested();
        bool enqueued;
        bool startedBatch;
        Task writeTask;
        try
        {
            enqueued = TryEnqueueForWrite(in command, commandName, out startedBatch, out writeTask);
        }
        catch (RespireConnectionRetiredException) when (TryReroute(pinToConnection: false, capacityDeadline, out var target, out var rerouted))
        {
            return target.SendFireAndForgetAsync(in command, cancellationToken, commandName, rerouted);
        }

        if (enqueued)
        {
            ScheduleFlush(startedBatch);
            return WaitForWriteAsync(writeTask, cancellationToken);
        }

        return SendFireAndForgetSlowAsync(command, cancellationToken, commandName, capacityDeadline);
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
        bool retainRepliesBefore = false,
        bool armCommandDeadline = true)
        where TCommand : struct, IRespCommand
        => TryEnqueue(
            in command,
            source,
            out startedBatch,
            out _,
            trackWrite: false,
            discardRepliesBefore,
            retainRepliesBefore,
            armCommandDeadline);

    private bool TryEnqueueForWrite<TCommand>(
        in TCommand command,
        string? commandName,
        out bool startedBatch,
        out Task writeTask)
        where TCommand : struct, IRespCommand
    {
        var enqueued = TryEnqueue(
            in command,
            InflightRing.DiscardSentinel,
            out startedBatch,
            out var trackedWrite,
            trackWrite: true,
            discardedOperation: _generation is null ? null : commandName);
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
        bool retainRepliesBefore = false,
        bool armCommandDeadline = true,
        string? discardedOperation = null)
        where TCommand : struct, IRespCommand
    {
        startedBatch = false;
        writeTask = null;

        ThrowIfRetired();
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
                retainRepliesBefore,
                armCommandDeadline,
                discardedOperation);
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
                ThrowIfRetired();
                if (_dead)
                {
                    throw ClosedBeforeEnqueue();
                }

                if (_streamingActive
                    || (_credentialRenewalPending && typeof(TCommand) != typeof(CredentialRenewalAuthCommand))
                    || _inflight.Capacity - _inflight.Count < discardRepliesBefore + 1)
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

                var writeStart = StampWritePosition(source, frame.Length);
                StampDeadline(source, armCommandDeadline);
                for (var i = 0; i < discardRepliesBefore; i++)
                {
                    _inflight.TryEnqueue(retainRepliesBefore ? source : InflightRing.DiscardSentinel, writeStart);
                }

                if (discardedOperation is not null) _inflight.TryEnqueueDiscard(discardedOperation, _enqueuedBytes);
                else _inflight.TryEnqueue(source, _enqueuedBytes);
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
        bool retainRepliesBefore,
        bool armCommandDeadline,
        string? discardedOperation)
        where TCommand : struct, IRespCommand
    {
        startedBatch = false;
        writeTask = null;
        lock (_writeGate)
        {
            ThrowIfRetired();
            if (_dead)
            {
                throw ClosedBeforeEnqueue();
            }

            if (_streamingActive
                || (_credentialRenewalPending && typeof(TCommand) != typeof(CredentialRenewalAuthCommand))
                || _inflight.Capacity - _inflight.Count < discardRepliesBefore + 1)
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

            var writeStart = StampWritePosition(source, _activeBuffer.Count - mark);
            StampDeadline(source, armCommandDeadline);
            for (var i = 0; i < discardRepliesBefore; i++)
            {
                _inflight.TryEnqueue(retainRepliesBefore ? source : InflightRing.DiscardSentinel, writeStart);
            }

            if (discardedOperation is not null) _inflight.TryEnqueueDiscard(discardedOperation, _enqueuedBytes);
            else _inflight.TryEnqueue(source, _enqueuedBytes);
            if (trackWrite)
            {
                writeTask = _activeBuffer.WriteCompletion;
            }

            return true;
        }
    }

    // Stamp byte offsets under the write gate before publishing reply slots.
    private long StampWritePosition(PendingResponse source, int length)
    {
        var start = _enqueuedBytes;
        Volatile.Write(ref _enqueuedBytes, start + length);
        if (!ReferenceEquals(source, InflightRing.DiscardSentinel))
        {
            source.WriteStart = start;
            source.WriteEnd = start + length;
        }
        return start;
    }

    private void RecordWrite(int bytes)
    {
        // Only the persistent FlushLoopAsync sender calls this method, including TLS writes.
        Volatile.Write(ref _sentBytes, _sentBytes + bytes);
        Volatile.Write(ref _lastWriteTimestamp, Stopwatch.GetTimestamp());
    }

    /// <summary>Captures the sole outstanding frame on an exclusively rented connection.</summary>
    internal RespireTimeoutDiagnostics CaptureDedicatedTimeoutDiagnostics()
    {
        // Dedicated callers never pipeline another operation on this lease. The completed
        // reply watermark therefore starts the current frame, including an ASKING prefix.
        // If the reply won the race after cancellation, retain counters but leave stage unknown.
        var start = _inflight.CompletedWriteEnd;
        var end = Volatile.Read(ref _enqueuedBytes);
        return end > start
            ? CaptureTimeoutDiagnostics(start, end)
            : CaptureTimeoutDiagnostics();
    }

    internal RespireTimeoutDiagnostics CaptureTimeoutDiagnostics(
        long writeStart = 0, long writeEnd = 0, RespireCommandStage stage = RespireCommandStage.Unknown)
    {
        var sent = Volatile.Read(ref _sentBytes);
        var enqueued = Volatile.Read(ref _enqueuedBytes);
        if (writeEnd > 0)
        {
            stage = RespireTimeoutDiagnostics.ComputeStage(sent, writeStart, writeEnd);
        }
        var read = Volatile.Read(ref _lastReadTimestamp);
        var write = Volatile.Read(ref _lastWriteTimestamp);
        var serverId = ServerClientId;
        // Counters advance independently; clamp differences that cross concurrent observations.
        return RespireTimeoutDiagnostics.Capture(
            stage: stage, endpoint: new RespireEndpoint(Host, Port), connectionId: _diagnosticId,
            serverClientId: serverId == 0 ? null : serverId, inflightCount: Math.Max(0, _inflight.Count),
            inflightBytes: Math.Max(0, enqueued - _inflight.CompletedWriteEnd),
            pendingWriteBytes: Math.Max(0, enqueued - sent),
            timeSinceLastRead: read == 0 ? null : Stopwatch.GetElapsedTime(read),
            timeSinceLastWrite: write == 0 ? null : Stopwatch.GetElapsedTime(write),
            isConnected: IsConnected, isReconnecting: Multiplexer?.GetReconnectState(this), writtenBytes: sent);
    }

    /// <summary>
    /// Stamps (or clears — pooled sources carry the previous command's value) the command
    /// deadline before the source is published to the ring. The shared discard sentinel is
    /// never written: it sits in many slots at once and the sweep skips it by reference.
    /// </summary>
    private void StampDeadline(PendingResponse source, bool armCommandDeadline)
    {
        if (ReferenceEquals(source, InflightRing.DiscardSentinel))
        {
            return;
        }

        source.Deadline = armCommandDeadline
            ? CommandDeadline.After(_commandTimeoutMilliseconds)
            : CommandDeadline.None;
    }

#if NET
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
#endif
    private async ValueTask<RespValue> SendSlowAsync<TCommand>(
        TCommand command,
        PendingResponseSource source,
        int discardRepliesBefore,
        CancellationToken cancellationToken,
        bool throwOnError,
        string? commandName,
        bool armCommandDeadline,
        CommandDeadline commandDeadline,
        bool pinToConnection)
        where TCommand : struct, IRespCommand
    {
        bool startedBatch;
        try
        {
            startedBatch = await WaitForInflightCapacityAsync(
                    command, source, discardRepliesBefore, cancellationToken, armCommandDeadline,
                    commandDeadline: commandDeadline)
                .ConfigureAwait(false);
        }
        catch (RespireConnectionRetiredException) when (TryReroute(pinToConnection, commandDeadline, out var target, out var rerouted))
        {
            return await target.SendCoreAsync(in command, discardRepliesBefore, throwOnError,
                cancellationToken, commandName, armCommandDeadline, rerouted).ConfigureAwait(false);
        }

        source.RegisterCancellation(cancellationToken);
        ScheduleFlush(startedBatch);
        return await source.Task.ConfigureAwait(false);
    }

#if NET
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
#endif
    private async ValueTask<RespValue> SendMultiReplySlowAsync<TCommand>(
        TCommand command,
        MultiReplyPendingResponseSource source,
        int repliesBeforeFinal,
        int firstQueueReply,
        int replyCount,
        CancellationToken cancellationToken,
        string commandName,
        TimeSpan? cancellationTimeout, CancellationToken callerCancellationToken,
        CommandDeadline commandDeadline)
        where TCommand : struct, IRespCommand
    {
        bool startedBatch;
        try
        {
            var deadline = commandDeadline;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var capacityAvailable = _capacitySignal.WaitAsync(cancellationToken);
                if (TryEnqueue(
                    in command, source, out startedBatch, repliesBeforeFinal, retainRepliesBefore: true))
                {
                    ClampDeadline(source, deadline);
                    break;
                }

                ScheduleFlush(startedBatch: false);
                await WaitForCapacityAsync(capacityAvailable, deadline, source.CommandName, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException ex) when (cancellationTimeout is not null
            && IsDeadlineCancellation(ex, cancellationToken, callerCancellationToken))
        {
            ReclaimUnpublished(source, replyCount + 1);
            throw new RespireTimeoutException("MULTI/EXEC", cancellationTimeout.Value, ex,
                CaptureTimeoutDiagnostics(stage: RespireCommandStage.WaitingForCapacity));
        }
        catch (RespireConnectionRetiredException) when (TryReroute(pinToConnection: false, commandDeadline, out var target, out var rerouted))
        {
            ReclaimUnpublished(source, replyCount + 1);
            return await target.SendMultiReplyCoreAsync(in command, repliesBeforeFinal,
                firstQueueReply, cancellationToken, commandName, cancellationTimeout, callerCancellationToken,
                rerouted).ConfigureAwait(false);
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
        TState state,
        ResponseConverter<TState, TResult> converter,
        bool transferOwnership,
        CancellationToken cancellationToken,
        string? commandName,
        CommandDeadline commandDeadline)
        where TCommand : struct, IRespCommand
    {
        bool startedBatch;
        try
        {
            startedBatch = await WaitForInflightCapacityAsync(
                command, source, 0, cancellationToken, commandDeadline: commandDeadline).ConfigureAwait(false);
        }
        catch (RespireConnectionRetiredException) when (TryReroute(pinToConnection: false, commandDeadline, out var target, out var rerouted))
        {
            return await target.SendConvertedAsync(in command, state, converter,
                transferOwnership, cancellationToken, commandName, rerouted).ConfigureAwait(false);
        }

        source.RegisterCancellation(cancellationToken);
        ScheduleFlush(startedBatch);
        return await source.Task.ConfigureAwait(false);
    }

#if NET
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
#endif
    private async ValueTask SendFireAndForgetSlowAsync<TCommand>(TCommand command, CancellationToken cancellationToken,
        string? commandName, CommandDeadline capacityDeadline)
        where TCommand : struct, IRespCommand
    {
        // A rerouted send keeps the capacity budget that started on the retired socket.
        var deadline = capacityDeadline.IsSet ? capacityDeadline : CommandDeadline.After(_commandTimeoutMilliseconds);
        bool startedBatch;
        Task writeTask;
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var capacityAvailable = _capacitySignal.WaitAsync(cancellationToken);
                if (TryEnqueueForWrite(in command, commandName, out startedBatch, out writeTask))
                {
                    break;
                }

                ScheduleFlush(startedBatch: false);
                await WaitForCapacityAsync(capacityAvailable, deadline, commandName: null, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (RespireConnectionRetiredException) when (TryReroute(pinToConnection: false, deadline, out var target, out var rerouted))
        {
            await target.SendFireAndForgetAsync(in command, cancellationToken, commandName, rerouted).ConfigureAwait(false);
            return;
        }

        ScheduleFlush(startedBatch);
        await WaitForWriteAsync(writeTask, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// In-flight ring full: flush, then park until the receive loop frees capacity. Returns
    /// whether the eventual enqueue started a new write batch.
    /// </summary>
#if NET
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
#endif
    private async ValueTask<bool> WaitForInflightCapacityAsync<TCommand>(
        TCommand command,
        PendingResponse source,
        int discardRepliesBefore,
        CancellationToken cancellationToken,
        bool armCommandDeadline = true,
        bool retainRepliesBefore = false,
        CommandDeadline commandDeadline = default)
        where TCommand : struct, IRespCommand
    {
        try
        {
            var deadline = commandDeadline.IsSet || !armCommandDeadline
                ? commandDeadline
                : CommandDeadline.After(_commandTimeoutMilliseconds);
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var capacityAvailable = _capacitySignal.WaitAsync(cancellationToken);

                // Arm before retrying so a concurrent dequeue cannot pulse between the
                // failed enqueue and waiter registration.
                if (TryEnqueue(
                    in command, source, out var startedBatch, discardRepliesBefore,
                    retainRepliesBefore, armCommandDeadline))
                {
                    ClampDeadline(source, deadline);
                    return startedBatch;
                }

                ScheduleFlush(startedBatch: false);
                await WaitForCapacityAsync(capacityAvailable, deadline, source.CommandName, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException error)
        {
            // The command never reached the ring, so this cancellation proves it was not sent.
            // Callers that must tell a safe retry from an uncertain outcome (lock release) rely
            // on the marker; everyone else still sees an OperationCanceledException.
            ReclaimUnpublished(source, retainRepliesBefore ? discardRepliesBefore + 2 : 2);
            throw new RespireCommandNotSubmittedException(error);
        }
        catch
        {
            ReclaimUnpublished(source, retainRepliesBefore ? discardRepliesBefore + 2 : 2);
            throw;
        }
    }

    /// <summary>
    /// Re-stamps a source enqueued after a capacity wait with the effective deadline computed
    /// when the send began. This also carries a maintenance-relaxed deadline across reroutes.
    /// The store may race a sweep that already read the fresher stamp; that only delays the
    /// timeout, by at most one sweep granularity interval.
    /// </summary>
    private static void ClampDeadline(PendingResponse source, CommandDeadline deadline)
    {
        if (deadline.IsSet) source.Deadline = deadline;
    }

    /// <summary>
    /// Awaits freed ring capacity, bounded by the command deadline when one is armed — a full
    /// ring on a stalled connection must not park a caller past its command timeout.
    /// </summary>
    private async Task WaitForCapacityAsync(
        Task capacityAvailable, CommandDeadline deadline, string? commandName, CancellationToken cancellationToken)
    {
        if (!deadline.IsSet)
        {
            await capacityAvailable.ConfigureAwait(false);
            return;
        }

        if (_maintenanceOptions is not null)
        {
            await WaitForMaintenanceCapacityAsync(capacityAvailable, deadline, commandName, cancellationToken).ConfigureAwait(false);
            return;
        }
        var remaining = deadline.Ticks - Environment.TickCount64;
        if (remaining <= 0)
        {
            throw new RespireTimeoutException(commandName ?? "(command)", _commandTimeout!.Value, null,
                CaptureTimeoutDiagnostics(stage: RespireCommandStage.WaitingForCapacity));
        }

        try
        {
            await capacityAvailable
                .WaitAsync(TimeSpan.FromMilliseconds(remaining), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new RespireTimeoutException(commandName ?? "(command)", _commandTimeout!.Value, null,
                CaptureTimeoutDiagnostics(stage: RespireCommandStage.WaitingForCapacity));
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

                        Volatile.Write(ref _sending, true);
                        sending = _activeBuffer;
                        _activeBuffer = _spareBuffer;
                        _spareBuffer = sending;
                        sendingReplyCount = _activeReplyCount;
                        _activeReplyCount = 0;
                    }

                    // Never cancelled: a partial RESP frame on the wire is unrecoverable.
                    var memory = sending.WrittenMemory;
                    if (_stream is null)
                    {
                        while (memory.Length > 0)
                        {
                            var pending = _socket!.SendAsync(memory, SocketFlags.None);
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

                            RecordWrite(sent);
                            memory = memory[sent..];
                        }
                    }
                    else
                    {
                        var pending = _stream.WriteAsync(memory);
                        if (!pending.IsCompletedSuccessfully)
                        {
                            synchronousBatches = -1;
                        }

                        await pending.ConfigureAwait(false);
                        RecordWrite(memory.Length);
                    }

                    MarkRepliesSent(sendingReplyCount);

                    // The socket write has completed; publish idle before completing the buffer
                    // so a streaming producer cannot miss the send-completion signal.
                    Volatile.Write(ref _sending, false);
                    sending.CompleteWrite();
                    sending.Reset();
                    sending = null;
                    if (Volatile.Read(ref _retired)) _capacitySignal.Signal();

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
                        if (hasBulkHeader
                            && bulkType == RespDataType.BulkString
                            && _inflight.TryPeek(out var pending)
                            && pending is BulkStreamPendingResponseSource streamSource
                            && streamSource.IsFinalReply)
                        {
                            if (bulkLength < -1 || bulkLength > MaxResponseSize - 2L)
                            {
                                throw new RespireProtocolException(
                                    $"Response exceeds the {MaxResponseSize} byte limit.");
                            }

                            // Publish the active stream before dequeuing it so retirement drain
                            // never observes an empty ring while the payload is still being read.
                            Volatile.Write(ref _activeBulkStreamSource, streamSource);
                            try
                            {
                                if (!_inflight.TryDequeue(out var dequeued)
                                    || !ReferenceEquals(dequeued, streamSource))
                                {
                                    throw new RespireProtocolException("Streaming response order changed unexpectedly.");
                                }

                                if (bulkLength == -1)
                                {
                                    start = headerEnd;
                                    streamSource.CompleteMissing();
                                    MarkReplyReceived();
                                    streamSource.ReleaseRef();
                                }
                                else
                                {
                                    start = headerEnd;
                                    var streamed = await ReceiveBulkStreamAsync(
                                        buffer, start, end, streamSource, (int)bulkLength).ConfigureAwait(false);
                                    start = streamed.Start;
                                    end = streamed.End;
                                    MarkReplyReceived();
                                }
                            }
                            finally
                            {
                                Interlocked.CompareExchange(ref _activeBulkStreamSource, null, streamSource);
                                // Wake a retirement drain that saw the frame still active.
                                _capacitySignal.Signal();
                            }

                            responseBytes = 0;
                            continue;
                        }

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
                            if (hasBulkHeader
                                && TryCompleteStringDirect(bufferedData, bulkType, bulkLength, headerEnd, out var frameEnd))
                            {
                                start = frameEnd;
                                responseBytes = 0;
                                continue;
                            }

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
            var closeError = Volatile.Read(ref _abortReason)
                ?? fault
                ?? new RespireConnectionException($"Connection to {Host}:{Port} closed.");
            Abort(closeError);
            try
            {
                _generation?.ConnectionClosed(this,
                    Volatile.Read(ref _disposeCompletion) is null && !Volatile.Read(ref _retired));
                PendingCommandsFailing?.Invoke();
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Connection failure observer threw for {Host}:{Port}", Host, Port);
            }

            FailAllPending(Volatile.Read(ref _abortReason) ?? closeError);
        }
    }

    /// <summary>Receives a top-level GET bulk payload through a bounded caller pipe.</summary>
#if NET
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
#endif
    private async ValueTask<(int Start, int End)> ReceiveBulkStreamAsync(
        byte[] buffer,
        int start,
        int end,
        BulkStreamPendingResponseSource source,
        int payloadLength)
    {
        var payload = source.BeginPayload();
        Exception? failure = null;
        var remaining = payloadLength;

        try
        {
            var buffered = Math.Min(remaining, end - start);
            if (payload is not null)
            {
                var copied = 0;
                while (copied < buffered && payload is not null)
                {
                    var destination = payload.GetMemory(Math.Min(4096, buffered - copied));
                    var count = Math.Min(destination.Length, buffered - copied);
                    buffer.AsMemory(start + copied, count).CopyTo(destination);
                    payload.Advance(count);
                    copied += count;
                    if (!await FlushBulkStreamAsync(payload).ConfigureAwait(false))
                    {
                        payload = null;
                    }
                }
            }

            start += buffered;
            remaining -= buffered;
            if (remaining > 0)
            {
                // There cannot be frame bytes after an incomplete payload in this buffer.
                start = 0;
                end = 0;
            }

            while (remaining > 0)
            {
                int received;
                if (payload is null)
                {
                    var target = buffer.AsMemory(0, Math.Min(buffer.Length, remaining));
                    received = await ReceiveAsync(target).ConfigureAwait(false);
                }
                else
                {
                    var destination = payload.GetMemory(Math.Min(4096, remaining));
                    var target = destination[..Math.Min(destination.Length, remaining)];
                    received = await ReceiveAsync(target).ConfigureAwait(false);
                    if (received > 0)
                    {
                        payload.Advance(received);
                    }
                }

                if (received == 0)
                {
                    throw new RespireConnectionException($"Connection to {Host}:{Port} closed mid-frame.");
                }

                ResetReceiveDeadline();
                remaining -= received;
                if (payload is not null
                    && !await FlushBulkStreamAsync(payload).ConfigureAwait(false))
                {
                    payload = null;
                }
            }

            while (end - start < 2)
            {
                if (start > 0 && end > start)
                {
                    Buffer.BlockCopy(buffer, start, buffer, 0, end - start);
                    end -= start;
                    start = 0;
                }
                else if (start == end)
                {
                    start = 0;
                    end = 0;
                }

                var received = await ReceiveAsync(buffer.AsMemory(end, 2 - (end - start)))
                    .ConfigureAwait(false);
                if (received == 0)
                {
                    throw new RespireConnectionException($"Connection to {Host}:{Port} closed mid-frame.");
                }

                ResetReceiveDeadline();
                end += received;
            }

            if (buffer[start] != RespConstants.CarriageReturn
                || buffer[start + 1] != RespConstants.LineFeed)
            {
                throw new RespireProtocolException($"Bulk payload from {Host}:{Port} not terminated by CRLF.");
            }

            start += 2;
            source.CompleteDiscardedPayload();
            return (start, end);
        }
        catch (Exception ex)
        {
            failure = ex;
            throw;
        }
        finally
        {
            source.CompletePayload(failure);
            source.ReleaseRef();
        }
    }

    /// <summary>
    /// Flushes streamed payload bytes to the caller. Returns false once the reader is gone
    /// (disposed stream) or the flush was cancelled by connection abort, so the remaining
    /// frame is discarded. Waiting on caller backpressure suspends the receive watchdog:
    /// no socket read is outstanding, so the wait says nothing about server liveness.
    /// </summary>
    private ValueTask<bool> FlushBulkStreamAsync(RespBulkPayloadPipe payload)
    {
        var flush = payload.FlushAsync();
        if (flush.IsCompletedSuccessfully)
        {
            var result = flush.Result;
            return new(!result.IsCompleted && !result.IsCanceled);
        }

        return AwaitBulkStreamBackpressureAsync(flush);
    }

#if NET
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
#endif
    private async ValueTask<bool> AwaitBulkStreamBackpressureAsync(ValueTask<FlushResult> flush)
    {
        Interlocked.Increment(ref _responseTimeoutSuppressions);
        try
        {
            var result = await flush.ConfigureAwait(false);
            return !result.IsCompleted && !result.IsCanceled;
        }
        finally
        {
            // Restart the deadline before re-enabling the watchdog so it never judges the
            // resumed read against a timestamp taken before the consumer stalled.
            RestartResponseDeadline();
            Interlocked.Decrement(ref _responseTimeoutSuppressions);
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

        string? discardedOperation = null;
        PendingResponse source;
        var dequeued = _generation is null
            ? _inflight.TryDequeue(out source)
            : _inflight.TryDequeue(out source, out discardedOperation);
        if (!dequeued)
        {
            value.Dispose();
            throw new RespireProtocolException($"Unsolicited response from {Host}:{Port} with no command in flight.");
        }

        if (source is BulkStreamPendingResponseSource streamSource)
            streamSource.ObservePrefix(in value);

        // Retire a demoted generation before MarkReplyReceived pulses freed capacity: a parked
        // full-ring waiter must observe retirement at admission, not enqueue onto the old primary.
        _generation?.ObserveResponse(this, discardedOperation ?? source.CommandName, in value);

        MarkReplyReceived();

        if (_maintenanceStatus == MaintenanceNegotiating)
        {
            ObserveMaintenanceAcknowledgement(in value);
        }

        if (ReferenceEquals(source, InflightRing.DiscardSentinel))
        {
            value.Dispose();
            return;
        }

        // Deferred: the scheduler runs TrySetResult + ReleaseRef on a pool thread, one work
        // item per receive drain rather than one per reply.
        _completions.Add(source, in value);
    }

    /// <summary>
    /// Completes the head in-flight command with a string decoded straight from the receive
    /// buffer when the reply is a small, fully buffered bulk string and the head source asked
    /// for a <c>string?</c>. Skips the pooled payload rent/copy/return and the
    /// <see cref="RespValue"/> round-trip of the general path. A bulk string is never a push
    /// frame, so FIFO pairing with the ring head is safe. Returns false — leaving the general
    /// parser to handle the frame — for any other reply shape, an incomplete or malformed
    /// frame, or a head source of another type.
    /// </summary>
    private bool TryCompleteStringDirect(
        ReadOnlySpan<byte> buffer, RespDataType type, long payloadLength, int headerEnd, out int frameEnd)
    {
        frameEnd = 0;
        if (type != RespDataType.BulkString
            || !_inflight.TryPeek(out var head)
            || head is not StringPendingResponseSource source)
        {
            return false;
        }

        string? result;
        if (payloadLength == -1)
        {
            result = null;
            frameEnd = headerEnd;
        }
        else
        {
            if (payloadLength < 0)
            {
                return false;
            }

            // The caller's direct-fill branch bounds payloadLength below DirectFillThreshold.
            var length = (int)payloadLength;
            if (buffer.Length - headerEnd < length + 2)
            {
                return false;
            }

            if (buffer[headerEnd + length] != RespConstants.CarriageReturn
                || buffer[headerEnd + length + 1] != RespConstants.LineFeed)
            {
                return false;
            }

            result = Utf8String.GetString(buffer.Slice(headerEnd, length));
            frameEnd = headerEnd + length + 2;
        }

        _inflight.TryDequeue(out _);
        MarkReplyReceived();
        source.SetDirectResult(result);
        _completions.Add(source, default);
        return true;
    }

    private void MarkReplyReceived()
    {
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
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ValueTask<int> ReceiveAsync(Memory<byte> buffer)
        => _stream is null
            ? _socket!.ReceiveAsync(buffer, SocketFlags.None)
            : _stream.ReadAsync(buffer);

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
            if (isPushFrame && TryHandleMaintenancePush(in value))
            {
                value.Dispose();
                return true;
            }
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
                // Redis can remove a sharded subscription unsolicited when its slot moves.
                // Such a frame must not consume another control command's FIFO entry.
                if (_subscriptionPushFilter?.Invoke(in value, _inflight.Count != 0) == true)
                {
                    value.Dispose();
                    return true;
                }
                // Observe without disposing: this frame still belongs to normal FIFO completion.
                _subscriptionConfirmationHandler?.Invoke(in value);
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
        public ReadCommandKind ReadKind => ReadCommandKind.None;

        public void Write(ref RespWriter writer)
        {
            writer.WriteRaw(RespCommands.Multi);
            writer.WriteRaw(serializedCommands.Span);
            writer.WriteRaw(RespCommands.Exec);
        }
    }

    /// <summary>Writes two RESP commands as one buffer append.</summary>
    private readonly struct PrefixedCommand<TPrefix, TCommand> : IRespCommand
        where TPrefix : struct, IRespCommand
        where TCommand : struct, IRespCommand
    {
        private readonly TPrefix _prefix;
        private readonly TCommand _command;

        public PrefixedCommand(TPrefix prefix, TCommand command)
        {
            _prefix = prefix;
            _command = command;
        }

        public void Write(ref RespWriter writer)
        {
            _prefix.Write(ref writer);
            _command.Write(ref writer);
        }

        public ReadCommandKind ReadKind => _command.ReadKind;
        public int CursorArgumentIndex => _command.CursorArgumentIndex;
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

                var effectiveTimeout = MaintenanceTimeout(timeout, Environment.TickCount64, out var window, out _);
                var elapsed = Stopwatch.GetElapsedTime(deadlineStart);
                var delay = GetWatchdogDelay(effectiveTimeout, elapsed);
                if (window > 0)
                {
                    // Completion can restore the normal timeout while this timer is sleeping.
                    var checkMilliseconds = Math.Clamp(timeout.TotalMilliseconds / 4, 10, 1000);
                    delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds, Math.Min(window, checkMilliseconds)));
                }
                if (delay > TimeSpan.Zero)
                {
                    await DelayWatchdogAsync(delay, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                lock (_receiveDeadlineGate)
                {
                    if (deadlineStart != _receiveDeadlineTimestamp
                        || _sentReplyCount <= _receivedReplyCount
                        || Volatile.Read(ref _responseTimeoutSuppressions) != 0
                        || Stopwatch.GetElapsedTime(deadlineStart) < MaintenanceTimeout(timeout, Environment.TickCount64, out _, out _))
                    {
                        continue;
                    }

                    Abort(new RespireConnectionException(
                        $"Connection to {Host}:{Port} received no data for {effectiveTimeout} while responses were pending."));
                }

                return;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal connection teardown.
        }
    }

    /// <summary>
    /// Enforces <see cref="RespireConnectionOptions.CommandTimeout"/> without per-command
    /// timers: each command is stamped with a deadline at enqueue and this loop expires the
    /// oldest in-flight entries, completing only the caller — the reply is still consumed
    /// from the wire when it arrives, so the RESP stream stays in sync. Sleep is capped at
    /// the granularity so a command armed while the ring looked idle (or a stale slot read
    /// from a recycled source) can delay a timeout by at most one granularity interval.
    /// </summary>
    private async Task SweepCommandDeadlinesAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var granularityMilliseconds = (long)Math.Clamp(timeout.TotalMilliseconds / 4, 10, 1000);
        var granularity = TimeSpan.FromMilliseconds(granularityMilliseconds);
        try
        {
            while (true)
            {
                var now = Environment.TickCount64;
                var effectiveTimeout = MaintenanceTimeout(timeout, now, out _, out var maintenanceStarted);
                var next = _inflight.SweepExpired(now, timeout, this,
                    (long)(effectiveTimeout - timeout).TotalMilliseconds, maintenanceStarted,
                    _maintenanceOptions?.MaintenanceRelaxedTimeout);
                var delay = next < 0 || next > granularityMilliseconds
                    ? granularity
                    : TimeSpan.FromMilliseconds(next);
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal connection teardown.
        }
    }

    private static Task DelayWatchdogAsync(TimeSpan delay, CancellationToken cancellationToken)
        => Task.Delay(delay > MaxWatchdogSleep ? MaxWatchdogSleep : delay, cancellationToken);

    internal static TimeSpan GetWatchdogDelay(TimeSpan timeout, TimeSpan elapsed)
        => elapsed < timeout ? timeout - elapsed : TimeSpan.Zero;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ResetReceiveDeadline()
    {
        Volatile.Write(ref _lastReadTimestamp, Stopwatch.GetTimestamp());
        RestartResponseDeadline();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void RestartResponseDeadline()
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
        _credentialSession?.RequestStop();
        _watchdogCancellation?.Cancel();
        var writeFailure = reason
            ?? new RespireConnectionException($"Connection to {Host}:{Port} closed before writing completed.");
        lock (_writeGate)
        {
            if (_dead)
            {
                return;
            }

            _dead = true;
            _abortReason = reason;
            // The flush loop owns an in-progress send: it completes that buffer when the socket
            // accepted every byte, or fails it when the closed socket rejects the write.
            _activeBuffer.FailWrite(writeFailure);
        }

        Volatile.Read(ref _activeBulkStreamSource)?.AbortPayload(writeFailure);

        try
        {
            _stream?.Dispose();
        }
        catch
        {
            // Already closed or faulted.
        }

        try
        {
            _socket?.Close(0);
        }
        catch
        {
            // Already closed.
        }

        // Wake the parked flush loop so it can observe the dead flag and exit.
        _flushSignal.Signal();
        _closedCancellation.Cancel();
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

    /// <summary>
    /// The failure for a command that found the connection already closed under the write gate,
    /// before anything was appended or reserved. A generic close becomes
    /// <see cref="RespireConnectionClosedBeforeSendException"/>. Known Respire close reasons keep
    /// their public exception type and carry a per-command marker, so lock release can distinguish
    /// a pre-enqueue failure from the same failure on an in-flight command.
    /// </summary>
    private Exception ClosedBeforeEnqueue()
    {
        var reason = Volatile.Read(ref _abortReason);
        if (reason is null)
        {
            return new RespireConnectionClosedBeforeSendException($"Connection to {Host}:{Port} is closed.", null);
        }

        return reason switch
        {
            RespireAuthenticationException authentication => MarkNotSubmitted(authentication),
            RespireReconnectLimitException reconnectLimit => MarkNotSubmitted(reconnectLimit),
            RespireProtocolException protocol => MarkNotSubmitted(protocol),
            RespireConnectionException connection when connection.GetType() == typeof(RespireConnectionException)
                => new RespireConnectionClosedBeforeSendException(connection.Message, connection.InnerException),
            _ => new RespireConnectionClosedBeforeSendException(reason.Message, reason),
        };
    }

    private static RespireAuthenticationException MarkNotSubmitted(RespireAuthenticationException reason)
    {
        var failure = reason.InnerException is { } inner
            ? new RespireAuthenticationException(reason.Message, inner)
            : new RespireAuthenticationException(reason.Message);
        failure.IsCommandNotSubmitted = true;
        return failure;
    }

    private static RespireReconnectLimitException MarkNotSubmitted(RespireReconnectLimitException reason)
    {
        var failure = reason.InnerException is { } inner
            ? new RespireReconnectLimitException(reason.Message, inner)
            : new RespireReconnectLimitException(reason.Message);
        failure.IsCommandNotSubmitted = true;
        return failure;
    }

    private static RespireProtocolException MarkNotSubmitted(RespireProtocolException reason)
    {
        var failure = reason.InnerException is { } inner
            ? new RespireProtocolException(reason.Message, inner)
            : new RespireProtocolException(reason.Message);
        failure.IsCommandNotSubmitted = true;
        return failure;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ThrowIfRetired()
    {
        if (Volatile.Read(ref _retired) || _generation?.IsRetired == true)
            throw new RespireConnectionRetiredException(Host, Port);
    }

    /// <summary>
    /// True when a streamed bulk reply is still open and its consumer has not read bytes for at
    /// least <paramref name="idle"/>. Socket reads can pause during pipe backpressure even while
    /// a slow consumer is making progress, so retirement tracks reads from the returned stream.
    /// </summary>
    internal bool HasStalledBulkStream(TimeSpan idle)
    {
        return Volatile.Read(ref _activeBulkStreamSource)?.HasStalledReader(idle) == true;
    }

    /// <summary>Stops acceptance atomically with enqueue, then drains accepted frames and replies.</summary>
    internal Task RetireAsync()
    {
        _completions.ReleaseCurrentRunner();
        TaskCompletionSource completion;
        lock (_writeGate)
        {
            if (_retirementCompletion is not null) return _retirementCompletion.Task;
            completion = _retirementCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Volatile.Write(ref _retired, true);
        }
        _retiredSignal.TrySetResult();
        // Stop refresh deadlines and provider work while accepted transport frames drain.
        _credentialSession?.RequestStop();
        _capacitySignal.Signal(); // Unaccepted full-ring waiters must fail immediately.
        // The drain catches every failure and transfers it to the shared completion task.
        _ = DrainAndDisposeAsync(completion);
        return completion.Task;
    }

    private async Task DrainAndDisposeAsync(TaskCompletionSource completion)
    {
        try
        {
            while (true)
            {
                // All current waiters share this pulse; a producer cannot consume the drain wakeup.
                // Subscribe before checking state so the last send/reply cannot race past the wait.
                var progress = _capacitySignal.WaitAsync(CancellationToken.None);
                lock (_writeGate)
                {
                    // An exited producer cannot supply another reply; abort cleanup also covers
                    // an unexpected exit before _dead is published, without spinning on its task.
                    if (_dead || _receiveTask.IsCompleted) break;
                    if (_inflight.Count == 0 && _activeBuffer.Count == 0 && !Volatile.Read(ref _sending)
                        && !_streamingActive
                        && Volatile.Read(ref _activeBulkStreamSource) is null)
                    {
                        Volatile.Write(ref _drainedSuccessfully, true);
                        break;
                    }
                }
                ScheduleFlush(startedBatch: false);
                await Task.WhenAny(progress, _receiveTask).ConfigureAwait(false);
            }
            await DisposeAsync().ConfigureAwait(false);
            // The receive loop has published its final batch. Preserve successful replies
            // already dequeued from the ring before announcing retirement completion.
            await _completions.WaitForIdleAsync().ConfigureAwait(false);
            completion.TrySetResult();
        }
        catch (Exception ex)
        {
            completion.TrySetException(ex);
        }
    }

    public ValueTask DisposeAsync()
    {
        TaskCompletionSource completion;
        lock (_writeGate)
        {
            if (_disposeCompletion is not null) return new ValueTask(_disposeCompletion.Task);
            completion = _disposeCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        Abort();
        _ = DisposeCoreAsync(completion);
        return new ValueTask(completion.Task);
    }

    private async Task DisposeCoreAsync(TaskCompletionSource completion)
    {
        try
        {
            await _receiveTask.ConfigureAwait(false);
            await _flushTask.ConfigureAwait(false);
            if (_watchdogTask is not null)
                await _watchdogTask.ConfigureAwait(false);
            if (_deadlineSweepTask is not null)
                await _deadlineSweepTask.ConfigureAwait(false);
            if (_credentialSession is not null)
                await _credentialSession.DisposeAsync().ConfigureAwait(false);

            lock (_writeGate)
            {
                _activeBuffer.Release();
                _spareBuffer.Release();
            }
            _stream?.Dispose();
            _socket?.Dispose();
            _watchdogCancellation?.Dispose();
            _logger?.LogDebug("Disconnected from {Host}:{Port}", Host, Port);
            completion.TrySetResult();
        }
        catch (Exception ex)
        {
            completion.TrySetException(ex);
        }
    }
}

/// <summary>
/// Receives out-of-band frames (pub/sub messages, RESP3 pushes) on the connection's receive
/// loop. The value is only valid for the duration of the callback — copy what you need; the
/// connection disposes it afterwards. Do not block.
/// </summary>
internal delegate void RespirePushHandler(in RespValue value);

internal delegate bool RespirePushFilter(in RespValue value, bool hasPendingResponse);

/// <summary>Tuning options for a single RESP connection.</summary>
internal sealed record RespireConnectionOptions
{
    public static readonly RespireConnectionOptions Default = new();

    internal IConnectionGeneration? Generation { get; init; }

    internal Func<string, int, CancellationToken, ValueTask<Stream>>? TestingStreamFactory { get; init; }

    internal RespireReconnectPolicy? ReconnectPolicy { get; init; }
    internal RespireMaintenanceNotificationMode MaintenanceNotifications { get; init; }
    internal TimeSpan MaintenanceRelaxedTimeout { get; init; } = TimeSpan.FromSeconds(30);
    internal TimeSpan MaintenanceWindowTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Receives out-of-band frames on connections built from these options (see
    /// <see cref="RespirePushHandler"/>). Set by the client's pub/sub hub.
    /// </summary>
    public RespirePushHandler? PushHandler { get; init; }

    /// <summary>Observes subscription acknowledgements before FIFO completion; must not dispose the frame.</summary>
    public RespirePushHandler? SubscriptionConfirmationHandler { get; init; }

    /// <summary>Returns true for unsolicited subscription frames; must not dispose the frame.</summary>
    internal RespirePushFilter? SubscriptionPushFilter { get; init; }

    /// <summary>Enables CLIENT TRACKING before this connection is published.</summary>
    public bool EnableClientTracking { get; init; }

    /// <summary>Validated wire tracking configuration; the default value selects OPTIN.</summary>
    public Commands.ClientTrackingConfiguration ClientTrackingOptions { get; init; }

    /// <summary>Timeout for the initial TCP connect.</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Aborts the connection when responses are pending and no bytes arrive within this period.
    /// Null disables the watchdog.
    /// </summary>
    public TimeSpan? ResponseTimeout { get; init; }

    /// <summary>
    /// Client-side cap on how long each command waits for its response. Commands are stamped
    /// with a deadline at enqueue and expired by a per-connection sweep — no per-command
    /// timer. Null disables the cap.
    /// </summary>
    public TimeSpan? CommandTimeout { get; init; }

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

    internal IRespireCredentialProvider? CredentialProvider { get; init; }
    internal RespireCredentials? InitialCredentials { get; init; }
    internal TimeSpan CredentialRefreshBeforeExpiry { get; init; } = TimeSpan.FromMinutes(5);
    internal TimeSpan CredentialRefreshRetryDelay { get; init; } = TimeSpan.FromSeconds(5);
    internal TimeProvider CredentialTimeProvider { get; init; } = TimeProvider.System;
    internal Func<int>? CredentialCacheInvalidation { get; init; }
    internal Action? CredentialCacheRetirementFence { get; init; }

    /// <summary>When set, CLIENT SETNAME runs during the handshake.</summary>
    public string? ClientName { get; init; }

    /// <summary>Logical database SELECTed during the handshake; 0 skips the SELECT.</summary>
    public int Database { get; init; }

    /// <summary>Verify Valkey 9+ Cluster support before selecting a non-zero database.</summary>
    internal bool RequireClusterDatabaseSupport { get; init; }

    /// <summary>Requested wire protocol. Auto permits only explicit unsupported-HELLO fallback.</summary>
    /// <remarks>Low-level connections retain their RESP2 default; RespireOptions supplies the client preference.</remarks>
    public RespProtocol Protocol { get; init; } = RespProtocol.Resp2;

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
