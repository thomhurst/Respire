using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using Respire.Commands;
using Respire.Infrastructure;
using Respire.Internal;
using Respire.Networking;
using Respire.Protocol;
using Respire.Serialization;

namespace Respire;

/// <summary>
/// The Respire Redis client. Connect once, share the instance, and call commands from any
/// thread — every call pipelines onto a small set of multiplexed connections. Commands that
/// intentionally block (BLPOP-style waits) transparently run on dedicated pooled connections
/// instead, so they are fully supported.
/// </summary>
public sealed partial class RespireClient : IRespireClient
{
    private readonly ClientCore _core;
    private readonly KeyPrefix? _keyPrefix;
    private readonly KeyPrefix? _pubSubPrefix;
    private readonly byte[]? _pubSubPatternPrefix;
    private RespireClient? _deferredBatchClient;
    private IStringCommands? _strings;
    private IKeyCommands? _keys;
    private ILockCommands? _locks;
    private IHashCommands? _hashes;
    private IListCommands? _lists;
    private IArrayCommands? _arrays;
    private ISetCommands? _sets;
    private ISortedSetCommands? _sortedSets;
    private IStreamCommands? _streams;
    private IBitmapCommands? _bitmaps;
    private IHyperLogLogCommands? _hyperLogLog;
    private IGeoCommands? _geo;
    private IVectorSetCommands? _vectorSets;
    private IScriptCommands? _scripts;
    private IFunctionCommands? _functions;
    private IServerCommands? _server;
    private readonly bool _ownsCore;
    private readonly bool _broadcastTracking;
    private readonly bool _bypassClientCache;
    private readonly bool _snapshotPrefixedBinaryKeys;
    private readonly RespireReadFrom _readFrom;
    private static readonly bool s_getIsReadOnly = RespireCommands.String.GET.IsReadOnly;
    [ThreadStatic] private static PooledByteBufferWriter? s_serializationBuffer;

    private RespireClient(
        ClientCore core, KeyPrefix? keyPrefix, bool ownsCore, RespireReadFrom? readFrom = null, bool bypassClientCache = false,
        bool snapshotPrefixedBinaryKeys = false, KeyPrefix? pubSubPrefix = null)
    {
        _core = core;
        _bypassClientCache = bypassClientCache;
        _snapshotPrefixedBinaryKeys = snapshotPrefixedBinaryKeys;
        _keyPrefix = keyPrefix;
        _pubSubPrefix = pubSubPrefix;
        _pubSubPatternPrefix = pubSubPrefix is null ? null : RespireChannel.EscapePattern(pubSubPrefix.Bytes);
        _ownsCore = ownsCore;
        _readFrom = readFrom ?? core.Options.ReadFrom;
        _broadcastTracking = core.Options.ClientSideCache?.TrackingMode == RespireClientTrackingMode.Broadcast;
    }

    /// <summary>
    /// Connects using a connection string: "host", "host:port", a single-endpoint
    /// StackExchange.Redis-compatible comma-delimited string, or a
    /// <c>redis://[user[:password]@]host[:port][/db]</c> URI
    /// (see <see cref="RespireOptions.Parse"/>).
    /// </summary>
    public static ValueTask<RespireClient> ConnectAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        RespireOptions options;
        try
        {
            options = RespireOptions.Parse(connectionString);
        }
        catch (Exception error)
        {
            RespireTelemetry.RecordError(error, internallyHandled: false);
            throw;
        }

        // Structured connection setup owns its final failure; observe only parsing here.
        return ConnectAsync(options, cancellationToken);
    }

    /// <summary>Connects eagerly using structured client options.</summary>
    public static async ValueTask<RespireClient> ConnectAsync(RespireOptions options, CancellationToken cancellationToken = default)
    {
        try
        {
            options = (options ?? throw new ArgumentNullException(nameof(options))).ValidateAndSnapshot();
            return await ConnectPrimaryAsync(options, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            RespireTelemetry.RecordError(error, internallyHandled: false);
            throw;
        }
    }

    private static async ValueTask<RespireClient> ConnectPrimaryAsync(
        RespireOptions options,
        CancellationToken cancellationToken)
    {
        var client = Create(options);
        try
        {
            await client._core.EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return client;
    }

    /// <summary>
    /// Tries independent Redis deployments in order and returns the first client that connects.
    /// This is connection-time failover only: after a client is returned, commands use that
    /// deployment and normal reconnect behavior rather than health-checked routing across all
    /// candidates. Every candidate's full <see cref="RespireOptions"/> is used as provided.
    /// </summary>
    /// <exception cref="RespireConnectionException">
    /// Thrown with an aggregate inner exception when every candidate fails to connect.
    /// </exception>
    public static async ValueTask<RespireClient> ConnectAnyAsync(
        IEnumerable<RespireOptions> candidates,
        CancellationToken cancellationToken = default)
    {
        var retryAttempts = 0;
        try
        {
            ArgumentNullException.ThrowIfNull(candidates);

            List<Exception>? failures = null;
            List<string>? failureMessages = null;
            var candidateIndex = 0;
            foreach (var candidate in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                candidateIndex++;
                if (candidate is null)
                {
                    throw new ArgumentException("Connection candidates cannot contain null entries.", nameof(candidates));
                }

                try
                {
                    return await ConnectPrimaryAsync(candidate.ValidateAndSnapshot(), cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    RespireTelemetry.RecordError(ex, internallyHandled: true, retryAttempts++);
                    (failures ??= []).Add(ex);
                    (failureMessages ??= []).Add(
                        $"{candidateIndex}. {FormatCandidateEndpoints(candidate)}: {ex.GetType().Name}: {ex.Message}");
                }
            }

            if (candidateIndex == 0)
            {
                throw new RespireConnectionException("No Redis connection candidates were provided.");
            }

            // Exhaustion publishes the last failed connection attempt. Preflight, iteration,
            // and cancellation failures instead follow every completed failed candidate.
            retryAttempts = candidateIndex - 1;
            throw new RespireConnectionException(
                "Unable to connect to any Redis endpoint candidate. Attempts:" +
                Environment.NewLine +
                string.Join(Environment.NewLine, failureMessages!),
                new AggregateException("All Redis connection candidates failed.", failures!));
        }
        catch (Exception error)
        {
            RespireTelemetry.RecordError(error, internallyHandled: false, retryAttempts);
            throw;
        }
    }

    private static string FormatCandidateEndpoints(RespireOptions options)
        => options.Endpoints.Count == 0
            ? "(no endpoints)"
            : string.Join(", ", options.Endpoints);

    /// <summary>
    /// Creates a client without connecting; the first command connects. Useful for dependency
    /// injection, where construction should not block on the network.
    /// </summary>
    /// <remarks>
    /// <see cref="RespireOptions.ConnectTimeout"/> bounds socket and TLS setup; the Redis handshake
    /// and non-blocking commands use <see cref="RespireOptions.CommandTimeout"/>. Blocking commands
    /// use their explicit wait timeout, and caller cancellation applies throughout. Standalone
    /// clients surface setup exceptions directly, while cluster clients wrap seed failures in
    /// <see cref="RespireConnectionException"/>. A later command starts a new attempt.
    /// </remarks>
    public static RespireClient Create(string connectionString) => Create(RespireOptions.Parse(connectionString));

    /// <summary>
    /// Creates a lazy client using structured options; the first command connects.
    /// </summary>
    /// <remarks>
    /// <see cref="RespireOptions.ConnectTimeout"/> bounds socket and TLS setup; the Redis handshake
    /// and non-blocking commands use <see cref="RespireOptions.CommandTimeout"/>. Blocking commands
    /// use their explicit wait timeout, and caller cancellation applies throughout. Standalone
    /// clients surface setup exceptions directly, while cluster clients wrap seed failures in
    /// <see cref="RespireConnectionException"/>. A later command starts a new attempt.
    /// </remarks>
    public static RespireClient Create(RespireOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options = options.ValidateAndSnapshot();
        KeyPrefix? prefix = null;
        if (!options.KeyPrefix.IsEmpty)
            prefix = options.KeyPrefix.Text is { } text ? new KeyPrefix(text) : new KeyPrefix(options.KeyPrefix.ToBytes());
        KeyPrefix? pubSubPrefix = null;
        if (!options.PubSubPrefix.IsEmpty)
            pubSubPrefix = options.PubSubPrefix.Text is { } pubSubText
                ? new KeyPrefix(pubSubText) : new KeyPrefix(options.PubSubPrefix.ToBytes());
        return new RespireClient(new ClientCore(options), keyPrefix: prefix, ownsCore: true, pubSubPrefix: pubSubPrefix);
    }

    /// <inheritdoc/>
    public RespireEndpoint Endpoint
        => _core.Sentinel is { } sentinel
            ? sentinel.Current?.Endpoint ?? throw new InvalidOperationException(
                "The Sentinel primary endpoint is unavailable until discovery succeeds. Use ConnectAsync or await the first command.")
            : _core.Endpoint;

    /// <inheritdoc/>
    public bool IsConnected
        => !_core.Disposed && (_core.Sentinel?.IsConnected == true || _core.Cluster?.IsConnected == true
            || _core.Multiplexer.IsConnected || _core.ReadRouter.IsConnected);

    /// <summary>Captures owned Cluster retirement diagnostics, or null for a non-Cluster client.</summary>
    /// <remarks>Performs no network I/O. Prefix views share the underlying router's state.
    /// Counters are best-effort observations, not a completion or correction-ordering barrier.
    /// Capture after client disposal begins throws; an already captured snapshot remains valid.</remarks>
    public RespireClusterRetirementSnapshot? GetClusterRetirementSnapshot()
    {
        ObjectDisposedException.ThrowIf(_core.Disposed, this);
        return _core.Cluster?.CaptureRetirementSnapshot();
    }

    /// <inheritdoc/>
    public IRespireClientSideCache? ClientSideCache => _core.ClientCache;

    /// <summary>Raised when connection health changes, including endpoint and error context.</summary>
    public event Action<RespireConnectionStateChange>? ConnectionStateChanged
    {
        add => _core.ConnectionStateChanged += value;
        remove => _core.ConnectionStateChanged -= value;
    }

    /// <inheritdoc/>
    public IStringCommands Strings => Volatile.Read(ref _strings) ?? InitializeFacet(ref _strings, static client => new StringCommands(client));
    /// <inheritdoc/>
    public IKeyCommands Keys => Volatile.Read(ref _keys) ?? InitializeFacet(ref _keys, static client => new KeyCommands(client));
    /// <inheritdoc/>
    public ILockCommands Locks => Volatile.Read(ref _locks) ?? InitializeFacet(ref _locks, static client => new LockCommands(client));
    /// <inheritdoc/>
    public IHashCommands Hashes => Volatile.Read(ref _hashes) ?? InitializeFacet(ref _hashes, static client => new HashCommands(client));
    /// <inheritdoc/>
    public IListCommands Lists => Volatile.Read(ref _lists) ?? InitializeFacet(ref _lists, static client => new ListCommands(client));

    /// <summary>Redis sparse array commands.</summary>
    public IArrayCommands Arrays => Volatile.Read(ref _arrays) ?? InitializeFacet(ref _arrays, static client => new ArrayCommands(client));
    /// <inheritdoc/>
    public ISetCommands Sets => Volatile.Read(ref _sets) ?? InitializeFacet(ref _sets, static client => new SetCommands(client));
    /// <inheritdoc/>
    public ISortedSetCommands SortedSets => Volatile.Read(ref _sortedSets) ?? InitializeFacet(ref _sortedSets, static client => new SortedSetCommands(client));
    /// <inheritdoc/>
    public IStreamCommands Streams => Volatile.Read(ref _streams) ?? InitializeFacet(ref _streams, static client => new StreamCommands(client));
    /// <inheritdoc/>
    public IBitmapCommands Bitmaps => Volatile.Read(ref _bitmaps) ?? InitializeFacet(ref _bitmaps, static client => new BitmapCommands(client));
    /// <inheritdoc/>
    public IHyperLogLogCommands HyperLogLog => Volatile.Read(ref _hyperLogLog) ?? InitializeFacet(ref _hyperLogLog, static client => new HyperLogLogCommands(client));
    /// <inheritdoc/>
    public IGeoCommands Geo => Volatile.Read(ref _geo) ?? InitializeFacet(ref _geo, static client => new GeoCommands(client));
    /// <inheritdoc/>
    public IVectorSetCommands VectorSets => Volatile.Read(ref _vectorSets) ?? InitializeFacet(ref _vectorSets, static client => new VectorSetCommands(client));
    /// <inheritdoc/>
    public IScriptCommands Scripts => Volatile.Read(ref _scripts) ?? InitializeFacet(ref _scripts, static client => new ScriptCommands(client));

    /// <summary>Redis Functions (Redis 7+).</summary>
    public IFunctionCommands Functions => Volatile.Read(ref _functions) ?? InitializeFacet(ref _functions, static client => new FunctionCommands(client));
    /// <inheritdoc/>
    public IServerCommands Server => Volatile.Read(ref _server) ?? InitializeFacet(ref _server, static client => new ServerCommands(client));

    private T InitializeFacet<T>(ref T? field, Func<RespireClient, T> create) where T : class
    {
        var created = create(this);
        return Interlocked.CompareExchange(ref field, created, null) ?? created;
    }

    /// <summary>
    /// A view of this client that prepends <paramref name="prefix"/> to every key (channels and
    /// server-level commands are untouched). Views share this client's connections; disposing a
    /// view is a no-op — dispose the root client.
    /// </summary>
    public IRespireClient WithKeyPrefix(string prefix)
    {
        ArgumentException.ThrowIfNullOrEmpty(prefix);
        return new RespireClient(_core, _keyPrefix is null ? new KeyPrefix(prefix) : _keyPrefix.Append(prefix),
            ownsCore: false, readFrom: _readFrom, bypassClientCache: _bypassClientCache,
            snapshotPrefixedBinaryKeys: _snapshotPrefixedBinaryKeys, pubSubPrefix: _pubSubPrefix);
    }

    /// <inheritdoc/>
    public IRespireClient WithKeyPrefix(RespireKey prefix)
    {
        if (prefix.IsEmpty) throw new ArgumentException("A key prefix cannot be empty.", nameof(prefix));
        if (prefix.Text is { } text) return WithKeyPrefix(text);
        return new RespireClient(_core, _keyPrefix is null ? new KeyPrefix(prefix.ToBytes()) : _keyPrefix.Append(prefix),
            ownsCore: false, readFrom: _readFrom, bypassClientCache: _bypassClientCache,
            snapshotPrefixedBinaryKeys: _snapshotPrefixedBinaryKeys, pubSubPrefix: _pubSubPrefix);
    }

    /// <inheritdoc/>
    public IRespireClient WithPubSubPrefix(string prefix)
    {
        ArgumentException.ThrowIfNullOrEmpty(prefix);
        Utf8RouteName.Validate(prefix);
        return new RespireClient(_core, _keyPrefix, ownsCore: false, readFrom: _readFrom,
            bypassClientCache: _bypassClientCache, snapshotPrefixedBinaryKeys: _snapshotPrefixedBinaryKeys,
            pubSubPrefix: _pubSubPrefix is null ? new KeyPrefix(prefix) : _pubSubPrefix.Append(prefix));
    }

    /// <inheritdoc/>
    public IRespireClient WithPubSubPrefix(RespireKey prefix)
    {
        if (prefix.IsEmpty) throw new ArgumentException("A pub/sub prefix cannot be empty.", nameof(prefix));
        if (prefix.Text is { } text) return WithPubSubPrefix(text);
        return new RespireClient(_core, _keyPrefix, ownsCore: false, readFrom: _readFrom,
            bypassClientCache: _bypassClientCache, snapshotPrefixedBinaryKeys: _snapshotPrefixedBinaryKeys,
            pubSubPrefix: _pubSubPrefix is null ? new KeyPrefix(prefix.ToBytes()) : _pubSubPrefix.Append(prefix));
    }

    /// <inheritdoc/>
    public RespireChannel ResolveChannel(RespireChannel channel)
        => _pubSubPrefix is null || channel.IsNotification ? channel
            : channel.Prepend(channel.Kind == SubscriptionKind.Pattern ? _pubSubPatternPrefix! : _pubSubPrefix.Bytes);

    /// <summary>Returns a view that applies a read-routing policy to metadata-confirmed read-only commands.</summary>
    /// <remarks>
    /// The view shares this client's connections. Only commands whose <see cref="RespireCommand.IsReadOnly"/>
    /// metadata is set and that have a routable key follow the policy; everything else stays on the primary.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="readFrom"/> is not a defined policy.</exception>
    /// <exception cref="InvalidOperationException">
    /// A non-primary policy was requested without Redis Cluster, Sentinel, or configured replica endpoints.
    /// </exception>
    public IRespireClient WithReadFrom(RespireReadFrom readFrom)
    {
        if (!Enum.IsDefined(readFrom)) throw new ArgumentOutOfRangeException(nameof(readFrom));
        if (readFrom is RespireReadFrom.AzAffinity or RespireReadFrom.AzAffinityReplicasAndPrimary
            && _core.Options.ClientAvailabilityZone is null)
            throw new InvalidOperationException("AZ-affinity reads require ClientAvailabilityZone before connecting.");
        if (readFrom != RespireReadFrom.Primary && !_core.Options.UseCluster
            && _core.Options.ReplicaEndpoints.Count == 0
            && string.IsNullOrWhiteSpace(_core.Options.SentinelPrimaryName))
            throw new InvalidOperationException("Replica read routing requires Cluster, Sentinel discovery, or configured ReplicaEndpoints.");
        return new RespireClient(_core, _keyPrefix, ownsCore: false, readFrom: readFrom,
            bypassClientCache: _bypassClientCache,
            snapshotPrefixedBinaryKeys: _snapshotPrefixedBinaryKeys, pubSubPrefix: _pubSubPrefix);
    }

    /// <summary>
    /// A view whose reads always go to Redis instead of the client-side cache. Writes still
    /// invalidate cached entries. Shares this client's connections; disposing a view is a no-op.
    /// </summary>
    /// <remarks>Returns this client when client-side caching is disabled or already bypassed.</remarks>
    public IRespireClient WithoutClientCache()
        => ReadCache is null
            ? this
            : new RespireClient(_core, _keyPrefix, ownsCore: false, readFrom: _readFrom, bypassClientCache: true,
                snapshotPrefixedBinaryKeys: _snapshotPrefixedBinaryKeys, pubSubPrefix: _pubSubPrefix);

    /// <summary>The cache consulted for reads; null when caching is disabled or bypassed by this view.</summary>
    internal ClientSideCacheCoordinator? ReadCache => _bypassClientCache ? null : _core.ClientCache;

    private RespireReadFrom EffectiveReadFrom => _readFrom;
    internal RespireReadFrom GetBatchReadFromPolicy() => EffectiveReadFrom;

    internal RespireReadFrom GetReadFromForCommand<TCommand>(in TCommand command, bool allowReadFrom = true)
        where TCommand : struct, IRespCommand
    {
        var policy = EffectiveReadFrom;
        return allowReadFrom && policy != RespireReadFrom.Primary && command.ReadKind != ReadCommandKind.None
            ? policy
            : RespireReadFrom.Primary;
    }

    /// <summary>Sends PING and returns the measured round-trip time. Redis: PING.</summary>
    public ValueTask<TimeSpan> PingAsync(CancellationToken cancellationToken = default)
    {
        var start = Stopwatch.GetTimestamp();
        return ConvertResponseAsync(
            "PING",
            new RawCommand(RespCommands.Ping),
            cancellationToken,
            start,
            static (long timestamp, in RespValue _) => Stopwatch.GetElapsedTime(timestamp));
    }

    // Command escape hatch

    /// <summary>
    /// Sends a command. Catalog descriptors are pre-encoded; a string converts implicitly to a
    /// caller-supplied descriptor and may contain spaces. The result is a lease.
    /// </summary>
    /// <remarks>Cluster execution validates all keys in known raw layouts before I/O. Unknown layouts remain server-validated.</remarks>
    public ValueTask<RespireResult> ExecuteAsync(RespireCommand command, params RespireValue[] args)
        => ExecuteCommandAsync(command, args, RespireCommandFlags.None, CancellationToken.None);

    /// <summary>
    /// Sends a command with optional policy flags and cancellation. Blocking commands use a
    /// dedicated pooled connection; cancellation abandons that connection without stalling
    /// multiplexed traffic.
    /// </summary>
    public ValueTask<RespireResult> ExecuteAsync(
        RespireCommand command,
        RespireValue[] args,
        RespireCommandFlags flags = RespireCommandFlags.None,
        CancellationToken cancellationToken = default)
        => ExecuteCommandAsync(command, args, flags, cancellationToken);

    /// <summary>
    /// Queues a command and discards its reply once it arrives.
    /// </summary>
    /// <remarks>
    /// Standalone execution returns after writing. Cluster execution may await replies to process
    /// <c>MOVED</c> and <c>ASK</c> redirects, adding round-trip latency. Redirects that cannot be
    /// followed are surfaced as server errors; ordinary command errors are discarded.
    /// </remarks>
    public ValueTask ExecuteFireAndForgetAsync(RespireCommand command, params RespireValue[] args)
        => ExecuteCommandFireAndForgetAsync(command, args, CancellationToken.None);

    /// <summary>
    /// Queues a command and discards its reply once it arrives.
    /// </summary>
    /// <remarks>
    /// Standalone execution returns after writing. Cluster execution may await replies to process
    /// <c>MOVED</c> and <c>ASK</c> redirects, adding round-trip latency. Redirects that cannot be
    /// followed are surfaced as server errors; ordinary command errors are discarded.
    /// </remarks>
    public ValueTask ExecuteFireAndForgetAsync(
        RespireCommand command, RespireValue[] args, CancellationToken cancellationToken = default)
        => ExecuteCommandFireAndForgetAsync(command, args, cancellationToken);

    /// <summary>
    /// Queues a command written as an interpolated string and discards its reply. Literal text
    /// splits on spaces; every interpolation hole is exactly one argument and is never
    /// re-tokenized, so values containing spaces are safe.
    /// </summary>
    /// <remarks>Cluster redirects that cannot be followed are surfaced as server errors.</remarks>
    public ValueTask ExecuteFireAndForgetAsync(
        RespireCommandInterpolatedStringHandler command,
        CancellationToken cancellationToken = default)
        => ExecuteInterpolatedFireAndForgetAsync(command, cancellationToken);

    private ValueTask<RespireResult> ExecuteCommandAsync(
        RespireCommand command,
        RespireValue[] args,
        RespireCommandFlags flags,
        CancellationToken cancellationToken)
    {
        if (command.IsCallerSupplied)
        {
            if (_keyPrefix is not null && IsModuleCommand(command.Name))
            {
                var moduleOperation = command.Name.ToUpperInvariant();
                var descriptorPrefixError = PrefixModuleKeysOrError(
                    moduleOperation, args, out var descriptorPrefixedArguments);
                if (descriptorPrefixError is not null)
                    return ValueTask.FromException<RespireResult>(descriptorPrefixError);
                args = descriptorPrefixedArguments;
            }

            return ExecuteRawAsync(
                command.Name, args, flags, cancellationToken,
                cacheMutation: command.CacheMutation,
                hasExplicitCacheMutation: command.HasExplicitCacheMutation,
                readKind: RawCommandDescriptorLookup.GetReadKind(command.Name, args),
                allowReadFrom: false);
        }

        // Catalog execution handles typed commands; explicit prefixable layouts also allow raw module commands.
        if (!TryGetPreencodedRawOperation(command, args, out var operation, out var rawArguments))
        {
            if (_keyPrefix is null || !RawCommandKeyLayouts.HasPrefixableLayout(command.Name))
                return ExecuteCatalogAsync(command, args, flags, cancellationToken);
            operation = command.Name;
            rawArguments = args;
        }

        var readKind = command.ReadKind != ReadCommandKind.None
            ? command.ReadKind : RawCommandDescriptorLookup.GetReadKind(operation, rawArguments);
        var cacheMutation = command.HasExplicitCacheMutation
            ? command.CacheMutation
            : RespireCommands.GetCacheMutation(operation);
        if (_keyPrefix is null) return ExecuteRawAsync(
            operation, rawArguments, flags, cancellationToken,
            cacheMutation: cacheMutation,
            hasExplicitCacheMutation: command.HasExplicitCacheMutation,
            readKind: readKind, allowReadFrom: command.Sources != RespireCommandSource.None);
        var prefixError = PrefixModuleKeysOrError(operation, rawArguments, out var prefixedArguments);
        return prefixError is null
            ? ExecuteRawAsync(operation, prefixedArguments, flags, cancellationToken,
                cacheMutation: cacheMutation,
                hasExplicitCacheMutation: command.HasExplicitCacheMutation,
                readKind: readKind, allowReadFrom: command.Sources != RespireCommandSource.None)
            : ValueTask.FromException<RespireResult>(prefixError);
    }

    private ValueTask ExecuteCommandFireAndForgetAsync(
        RespireCommand command,
        RespireValue[] args,
        CancellationToken cancellationToken)
    {
        if (command.IsCallerSupplied)
        {
            if (_keyPrefix is not null && IsModuleCommand(command.Name))
            {
                var moduleOperation = command.Name.ToUpperInvariant();
                var descriptorPrefixError = PrefixModuleKeysOrError(
                    moduleOperation, args, out var descriptorPrefixedArguments);
                if (descriptorPrefixError is not null)
                    return ValueTask.FromException(descriptorPrefixError);
                args = descriptorPrefixedArguments;
            }

            return ExecuteRawFireAndForgetAsync(
                command.Name, args, cancellationToken,
                cacheMutation: command.CacheMutation,
                hasExplicitCacheMutation: command.HasExplicitCacheMutation,
                readKind: RawCommandDescriptorLookup.GetReadKind(command.Name, args),
                allowReadFrom: false);
        }

        if (!TryGetPreencodedRawOperation(command, args, out var operation, out var rawArguments))
        {
            if (_keyPrefix is null || !RawCommandKeyLayouts.HasPrefixableLayout(command.Name))
                return ExecuteCatalogFireAndForgetAsync(command, args, cancellationToken);
            operation = command.Name;
            rawArguments = args;
        }

        var readKind = command.ReadKind != ReadCommandKind.None
            ? command.ReadKind : RawCommandDescriptorLookup.GetReadKind(operation, rawArguments);
        var cacheMutation = command.HasExplicitCacheMutation
            ? command.CacheMutation
            : RespireCommands.GetCacheMutation(operation);
        if (_keyPrefix is null) return ExecuteRawFireAndForgetAsync(
            operation, rawArguments, cancellationToken,
            cacheMutation: cacheMutation,
            hasExplicitCacheMutation: command.HasExplicitCacheMutation,
            readKind: readKind, allowReadFrom: command.Sources != RespireCommandSource.None);
        var prefixError = PrefixModuleKeysOrError(operation, rawArguments, out var prefixedArguments);
        return prefixError is null
            ? ExecuteRawFireAndForgetAsync(
                operation, prefixedArguments, cancellationToken,
                cacheMutation: cacheMutation,
                hasExplicitCacheMutation: command.HasExplicitCacheMutation,
                readKind: readKind, allowReadFrom: command.Sources != RespireCommandSource.None)
            : ValueTask.FromException(prefixError);
    }

    /// <summary>
    /// Shared by the result and fire-and-forget paths. Returns the exception to report through the task,
    /// as ExecuteCatalogAsync does, instead of throwing synchronously: a rejection for commands without a
    /// known layout, or the layout's own argument error for malformed arguments.
    /// </summary>
    private Exception? PrefixModuleKeysOrError(string operation, RespireValue[] arguments, out RespireValue[] prefixedArguments)
    {
        try
        {
            return TryApplyKeyPrefix(operation, arguments, out prefixedArguments) ? null : KeyPrefixNotSupported();
        }
        catch (ArgumentException exception)
        {
            prefixedArguments = [];
            return exception;
        }
    }

    /// <summary>
    /// Applies a key-prefixed view's prefix to a catalog command whose key layout names every key.
    /// Other commands are rejected because their keys cannot be located reliably.
    /// </summary>
    private RespireValue[] PrefixCatalogKeys(string operation, RespireValue[] arguments)
    {
        if (_keyPrefix is null) return arguments;
        return TryApplyKeyPrefix(operation, arguments, out var prefixed) ? prefixed : throw KeyPrefixNotSupported();
    }

    /// <summary>
    /// Rewrites every key of a command whose layout is marked prefixable in
    /// <see cref="RawCommandKeyLayouts"/>, the single source of truth for key-prefixed views. The
    /// caller's array is copied, never mutated. Returns false for commands without such a layout.
    /// </summary>
    /// <exception cref="ArgumentNullException">A key position holds <see cref="RespireValue.Null"/>.</exception>
    private bool TryApplyKeyPrefix(string operation, RespireValue[] arguments, out RespireValue[] prefixedArguments)
    {
        if (!RawCommandKeyLayouts.TryGetPrefixableLayout(operation, arguments, out var layout))
        {
            prefixedArguments = [];
            return false;
        }

        // Every key is rewritten, so the copy is always needed; it keeps the caller's array untouched.
        prefixedArguments = arguments.ToArray();
        for (var index = 0; index < layout.Count; index++)
        {
            var keyIndex = layout.Start + index * layout.Stride;
            prefixedArguments[keyIndex] = PrefixKey(arguments[keyIndex]);
        }
        if (layout.Extra >= 0)
            prefixedArguments[layout.Extra] = PrefixKey(arguments[layout.Extra]);
        return true;
    }

    private RespireValue PrefixKey(RespireValue key)
    {
        // An absent key would otherwise become the empty key, and so the prefix itself.
        RespireValue.ThrowIfNull(key, "args");
        return Key(key.AsKey());
    }

    /// <summary>
    /// Selects the subcommand-aware raw path for pre-encoded parent commands whose first argument is a
    /// known subcommand. Callers must still apply the key-prefix rejection that catalog execution applies.
    /// </summary>
    private static bool TryGetPreencodedRawOperation(
        RespireCommand command,
        RespireValue[] args,
        out string operation,
        out RespireValue[] rawArguments)
    {
        var multiplexedSubcommand = IsMultiplexedRawSubcommand(command.Name, ReadOnlySpan<string>.Empty, args);
        if (command.Behavior == RespireCommandBehavior.ConnectionScoped && !multiplexedSubcommand)
        {
            operation = string.Empty;
            rawArguments = args;
            return false;
        }

        if (args.Length > 0 && KnownRawOperation(command.Name, args[0]) is { } normalized)
        {
            operation = normalized;
            rawArguments = args.AsSpan(1).ToArray();
            return true;
        }

        operation = command.Name;
        rawArguments = args;
        return multiplexedSubcommand;
    }

#if NET
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
#endif
    private async ValueTask<RespireResult> ExecuteCatalogAsync(
        RespireCommand command,
        RespireValue[] args,
        RespireCommandFlags flags,
        CancellationToken cancellationToken)
    {
        string? storedProcedureName;
        CatalogCommand commandValue;
        ClusterRouter? clusterWide = null;
        try
        {
            ValidateResultFlags(flags);
            ValidateCatalogCommand(command);
            args = PrefixCatalogKeys(command.Name, args);
            storedProcedureName = StoredProcedureName(command.Name, args);
            commandValue = new CatalogCommand(command, args, ValidateClusterRawKeys(command.Name, args));
            if (_core.Cluster is { } router && DynamicCommandRouting.IsClusterWideMutation(command.Name, args))
            {
                ValidateClusterWideFlags(command.Name, flags);
                clusterWide = router;
            }
        }
        catch (Exception error) { RecordExecutePreflightFailure(error); throw; }

        RespValue response;
        if (clusterWide is { } cluster)
        {
            response = await SendClusterWideAsync(
                    command.Name, cluster, commandValue, cancellationToken)
                .ConfigureAwait(false);
        }
        else if (command.IsBlocking(args))
        {
            response = await SendBlockingAsync(
                    command.Name,
                    commandValue,
                    cancellationToken,
                    noRedirect: HasFlag(flags, RespireCommandFlags.NoRedirect),
                    allowReadFrom: command.Sources != RespireCommandSource.None
                        && commandValue.ReadKind != ReadCommandKind.None)
                .ConfigureAwait(false);
        }
        else if (storedProcedureName is null)
        {
            response = await SendAsync(command.Name, commandValue, cancellationToken, flags,
                allowReadFrom: command.Sources != RespireCommandSource.None
                    && commandValue.ReadKind != ReadCommandKind.None).ConfigureAwait(false);
        }
        else
        {
            response = await SendStoredProcedureAsync(
                    command.Name, commandValue, cancellationToken, storedProcedureName, flags,
                    allowReadFrom: command.Sources != RespireCommandSource.None
                        && commandValue.ReadKind != ReadCommandKind.None)
                .ConfigureAwait(false);
        }

        return new RespireResult(in response, _core.Options.Serializer);
    }

#if NET
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
#endif
    private async ValueTask ExecuteCatalogFireAndForgetAsync(
        RespireCommand command,
        RespireValue[] args,
        CancellationToken cancellationToken)
    {
        ValidateCatalogCommand(command);
        args = PrefixCatalogKeys(command.Name, args);
        if (command.IsBlocking(args))
        {
            throw new NotSupportedException(
                $"{command.Name} can block and cannot run through ExecuteFireAndForgetAsync.");
        }

        var storedProcedureName = StoredProcedureName(command.Name, args);
        var commandValue = new CatalogCommand(command, args, ValidateClusterRawKeys(command.Name, args));
        if (_core.Cluster is { } cluster
            && DynamicCommandRouting.IsClusterWideMutation(command.Name, args))
        {
            await SendClusterWideFireAndForgetAsync(
                    command.Name, cluster, commandValue, cancellationToken, storedProcedureName)
                .ConfigureAwait(false);
            return;
        }

        await SendFireAndForgetAsync(
                command.Name, commandValue, cancellationToken, storedProcedureName,
                allowReadFrom: command.Sources != RespireCommandSource.None
                    && commandValue.ReadKind != ReadCommandKind.None)
            .ConfigureAwait(false);
    }

#if NET
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
#endif
    private async ValueTask<RespireResult> ExecuteRawAsync(
        string command,
        RespireValue[] args,
        RespireCommandFlags flags,
        CancellationToken cancellationToken,
        RespireCacheMutation cacheMutation = RespireCacheMutation.Unknown,
        bool hasExplicitCacheMutation = false,
        ReadCommandKind readKind = ReadCommandKind.None,
        bool allowReadFrom = false)
    {
        string operation;
        string? storedProcedureName;
        DynamicCommand commandValue;
        bool isBlocking;
        ClusterRouter? clusterWide = null;
        try
        {
            ValidateResultFlags(flags);
            (operation, var words, var firstArgumentIndex) = ParseRawCommand(command, ref args);
            (storedProcedureName, commandValue) = CreateRawCommand(
                operation, words, firstArgumentIndex, args, cacheMutation, hasExplicitCacheMutation, readKind);
            isBlocking = RespireCommand.IsBlocking(
                operation,
                RespireCommand.Classify(operation),
                words.AsSpan(firstArgumentIndex),
                args);
            if (_core.Cluster is { } router && DynamicCommandRouting.IsClusterWideMutation(operation, args))
            {
                ValidateClusterWideFlags(operation, flags);
                clusterWide = router;
            }
        }
        catch (Exception error) { RecordExecutePreflightFailure(error); throw; }

        RespValue response;
        if (clusterWide is { } cluster)
        {
            response = await SendClusterWideAsync(
                    operation, cluster, commandValue, cancellationToken)
                .ConfigureAwait(false);
        }
        else if (isBlocking)
        {
            response = await SendBlockingAsync(
                    operation,
                    commandValue,
                    cancellationToken,
                    storedProcedureName,
                    noRedirect: HasFlag(flags, RespireCommandFlags.NoRedirect),
                    allowReadFrom: allowReadFrom && readKind != ReadCommandKind.None)
                .ConfigureAwait(false);
        }
        else if (storedProcedureName is null)
        {
            response = await SendAsync(operation, commandValue, cancellationToken, flags,
                allowReadFrom: allowReadFrom && readKind != ReadCommandKind.None).ConfigureAwait(false);
        }
        else
        {
            response = await SendStoredProcedureAsync(
                    operation, commandValue, cancellationToken, storedProcedureName, flags,
                    allowReadFrom: allowReadFrom && readKind != ReadCommandKind.None)
                .ConfigureAwait(false);
        }

        return new RespireResult(in response, _core.Options.Serializer);
    }

#if NET
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
#endif
    private async ValueTask ExecuteRawFireAndForgetAsync(
        string command,
        RespireValue[] args,
        CancellationToken cancellationToken,
        RespireCacheMutation cacheMutation = RespireCacheMutation.Unknown,
        bool hasExplicitCacheMutation = false,
        ReadCommandKind readKind = ReadCommandKind.None,
        bool allowReadFrom = false)
    {
        var (operation, words, firstArgumentIndex) = ParseRawCommand(command, ref args);
        ValidateRawFireAndForgetCommand(
            operation, words.AsSpan(firstArgumentIndex), args);
        var (storedProcedureName, commandValue) = CreateRawCommand(
            operation, words, firstArgumentIndex, args, cacheMutation, hasExplicitCacheMutation, readKind);

        if (_core.Cluster is { } cluster
            && DynamicCommandRouting.IsClusterWideMutation(operation, args))
        {
            await SendClusterWideFireAndForgetAsync(
                    operation, cluster, commandValue, cancellationToken, storedProcedureName)
                .ConfigureAwait(false);
            return;
        }

        await SendFireAndForgetAsync(
                operation, commandValue, cancellationToken, storedProcedureName,
                allowReadFrom: allowReadFrom && readKind != ReadCommandKind.None)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Sends a command written as an interpolated string — <c>ExecuteAsync($"SET {key} {value} EX {60}")</c>.
    /// Literal text splits on spaces; every interpolation hole is exactly one argument and is
    /// never re-tokenized, so values containing spaces are safe. Flags and cancellation are
    /// optional arguments — pass them by name.
    /// </summary>
    public ValueTask<RespireResult> ExecuteAsync(
        RespireCommandInterpolatedStringHandler command,
        RespireCommandFlags flags = RespireCommandFlags.None,
        CancellationToken cancellationToken = default)
        => ExecuteInterpolatedAsync(command, flags, cancellationToken);

#if NET
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
#endif
    private async ValueTask<RespireResult> ExecuteInterpolatedAsync(
        RespireCommandInterpolatedStringHandler command,
        RespireCommandFlags flags,
        CancellationToken cancellationToken)
    {
        string operation;
        string? storedProcedureName;
        DynamicCommand commandValue;
        bool isBlocking;
        ClusterRouter? clusterWide = null;
        try
        {
            ValidateResultFlags(flags);
            var (initialOperation, tokens) = command.Build();
            (operation, var firstArgumentIndex) = NormalizeInterpolatedOperation(initialOperation, tokens);
            var arguments = tokens.AsSpan(firstArgumentIndex);
            storedProcedureName = StoredProcedureName(operation, arguments);
            var routingKeyIndex = GetRawRoutingKeyIndex(operation, tokens, firstArgumentIndex);
            commandValue = new DynamicCommand(
                tokens, routingKeyIndex, firstArgumentIndex,
                readKind: RawCommandDescriptorLookup.GetReadKind(operation, arguments),
                cursorArgumentIndex: Verb.GetCursorArgumentIndex(operation),
                cacheMetadata: _core.ClientCache is null ? default : ClientCacheCommandMetadata.Get(operation));
            isBlocking = RespireCommand.IsBlocking(
                operation, RespireCommand.Classify(operation), arguments);
            if (_core.Cluster is { } router && DynamicCommandRouting.IsClusterWideMutation(operation, arguments))
            {
                ValidateClusterWideFlags(operation, flags);
                clusterWide = router;
            }
        }
        catch (Exception error) { RecordExecutePreflightFailure(error); throw; }

        RespValue response;
        if (clusterWide is { } cluster)
        {
            response = await SendClusterWideAsync(
                    operation, cluster, commandValue, cancellationToken)
                .ConfigureAwait(false);
        }
        else if (isBlocking)
        {
            response = await SendBlockingAsync(
                    operation,
                    commandValue,
                    cancellationToken,
                    storedProcedureName,
                    noRedirect: HasFlag(flags, RespireCommandFlags.NoRedirect),
                    allowReadFrom: false)
                .ConfigureAwait(false);
        }
        // Resolve read metadata only from the audited command catalog. Unknown operations stay primary.
        else if (storedProcedureName is null)
        {
            response = await SendAsync(operation, commandValue, cancellationToken, flags, allowReadFrom: false)
                .ConfigureAwait(false);
        }
        else
        {
            response = await SendStoredProcedureAsync(
                    operation, commandValue, cancellationToken, storedProcedureName, flags, allowReadFrom: false)
                .ConfigureAwait(false);
        }

        return new RespireResult(in response, _core.Options.Serializer);
    }

#if NET
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
#endif
    private async ValueTask ExecuteInterpolatedFireAndForgetAsync(
        RespireCommandInterpolatedStringHandler command,
        CancellationToken cancellationToken)
    {
        var (initialOperation, tokens) = command.Build();
        var (operation, firstArgumentIndex) = NormalizeInterpolatedOperation(initialOperation, tokens);
        var arguments = tokens.AsSpan(firstArgumentIndex);
        ValidateRawFireAndForgetCommand(operation, ReadOnlySpan<string>.Empty, arguments);

        var storedProcedureName = StoredProcedureName(operation, arguments);
        var routingKeyIndex = GetRawRoutingKeyIndex(
            operation, tokens, firstArgumentIndex);
        var commandValue = new DynamicCommand(
            tokens, routingKeyIndex, firstArgumentIndex,
            readKind: RawCommandDescriptorLookup.GetReadKind(operation, arguments),
            cursorArgumentIndex: Verb.GetCursorArgumentIndex(operation),
            cacheMetadata: _core.ClientCache is null ? default : ClientCacheCommandMetadata.Get(operation));
        if (_core.Cluster is { } cluster
            && DynamicCommandRouting.IsClusterWideMutation(operation, arguments))
        {
            await SendClusterWideFireAndForgetAsync(
                    operation, cluster, commandValue, cancellationToken, storedProcedureName)
                .ConfigureAwait(false);
            return;
        }

        await SendFireAndForgetAsync(
                operation, commandValue, cancellationToken, storedProcedureName, allowReadFrom: false)
            .ConfigureAwait(false);
    }

    private void ValidateCatalogCommand(RespireCommand command)
    {
        // The typed Search configuration API executes through the catalog rather than a server facet.
        if (command.Name == "FT.CONFIG SET") ServerCommands.EnsureAdminAllowed(this, command.Name);

        if (string.IsNullOrEmpty(command.Name))
        {
            throw new ArgumentException("Command must be an entry from RespireCommands.", nameof(command));
        }

        if (command.Behavior == RespireCommandBehavior.ConnectionScoped)
        {
            throw new NotSupportedException(
                $"{command.Name} requires connection affinity and cannot run through ExecuteAsync. " +
                "Use RespireOptions, CreateTransaction, or the subscription APIs instead.");
        }
    }

    private static NotSupportedException KeyPrefixNotSupported()
        => new(
            "This command cannot run through a key-prefixed view because its key positions are not known, " +
            "or because it selects keys by label or pattern and could reach keys outside the prefix. " +
            "Use the typed command facets, or run the command through an unprefixed client.");

    private static void ValidateResultFlags(RespireCommandFlags flags)
    {
        if ((flags & ~RespireCommandFlags.NoRedirect) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(flags), flags, "Unsupported command flags.");
        }
    }

    private static bool HasFlag(RespireCommandFlags flags, RespireCommandFlags flag)
        => (flags & flag) != 0;

    private static void ValidateClusterWideFlags(string operation, RespireCommandFlags flags)
    {
        if (flags != RespireCommandFlags.None)
        {
            throw new NotSupportedException(
                $"{flags} cannot be used with cluster-wide {operation} commands.");
        }
    }

    private static void ValidateRawFireAndForgetCommand(
        string operation,
        ReadOnlySpan<string> inlineArguments,
        ReadOnlySpan<RespireValue> arguments)
    {
        var behavior = RespireCommand.Classify(operation);
        if (behavior == RespireCommandBehavior.ConnectionScoped
            && IsMultiplexedRawSubcommand(operation, inlineArguments, arguments))
        {
            behavior = RespireCommandBehavior.Multiplexed;
        }

        if (behavior == RespireCommandBehavior.ConnectionScoped)
        {
            throw new NotSupportedException(
                $"{operation} requires connection affinity and cannot run through ExecuteFireAndForgetAsync.");
        }

        if (RespireCommand.IsBlocking(operation, behavior, inlineArguments, arguments))
        {
            throw new NotSupportedException(
                $"{operation} can block and cannot run through ExecuteFireAndForgetAsync.");
        }
    }

    private static bool IsMultiplexedRawSubcommand(
        string operation,
        ReadOnlySpan<string> inlineArguments,
        ReadOnlySpan<RespireValue> arguments)
    {
        if (operation == "SCRIPT")
        {
            return arguments.Length > 0 && IsMultiplexedScriptSubcommand(arguments[0]);
        }

        if (operation != "CLIENT")
        {
            return false;
        }

        return inlineArguments.Length > 0
            ? IsMultiplexedClientSubcommand(inlineArguments[0])
            : arguments.Length > 0 && IsMultiplexedClientSubcommand(arguments[0]);
    }

    private static bool IsMultiplexedScriptSubcommand(RespireValue candidate)
        => candidate.EqualsAsciiIgnoreCase("EXISTS")
           || candidate.EqualsAsciiIgnoreCase("FLUSH")
           || candidate.EqualsAsciiIgnoreCase("HELP")
           || candidate.EqualsAsciiIgnoreCase("KILL")
           || candidate.EqualsAsciiIgnoreCase("LOAD")
           || candidate.EqualsAsciiIgnoreCase("SHOW");

    private static bool IsMultiplexedClientSubcommand(string candidate)
        => candidate.Equals("HELP", StringComparison.OrdinalIgnoreCase)
           || candidate.Equals("KILL", StringComparison.OrdinalIgnoreCase)
           || candidate.Equals("LIST", StringComparison.OrdinalIgnoreCase)
           || candidate.Equals("PAUSE", StringComparison.OrdinalIgnoreCase)
           || candidate.Equals("UNBLOCK", StringComparison.OrdinalIgnoreCase)
           || candidate.Equals("UNPAUSE", StringComparison.OrdinalIgnoreCase);

    private static bool IsMultiplexedClientSubcommand(RespireValue candidate)
        => candidate.EqualsAsciiIgnoreCase("HELP")
           || candidate.EqualsAsciiIgnoreCase("KILL")
           || candidate.EqualsAsciiIgnoreCase("LIST")
           || candidate.EqualsAsciiIgnoreCase("PAUSE")
           || candidate.EqualsAsciiIgnoreCase("UNBLOCK")
           || candidate.EqualsAsciiIgnoreCase("UNPAUSE");

    // Execute entry points validate and build commands before a send owns final errors.
    // Each selected send records its own final failure, so preflight records separately.
    private static void RecordExecutePreflightFailure(Exception error)
        => RespireTelemetry.RecordError(error, internallyHandled: false);

    private static (string Operation, string[] Words, int FirstArgumentIndex) ParseRawCommand(string command, ref RespireValue[] args)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        var words = command.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var operation = RawOperationName(words, out var firstArgumentIndex);
        // Both raw spellings must expose the same source position to scripting diagnostics.
        if (words.Length == 1 && args.Length > 0 && operation is "SCRIPT" or "FUNCTION"
            && KnownRawOperation(operation, args[0]) is { } normalized
            && ScriptingEngineInfo.IsScriptingCommand(normalized))
        {
            words = [words[0], normalized[(operation.Length + 1)..]];
            operation = normalized;
            firstArgumentIndex = 2;
            args = args[1..];
        }
        return (operation, words, firstArgumentIndex);
    }

    private (string? StoredProcedureName, DynamicCommand Command) CreateRawCommand(
        string operation,
        string[] words,
        int firstArgumentIndex,
        RespireValue[] args,
        RespireCacheMutation cacheMutation = RespireCacheMutation.Unknown,
        bool hasExplicitCacheMutation = false,
        ReadCommandKind readKind = ReadCommandKind.None)
    {
        var tokens = new RespireValue[words.Length + args.Length];
        for (var i = 0; i < words.Length; i++)
        {
            tokens[i] = words[i];
        }

        args.CopyTo(tokens, words.Length);
        if (readKind == ReadCommandKind.None)
            readKind = RawCommandDescriptorLookup.GetReadKind(operation, tokens.AsSpan(firstArgumentIndex));
        var storedProcedureName = words.Length == 1 ? StoredProcedureName(operation, args) : null;
        var routingKeyIndex = GetRawRoutingKeyIndex(
            operation, tokens, firstArgumentIndex);
        return (storedProcedureName,
            new DynamicCommand(tokens, routingKeyIndex, firstArgumentIndex, cacheMutation, readKind,
                Verb.GetCursorArgumentIndex(operation), hasExplicitCacheMutation,
                _core.ClientCache is null ? default : ClientCacheCommandMetadata.Get(operation)));
    }

    private RawCommandKeyLayouts.KeyRouting ValidateClusterRawKeys(string operation, ReadOnlySpan<RespireValue> arguments)
        => _core.Cluster is null ? default : RawCommandKeyLayouts.ValidateClusterKeys(operation, arguments);

    private int GetRawRoutingKeyIndex(string operation, RespireValue[] tokens, int firstArgumentIndex)
    {
        var arguments = tokens.AsSpan(firstArgumentIndex);
        var validated = ValidateClusterRawKeys(operation, arguments);
        if (validated.Known)
            return validated.Index < 0 ? RawCommandKeyLayouts.KeyRouting.NoKeyIndex : firstArgumentIndex + validated.Index;
        // Registered module commands route by their layout even outside Cluster validation, so commands whose
        // key is not the first argument (JSON.DEBUG MEMORY, CMS.MERGE) still pick the right key.
        if (IsModuleCommand(operation)
            && RawCommandKeyLayouts.TryGetLayout(operation, arguments, out var layout))
            // Same precedence as RawCommandKeyLayouts.ValidateClusterKeys: a destination key comes first.
            return layout.Extra >= 0 ? firstArgumentIndex + layout.Extra
                : layout.Count > 0 ? firstArgumentIndex + layout.Start
                : RawCommandKeyLayouts.KeyRouting.NoKeyIndex;
        return DynamicCommandRouting.GetRoutingKeyIndex(operation, tokens, firstArgumentIndex);
    }

    private static bool IsModuleCommand(string operation)
        => operation.StartsWith("BF.", StringComparison.OrdinalIgnoreCase)
            || operation.StartsWith("CF.", StringComparison.OrdinalIgnoreCase)
            || operation.StartsWith("CMS.", StringComparison.OrdinalIgnoreCase)
            || operation.StartsWith("TOPK.", StringComparison.OrdinalIgnoreCase)
            || operation.StartsWith("TDIGEST.", StringComparison.OrdinalIgnoreCase)
            || operation.StartsWith("JSON.", StringComparison.OrdinalIgnoreCase)
            || operation.StartsWith("TS.", StringComparison.OrdinalIgnoreCase);

    private static string? StoredProcedureName(string operation, ReadOnlySpan<RespireValue> arguments)
        => arguments.Length > 0 &&
           (operation.Equals("EVALSHA", StringComparison.OrdinalIgnoreCase) ||
            operation.Equals("FCALL", StringComparison.OrdinalIgnoreCase) ||
            operation.Equals("FCALL_RO", StringComparison.OrdinalIgnoreCase))
            ? arguments[0].ToString()
            : null;

    private static string RawOperationName(string[] words, out int firstArgumentIndex)
    {
        var command = words[0].ToUpperInvariant();
        if (words.Length == 1)
        {
            firstArgumentIndex = 1;
            return command;
        }

        var operation = KnownRawOperation(command, words[1]);
        firstArgumentIndex = operation is null ? 1 : 2;
        return operation ?? command;
    }

    private static (string Operation, int FirstArgumentIndex) NormalizeInterpolatedOperation(
        string command,
        RespireValue[] tokens)
    {
        if (tokens.Length > 1 && KnownRawOperation(command, tokens[1]) is { } operation)
        {
            return (operation, 2);
        }

        return (command, 1);
    }

    internal static string? KnownRawOperation(string command, string candidate)
        => KnownRawOperation(command, (RespireValue)candidate);

    internal static string? KnownRawOperation(string command, RespireValue candidate)
        => command switch
        {
            "CONFIG" when candidate.EqualsAsciiIgnoreCase("GET") => "CONFIG GET",
            "CONFIG" when candidate.EqualsAsciiIgnoreCase("SET") => "CONFIG SET",
            "FT.CONFIG" when candidate.EqualsAsciiIgnoreCase("GET") => "FT.CONFIG GET",
            "FT.CONFIG" when candidate.EqualsAsciiIgnoreCase("SET") => "FT.CONFIG SET",
            "CLIENT" when candidate.EqualsAsciiIgnoreCase(ClientCacheCommandMetadata.CachingSubcommand)
                => ClientCacheCommandMetadata.CachingOperation,
            "CLIENT" when candidate.EqualsAsciiIgnoreCase(ClientCacheCommandMetadata.TrackingSubcommand)
                => ClientCacheCommandMetadata.TrackingOperation,
            "MEMORY" when candidate.EqualsAsciiIgnoreCase("USAGE") => "MEMORY USAGE",
            "OBJECT" when candidate.EqualsAsciiIgnoreCase("ENCODING") => "OBJECT ENCODING",
            "OBJECT" when candidate.EqualsAsciiIgnoreCase("FREQ") => "OBJECT FREQ",
            "OBJECT" when candidate.EqualsAsciiIgnoreCase("IDLETIME") => "OBJECT IDLETIME",
            "OBJECT" when candidate.EqualsAsciiIgnoreCase("REFCOUNT") => "OBJECT REFCOUNT",
            "FUNCTION" when candidate.EqualsAsciiIgnoreCase("DELETE") => "FUNCTION DELETE",
            "FUNCTION" when candidate.EqualsAsciiIgnoreCase("FLUSH") => "FUNCTION FLUSH",
            "FUNCTION" when candidate.EqualsAsciiIgnoreCase("LOAD") => "FUNCTION LOAD",
            "FUNCTION" when candidate.EqualsAsciiIgnoreCase("RESTORE") => "FUNCTION RESTORE",
            "SCRIPT" when candidate.EqualsAsciiIgnoreCase("DEBUG") => "SCRIPT DEBUG",
            "SCRIPT" when candidate.EqualsAsciiIgnoreCase("EXISTS") => "SCRIPT EXISTS",
            "SCRIPT" when candidate.EqualsAsciiIgnoreCase("FLUSH") => "SCRIPT FLUSH",
            "SCRIPT" when candidate.EqualsAsciiIgnoreCase("HELP") => "SCRIPT HELP",
            "SCRIPT" when candidate.EqualsAsciiIgnoreCase("KILL") => "SCRIPT KILL",
            "SCRIPT" when candidate.EqualsAsciiIgnoreCase("LOAD") => "SCRIPT LOAD",
            "SCRIPT" when candidate.EqualsAsciiIgnoreCase("SHOW") => "SCRIPT SHOW",
            "XGROUP" when candidate.EqualsAsciiIgnoreCase("CREATE") => "XGROUP CREATE",
            "XGROUP" when candidate.EqualsAsciiIgnoreCase("SETID") => "XGROUP SETID",
            "XGROUP" when candidate.EqualsAsciiIgnoreCase("DESTROY") => "XGROUP DESTROY",
            "XGROUP" when candidate.EqualsAsciiIgnoreCase("CREATECONSUMER") => "XGROUP CREATECONSUMER",
            "XGROUP" when candidate.EqualsAsciiIgnoreCase("DELCONSUMER") => "XGROUP DELCONSUMER",
            "XINFO" when candidate.EqualsAsciiIgnoreCase("GROUPS") => "XINFO GROUPS",
            "XINFO" when candidate.EqualsAsciiIgnoreCase("STREAM") => "XINFO STREAM",
            "XINFO" when candidate.EqualsAsciiIgnoreCase("CONSUMERS") => "XINFO CONSUMERS",
            _ => null,
        };

    // Pub/sub

    /// <summary>Publishes to a channel; returns the number of subscribers that received it. Redis: PUBLISH.</summary>
    public ValueTask<long> PublishAsync(string channel, RespireValue message, CancellationToken cancellationToken = default)
        => PublishAsync(new RespireChannel(channel), message, cancellationToken);

    /// <summary>Publishes to a sharded channel (Redis 7+). Redis: SPUBLISH.</summary>
    public ValueTask<long> PublishShardedAsync(string channel, RespireValue message, CancellationToken cancellationToken = default)
        => PublishShardedAsync(new RespireChannel(channel), message, cancellationToken);

    /// <summary>
    /// Subscribes to a channel and returns once the server has acknowledged the SUBSCRIBE, so the
    /// next publish is guaranteed to reach this subscriber — no readiness polling. Messages are
    /// buffered from that moment, whenever enumeration starts; disposing the subscription
    /// unsubscribes. Redis: SUBSCRIBE.
    /// </summary>
    public ValueTask<RespireSubscription> SubscribeAsync(string channel, CancellationToken cancellationToken = default)
        => SubscribeAsync(SubscriptionChannel(channel), cancellationToken);

    /// <summary>Subscribes to one channel with per-subscription buffer settings. Redis: SUBSCRIBE.</summary>
    public ValueTask<RespireSubscription> SubscribeAsync(
        string channel, RespireSubscriptionOptions options, CancellationToken cancellationToken)
        => SubscribeAsync(SubscriptionChannel(channel), options, cancellationToken);

    /// <inheritdoc cref="SubscribeAsync(string, CancellationToken)"/>
    public ValueTask<RespireSubscription> SubscribeAsync(params ReadOnlySpan<string> channels)
        => SubscribeAsync(channels, CancellationToken.None);

    /// <inheritdoc cref="SubscribeAsync(string, CancellationToken)"/>
    public ValueTask<RespireSubscription> SubscribeAsync(
        ReadOnlySpan<string> channels, CancellationToken cancellationToken)
        => SubscribeCoreAsync(SubscriptionKind.Channel, MapChannels(channels, SubscriptionKind.Channel), default, cancellationToken);

    /// <summary>Subscribes to channels with per-subscription buffer settings. Redis: SUBSCRIBE.</summary>
    public ValueTask<RespireSubscription> SubscribeAsync(
        ReadOnlySpan<string> channels,
        RespireSubscriptionOptions options,
        CancellationToken cancellationToken)
        => SubscribeCoreAsync(SubscriptionKind.Channel, MapChannels(channels, SubscriptionKind.Channel), options, cancellationToken);

    /// <summary>
    /// Subscribes to a glob pattern ("news.*") and returns once the server has acknowledged.
    /// Redis: PSUBSCRIBE.
    /// </summary>
    public ValueTask<RespireSubscription> SubscribePatternAsync(string pattern, CancellationToken cancellationToken = default)
        => SubscribeAsync(SubscriptionChannel(pattern).WithKind(SubscriptionKind.Pattern), cancellationToken);

    /// <summary>Subscribes to one pattern with per-subscription buffer settings. Redis: PSUBSCRIBE.</summary>
    public ValueTask<RespireSubscription> SubscribePatternAsync(
        string pattern, RespireSubscriptionOptions options, CancellationToken cancellationToken)
        => SubscribeAsync(SubscriptionChannel(pattern).WithKind(SubscriptionKind.Pattern), options, cancellationToken);

    /// <inheritdoc cref="SubscribePatternAsync(string, CancellationToken)"/>
    public ValueTask<RespireSubscription> SubscribePatternAsync(params ReadOnlySpan<string> patterns)
        => SubscribePatternAsync(patterns, CancellationToken.None);

    /// <inheritdoc cref="SubscribePatternAsync(string, CancellationToken)"/>
    public ValueTask<RespireSubscription> SubscribePatternAsync(
        ReadOnlySpan<string> patterns, CancellationToken cancellationToken)
        => SubscribeCoreAsync(SubscriptionKind.Pattern, MapChannels(patterns, SubscriptionKind.Pattern), default, cancellationToken);

    /// <summary>Subscribes to patterns with per-subscription buffer settings. Redis: PSUBSCRIBE.</summary>
    public ValueTask<RespireSubscription> SubscribePatternAsync(
        ReadOnlySpan<string> patterns,
        RespireSubscriptionOptions options,
        CancellationToken cancellationToken)
        => SubscribeCoreAsync(SubscriptionKind.Pattern, MapChannels(patterns, SubscriptionKind.Pattern), options, cancellationToken);

    /// <summary>
    /// Subscribes to a sharded channel (Redis 7+) and returns once the server has acknowledged.
    /// Redis: SSUBSCRIBE.
    /// </summary>
    public ValueTask<RespireSubscription> SubscribeShardedAsync(string channel, CancellationToken cancellationToken = default)
        => SubscribeAsync(SubscriptionChannel(channel).WithKind(SubscriptionKind.Sharded), cancellationToken);

    /// <summary>Subscribes to one sharded channel with per-subscription buffer settings. Redis: SSUBSCRIBE.</summary>
    public ValueTask<RespireSubscription> SubscribeShardedAsync(
        string channel, RespireSubscriptionOptions options, CancellationToken cancellationToken)
        => SubscribeAsync(SubscriptionChannel(channel).WithKind(SubscriptionKind.Sharded), options, cancellationToken);

    /// <inheritdoc cref="SubscribeShardedAsync(string, CancellationToken)"/>
    public ValueTask<RespireSubscription> SubscribeShardedAsync(params ReadOnlySpan<string> channels)
        => SubscribeShardedAsync(channels, CancellationToken.None);

    /// <inheritdoc cref="SubscribeShardedAsync(string, CancellationToken)"/>
    public ValueTask<RespireSubscription> SubscribeShardedAsync(
        ReadOnlySpan<string> channels, CancellationToken cancellationToken)
        => SubscribeCoreAsync(SubscriptionKind.Sharded, MapChannels(channels, SubscriptionKind.Sharded), default, cancellationToken);

    /// <summary>Subscribes to sharded channels with per-subscription buffer settings. Redis: SSUBSCRIBE.</summary>
    public ValueTask<RespireSubscription> SubscribeShardedAsync(
        ReadOnlySpan<string> channels,
        RespireSubscriptionOptions options,
        CancellationToken cancellationToken)
        => SubscribeCoreAsync(SubscriptionKind.Sharded, MapChannels(channels, SubscriptionKind.Sharded), options, cancellationToken);

    private ValueTask<RespireSubscription> SubscribeCoreAsync(
        SubscriptionKind kind,
        RespireChannel[] names,
        RespireSubscriptionOptions options,
        CancellationToken cancellationToken)
    {
        // One activation owns admission, control replies, redirects and rollback. Background
        // recovery never borrows this lease after the subscription has been returned.
        var observation = RespireTelemetry.ErrorObservation.Rent(force: true);
        try
        {
            if (_pubSubPrefix is not null)
                for (var i = 0; i < names.Length; i++) names[i] = ResolveChannel(names[i]);
            return RespireTelemetry.ObserveFinalError(
                _core.Hub.SubscribeAsync(kind, names, options, cancellationToken, observation), observation);
        }
        catch (Exception error)
        {
            observation.Final(error);
            observation.Dispose();
            throw;
        }
    }

    /// <summary>Publishes channel bytes using this view's explicit pub/sub prefix; sharded metadata selects SPUBLISH. Patterns cannot be published.</summary>
    public ValueTask<long> PublishAsync(RespireChannel channel, RespireValue message, CancellationToken cancellationToken = default)
    {
        if (channel.IsNotification)
            throw new ArgumentException("Notification descriptors are server-owned and cannot be published.", nameof(channel));
        if (channel.Kind == SubscriptionKind.Pattern)
        {
            throw new ArgumentException("A pattern cannot be published; use a literal channel.", nameof(channel));
        }
        return channel.Kind == SubscriptionKind.Sharded
            ? PublishShardedAsync(channel, message, cancellationToken)
            : IntegerAsync("PUBLISH", new Cmd2(Verbs.Publish, ResolveChannel(channel).AsValue(), message), cancellationToken);
    }

    /// <summary>Publishes with SPUBLISH using this view's explicit pub/sub prefix.</summary>
    public ValueTask<long> PublishShardedAsync(RespireChannel channel, RespireValue message, CancellationToken cancellationToken = default)
    {
        if (channel.IsNotification)
            throw new ArgumentException("Notification descriptors are server-owned and cannot be published.", nameof(channel));
        if (channel.Kind == SubscriptionKind.Pattern)
        {
            throw new ArgumentException("A pattern cannot be published; use a sharded channel.", nameof(channel));
        }
        return IntegerAsync("SPUBLISH", new Cmd2(Verbs.SPublish, ResolveChannel(channel).AsValue(), message), cancellationToken);
    }

    /// <summary>Subscribes using the channel's explicit literal, pattern, or sharded kind.</summary>
    public ValueTask<RespireSubscription> SubscribeAsync(RespireChannel channel, CancellationToken cancellationToken = default)
        => SubscribeCoreAsync(channel.Kind, [channel], default, cancellationToken);

    /// <summary>Subscribes using explicit channel metadata and per-subscription buffer settings.</summary>
    public ValueTask<RespireSubscription> SubscribeAsync(
        RespireChannel channel, RespireSubscriptionOptions options, CancellationToken cancellationToken)
        => SubscribeCoreAsync(channel.Kind, [channel], options, cancellationToken);

    /// <summary>Subscribes to owned binary channels. All targets must have the same kind.</summary>
    public ValueTask<RespireSubscription> SubscribeAsync(ReadOnlySpan<RespireChannel> channels, CancellationToken cancellationToken)
        => SubscribeAsync(channels, default, cancellationToken);

    /// <summary>Subscribes to same-kind binary channels with per-subscription buffer settings.</summary>
    public ValueTask<RespireSubscription> SubscribeAsync(
        ReadOnlySpan<RespireChannel> channels, RespireSubscriptionOptions options, CancellationToken cancellationToken)
    {
        var kind = channels.IsEmpty ? SubscriptionKind.Channel : channels[0].Kind;
        foreach (var channel in channels)
        {
            if (channel.Kind != kind)
            {
                var error = new ArgumentException("All targets in one subscription must have the same kind.", nameof(channels));
                RecordSubscriptionPreflightFailure(error);
                throw error;
            }
        }
        return SubscribeCoreAsync(kind, channels.ToArray(), options, cancellationToken);
    }

    /// <summary>Subscribes to raw bytes using the pattern command family.</summary>
    public ValueTask<RespireSubscription> SubscribePatternAsync(RespireChannel channel, CancellationToken cancellationToken = default)
        => SubscribeAsync(SubscriptionChannel(channel, SubscriptionKind.Pattern), cancellationToken);

    /// <summary>Subscribes to raw bytes with per-subscription buffer settings.</summary>
    public ValueTask<RespireSubscription> SubscribePatternAsync(
        RespireChannel channel, RespireSubscriptionOptions options, CancellationToken cancellationToken)
        => SubscribeAsync(SubscriptionChannel(channel, SubscriptionKind.Pattern), options, cancellationToken);

    /// <summary>Subscribes to binary targets using the pattern command family.</summary>
    public ValueTask<RespireSubscription> SubscribePatternAsync(ReadOnlySpan<RespireChannel> channels, CancellationToken cancellationToken)
        => SubscribePatternAsync(channels, default, cancellationToken);

    /// <summary>Subscribes to binary targets with per-subscription buffer settings.</summary>
    public ValueTask<RespireSubscription> SubscribePatternAsync(
        ReadOnlySpan<RespireChannel> channels, RespireSubscriptionOptions options, CancellationToken cancellationToken)
        => SubscribeCoreAsync(SubscriptionKind.Pattern, MapChannels(channels, SubscriptionKind.Pattern), options, cancellationToken);

    /// <summary>Subscribes to raw bytes using the sharded command family.</summary>
    public ValueTask<RespireSubscription> SubscribeShardedAsync(RespireChannel channel, CancellationToken cancellationToken = default)
        => SubscribeAsync(SubscriptionChannel(channel, SubscriptionKind.Sharded), cancellationToken);

    /// <summary>Subscribes to raw bytes with per-subscription buffer settings.</summary>
    public ValueTask<RespireSubscription> SubscribeShardedAsync(
        RespireChannel channel, RespireSubscriptionOptions options, CancellationToken cancellationToken)
        => SubscribeAsync(SubscriptionChannel(channel, SubscriptionKind.Sharded), options, cancellationToken);

    /// <summary>Subscribes to binary targets using the sharded command family.</summary>
    public ValueTask<RespireSubscription> SubscribeShardedAsync(ReadOnlySpan<RespireChannel> channels, CancellationToken cancellationToken)
        => SubscribeShardedAsync(channels, default, cancellationToken);

    /// <summary>Subscribes to binary targets with per-subscription buffer settings.</summary>
    public ValueTask<RespireSubscription> SubscribeShardedAsync(
        ReadOnlySpan<RespireChannel> channels, RespireSubscriptionOptions options, CancellationToken cancellationToken)
        => SubscribeCoreAsync(SubscriptionKind.Sharded, MapChannels(channels, SubscriptionKind.Sharded), options, cancellationToken);

    // Channel mapping and validation run before SubscribeCoreAsync starts the activation owner.
    // Report their caller-visible failures here; the activation never sees these inputs.
    private static void RecordSubscriptionPreflightFailure(Exception error)
        => RespireTelemetry.RecordError(error, internallyHandled: false);

    private static RespireChannel SubscriptionChannel(string name)
    {
        try { return new RespireChannel(name); }
        catch (Exception error) { RecordSubscriptionPreflightFailure(error); throw; }
    }

    private static RespireChannel SubscriptionChannel(RespireChannel channel, SubscriptionKind kind)
    {
        try { return channel.WithKind(kind); }
        catch (Exception error) { RecordSubscriptionPreflightFailure(error); throw; }
    }

    private static RespireChannel[] MapChannels(ReadOnlySpan<RespireChannel> channels, SubscriptionKind kind)
    {
        try
        {
            var names = channels.ToArray();
            for (var i = 0; i < names.Length; i++)
            {
                names[i] = names[i].WithKind(kind);
            }
            return names;
        }
        catch (Exception error) { RecordSubscriptionPreflightFailure(error); throw; }
    }

    private static RespireChannel[] MapChannels(ReadOnlySpan<string> names, SubscriptionKind kind)
    {
        try
        {
            var channels = new RespireChannel[names.Length];
            for (var i = 0; i < names.Length; i++)
            {
                channels[i] = new RespireChannel(names[i]).WithKind(kind);
            }
            return channels;
        }
        catch (Exception error) { RecordSubscriptionPreflightFailure(error); throw; }
    }

    // Batches and transactions

    /// <summary>
    /// Starts an explicit pipeline: queue commands, then <see cref="RespireBatch.ExecuteAsync"/>
    /// flushes them together. Queued results are unreadable until the batch is sent — awaiting
    /// early throws instead of deadlocking. Always use a <c>using</c> declaration: disposal faults
    /// unsent pendings and preserves their results and errors after execution.
    /// </summary>
    public RespireBatch CreateBatch() => new(this);

    /// <summary>
    /// Starts a MULTI/EXEC transaction. Queue commands, then
    /// <see cref="RespireTransaction.CommitAsync"/>. Always commit or dispose the transaction.
    /// </summary>
    public RespireTransaction CreateTransaction() => new(this);

    /// <summary>
    /// Starts a transaction that WATCHes keys first: if any watched key changes before commit,
    /// <see cref="RespireWatchedTransaction.CommitAsync"/> returns false. Runs on a dedicated
    /// connection for correct WATCH isolation; always commit or dispose the transaction.
    /// For read-modify-write loops, prefer a Lua script (<see cref="Scripts"/>) — one round
    /// trip, no retry loop.
    /// </summary>
    /// <remarks>
    /// In an optimistic retry loop, read watched values through this client after creating the
    /// transaction, then queue writes on the transaction. Transaction reads are deferred and
    /// cannot be inspected before commit. A failed commit ends that attempt, so create a new
    /// watched transaction and re-read the values before retrying. In Cluster mode, all watched and
    /// queued keys must share one effective hash slot. A cluster rejection throws
    /// <see cref="RespireTransactionRetryException"/>; restart WATCH and re-read inputs before retrying.
    /// </remarks>
    /// <example>
    /// <code>
    /// var committed = false;
    /// for (var attempt = 0; attempt &lt; 5 &amp;&amp; !committed; attempt++)
    /// {
    ///     await using var transaction = await redis.CreateTransactionAsync(["balance"], ct);
    ///     var current = await redis.GetAsync&lt;long&gt;("balance", ct);
    ///     transaction.Set("balance", current - 100);
    ///     committed = await transaction.CommitAsync(ct);
    /// }
    /// if (!committed)
    /// {
    ///     throw new InvalidOperationException("Balance changed too often; retry later.");
    /// }
    /// </code>
    /// </example>
    public ValueTask<RespireWatchedTransaction> CreateTransactionAsync(
        RespireKey[] watchKeys, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(watchKeys);
        return CreateTransactionAsync(watchKeys.AsSpan(), cancellationToken);
    }

    /// <inheritdoc cref="CreateTransactionAsync(RespireKey[], CancellationToken)"/>
    public ValueTask<RespireWatchedTransaction> CreateTransactionAsync(params ReadOnlySpan<RespireKey> watchKeys)
        => CreateTransactionAsync(watchKeys, CancellationToken.None);

    /// <inheritdoc cref="CreateTransactionAsync(RespireKey[], CancellationToken)"/>
    public ValueTask<RespireWatchedTransaction> CreateTransactionAsync(
        ReadOnlySpan<RespireKey> watchKeys, CancellationToken cancellationToken)
    {
        if (watchKeys.Length == 0)
        {
            return new ValueTask<RespireWatchedTransaction>(new RespireWatchedTransaction(this));
        }

        var observation = RespireTelemetry.ErrorObservation.Rent(force: true);
        try
        {
            // Resolve and own keys before the first await so routing and WATCH use the same bytes.
            var keys = MapKeys(watchKeys);
            int? slot = null;
            for (var i = 0; i < keys.Length; i++)
            {
                keys[i] = keys[i].Snapshot();
                if (_core.Cluster is not null && keys[i].TryGetClusterSlot(out var keySlot))
                    RespireTransactionBase.ValidateClusterSlot(keySlot, ref slot);
            }
            // The asynchronous setup takes ownership only after preflight succeeds.
            return CreateWatchedTransactionAsync(keys, slot, cancellationToken, observation);
        }
        catch (Exception error)
        {
            observation.Final(error);
            observation.Dispose();
            throw;
        }
    }

    private async ValueTask<RespireWatchedTransaction> CreateWatchedTransactionAsync(
        RespireValue[] watchKeys, int? slot, CancellationToken cancellationToken,
        RespireTelemetry.ErrorObservation observation)
    {
        using var ownedObservation = observation;
        try
        {
            ObjectDisposedException.ThrowIf(_core.Disposed, this);
            var cluster = _core.Cluster;
            var pool = cluster is null ? await _core.GetDedicatedPoolAsync(cancellationToken).ConfigureAwait(false)
                : await cluster.GetDedicatedPoolAsync(slot, cancellationToken, discovery: null).ConfigureAwait(false);
            // The owning pool must follow the lease through commit/disposal, even if topology changes.
            RespireConnection connection;
            if (cluster is null)
                (pool, connection) = await _core.RentDedicatedConnectionAsync(pool, cancellationToken).ConfigureAwait(false);
            else
                (pool, connection) = await cluster.RentDedicatedConnectionAsync(pool, slot, cancellationToken, discovery: null).ConfigureAwait(false);
            try
            {
                var command = new CmdN(Verbs.Watch, watchKeys);
                using var reply = await SendOnConnectionAsync("WATCH", connection,
                    new ProtocolCommand<CmdN>(command), cancellationToken, observation: observation).ConfigureAwait(false);
                if (_core.ClientCache is { } cache)
                {
                    // Tracking pushes use other sockets and may lag writes processed before WATCH.
                    // Also fence reads already in flight so they cannot restore a pre-WATCH value.
                    foreach (var key in watchKeys) cache.Invalidate(key.AsKey());
                }
                return new RespireWatchedTransaction(this, connection, pool, slot);
            }
            catch (Exception error)
            {
                // Cancellation and I/O failures can leave WATCH state or unread replies on the lease.
                try
                {
                    await pool.DiscardAsync(connection).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // The pool reports cleanup failures. Preserve the original WATCH failure.
                }
                if (cluster is not null && error is RespireServerException rejection
                    && ClusterRouter.CanRecover(rejection, slot))
                {
                    cluster.LearnWatchedRoute(rejection, connection, slot);
                    throw new RespireTransactionRetryException(rejection);
                }
                throw;
            }
        }
        catch (Exception error)
        {
            observation.Final(error);
            throw;
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_ownsCore)
        {
            await _core.DisposeAsync().ConfigureAwait(false);
        }
    }

    // Internal machinery shared by facets, batches, and transactions.

    internal ClientCore Core => _core;

    internal KeyPrefix? KeyPrefix => _keyPrefix;
    internal KeyPrefix? EncodedKeyPrefix => _keyPrefix;
    internal ReadOnlySpan<byte> KeyPrefixBytes => _keyPrefix?.Bytes;

    /// <summary>Shares routing and encoding, but snapshots prefixed binary keys as batch facets resolve them.</summary>
    internal RespireClient ForDeferredBatch()
    {
        if (_snapshotPrefixedBinaryKeys) return this;
        var cached = Volatile.Read(ref _deferredBatchClient);
        if (cached is not null) return cached;
        var created = new RespireClient(_core, _keyPrefix, ownsCore: false, readFrom: _readFrom,
            bypassClientCache: _bypassClientCache, snapshotPrefixedBinaryKeys: true, pubSubPrefix: _pubSubPrefix);
        return Interlocked.CompareExchange(ref _deferredBatchClient, created, null) ?? created;
    }

    /// <inheritdoc/>
    public RespireKey ResolveKey(RespireKey key)
    {
        if (_keyPrefix is not null) return key.Prepend(_keyPrefix, _snapshotPrefixedBinaryKeys);
        return _snapshotPrefixedBinaryKeys ? key.SnapshotIfPrefixed() : key;
    }

    /// <summary>Resolves a user key to a command argument, applying this view's key prefix.</summary>
    internal RespireValue Key(in RespireKey key)
    {
        if (_keyPrefix is not null) return key.PrependAsValue(_keyPrefix, _snapshotPrefixedBinaryKeys);
        return _snapshotPrefixedBinaryKeys ? key.SnapshotIfPrefixed().AsValue() : key.AsValue();
    }

    internal RespireValue[] MapKeys(ReadOnlySpan<RespireKey> keys)
    {
        if (keys.Length == 0)
        {
            return [];
        }

        var mapped = new RespireValue[keys.Length];
        for (var i = 0; i < keys.Length; i++)
        {
            mapped[i] = Key(in keys[i]);
        }

        return mapped;
    }

    internal static RespireValue[] MapValues(ReadOnlySpan<RespireValue> values) => values.ToArray();

    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    internal RespireValue Serialize<T>(T value)
    {
        // Passing an unconstrained T to the object-based guard boxes values on .NET 8.
        if (value is null) throw new ArgumentNullException(nameof(value));
        if (typeof(T) == typeof(string))
        {
            return (string)(object)value;
        }

        if (typeof(T) == typeof(byte[]))
        {
            return (byte[])(object)value;
        }

        if (typeof(T) == typeof(char))
        {
            return (char)(object)value;
        }

        // Pass payload types straight through: the typed overloads sit next to RespireValue ones,
        // so an argument that is already a command argument must not be run through the serializer.
        if (typeof(T) == typeof(RespireValue))
        {
            return (RespireValue)(object)value;
        }

        if (PrimitiveCodec.TrySerialize(value, out var primitive))
        {
            return primitive;
        }

        var buffer = s_serializationBuffer ?? new PooledByteBufferWriter();
        s_serializationBuffer = null;
        try
        {
            _core.Options.Serializer.Serialize(buffer, value);
            // Command arguments can outlive this call (admission waits, retries, batches).
            // Only scratch storage is reusable; the returned payload must remain owned.
            return buffer.WrittenSpan.ToArray();
        }
        finally
        {
            // Reset clears and returns the entire rental, including uncommitted serializer writes.
            buffer.Reset();
            if (s_serializationBuffer is null) s_serializationBuffer = buffer;
            else buffer.Dispose();
        }
    }

    internal RespireResult CreateResult(in RespValue value)
        => new(in value, _core.Options.Serializer);

    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    internal RespireValue SerializeRawCompatible<T>(T value)
    {
        if (value is null && (typeof(T) == typeof(string) || typeof(T) == typeof(byte[])))
        {
            return RespireValue.Null;
        }

        if (typeof(T) == typeof(ReadOnlyMemory<byte>))
        {
            return (ReadOnlyMemory<byte>)(object)value!;
        }

        if (typeof(T) == typeof(float) && !float.IsFinite((float)(object)value!))
        {
            return (float)(object)value!;
        }

        if (typeof(T) == typeof(double) && !double.IsFinite((double)(object)value!))
        {
            return (double)(object)value!;
        }

        return Serialize(value);
    }

    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    internal RespireValue SerializeCollectionMember<T>(T value)
        => value is bool boolean ? boolean : SerializeRawCompatible(value);

    /// <summary>
    /// Reads a value the caller does not own, keeping "reply was null" distinct from a
    /// deserialized <c>default(T)</c>.
    /// </summary>
    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    internal RespireGet<T> TryDeserializeBorrowed<T>(in RespValue value)
        => value.IsNull ? default : new RespireGet<T>(true, DeserializeBorrowed<T>(in value)!);

    /// <summary>Reads a value the caller does not own (e.g. a transaction-array element).</summary>
    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    internal T? DeserializeBorrowed<T>(in RespValue value)
    {
        if (value.IsNull)
        {
            return default;
        }

        if (typeof(T) == typeof(string))
        {
            return (T)(object)value.AsString();
        }

        if (typeof(T) == typeof(byte[]))
        {
            return (T)(object)value.AsSpan().ToArray();
        }

        if (PrimitiveCodec.TryDeserialize<T>(in value, out var primitive))
        {
            return primitive;
        }

        return _core.Options.Serializer.Deserialize<T>(value.AsSpan());
    }

    private ClientSideCacheCoordinator? GetReadCache
        => _readFrom != RespireReadFrom.Primary && s_getIsReadOnly ? null : ReadCache;

    internal ValueTask<TResult> CachedGetAsync<TResult>(
        RespireKey resolvedKey,
        CancellationToken cancellationToken,
        ResponseConverter<RespireClient, TResult> converter,
        bool observeErrors = true, RespireTelemetry.ErrorObservation observation = default)
    {
        var cache = GetReadCache;
        var command = new Cmd1(Verbs.Get, resolvedKey.AsValue());
        if (cache is null)
        {
            return ConvertCachedResponseAsync("GET", command, cancellationToken, this, converter, observeErrors, observation);
        }

        try
        {
            var generation = _core.Sentinel?.Current;
            if (cache.TryGet(in resolvedKey, out var cached) && IsCacheGenerationCurrent(generation))
                return new ValueTask<TResult>(converter(this, in cached));

            if (observeErrors) observation = RespireTelemetry.ErrorObservation.Rent(force: true);
            var response = GetAndCacheAsync(resolvedKey, cache, cancellationToken, converter, observation);
            return observeErrors ? RespireTelemetry.ObserveFinalError(response, observation) : response;
        }
        catch (Exception error)
        {
            if (observeErrors)
            {
                if (observation.IsEmpty) RespireTelemetry.RecordError(error, internallyHandled: false);
                else { observation.Final(error); observation.Dispose(); }
            }
            throw;
        }
    }

    /// <summary>
    /// GET decoded as a string. Uncached reads take <see cref="StringOrNullAsync{TCommand}"/>,
    /// which decodes small bulk replies straight from the receive buffer.
    /// </summary>
    internal ValueTask<string?> CachedGetStringAsync(RespireKey resolvedKey, CancellationToken cancellationToken)
    {
        var cache = GetReadCache;
        if (cache is null)
            return StringOrNullAsync("GET", new Cmd1(Verbs.Get, resolvedKey.AsValue()), cancellationToken);

        RespireTelemetry.ErrorObservation observation = default;
        try
        {
            var generation = _core.Sentinel?.Current;
            if (cache.TryGetString(in resolvedKey, out var cached) && IsCacheGenerationCurrent(generation))
                return new ValueTask<string?>(cached);

            observation = RespireTelemetry.ErrorObservation.Rent(force: true);
            var response = cache.CoalesceConcurrentMisses
                ? GetSharedCacheReadAsync(resolvedKey, cache, cancellationToken, 0,
                    static (int _, in ClientSideCacheCoordinator.GetReadResult result) => result.GetString(),
                    decodeString: true, observation: observation)
                : FetchGetCacheReadAsync(resolvedKey, cache, cancellationToken, 0,
                    static (int _, in ClientSideCacheCoordinator.GetReadResult result) => result.GetString(),
                    decodeString: true, observation: observation);
            return RespireTelemetry.ObserveFinalError(response, observation);
        }
        catch (Exception error)
        {
            if (observation.IsEmpty) RespireTelemetry.RecordError(error, internallyHandled: false);
            else { observation.Final(error); observation.Dispose(); }
            throw;
        }
    }

    internal ValueTask<byte[]?> CachedGetBytesAsync(RespireKey resolvedKey, CancellationToken cancellationToken)
        => GetReadCache is null
            ? BytesOrNullAsync("GET", new Cmd1(Verbs.Get, resolvedKey.AsValue()), cancellationToken)
            : CachedGetAsync(resolvedKey, cancellationToken,
                static (RespireClient _, in RespValue value) => ResponseReader.BytesOrNull(in value));

    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    internal ValueTask<T?> CachedDeserializeAsync<T>(RespireKey resolvedKey, CancellationToken cancellationToken)
        => typeof(T) == typeof(byte[])
            ? CastBytesAsync<T>(CachedGetBytesAsync(resolvedKey, cancellationToken))
            : CachedGetAsync(resolvedKey, cancellationToken,
                static (RespireClient client, in RespValue value) => client.DeserializeBorrowed<T>(in value));

#if NET
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
#endif
    private static async ValueTask<T?> CastBytesAsync<T>(ValueTask<byte[]?> response)
        => (T?)(object?)await response.ConfigureAwait(false);

    /// <summary>Resolves and validates the complete MGET key set before combining cached and server values.</summary>
    internal ValueTask<TResult[]> CachedGetManyAsync<TResult>(
        ReadOnlySpan<RespireKey> keys,
        CancellationToken cancellationToken,
        ResponseConverter<RespireClient, TResult> converter,
        bool keysResolved = false,
        bool observeErrors = true, RespireTelemetry.ErrorObservation observation = default)
    {
        var cache = _readFrom == RespireReadFrom.Primary ? ReadCache : null;
        try
        {
            if (observeErrors) observation = RespireTelemetry.ErrorObservation.Rent(force: true);
            ValueTask<TResult[]> response;
            if (keys.Length == 0)
            {
                response = ConvertCachedResponseAsync(
                    "MGET",
                    new CmdN(Verbs.MGet, MapKeys(keys)),
                    cancellationToken,
                    (Client: this, Converter: converter),
                    static ((RespireClient Client, ResponseConverter<RespireClient, TResult> Converter) state, in RespValue response) =>
                    {
                        var values = response.AsArray();
                        var result = new TResult[values.Length];
                        for (var i = 0; i < values.Length; i++)
                        {
                            result[i] = state.Converter(state.Client, in values[i]);
                        }

                        return result;
                    }, observeErrors: false, observation: observation);
            }

            else if (cache is null)
            {
                var arguments = new RespireValue[keys.Length];
                int? clusterSlot = null;
                for (var i = 0; i < keys.Length; i++)
                {
                    var resolvedKey = keysResolved ? keys[i] : ResolveKey(keys[i]);
                    arguments[i] = resolvedKey.AsValue();
                    ValidateMGetClusterSlot(in resolvedKey, ref clusterSlot);
                }

                response = ConvertCachedResponseAsync(
                    "MGET",
                    new MGetCommand(arguments, clusterSlot),
                    cancellationToken,
                    (Client: this, Converter: converter),
                    static ((RespireClient Client, ResponseConverter<RespireClient, TResult> Converter) state, in RespValue response) =>
                    {
                        var values = response.AsArray();
                        var converted = new TResult[values.Length];
                        for (var i = 0; i < values.Length; i++)
                        {
                            converted[i] = state.Converter(state.Client, in values[i]);
                        }

                        return converted;
                    }, observeErrors: false, observation: observation);
            }
            else
            {
                response = CachedGetManyCoreAsync(keys, cancellationToken, converter, keysResolved, cache, observation);
            }
            return observeErrors ? RespireTelemetry.ObserveFinalError(response, observation) : response;
        }
        catch (Exception error)
        {
            if (observeErrors)
            {
                if (observation.IsEmpty) RespireTelemetry.RecordError(error, internallyHandled: false);
                else { observation.Final(error); observation.Dispose(); }
            }
            throw;
        }
    }

    private ValueTask<TResult[]> CachedGetManyCoreAsync<TResult>(
        ReadOnlySpan<RespireKey> keys, CancellationToken cancellationToken,
        ResponseConverter<RespireClient, TResult> converter, bool keysResolved,
        ClientSideCacheCoordinator cache, RespireTelemetry.ErrorObservation observation = default)
    {
        var result = new TResult[keys.Length];
        RespireKey[]? missingKeys = null;
        int[]? missingIndexes = null;
        var missingCount = 0;
        var cachedCount = 0;
        int? cachedClusterSlot = null;
        SentinelRouter.Generation? generation = _core.Sentinel?.Current;
        for (var i = 0; i < keys.Length; i++)
        {
            var resolvedKey = (keysResolved ? keys[i] : ResolveKey(keys[i])).Snapshot();
            ValidateMGetClusterSlot(in resolvedKey, ref cachedClusterSlot);
            if (cache.TryGet(in resolvedKey, out var cached))
            {
                cachedCount++;
                result[i] = converter(this, in cached);
            }
            else
            {
                if (missingKeys is null)
                {
                    missingKeys = new RespireKey[keys.Length];
                    missingIndexes = new int[keys.Length];
                }

                missingKeys[missingCount] = resolvedKey;
                missingIndexes![missingCount++] = i;
            }
        }

        // All cached elements must belong to the same live Sentinel generation. If it
        // retired during lookup/conversion, discard the entire mixed result and read again.
        if (!IsCacheGenerationCurrent(generation))
        {
            missingKeys ??= new RespireKey[keys.Length];
            missingIndexes ??= new int[keys.Length];
            missingCount = keys.Length;
            for (var i = 0; i < keys.Length; i++)
            {
                missingKeys[i] = keysResolved ? keys[i] : ResolveKey(keys[i]);
                missingIndexes[i] = i;
            }
            cachedCount = 0;
            generation = _core.Sentinel?.Current;
        }

        RespireKey[]? allKeys = null;
        if (missingCount != 0 && cachedCount != 0)
        {
            allKeys = new RespireKey[keys.Length];
            for (var i = 0; i < keys.Length; i++)
                allKeys[i] = (keysResolved ? keys[i] : ResolveKey(keys[i])).Snapshot();
        }

        return missingCount == 0
            ? new ValueTask<TResult[]>(result)
            : GetManyAndCacheAsync(
                missingKeys!,
                result,
                missingIndexes!,
                missingCount,
                cache,
                cancellationToken,
                converter,
                allKeys,
                generation,
                cachedClusterSlot, observation);
    }

    // Retirement is read last: Invalidate retires the still-current generation before it
    // flushes the cache, so a retirement racing the identity/connectivity reads is still seen.
    private bool IsCacheGenerationCurrent(SentinelRouter.Generation? generation)
        => _core.Sentinel is null || generation is not null
            && ReferenceEquals(generation, _core.Sentinel.Current) && generation.Multiplexer.IsConnected
            && !generation.IsRetired;

    private void ValidateMGetClusterSlot(in RespireKey key, ref int? clusterSlot)
    {
        if (_core.Cluster is null)
        {
            return;
        }

        var keySlot = key.ClusterSlot;
        if (clusterSlot is { } expectedSlot && keySlot != expectedSlot)
        {
            throw new RespireServerException(
                "CROSSSLOT Keys in request don't hash to the same slot", "MGET");
        }

        clusterSlot = keySlot;
    }

    // Ordinary misses convert and release their wire response inside one pooled async state.
    // Only shared producers transfer that response to the coordinator's ownership boundary.
    private ValueTask<TResult> GetAndCacheAsync<TResult>(
        RespireKey resolvedKey,
        ClientSideCacheCoordinator cache,
        CancellationToken cancellationToken,
        ResponseConverter<RespireClient, TResult> converter, RespireTelemetry.ErrorObservation observation = default)
        => cache.CoalesceConcurrentMisses
            ? GetSharedAndCacheAsync(resolvedKey, cache, cancellationToken, converter, observation)
            : FetchGetAndCacheAsync(resolvedKey, cache, cancellationToken, converter, observation: observation);

    private ValueTask<TResult> GetSharedAndCacheAsync<TResult>(
        RespireKey resolvedKey, ClientSideCacheCoordinator cache, CancellationToken cancellationToken,
        ResponseConverter<RespireClient, TResult> converter, RespireTelemetry.ErrorObservation observation = default)
        => GetSharedCacheReadAsync(resolvedKey, cache, cancellationToken, (Client: this, Converter: converter),
            static ((RespireClient Client, ResponseConverter<RespireClient, TResult> Converter) state,
                in ClientSideCacheCoordinator.GetReadResult result) => state.Converter(state.Client, in result.Response),
            observation: observation);

    private ValueTask<TResult> GetSharedCacheReadAsync<TState, TResult>(
        RespireKey resolvedKey, ClientSideCacheCoordinator cache, CancellationToken cancellationToken,
        TState state, ClientSideCacheCoordinator.GetReadConverter<TState, TResult> converter, bool decodeString = false,
        RespireTelemetry.ErrorObservation observation = default)
    {
        var identity = new ClientCacheCommandKey("GET", resolvedKey.AsValue());
        return cache.CoalesceGetReadAsync(
            identity, (Client: this, Key: resolvedKey, Cache: cache, DecodeString: decodeString),
            static (state, token, producerObservation) => state.Client.FetchGetCacheReadAsync(state.Key, state.Cache, token, 0,
                static (int _, in ClientSideCacheCoordinator.GetReadResult value) => value,
                transferResponse: true, decodeString: state.DecodeString, observation: producerObservation),
            state, converter, cancellationToken, observation);
    }

    private ValueTask<TResult> FetchGetAndCacheAsync<TResult>(
        RespireKey resolvedKey, ClientSideCacheCoordinator cache, CancellationToken cancellationToken,
        ResponseConverter<RespireClient, TResult> converter, bool transferResponse = false,
        RespireTelemetry.ErrorObservation observation = default)
        => FetchGetCacheReadAsync(resolvedKey, cache, cancellationToken, (Client: this, Converter: converter),
            static ((RespireClient Client, ResponseConverter<RespireClient, TResult> Converter) state,
                in ClientSideCacheCoordinator.GetReadResult result) => state.Converter(state.Client, in result.Response),
            transferResponse, observation: observation);

    private ValueTask<TResult> FetchGetCacheReadAsync<TState, TResult>(
        RespireKey resolvedKey, ClientSideCacheCoordinator cache, CancellationToken cancellationToken,
        TState state, ClientSideCacheCoordinator.GetReadConverter<TState, TResult> converter, bool transferResponse = false, bool decodeString = false,
        RespireTelemetry.ErrorObservation observation = default)
    {
        // Another producer may publish after the public lookup. Keep this second lookup
        // outside the async frame: Debug builds allocate a reference-type state machine.
        try
        {
            var generation = _core.Sentinel?.Current;
            if (cache.CoalesceConcurrentMisses && cache.TryPeekRead(in resolvedKey, out var cached)
                && IsCacheGenerationCurrent(generation))
            {
                // Start restores ExecutionContext/SynchronizationContext changes made by the converter.
                // An explicit struct keeps that async-start contract without Debug's generated heap frame.
                var conversion = new CachedReadConversion<TState, TResult>(state, cached, converter);
                var builder = AsyncTaskMethodBuilder.Create();
                builder.Start(ref conversion);
                return new ValueTask<TResult>(conversion.Result);
            }
        }
        catch (Exception error)
        {
            // Preserve the former async failure, including cancellation with an uncanceled token.
            return ReadySendFailureAsync<TResult>(error);
        }

        return FetchGetCacheReadCoreAsync(resolvedKey, cache, cancellationToken, state, converter,
            transferResponse, decodeString, observation);
    }

    private struct CachedReadConversion<TState, TResult>(TState state,
        ClientSideCacheCoordinator.GetReadResult cached,
        ClientSideCacheCoordinator.GetReadConverter<TState, TResult> converter) : IAsyncStateMachine
    {
        internal TResult Result = default!;
        public void MoveNext() => Result = converter(state, in cached);
        // Start invokes MoveNext synchronously. This converter has no suspension point,
        // so it never needs a boxed state machine or a SetStateMachine callback.
        void IAsyncStateMachine.SetStateMachine(IAsyncStateMachine stateMachine)
            => throw new InvalidOperationException("Cached conversion cannot suspend.");
    }

#if NET
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
#endif
    private async ValueTask<TResult> FetchGetCacheReadCoreAsync<TState, TResult>(
        RespireKey resolvedKey, ClientSideCacheCoordinator cache, CancellationToken cancellationToken,
        TState state, ClientSideCacheCoordinator.GetReadConverter<TState, TResult> converter,
        bool transferResponse, bool decodeString, RespireTelemetry.ErrorObservation observation)
    {
        var token = cache.BeginRead(in resolvedKey);
        var command = new Cmd1(Verbs.Get, token.State.Key.AsValue());
        var response = default(RespValue);
        var released = false;
        var returned = false;
        var redirect = _core.Cluster is null ? null : new CacheGetRedirect(cache, token);
        Action? onRedirect = redirect is null ? null : redirect.Rebase;

        try
        {
            response = await SendTrackedAsync(
                "GET", command, cancellationToken, onRedirect, token.State.CanCache, observation).ConfigureAwait(false);
            var decodedText = decodeString ? ResponseReader.StringOrNull(in response) : null;
            released = true;
            var currentToken = redirect is null ? token : redirect.Token;
            var entry = cache.CompleteRead(in currentToken, in response, allowInsert: true, decodedText);
            var readResult = new ClientSideCacheCoordinator.GetReadResult(
                response, currentToken.State.Key, currentToken.Store, entry, decodedText);
            var result = converter(state, in readResult);
            returned = transferResponse;
            return result;
        }
        finally
        {
            if (!released)
            {
                var currentToken = redirect is null ? token : redirect.Token;
                cache.CompleteRead(in currentToken, in response, allowInsert: false);
            }
            if (!returned) response.Dispose();
        }
    }

    private sealed class CacheGetRedirect(ClientSideCacheCoordinator cache, ClientSideCacheCoordinator.ReadToken token)
    {
        internal ClientSideCacheCoordinator.ReadToken Token = token;
        internal void Rebase() => Token = cache.RebaseRead(in Token);
    }

    /// <summary>Fills missing result positions and retries if the Sentinel cache generation changes.</summary>
    private async ValueTask<TResult[]> GetManyAndCacheAsync<TResult>(
        RespireKey[] missingKeys,
        TResult[] result,
        int[] missingIndexes,
        int missingCount,
        ClientSideCacheCoordinator cache,
        CancellationToken cancellationToken,
        ResponseConverter<RespireClient, TResult> converter,
        RespireKey[]? allKeys,
        SentinelRouter.Generation? generation,
        int? clusterSlot, RespireTelemetry.ErrorObservation observation = default)
    {
        var fetchedResult = await (cache.CoalesceConcurrentMisses
            ? GetManySharedAndCacheAsync(missingKeys, result, missingIndexes, missingCount, cache, cancellationToken, converter, clusterSlot, observation)
            : FetchManyAndCacheAsync(missingKeys, missingCount, cache, cancellationToken,
                (Client: this, Result: result, Indexes: missingIndexes, Converter: converter),
                static ((RespireClient Client, TResult[] Result, int[] Indexes, ResponseConverter<RespireClient, TResult> Converter) state, in RespValue response) =>
                {
                    var values = response.AsArray();
                    for (var index = 0; index < values.Length; index++)
                        state.Result[state.Indexes[index]] = state.Converter(state.Client, in values[index]);
                    return state.Result;
                }, clusterSlot: clusterSlot, observation: observation)).ConfigureAwait(false);

        return allKeys is null || IsCacheGenerationCurrent(generation)
            ? fetchedResult
            : await FetchManyForCurrentGenerationAsync(allKeys, cache, cancellationToken, converter, observation).ConfigureAwait(false);
    }

    private async ValueTask<TResult[]> FetchManyForCurrentGenerationAsync<TResult>(
        RespireKey[] keys, ClientSideCacheCoordinator cache, CancellationToken cancellationToken,
        ResponseConverter<RespireClient, TResult> converter, RespireTelemetry.ErrorObservation observation = default)
    {
        while (true)
        {
            var generation = _core.Sentinel?.Current;
            var result = new TResult[keys.Length];
            var indexes = new int[keys.Length];
            for (var index = 0; index < indexes.Length; index++) indexes[index] = index;
            var fetchedResult = await (cache.CoalesceConcurrentMisses
                ? GetManySharedAndCacheAsync(keys, result, indexes, keys.Length, cache, cancellationToken, converter, observation: observation)
                : FetchManyAndCacheAsync(keys, keys.Length, cache, cancellationToken,
                    (Client: this, Result: result, Indexes: indexes, Converter: converter),
                    static ((RespireClient Client, TResult[] Result, int[] Indexes,
                        ResponseConverter<RespireClient, TResult> Converter) state, in RespValue response) =>
                    {
                        var values = response.AsArray();
                        if (values.Length != state.Indexes.Length)
                            throw new RespireProtocolException(
                                $"MGET returned {values.Length} values for {state.Indexes.Length} keys.");
                        for (var index = 0; index < values.Length; index++)
                            state.Result[state.Indexes[index]] = state.Converter(state.Client, in values[index]);
                        return state.Result;
                    }, observation: observation)).ConfigureAwait(false);
            if (IsCacheGenerationCurrent(generation)) return fetchedResult;
        }
    }

    /// <summary>Coalesces matching MGET misses while preserving the original validated slot and each caller's result array.</summary>
#if NET
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
#endif
    private async ValueTask<TResult[]> GetManySharedAndCacheAsync<TResult>(
        RespireKey[] missingKeys, TResult[] result, int[] missingIndexes, int missingCount,
        ClientSideCacheCoordinator cache, CancellationToken cancellationToken,
        ResponseConverter<RespireClient, TResult> converter, int? clusterSlot = null,
        RespireTelemetry.ErrorObservation observation = default)
    {
        var identityArguments = new RespireValue[missingCount];
        for (var i = 0; i < missingCount; i++) identityArguments[i] = missingKeys[i].AsValue();
        var identity = new ClientCacheCommandKey("MGET", identityArguments);
        using var response = await cache.CoalesceReadAsync(
            identity, (Client: this, Keys: missingKeys, Count: missingCount, Cache: cache, Slot: clusterSlot),
            static (state, token, producerObservation) => state.Client.FetchManyAndCacheAsync(
                state.Keys, state.Count, state.Cache, token, state.Client,
                static (RespireClient _, in RespValue value) => value, transferResponse: true, clusterSlot: state.Slot,
                observation: producerObservation), cancellationToken, observation).ConfigureAwait(false);
        var values = response.AsArray();
        if (values.Length != missingCount)
            throw new RespireProtocolException($"MGET returned {values.Length} values for {missingCount} keys.");
        for (var i = 0; i < missingCount; i++)
            result[missingIndexes[i]] = converter(this, in values[i]);
        return result;
    }

    /// <summary>Tracks cache misses, sends them using their validated slot, and converts or transfers the owned reply.</summary>
#if NET
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
#endif
    private async ValueTask<TResult> FetchManyAndCacheAsync<TState, TResult>(
        RespireKey[] missingKeys, int missingCount, ClientSideCacheCoordinator cache,
        CancellationToken cancellationToken, TState state, ResponseConverter<TState, TResult> converter,
        bool transferResponse = false, int? clusterSlot = null, RespireTelemetry.ErrorObservation observation = default)
    {
        var generation = _core.Sentinel?.Current;
        if (cache.CoalesceConcurrentMisses && cache.TryPeek(in missingKeys[0], out var firstCached))
        {
            var cachedValues = new RespValue[missingCount];
            cachedValues[0] = firstCached;
            var allCached = true;
            for (var i = 1; i < missingCount; i++)
            {
                if (!cache.TryPeek(in missingKeys[i], out cachedValues[i]))
                {
                    allCached = false;
                    break;
                }
            }
            if (allCached && IsCacheGenerationCurrent(generation))
            {
                var cached = RespValue.Array(cachedValues);
                return converter(state, in cached);
            }
        }
        var arguments = new RespireValue[missingCount];
        var tokens = new ClientSideCacheCoordinator.ReadToken[missingCount];
        for (var i = 0; i < missingCount; i++)
        {
            ref readonly var key = ref missingKeys[i];
            tokens[i] = cache.BeginRead(in key);
            arguments[i] = tokens[i].State.Key.AsValue();
        }

        var response = default(RespValue);
        var completed = 0;
        var returned = false;
        Action? onRedirect = null;
        if (_core.Cluster is not null)
        {
            onRedirect = () =>
            {
                for (var i = 0; i < tokens.Length; i++)
                {
                    tokens[i] = cache.RebaseRead(in tokens[i]);
                }
            };
        }

        try
        {
            response = await SendTrackedAsync(
                "MGET", new MGetCommand(arguments, clusterSlot), cancellationToken, onRedirect,
                track: Array.Exists(tokens, static token => token.State.CanCache), observation: observation).ConfigureAwait(false);
            var values = response.AsArray();
            if (values.Length != missingCount)
            {
                throw new RespireProtocolException(
                    $"MGET returned {values.Length} values for {missingCount} keys.");
            }

            while (completed < missingCount)
            {
                var index = completed;
                ref readonly var value = ref values[index];
                cache.CompleteRead(in tokens[index], in value, allowInsert: true);
                completed++;
            }

            var result = converter(state, in response);
            returned = transferResponse;
            return result;
        }
        finally
        {
            if (!returned) response.Dispose();
            for (; completed < missingCount; completed++)
            {
                cache.CompleteRead(in tokens[completed], in response, allowInsert: false);
            }
        }
    }

#if NET
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
#endif
    private async ValueTask<RespValue> SendTrackedAsync<TCommand>(
        string operation,
        TCommand command,
        CancellationToken cancellationToken,
        Action? onRedirect = null,
        bool track = true, RespireTelemetry.ErrorObservation observation = default)
        where TCommand : struct, IRespCommand
    {
        var core = _core;
        ObjectDisposedException.ThrowIf(core.Disposed, this);
        if (core.Cluster is { } cluster)
        {
            return await SendTrackedClusterAsync(
                operation, cluster, command, cancellationToken, onRedirect, track, observation).ConfigureAwait(false);
        }

        RespValue response;
        for (var attempt = 0; ; attempt++)
        {
            await core.EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var connection = GetCircuitAwareConnection(core.Multiplexer, cancellationToken);
                response = await SendTrackedOnConnectionAsync(
                    operation, connection, command, cancellationToken, sendAsking: false, track, observation).ConfigureAwait(false);
                break;
            }
            catch (RespireConnectionRetiredException error) when (core.Sentinel is not null && attempt == 0
                && !core.Disposed && !cancellationToken.IsCancellationRequested)
            {
                // No bytes were admitted. Rediscover once and retry the entire tracking prelude
                // and read together. Sentinel retirement already fences the original cache
                // token; another flush would discard unrelated replacement-generation reads.
                if (observation.IsEmpty) RespireTelemetry.RecordError(error, internallyHandled: true, attempt);
                else observation.Handled(error);
            }
        }
        if (response.IsError)
        {
            var error = ResponseReader.ServerError(in response, operation);
            response.Dispose();
            throw error;
        }

        return response;
    }

#if NET
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
#endif
    private async ValueTask<RespValue> SendTrackedClusterAsync<TCommand>(
        string operation,
        ClusterRouter cluster,
        TCommand command,
        CancellationToken cancellationToken,
        Action? onRedirect,
        bool track, RespireTelemetry.ErrorObservation observation = default)
        where TCommand : struct, IRespCommand
    {
        var slot = command.TryGetClusterSlot(out var commandSlot) ? commandSlot : (int?)null;
        var readFrom = GetReadFromForCommand(in command);
        var preferredZone = ReadFallbackPolicy.UsesAvailabilityZone(readFrom) ? _core.Options.ClientAvailabilityZone : null;
        ClusterRouter.DiscoveryRound? discovery = null;
        // Keep the budget across sends; successful selection does not imply the final route accepts the command.
        var discoveryPending = false;
        try
        {
            var connection = await cluster.GetReadConnectionAsync(slot, readFrom, cancellationToken, observation: observation).ConfigureAwait(false);
            var sendAsking = false;
            for (var attempt = 0; ; attempt++)
            {
                RespValue response;
                try
                {
                    response = await SendTrackedOnConnectionAsync(
                        operation, connection, command, cancellationToken, sendAsking, track, observation).ConfigureAwait(false);
                }
                catch (RespireConnectionRetiredException retirement) when (cluster.CanRetryRetirement(attempt, cancellationToken))
                {
                    cluster.RecordRejection(ref discovery, connection, retirement);
                    if (observation.IsEmpty) RespireTelemetry.RecordError(retirement, internallyHandled: true, attempt);
                    else observation.Handled(retirement);
                    if (!ReadFallbackPolicy.IsReplicaConnection(connection))
                        _core.ClientCache?.FlushForContinuityLoss();
                    discoveryPending = true;
                    connection = sendAsking
                        ? await cluster.GetReplacementConnectionAsync(connection, slot, null, cancellationToken, discovery, preferredZone).ConfigureAwait(false)
                        : await cluster.GetReadReplacementConnectionAsync(slot, readFrom, cancellationToken, discovery, preferredZone, observation).ConfigureAwait(false);
                    discoveryPending = false;
                    onRedirect?.Invoke();
                    continue;
                }

                if (!response.IsError)
                {
                    return response;
                }

                var error = ResponseReader.ServerError(in response, operation);
                response.Dispose();
                if (attempt >= ClusterRouter.RedirectLimit || !ClusterRouter.CanRecover(error, slot))
                {
                    throw error;
                }

                if (ReadFallbackPolicy.IsStrictReplicaAsk(error, readFrom)) throw ReadFallbackPolicy.CreateStrictReplicaAskException(error, slot);
                _core.ClientCache?.FlushForContinuityLoss();
                cluster.RecordRejection(ref discovery, connection, error);
                if (observation.IsEmpty) RespireTelemetry.RecordError(error, internallyHandled: true, attempt);
                else observation.Handled(error);
                discoveryPending = true;
                connection = await cluster.GetRedirectConnectionAsync(error, connection, cancellationToken, slot, discovery, preferredZone)
                    .ConfigureAwait(false);
                if (error.Code != RespireErrorCodes.Ask && readFrom != RespireReadFrom.Primary)
                {
                    connection = await cluster.SelectReadConnectionAfterRedirectAsync(
                        connection, slot, readFrom, cancellationToken, discovery, preferredZone, observation).ConfigureAwait(false);
                }
                discoveryPending = false;
                sendAsking = error.Code == RespireErrorCodes.Ask;
                onRedirect?.Invoke();
            }
        }
        catch (Exception error)
        {
            discovery?.RecordCommandFailure(error, discoveryPending, slot, callerToken: cancellationToken);
            throw;
        }
        finally { discovery?.Finish(); }
    }

    private ValueTask<RespValue> SendTrackedOnConnectionAsync<TCommand>(
        string operation,
        RespireConnection connection,
        TCommand command,
        CancellationToken cancellationToken,
        bool sendAsking,
        bool track, RespireTelemetry.ErrorObservation observation = default)
        where TCommand : struct, IRespCommand
        => RespireTelemetry.IsOperationEnabled(operation)
            ? SendTrackedOnConnectionInstrumentedAsync(
                operation, connection, command, cancellationToken, sendAsking, track, observation)
            : SendTrackedOnConnectionCoreAsync(
                operation, connection, command, cancellationToken, sendAsking, track, observation);

    private ValueTask<RespValue> SendTrackedOnConnectionCoreAsync<TCommand>(
        string operation,
        RespireConnection connection,
        TCommand command,
        CancellationToken cancellationToken,
        bool sendAsking,
        bool track, RespireTelemetry.ErrorObservation observation = default)
        where TCommand : struct, IRespCommand
        => _core.Circuits is not null && !_snapshotPrefixedBinaryKeys
            ? SendCircuitTrackedAsync(operation, connection, command, cancellationToken, sendAsking, track, observation)
            : SendTrackedOnConnectionUncheckedAsync(operation, connection, command, cancellationToken, sendAsking, track, observation: observation);

    private ValueTask<RespValue> SendTrackedOnConnectionUncheckedAsync<TCommand>(
        string operation, RespireConnection connection, TCommand command, CancellationToken cancellationToken,
        bool sendAsking, bool track, CommandDeadline commandDeadline = default, bool pinToConnection = false, RespireTelemetry.ErrorObservation observation = default) where TCommand : struct, IRespCommand
    {
        var preferredZone = GetTransportReadZone(in command);
        // Broadcast registrations come from the handshake; uncached OPTIN reads stay untracked.
        var optIn = track && !_broadcastTracking;
        if (sendAsking)
        {
            if (optIn)
            {
                return ClusterRouter.SendTrackedAskingAsync(
                    connection, in command, cancellationToken, operation, preferredZone, observation);
            }
            return ClusterRouter.SendAskingAsync(
                connection, in command, cancellationToken, operation, preferredZone: preferredZone, observation: observation);
        }

        if (!optIn)
            return connection.SendAsync(command, cancellationToken, commandName: operation, commandDeadline: commandDeadline, preferredZone: preferredZone, pinToConnection: pinToConnection, observation: observation);
        var caching = new ClientCachingCommand();
        return connection.SendValidatedPrefixedAsync(
            in caching, in command, cancellationToken, operation, preferredZone, pinToConnection, commandDeadline, observation: observation);
    }

#if NET
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
#endif
    private async ValueTask<RespValue> SendTrackedOnConnectionInstrumentedAsync<TCommand>(
        string operation,
        RespireConnection connection,
        TCommand command,
        CancellationToken cancellationToken,
        bool sendAsking,
        bool track, RespireTelemetry.ErrorObservation observation = default)
        where TCommand : struct, IRespCommand
    {
        var telemetry = RespireTelemetry.StartOperation(
            operation, connection, _core.Options.Database);
        try
        {
            var response = await SendTrackedOnConnectionCoreAsync(
                operation, connection, command, cancellationToken, sendAsking, track, observation).ConfigureAwait(false);
            var error = response.IsError ? ResponseReader.ServerError(in response, operation) : null;
            telemetry.Complete(
                operation,
                connection.Host,
                connection.Port,
                _core.Options.Database,
                error: error,
                connection: connection);
            return response;
        }
        catch (Exception ex)
        {
            telemetry.Complete(
                operation,
                connection.Host,
                connection.Port,
                _core.Options.Database,
                error: ex,
                connection: connection);
            throw;
        }
    }

    /// <summary>The central send path: lazy connect, optional command timeout, telemetry, error translation.</summary>
    internal ValueTask<RespValue> SendAsync<TCommand>(
        string operation,
        in TCommand command,
        CancellationToken cancellationToken)
        where TCommand : struct, IRespCommand
        => SendAsync(operation, command, cancellationToken, RespireCommandFlags.None);

    private ValueTask<RespValue> SendCoreAsync<TCommand>(
        string operation, in TCommand command, CancellationToken cancellationToken,
        RespireCommandFlags flags, bool allowReadFrom, ReadAffinity? cursorAffinity,
        RespireTelemetry.ErrorObservation observation = default, bool observeErrors = true)
        where TCommand : struct, IRespCommand
    {
        var core = _core;
        ObjectDisposedException.ThrowIf(core.Disposed, this);
        if (command is IStreamingRespCommand)
        {
            // Large uploads must not own a multiplexed connection's write path while the
            // source or socket stalls. Keep their normal command deadline on the dedicated lease.
            return SendStreamedUploadAsync(operation, command, cancellationToken,
                noRedirect: command is not IReplayableStreamingRespCommand { CanReplay: true }, observation: observation);
        }

        var cache = core.ClientCache;
        var readKind = allowReadFrom && _readFrom != RespireReadFrom.Primary
            ? command.ReadKind : ReadCommandKind.None;
        var routeRead = readKind != ReadCommandKind.None;
        if (!routeRead && flags == RespireCommandFlags.None
            && !_bypassClientCache
            && cache is not null
            && ClientSideCacheCoordinator.TryCreateQuery(operation, in command, out var query))
        {
            // Shared GET/MGET producers must populate the same per-key representation,
            // regardless of whether a typed or raw caller wins the miss.
            if (cache.CoalesceConcurrentMisses)
            {
                if (operation == "GET" && query.Query.ArgumentCount == 1)
                    return ObserveClusterError(CachedGetAsync(query.PrimaryKey, cancellationToken,
                        static (RespireClient _, in RespValue value) => value.ToOwned(), observeErrors: false,
                        observation: observation), observeErrors, observation);
                if (operation == "MGET" && query.Query.ArgumentCount > 0)
                    return ObserveClusterError(CachedRawGetManyAsync(query.Query, cancellationToken, observation), observeErrors, observation);
            }

            if (cache.ReuseHashFields && operation == "HMGET" && query.Query.ArgumentCount >= 2
                && cache.CanTrack(query.PrimaryKey))
                return ObserveClusterError(CachedHashGetManyAsync(cache, query, cancellationToken, observation), observeErrors, observation);
            var generation = core.Sentinel?.Current;
            if (cache.TryGet(in query, out var cached) && IsCacheGenerationCurrent(generation))
            {
                return new ValueTask<RespValue>(cached);
            }

            return ObserveClusterError(QueryAndCacheAsync(operation, command, cache, query, cancellationToken, observation), observeErrors, observation);
        }

        var mutationFence = cache is null ? default : cache.BeforeCommand(operation, in command);
        if (!mutationFence.IsRequired)
            return SendRoutedResponseAsync(operation, command, cancellationToken, flags, allowReadFrom, cursorAffinity, readKind,
                observation, observeErrors);
        try
        {
            var bound = new MutationCommand<TCommand>(command, mutationFence);
            return CompleteMutationAsync(
                SendRoutedResponseAsync(operation, bound, cancellationToken, flags, allowReadFrom, cursorAffinity, readKind,
                    observation, observeErrors),
                cache!, mutationFence);
        }
        catch
        {
            cache!.CompleteMutation(in mutationFence);
            throw;
        }
    }

    private ValueTask<RespValue> SendRoutedResponseAsync<TCommand>(string operation, TCommand command,
        CancellationToken cancellationToken, RespireCommandFlags flags, bool allowReadFrom,
        ReadAffinity? cursorAffinity, ReadCommandKind readKind,
        RespireTelemetry.ErrorObservation observation, bool observeErrors) where TCommand : struct, IRespCommand
    {
        var core = _core;
        if (readKind == ReadCommandKind.Read && core.HedgedReads is { } hedgeBudget
            && command is not IRespCommandWrapper && HedgedReadPolicy.IsEligible(operation)
            && (core.Cluster is null || command.TryGetClusterSlot(out _)))
        {
            return SendHedgedReadAsync(operation, command, hedgeBudget, flags, cancellationToken, observation, observeErrors);
        }
        else if (readKind != ReadCommandKind.None && core.Cluster is null)
        {
            return SendReadFromAsync(operation, command, readKind, cursorAffinity, cancellationToken, observation);
        }
        else if (core.Cluster is { } cluster)
        {
            return SendClusterAsync(
                operation,
                cluster,
                command,
                cancellationToken,
                noRedirect: HasFlag(flags, RespireCommandFlags.NoRedirect)
                    || command is IStreamingRespCommand
                        && command is not IReplayableStreamingRespCommand { CanReplay: true },
                allowReadFrom: allowReadFrom,
                cursorAffinity: cursorAffinity,
                observeErrors: observeErrors, observation: observation);
        }
        else if (core.TryGetReadyPrimaryMultiplexer(out var readyMultiplexer))
        {
            return SendOnReadyPrimaryAsync(operation, readyMultiplexer, command, cancellationToken, observation);
        }
        else
        {
            return SendAfterConnectAsync(operation, command, cancellationToken, observation);
        }
    }

#if NET
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
#endif
    // Callers consume the pooled result once by awaiting or forwarding it.
    private async ValueTask<RespValue> SendReadFromAsync<TCommand>(
        string operation, TCommand command, ReadCommandKind readKind, ReadAffinity? affinity,
        CancellationToken cancellationToken, RespireTelemetry.ErrorObservation observation = default)
        where TCommand : struct, IRespCommand
    {
        // Scan cursors are server-local, so successive pages must reach the server that issued them.
        // A raw command's own cursor argument says whether it starts a scan or continues one.
        var connection = readKind == ReadCommandKind.CursorRead
            ? await _core.ReadRouter.GetCursorConnectionAsync(_readFrom, affinity,
                affinity is null && CursorCommandMetadata.IsCursorContinuation(in command),
                cancellationToken).ConfigureAwait(false)
            : await _core.ReadRouter.GetConnectionAsync(_readFrom, cancellationToken).ConfigureAwait(false);
        return await SendOnConnectionAsync(operation, connection, command, cancellationToken, observation: observation).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends one page of a cursor enumeration. Under a replica read policy, every page of the
    /// enumeration owning <paramref name="affinity"/> reaches the server that issued its cursor.
    /// </summary>
    internal ValueTask<RespValue> SendCursorPageAsync<TCommand>(
        string operation, TCommand command, ReadAffinity affinity, CancellationToken cancellationToken)
        where TCommand : struct, IRespCommand
    {
        return _readFrom != RespireReadFrom.Primary
            && command.ReadKind == ReadCommandKind.CursorRead
            ? SendAsync(operation, command, cancellationToken, RespireCommandFlags.None,
                allowReadFrom: true, cursorAffinity: affinity)
            : SendAsync(operation, command, cancellationToken);
    }

    /// <summary>This client, or a view of it that sends every command to the primary.</summary>
    internal RespireClient PrimaryReadView
        => _readFrom == RespireReadFrom.Primary
            ? this
            : new RespireClient(_core, _keyPrefix, ownsCore: false, readFrom: RespireReadFrom.Primary,
                bypassClientCache: _bypassClientCache,
                snapshotPrefixedBinaryKeys: _snapshotPrefixedBinaryKeys, pubSubPrefix: _pubSubPrefix);

#if NET
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
#endif
    private async ValueTask<RespValue> CachedRawGetManyAsync(
        ClientCacheCommandKey query, CancellationToken cancellationToken, RespireTelemetry.ErrorObservation observation = default)
    {
        var keys = new RespireKey[query.ArgumentCount];
        for (var index = 0; index < keys.Length; index++) keys[index] = query.GetArgument(index).AsKey();
        var values = await CachedGetManyAsync(keys, cancellationToken,
            static (RespireClient _, in RespValue value) => value.ToOwned(), keysResolved: true,
            observeErrors: false, observation: observation).ConfigureAwait(false);
        return RespValue.Array(values);
    }

    // Standalone sends already have an outer observer. Cluster cache waiters and hedge
    // races own their final boundary separately from the producer/leg retry owners.
    private ValueTask<T> ObserveClusterError<T>(ValueTask<T> response, bool observeErrors = true,
        RespireTelemetry.ErrorObservation observation = default)
        => _core.Cluster is null || !observeErrors || !observation.IsEmpty ? response : RespireTelemetry.ObserveFinalError(response);

    private ValueTask<RespValue> QueryAndCacheAsync<TCommand>(
        string operation, TCommand command, ClientSideCacheCoordinator cache,
        ClientSideCacheCoordinator.QueryRequest request, CancellationToken cancellationToken,
        RespireTelemetry.ErrorObservation observation = default)
        where TCommand : struct, IRespCommand
        => !cache.CoalesceConcurrentMisses
            ? FetchQueryAndCacheAsync(operation, command, cache, request, cancellationToken, observation)
            : cache.CoalesceReadAsync(
                request.Query, (Client: this, Operation: operation, Command: command, Cache: cache, Request: request),
                static (state, token, producerObservation) => state.Client.FetchQueryAndCacheAsync(
                    state.Operation, state.Command, state.Cache, state.Request, token, producerObservation), cancellationToken, observation);

#if NET
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
#endif
    private async ValueTask<RespValue> FetchQueryAndCacheAsync<TCommand>(
        string operation,
        TCommand command,
        ClientSideCacheCoordinator cache,
        ClientSideCacheCoordinator.QueryRequest request,
        CancellationToken cancellationToken, RespireTelemetry.ErrorObservation observation = default)
        where TCommand : struct, IRespCommand
    {
        var generation = _core.Sentinel?.Current;
        if (cache.CoalesceConcurrentMisses && cache.TryPeek(in request, out var cached)
            && IsCacheGenerationCurrent(generation)) return cached;
        var snapshot = SnapshotCommand.Create(in command);
        var token = cache.BeginRead(operation, in request);
        var completed = false;
        var redirect = _core.Cluster is null ? null : new CacheQueryRedirect(cache, token);
        Action? onRedirect = redirect is null ? null : redirect.Rebase;

        var response = default(RespValue);
        try
        {
            response = await SendTrackedAsync(
                operation, snapshot, cancellationToken, onRedirect, token.CanCache, observation).ConfigureAwait(false);
            var currentToken = redirect is null ? token : redirect.Token;
            cache.CompleteRead(in currentToken, in response, allowInsert: true);
            completed = true;
            return response;
        }
        finally
        {
            if (!completed)
            {
                var currentToken = redirect is null ? token : redirect.Token;
                cache.CompleteRead(in currentToken, in response, allowInsert: false);
                response.Dispose();
            }
        }
    }

    private sealed class CacheQueryRedirect(ClientSideCacheCoordinator cache, ClientSideCacheCoordinator.QueryReadToken token)
    {
        internal ClientSideCacheCoordinator.QueryReadToken Token = token;
        internal void Rebase() => Token = cache.RebaseRead(in Token);
    }

#if NET
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
#endif
    private static async ValueTask<TResult> CompleteMutationAsync<TResult>(
        ValueTask<TResult> response,
        ClientSideCacheCoordinator cache,
        ClientSideCacheCoordinator.MutationFence fence)
    {
        var succeeded = false;
        try
        {
            var result = await response.ConfigureAwait(false);
            succeeded = true;
            return result;
        }
        finally
        {
            cache.CompleteMutation(in fence, succeeded);
        }
    }

#if NET
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
#endif
    private static async ValueTask CompleteMutationAsync(
        ValueTask response,
        ClientSideCacheCoordinator cache,
        ClientSideCacheCoordinator.MutationFence fence)
    {
        var succeeded = false;
        try
        {
            await response.ConfigureAwait(false);
            succeeded = true;
        }
        finally
        {
            cache.CompleteMutation(in fence, succeeded);
        }
    }

#if NET
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
#endif
    private async ValueTask<RespValue> SendAfterConnectAsync<TCommand>(
        string operation,
        TCommand command,
        CancellationToken cancellationToken, RespireTelemetry.ErrorObservation observation = default)
        where TCommand : struct, IRespCommand
    {
        var core = _core;
        await core.EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        var connection = GetCircuitAwareConnection(core.Multiplexer, cancellationToken);
        return await SendOnConnectionAsync(operation, connection, command, cancellationToken, observation: observation)
            .ConfigureAwait(false);
    }

#if NET
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
#endif
    private async ValueTask<RespValue> SendStoredProcedureAsync<TCommand>(
        string operation,
        TCommand command,
        CancellationToken cancellationToken,
        string storedProcedureName,
        RespireCommandFlags flags = RespireCommandFlags.None,
        bool allowReadFrom = true)
        where TCommand : struct, IRespCommand
    {
        var core = _core;
        // Raw procedures retain their owner through preflight, reroutes and cache cleanup,
        // including selection enabled while the command is pending.
        using var observation = RespireTelemetry.ErrorObservation.Rent(force: true);
        try
        {
            ObjectDisposedException.ThrowIf(core.Disposed, this);
            var cache = core.ClientCache;
            var mutationFence = cache is null ? default : cache.BeginUnknownMutation();
            try
            {
                if (core.Cluster is { } cluster)
                {
                    return await SendClusterAsync(
                            operation,
                            cluster,
                            new MutationCommand<TCommand>(command, mutationFence),
                            cancellationToken,
                            storedProcedureName,
                            HasFlag(flags, RespireCommandFlags.NoRedirect),
                            allowReadFrom: allowReadFrom, observeErrors: false, observation: observation)
                        .ConfigureAwait(false);
                }

                RespireConnection connection;
                if (allowReadFrom && command.ReadKind != ReadCommandKind.None && _readFrom != RespireReadFrom.Primary)
                {
                    connection = await core.ReadRouter.GetConnectionAsync(_readFrom, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await core.EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
                    connection = GetCircuitAwareConnection(core.Multiplexer, cancellationToken);
                }
                return await SendMutationOnConnectionAsync(
                        operation, connection, command, mutationFence, cancellationToken, storedProcedureName, observation: observation)
                    .ConfigureAwait(false);
            }
            finally
            {
                if (mutationFence.IsRequired) cache!.CompleteMutation(in mutationFence);
            }
        }
        catch (Exception error)
        {
            observation.Final(error);
            throw;
        }
    }

    internal ValueTask<RespValue> ResumeRetiredClusterSendAsync<TCommand>(
        string operation, TCommand command, RespireConnection source,
        RespireConnectionRetiredException error, RespireReadFrom readFrom, CancellationToken cancellationToken,
        Action<Exception>? onRetry = null, RespireTelemetry.ErrorObservation observation = default)
        where TCommand : struct, IRespCommand
        => SendClusterAsync(operation, _core.Cluster!, command, cancellationToken,
            initialConnection: source, firstAttempt: 1, initialRetirement: error, readFromOverride: readFrom,
            cursorReadFromOverride: EffectiveReadFrom, onRetry: onRetry, observation: observation);

    internal ValueTask<RespValue> ResumeRejectedClusterSendAsync<TCommand>(
        string operation, TCommand command, RespireConnection source,
        RespireServerException error, RespireReadFrom readFrom, CancellationToken cancellationToken,
        Action<Exception>? onRetry = null, RespireTelemetry.ErrorObservation observation = default)
        where TCommand : struct, IRespCommand
        => SendClusterAsync(operation, _core.Cluster!, command, cancellationToken,
            initialConnection: source, firstAttempt: 1, initialRejection: error, readFromOverride: readFrom,
            cursorReadFromOverride: EffectiveReadFrom, onRetry: onRetry, observation: observation);

#if NET
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
#endif
    private async ValueTask<RespValue> SendClusterAsync<TCommand>(
        string operation,
        ClusterRouter cluster,
        TCommand command,
        CancellationToken cancellationToken,
        string? storedProcedureName = null,
        bool noRedirect = false,
        RespireConnection? initialConnection = null,
        int firstAttempt = 0,
        RespireConnectionRetiredException? initialRetirement = null,
        RespireServerException? initialRejection = null,
        bool allowReadFrom = false,
        RespireReadFrom? readFromOverride = null,
        ReadAffinity? cursorAffinity = null,
        HedgeOriginalRoute? hedgeOriginalRoute = null,
        bool isHedge = false,
        RespireReadFrom? cursorReadFromOverride = null,
        bool suppressTelemetry = false,
        ClusterScriptTelemetry? scriptTelemetry = null,
        bool observeErrors = false,
        bool discardServerErrors = false,
        Action<Exception>? onRetry = null,
        RespireTelemetry.ErrorObservation observation = default)
        where TCommand : struct, IRespCommand
    {
        var slot = command.TryGetClusterSlot(out var commandSlot) ? commandSlot : (int?)null;
        var readFrom = readFromOverride ?? GetReadFromForCommand(in command, allowReadFrom);
        // A mixed batch routes retries to the primary but publishes the cursor under its configured policy.
        var cursorReadFrom = cursorReadFromOverride ?? readFrom;
        var errorAttempts = firstAttempt;
        var cursorContinuation = command.ReadKind == ReadCommandKind.CursorRead
            && (cursorAffinity?.IsPinned == true || CursorCommandMetadata.IsCursorContinuation(in command));
        var preferredZone = ReadFallbackPolicy.UsesAvailabilityZone(readFrom) ? _core.Options.ClientAvailabilityZone : null;
        ClusterRouter.DiscoveryRound? discovery = null;
        var discoveryPending = false;
        var ownsObservation = observeErrors && observation.IsEmpty;
        if (ownsObservation) observation = RespireTelemetry.ErrorObservation.Rent(force: true);
        try
        {
            var connection = initialConnection;
            if (connection is null && slot is { } cursorSlot && readFrom != RespireReadFrom.Primary
                && command.ReadKind == ReadCommandKind.CursorRead)
            {
                connection = await _core.ReadRouter.Cursors.GetClusterConnectionAsync(
                    cluster, cursorSlot, readFrom, cursorAffinity,
                    cursorAffinity is null && CursorCommandMetadata.IsCursorContinuation(in command),
                    cancellationToken, observation).ConfigureAwait(false);
            }
            connection ??= await cluster.GetReadConnectionAsync(slot, readFrom, cancellationToken, observation: observation).ConfigureAwait(false);
            if (initialRetirement is not null)
            {
                if (cursorContinuation) throw initialRetirement;
                cluster.RecordRejection(ref discovery, connection, initialRetirement);
                RecordRetryError(initialRetirement, Math.Max(0, errorAttempts - 1), scriptTelemetry, onRetry, observation);
                discoveryPending = true;
                connection = await cluster.GetReadReplacementConnectionAsync(slot, readFrom, cancellationToken, discovery, preferredZone, observation).ConfigureAwait(false);
                discoveryPending = false;
            }
            var fallback = new ReadFallbackPolicy.RoleFallback(readFrom);
            if (initialRejection is not null && !cursorContinuation
                && fallback.TrySwitch(initialRejection, slot,
                    ReadFallbackPolicy.IsReplicaConnection(connection)))
            {
                RecordRetryError(initialRejection, Math.Max(0, errorAttempts - 1), scriptTelemetry, onRetry, observation);
                connection = await cluster.GetOtherRoleReadConnectionAsync(
                    slot!.Value, fallback, cancellationToken, discovery, observation).ConfigureAwait(false);
                readFrom = fallback.RecoveryPolicy;
            }
            else if (initialRejection is not null)
            {
                if (cursorContinuation) throw initialRejection;
                if (ReadFallbackPolicy.IsStrictReplicaAsk(initialRejection, readFrom)) throw ReadFallbackPolicy.CreateStrictReplicaAskException(initialRejection, slot);
                _core.ClientCache?.FlushForContinuityLoss();
                command.ValidateAdmission();
                cluster.RecordRejection(ref discovery, connection, initialRejection);
                RecordRetryError(initialRejection, Math.Max(0, errorAttempts - 1), scriptTelemetry, onRetry, observation);
                discoveryPending = true;
                connection = await cluster.GetRedirectConnectionAsync(
                    initialRejection, connection, cancellationToken, slot, discovery, preferredZone).ConfigureAwait(false);
                if (initialRejection.Code != RespireErrorCodes.Ask && readFrom != RespireReadFrom.Primary)
                {
                    connection = await cluster.SelectReadConnectionAfterRedirectAsync(
                        connection, slot, readFrom, cancellationToken, discovery, preferredZone, observation).ConfigureAwait(false);
                }
                discoveryPending = false;
            }
            var commandDeadline = command is IStreamingRespCommand && _core.Options.CommandTimeout is { } streamTimeout
                ? CommandDeadline.After(Math.Max(1L, (long)streamTimeout.TotalMilliseconds))
                : CommandDeadline.None;
            var sendAsking = initialRejection?.Code == RespireErrorCodes.Ask;
            for (var attempt = firstAttempt; ; attempt++)
            {
                if (hedgeOriginalRoute is not null)
                {
                    if (!isHedge) hedgeOriginalRoute.Connection = connection;
                    else if (!hedgeOriginalRoute.CanHedge(connection))
                        throw new RespireConnectionException("The optional hedge no longer has a distinct original peer.");
                }
                try
                {
                    scriptTelemetry?.UseConnection(connection);
                    var result = await (suppressTelemetry
                        ? SendOnConnectionCoreAsync(operation, connection, command, cancellationToken, sendAsking,
                            commandDeadline, allowStreamingConnectionReroute: false, observation: observation)
                        : SendOnConnectionAsync(
                            operation, connection, command, cancellationToken, storedProcedureName, sendAsking,
                            commandDeadline, allowStreamingConnectionReroute: false, observation: observation))
                        .ConfigureAwait(false);
                    if (cursorAffinity is not null && slot is { } issuedCursorSlot
                        && connection.Multiplexer is { } cursorNode)
                    {
                        cursorAffinity.ClusterSlot = issuedCursorSlot;
                        cursorAffinity.ClusterNode = cursorNode;
                    }
                    else if (command.ReadKind == ReadCommandKind.CursorRead && slot is { } rawCursorSlot
                        && cursorReadFrom != RespireReadFrom.Primary && connection.Multiplexer is { } rawCursorNode)
                    {
                        _core.ReadRouter.Cursors.PinClusterShared(cursorReadFrom, rawCursorSlot, rawCursorNode);
                    }
                    return result;
                }
                // Retirement rejects a streamed SET before its header is written. Its source is
                // untouched, or its consumed first chunk was restored in front of the source, so
                // the command can move to the replacement connection without losing bytes.
                catch (RespireConnectionRetiredException retirement) when (!cursorContinuation
                    && cluster.CanRetryRetirement(attempt, cancellationToken))
                {
                    if (!isHedge && hedgeOriginalRoute is not null) hedgeOriginalRoute.Connection = null;
                    commandDeadline = connection.GetReroutedCommandDeadline(commandDeadline);
                    cluster.RecordRejection(ref discovery, connection, retirement);
                    RecordRetryError(retirement, errorAttempts++, scriptTelemetry, onRetry, observation);
                    if (!ReadFallbackPolicy.IsReplicaConnection(connection))
                        _core.ClientCache?.FlushForContinuityLoss();
                    discoveryPending = true;
                    connection = sendAsking
                        ? await cluster.GetReplacementConnectionAsync(connection, slot, null, cancellationToken, discovery, preferredZone).ConfigureAwait(false)
                        : await cluster.GetReadReplacementConnectionAsync(slot, readFrom, cancellationToken, discovery, preferredZone, observation).ConfigureAwait(false);
                    discoveryPending = false;
                }
                catch (RespireServerException error)
                    when (!cursorContinuation && !noRedirect && attempt < ClusterRouter.RedirectLimit
                        && ClusterRouter.CanRecover(error, slot))
                {
                    if (!isHedge && hedgeOriginalRoute is not null) hedgeOriginalRoute.Connection = null;
                    if (ReadFallbackPolicy.IsStrictReplicaAsk(error, readFrom)) throw ReadFallbackPolicy.CreateStrictReplicaAskException(error, slot);
                    // A redirect invalidates tracking continuity even when retry admission expires.
                    _core.ClientCache?.FlushForContinuityLoss();
                    // An accepted reply may outlive admission. Check before redirect discovery
                    // so a failed topology query cannot replace an expired retry budget.
                    command.ValidateAdmission();
                    // Learn the new owner before touching the caller-owned stream. A broken seek
                    // must not leave later commands pinned to the stale slot owner.
                    cluster.RecordRejection(ref discovery, connection, error);
                    RecordRetryError(error, errorAttempts++, scriptTelemetry, onRetry, observation);
                    discoveryPending = true;
                    connection = await cluster.GetRedirectConnectionAsync(error, connection, cancellationToken, slot, discovery, preferredZone)
                        .ConfigureAwait(false);
                    if (error.Code != RespireErrorCodes.Ask && readFrom != RespireReadFrom.Primary)
                    {
                        connection = await cluster.SelectReadConnectionAfterRedirectAsync(
                            connection, slot, readFrom, cancellationToken, discovery, preferredZone, observation).ConfigureAwait(false);
                    }
                    discoveryPending = false;

                    if (command is IReplayableStreamingRespCommand replayable)
                    {
                        try
                        {
                            replayable.ResetSourceForReplay();
                        }
                        catch (Exception resetError) when (resetError is not OutOfMemoryException
                            and not AccessViolationException and not StackOverflowException)
                        {
                            // The caller-owned source can fail its seek with its own exception
                            // type. Preserve the redirect when any non-fatal reset failure makes
                            // retry unsafe; routing already learned the new owner.
                            RethrowPreservingStackTrace(error);
                        }
                    }
                    sendAsking = error.Code == RespireErrorCodes.Ask;
                }
                catch (RespireServerException error) when (!sendAsking
                    && !cursorContinuation
                    && fallback.TrySwitch(error, slot, ReadFallbackPolicy.IsReplicaConnection(connection)))
                {
                    if (!isHedge && hedgeOriginalRoute is not null) hedgeOriginalRoute.Connection = null;
                    // Reads are idempotent; retry once on the other server role. NoRedirect only
                    // surfaces MOVED and ASK, so it does not suppress this availability retry.
                    RecordRetryError(error, errorAttempts++, scriptTelemetry, onRetry, observation);
                    connection = await cluster.GetOtherRoleReadConnectionAsync(
                        slot!.Value, fallback, cancellationToken, discovery, observation).ConfigureAwait(false);
                    readFrom = fallback.RecoveryPolicy;
                }
            }
        }
        catch (Exception error)
        {
            discovery?.RecordCommandFailure(error, discoveryPending, slot, noRedirect, cancellationToken);
            if (discardServerErrors && RespireException.GetDefinitiveServerError(error) is { } serverError
                && !ClusterRouter.CanRecover(serverError, slot))
            {
                if (observation.IsEmpty) RespireTelemetry.RecordError(error, internallyHandled: true, errorAttempts);
                else observation.Handled(error);
                return default;
            }
            if (observeErrors) observation.Final(error);
            throw;
        }
        finally
        {
            if (ownsObservation) observation.Dispose();
            if (!isHedge) hedgeOriginalRoute?.Complete();
            discovery?.Finish();
        }
    }

    [DoesNotReturn]
    private static void RethrowPreservingStackTrace(Exception error)
        => ExceptionDispatchInfo.Capture(error).Throw();

#if NET
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
#endif
    private async ValueTask<RespValue> SendClusterWideAsync<TCommand>(
        string operation,
        ClusterRouter cluster,
        TCommand command,
        CancellationToken cancellationToken)
        where TCommand : struct, IRespCommand
    {
        ClusterRouter.DiscoveryRound? discovery = null;
        var discoveryPending = false;
        using var observation = RespireTelemetry.ErrorObservation.Rent(force: true);
        try
        {
            var cache = _core.ClientCache;
            var mutationFence = cache is null ? default : cache.BeginUnknownMutation();
            try
            {
                var connections = await cluster.GetMasterConnectionsAsync(cancellationToken, discovery: null).ConfigureAwait(false);
                var retainedReply = default(RespValue);
                var hasRetainedReply = false;
                try
                {
                    foreach (var connection in connections)
                    {
                        var target = connection;
                        RespValue reply;
                        for (var attempt = 0; ; attempt++)
                        {
                            try
                            {
                                reply = await SendMutationOnConnectionAsync(operation, target, command, mutationFence, cancellationToken,
                                        observation: observation)
                                    .ConfigureAwait(false);
                                break;
                            }
                            catch (RespireConnectionRetiredException retirement) when (cluster.CanRetryRetirement(attempt, cancellationToken))
                            {
                                cluster.RecordRejection(ref discovery, target, retirement);
                                // Keep this snapshot endpoint; earlier targets have already accepted the mutation.
                                observation.Handled(retirement);
                                discoveryPending = true;
                                target = await cluster.GetReplacementConnectionAsync(connection, null, null, cancellationToken, discovery)
                                    .ConfigureAwait(false);
                                discoveryPending = false;
                            }
                        }
                        if (!hasRetainedReply)
                        {
                            retainedReply = reply;
                            hasRetainedReply = true;
                        }
                        else
                        {
                            reply.Dispose();
                        }
                    }

                    return hasRetainedReply
                        ? retainedReply
                        : throw new RespireConnectionException(
                            $"{operation} did not reach any Redis Cluster master.");
                }
                catch
                {
                    if (hasRetainedReply)
                    {
                        retainedReply.Dispose();
                    }

                    throw;
                }
            }
            finally
            {
                if (mutationFence.IsRequired)
                {
                    cache!.CompleteMutation(in mutationFence);
                }
            }
        }
        catch (Exception error)
        {
            discovery?.RecordCommandFailure(error, discoveryPending, cancellationToken);
            observation.Final(error);
            throw;
        }
        finally { discovery?.Finish(); }
    }

    private ValueTask SendFireAndForgetAsync<TCommand>(
        string operation,
        TCommand command,
        CancellationToken cancellationToken,
        string? storedProcedureName = null,
        bool allowReadFrom = false)
        where TCommand : struct, IRespCommand
    {
        RespireTelemetry.ErrorObservation observation = default;
        try
        {
            // A listener or group can be enabled while submission waits for capacity.
            if (_core.Cluster is null) observation = RespireTelemetry.ErrorObservation.Rent(force: true);
            var response = SendFireAndForgetCoreAsync(operation, command, cancellationToken,
                storedProcedureName, allowReadFrom, observation);
            return observation.IsEmpty ? response : RespireTelemetry.ObserveFinalError(response, observation);
        }
        catch (Exception error)
        {
            if (observation.IsEmpty) RespireTelemetry.RecordError(error, internallyHandled: false);
            else { observation.Final(error); observation.Dispose(); }
            throw;
        }
    }

    private ValueTask SendFireAndForgetCoreAsync<TCommand>(
        string operation, TCommand command, CancellationToken cancellationToken,
        string? storedProcedureName, bool allowReadFrom, RespireTelemetry.ErrorObservation observation)
        where TCommand : struct, IRespCommand
    {
        var core = _core;
        ObjectDisposedException.ThrowIf(core.Disposed, this);
        if (allowReadFrom && _readFrom != RespireReadFrom.Primary
            && command.ReadKind != ReadCommandKind.None)
        {
            // A read discards its reply here, but the policy still decides which server serves it.
            if (core.Cluster is { } readCluster)
            {
                return SendFireAndForgetClusterAsync(
                    operation, readCluster, command, cancellationToken, storedProcedureName, allowReadFrom: true);
            }

            return SendFireAndForgetViaReadRouterAsync(operation, command, cancellationToken, storedProcedureName, observation);
        }
        var cache = core.ClientCache;
        var mutationFence = cache is null ? default : cache.BeforeCommand(operation, in command);
        if (mutationFence.IsRequired)
        {
            return SendFencedFireAndForgetAsync(
                operation,
                command,
                cancellationToken,
                storedProcedureName,
                cache!,
                mutationFence, observation);
        }

        if (core.Cluster is { } cluster)
        {
            return SendFireAndForgetClusterAsync(
                operation, cluster, command, cancellationToken, storedProcedureName);
        }

        if (core.Sentinel is not null || !core.Multiplexer.IsInitialized)
        {
            return SendFireAndForgetAfterConnectAsync(
                operation, command, cancellationToken, storedProcedureName, observation);
        }

        var connection = GetCircuitAwareConnection(core.Multiplexer, cancellationToken);
        return SendFireAndForgetOnConnectionAsync(
            operation, connection, command, cancellationToken, storedProcedureName, observation);
    }

#if NET
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
#endif
    private async ValueTask SendFencedFireAndForgetAsync<TCommand>(
        string operation,
        TCommand command,
        CancellationToken cancellationToken,
        string? storedProcedureName,
        ClientSideCacheCoordinator cache,
        ClientSideCacheCoordinator.MutationFence mutationFence, RespireTelemetry.ErrorObservation observation)
        where TCommand : struct, IRespCommand
    {
        try
        {
            var core = _core;
            if (core.Cluster is { } cluster)
            {
                await SendFireAndForgetClusterAsync(
                        operation, cluster, new MutationCommand<TCommand>(command, mutationFence), cancellationToken, storedProcedureName)
                    .ConfigureAwait(false);
                return;
            }

            await core.EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
            var connection = GetCircuitAwareConnection(core.Multiplexer, cancellationToken);
            if (RespireCommand.MayCloseWithoutReply(operation))
            {
                await SendFireAndForgetOnConnectionAsync(
                        operation, connection, new MutationCommand<TCommand>(command, mutationFence), cancellationToken, storedProcedureName, observation)
                    .ConfigureAwait(false);
                return;
            }

            try
            {
                using var response = await SendMutationOnConnectionAsync(
                        operation, connection, command, mutationFence, cancellationToken, storedProcedureName, observation: observation)
                    .ConfigureAwait(false);
            }
            catch (Exception error) when (RespireException.GetDefinitiveServerError(error) is not null)
            {
                // Fire-and-forget discards definitive command errors, including classified
                // missing-engine replies. Cancellation and transport failures still escape.
                RespireTelemetry.RecordError(error, internallyHandled: true, observation.Attempts);
            }
        }
        finally
        {
            cache.CompleteMutation(in mutationFence);
        }
    }

    private ValueTask SendFireAndForgetOnConnectionAsync<TCommand>(
        string operation,
        RespireConnection connection,
        TCommand command,
        CancellationToken cancellationToken,
        string? storedProcedureName = null, RespireTelemetry.ErrorObservation observation = default)
        where TCommand : struct, IRespCommand
        => _core.Circuits is not null && !_snapshotPrefixedBinaryKeys
            ? SendCircuitFireAndForgetAsync(operation, connection, command, cancellationToken, storedProcedureName, observation)
            : SendFireAndForgetOnConnectionUncheckedAsync(operation, connection, command, cancellationToken, storedProcedureName, observation: observation);

    private ValueTask SendFireAndForgetOnConnectionUncheckedAsync<TCommand>(
        string operation, RespireConnection connection, TCommand command, CancellationToken cancellationToken,
        string? storedProcedureName, CommandDeadline capacityDeadline = default, bool pinToConnection = false, RespireTelemetry.ErrorObservation observation = default) where TCommand : struct, IRespCommand
        => RespireTelemetry.IsOperationEnabled(operation)
            ? SendFireAndForgetOnConnectionInstrumentedAsync(
                operation, connection, command, cancellationToken, storedProcedureName, capacityDeadline, pinToConnection, observation: observation)
            : connection.SendFireAndForgetAsync(in command, cancellationToken, operation,
                capacityDeadline: capacityDeadline, preferredZone: GetTransportReadZone(in command), pinToConnection: pinToConnection, observation: observation);

#if NET
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
#endif
    private async ValueTask SendFireAndForgetOnConnectionInstrumentedAsync<TCommand>(
        string operation,
        RespireConnection connection,
        TCommand command,
        CancellationToken cancellationToken,
        string? storedProcedureName, CommandDeadline capacityDeadline = default, bool pinToConnection = false, RespireTelemetry.ErrorObservation observation = default)
        where TCommand : struct, IRespCommand
    {
        var core = _core;
        var telemetry = RespireTelemetry.StartOperation(
            operation,
            connection,
            core.Options.Database,
            storedProcedureName: storedProcedureName);
        try
        {
            await connection.SendFireAndForgetAsync(in command, cancellationToken, operation,
                capacityDeadline: capacityDeadline, preferredZone: GetTransportReadZone(in command), pinToConnection: pinToConnection, observation: observation).ConfigureAwait(false);
            telemetry.Complete(
                operation,
                connection.Host,
                connection.Port,
                core.Options.Database,
                storedProcedureName,
                connection: connection);
        }
        catch (Exception ex)
        {
            telemetry.Complete(
                operation,
                connection.Host,
                connection.Port,
                core.Options.Database,
                storedProcedureName,
                error: ex,
                connection: connection);
            throw;
        }
    }

    private async ValueTask SendFireAndForgetViaReadRouterAsync<TCommand>(
        string operation,
        TCommand command,
        CancellationToken cancellationToken,
        string? storedProcedureName, RespireTelemetry.ErrorObservation observation)
        where TCommand : struct, IRespCommand
    {
        var connection = await _core.ReadRouter.GetConnectionAsync(_readFrom, cancellationToken).ConfigureAwait(false);
        await SendFireAndForgetOnConnectionAsync(
                operation, connection, command, cancellationToken, storedProcedureName, observation)
            .ConfigureAwait(false);
    }

    private async ValueTask SendFireAndForgetAfterConnectAsync<TCommand>(
        string operation,
        TCommand command,
        CancellationToken cancellationToken,
        string? storedProcedureName, RespireTelemetry.ErrorObservation observation)
        where TCommand : struct, IRespCommand
    {
        var core = _core;
        await core.EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        var connection = GetCircuitAwareConnection(core.Multiplexer, cancellationToken);
        await SendFireAndForgetOnConnectionAsync(
                operation, connection, command, cancellationToken, storedProcedureName, observation)
            .ConfigureAwait(false);
    }

#if NET
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
#endif
    private async ValueTask SendFireAndForgetClusterAsync<TCommand>(
        string operation,
        ClusterRouter cluster,
        TCommand command,
        CancellationToken cancellationToken,
        string? storedProcedureName,
        bool allowReadFrom = false)
        where TCommand : struct, IRespCommand
    {
        ClusterRouter.DiscoveryRound? discovery = null;
        var discoveryPending = false;
        var delegatedErrors = false;
        var mayCloseWithoutReply = RespireCommand.MayCloseWithoutReply(operation);
        using var observation = mayCloseWithoutReply ? RespireTelemetry.ErrorObservation.Rent(force: true) : default;
        try
        {
            if (mayCloseWithoutReply)
            {
                var slot = command.TryGetClusterSlot(out var commandSlot) ? commandSlot : (int?)null;
                var connection = await cluster.GetConnectionAsync(slot, cancellationToken, discovery: null).ConfigureAwait(false);
                for (var attempt = 0; ; attempt++)
                {
                    try
                    {
                        await SendFireAndForgetOnConnectionAsync(
                                operation, connection, command, cancellationToken, storedProcedureName, observation)
                            .ConfigureAwait(false);
                        return;
                    }
                    catch (RespireConnectionRetiredException retirement) when (cluster.CanRetryRetirement(attempt, cancellationToken))
                    {
                        cluster.RecordRejection(ref discovery, connection, retirement);
                        observation.Handled(retirement);
                        discoveryPending = true;
                        connection = await cluster.GetReplacementConnectionAsync(null, slot, null, cancellationToken, discovery)
                            .ConfigureAwait(false);
                        discoveryPending = false;
                    }
                }
            }

            // The shared retry owner knows the final attempt count and whether the reply
            // is intentionally discarded or is an exhausted routing failure.
            delegatedErrors = true;
            using var response = await SendClusterAsync(
                    operation, cluster, command, cancellationToken, storedProcedureName,
                    allowReadFrom: allowReadFrom, observeErrors: true, discardServerErrors: true)
                .ConfigureAwait(false);
        }
        catch (Exception error)
        {
            discovery?.RecordCommandFailure(error, discoveryPending, cancellationToken);
            if (!delegatedErrors) observation.Final(error);
            throw;
        }
        finally { discovery?.Finish(); }
    }

    private async ValueTask SendClusterWideFireAndForgetAsync<TCommand>(
        string operation,
        ClusterRouter cluster,
        TCommand command,
        CancellationToken cancellationToken,
        string? storedProcedureName = null)
        where TCommand : struct, IRespCommand
    {
        ClusterRouter.DiscoveryRound? discovery = null;
        var discoveryPending = false;
        using var observation = RespireTelemetry.ErrorObservation.Rent(force: true);
        try
        {
            var cache = _core.ClientCache;
            var mutationFence = cache is null ? default : cache.BeginUnknownMutation();
            try
            {
                var connections = await cluster.GetMasterConnectionsAsync(cancellationToken, discovery: null).ConfigureAwait(false);
                List<Exception>? failures = null;
                foreach (var connection in connections)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        var target = connection;
                        for (var attempt = 0; ; attempt++)
                        {
                            try
                            {
                                await SendFireAndForgetOnConnectionAsync(
                                        operation, target, new MutationCommand<TCommand>(command, mutationFence), cancellationToken, storedProcedureName, observation)
                                    .ConfigureAwait(false);
                                break;
                            }
                            catch (RespireConnectionRetiredException retirement) when (cluster.CanRetryRetirement(attempt, cancellationToken))
                            {
                                cluster.RecordRejection(ref discovery, target, retirement);
                                // Retry only this rejected target, never a previously accepted send.
                                observation.Handled(retirement);
                                discoveryPending = true;
                                target = await cluster.GetReplacementConnectionAsync(connection, null, null, cancellationToken, discovery)
                                    .ConfigureAwait(false);
                                discoveryPending = false;
                            }
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // A later target may still succeed; retain this target's discovery
                        // failure before continuing, without classifying application failures.
                        discovery?.RecordCommandFailure(ex, discoveryPending, cancellationToken);
                        discoveryPending = false;
                        RespireTelemetry.RecordError(ex, internallyHandled: true, observation.Attempts);
                        (failures ??= []).Add(ex);
                    }
                }

                if (failures is not null)
                {
                    throw new AggregateException(
                        "One or more Redis Cluster masters did not accept the command.", failures);
                }
            }
            finally
            {
                if (mutationFence.IsRequired)
                {
                    cache!.CompleteMutation(in mutationFence);
                }
            }
        }
        catch (Exception error)
        {
            discovery?.RecordCommandFailure(error, discoveryPending, cancellationToken);
            observation.Final(error);
            throw;
        }
        finally { discovery?.Finish(); }
    }

    // The view's original policy survives role fallback; a physical retirement must retain
    // its zone even after higher-level routing has narrowed eligibility to one server role.
    private string? GetTransportReadZone<TCommand>(in TCommand command) where TCommand : struct, IRespCommand
        => command.ReadKind != ReadCommandKind.None && ReadFallbackPolicy.UsesAvailabilityZone(_readFrom)
            ? _core.Options.ClientAvailabilityZone : null;

    // CommandTimeout is enforced by the connection's deadline sweep (commands are stamped at
    // enqueue), so no per-command CancellationTokenSource or timer is created here.
    private ValueTask<RespValue> SendOnConnectionCoreAsync<TCommand>(
        string operation,
        RespireConnection connection,
        in TCommand command,
        CancellationToken cancellationToken,
        bool sendAsking = false,
        CommandDeadline commandDeadline = default,
        bool allowStreamingConnectionReroute = true,
        bool pinToConnection = false, RespireTelemetry.ErrorObservation observation = default)
        where TCommand : struct, IRespCommand
        => _core.Circuits is not null && !_snapshotPrefixedBinaryKeys && !pinToConnection
            && operation is not ("MULTI" or "WATCH")
            ? SendCircuitResponseAsync(operation, connection, command, cancellationToken, sendAsking,
                commandDeadline, allowStreamingConnectionReroute, observation)
            : SendOnConnectionUncheckedAsync(operation, connection, command, cancellationToken, sendAsking,
                commandDeadline, allowStreamingConnectionReroute, pinToConnection, observation);

    private ValueTask<RespValue> SendOnConnectionUncheckedAsync<TCommand>(
        string operation, RespireConnection connection, in TCommand command, CancellationToken cancellationToken,
        bool sendAsking, CommandDeadline commandDeadline, bool allowStreamingConnectionReroute,
        bool pinToConnection = false, RespireTelemetry.ErrorObservation observation = default) where TCommand : struct, IRespCommand
        => sendAsking
            ? ClusterRouter.SendAskingAsync(connection, in command, cancellationToken, operation,
                commandDeadline, allowStreamingConnectionReroute, preferredZone: GetTransportReadZone(in command),
                pinToConnection: pinToConnection, observation: observation)
            : connection.SendCheckedAsync(in command, cancellationToken, operation,
                commandDeadline, allowStreamingConnectionReroute, preferredZone: GetTransportReadZone(in command),
                pinToConnection: pinToConnection, observation: observation);

    private ValueTask<Stream?> SendBulkStreamCoreAsync<TCommand>(
        string operation, TCommand command, CancellationToken cancellationToken,
        RespireTelemetry.ErrorObservation observation)
        where TCommand : struct, IRespCommand
    {
        var core = _core;
        ObjectDisposedException.ThrowIf(core.Disposed, this);
        if (core.Cluster is { } cluster)
        {
            return SendClusterBulkStreamAsync(operation, cluster, command, cancellationToken, observation);
        }

        if (_readFrom != RespireReadFrom.Primary && command.ReadKind != ReadCommandKind.None)
            return SendBulkStreamViaReadRouterAsync(operation, command, cancellationToken, observation);

        if (core.Sentinel is not null || !core.Multiplexer.IsInitialized)
        {
            return SendBulkStreamAfterConnectAsync(operation, command, cancellationToken, observation);
        }

        return SendBulkStreamOnConnectionAsync(
            operation, GetCircuitAwareConnection(core.Multiplexer, cancellationToken), command, cancellationToken, observation: observation);
    }

#if NET
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
#endif
    // Callers consume the pooled result once by awaiting or forwarding it.
    private async ValueTask<Stream?> SendBulkStreamViaReadRouterAsync<TCommand>(
        string operation, TCommand command, CancellationToken cancellationToken,
        RespireTelemetry.ErrorObservation observation)
        where TCommand : struct, IRespCommand
    {
        var connection = await _core.ReadRouter.GetConnectionAsync(_readFrom, cancellationToken).ConfigureAwait(false);
        return await SendBulkStreamOnConnectionAsync(operation, connection, command, cancellationToken,
            observation: observation).ConfigureAwait(false);
    }

#if NET
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
#endif
    private async ValueTask<Stream?> SendBulkStreamAfterConnectAsync<TCommand>(
        string operation,
        TCommand command,
        CancellationToken cancellationToken,
        RespireTelemetry.ErrorObservation observation)
        where TCommand : struct, IRespCommand
    {
        await _core.EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        return await SendBulkStreamOnConnectionAsync(
            operation, GetCircuitAwareConnection(_core.Multiplexer, cancellationToken), command, cancellationToken,
            observation: observation).ConfigureAwait(false);
    }

#if NET
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
#endif
    private async ValueTask<Stream?> SendClusterBulkStreamAsync<TCommand>(
        string operation,
        ClusterRouter cluster,
        TCommand command,
        CancellationToken cancellationToken,
        RespireTelemetry.ErrorObservation observation)
        where TCommand : struct, IRespCommand
    {
        var slot = command.TryGetClusterSlot(out var commandSlot) ? commandSlot : (int?)null;
        var readFrom = GetReadFromForCommand(in command);
        var preferredZone = ReadFallbackPolicy.UsesAvailabilityZone(readFrom) ? _core.Options.ClientAvailabilityZone : null;
        ClusterRouter.DiscoveryRound? discovery = null;
        var discoveryPending = false;
        try
        {
            var connection = await cluster.GetReadConnectionAsync(slot, readFrom, cancellationToken, observation: observation).ConfigureAwait(false);
            var sendAsking = false;
            var fallback = new ReadFallbackPolicy.RoleFallback(readFrom);
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    var stream = await SendBulkStreamOnConnectionAsync(
                        operation, connection, command, cancellationToken, sendAsking, observation).ConfigureAwait(false);
                    return stream;
                }
                catch (RespireConnectionRetiredException retirement)
                    when (cluster.CanRetryRetirement(attempt, cancellationToken))
                {
                    cluster.RecordRejection(ref discovery, connection, retirement);
                    observation.Handled(retirement);
                    if (!ReadFallbackPolicy.IsReplicaConnection(connection))
                        _core.ClientCache?.FlushForContinuityLoss();
                    discoveryPending = true;
                    connection = sendAsking
                        ? await cluster.GetReplacementConnectionAsync(connection, slot, null, cancellationToken, discovery, preferredZone).ConfigureAwait(false)
                        : await cluster.GetReadReplacementConnectionAsync(slot, readFrom, cancellationToken, discovery, preferredZone, observation).ConfigureAwait(false);
                    discoveryPending = false;
                }
                catch (RespireServerException error)
                    when (attempt < ClusterRouter.RedirectLimit && ClusterRouter.CanRecover(error, slot))
                {
                    if (ReadFallbackPolicy.IsStrictReplicaAsk(error, readFrom)) throw ReadFallbackPolicy.CreateStrictReplicaAskException(error, slot);
                    _core.ClientCache?.FlushForContinuityLoss();
                    cluster.RecordRejection(ref discovery, connection, error);
                    observation.Handled(error);
                    discoveryPending = true;
                    connection = await cluster.GetRedirectConnectionAsync(error, connection, cancellationToken, slot, discovery, preferredZone)
                        .ConfigureAwait(false);
                    if (error.Code != RespireErrorCodes.Ask && readFrom != RespireReadFrom.Primary)
                    {
                        connection = await cluster.SelectReadConnectionAfterRedirectAsync(
                            connection, slot, readFrom, cancellationToken, discovery, preferredZone, observation).ConfigureAwait(false);
                    }
                    discoveryPending = false;
                    sendAsking = error.Code == RespireErrorCodes.Ask;
                }
                catch (RespireServerException error) when (!sendAsking
                    && fallback.TrySwitch(error, slot, ReadFallbackPolicy.IsReplicaConnection(connection)))
                {
                    observation.Handled(error);
                    connection = await cluster.GetOtherRoleReadConnectionAsync(
                        slot!.Value, fallback, cancellationToken, discovery, observation).ConfigureAwait(false);
                    readFrom = fallback.RecoveryPolicy;
                }
            }
        }
        catch (Exception error)
        {
            discovery?.RecordCommandFailure(error, discoveryPending, slot, callerToken: cancellationToken);
            throw;
        }
        finally { discovery?.Finish(); }
    }

    private ValueTask<Stream?> SendBulkStreamOnConnectionAsync<TCommand>(
        string operation,
        RespireConnection connection,
        TCommand command,
        CancellationToken cancellationToken,
        bool sendAsking = false, RespireTelemetry.ErrorObservation observation = default)
        where TCommand : struct, IRespCommand
    {
        if (RespireTelemetry.IsOperationEnabled(operation) || _core.Circuits is not null)
        {
            return SendBulkStreamOnConnectionInstrumentedAsync(
                operation, connection, command, cancellationToken, sendAsking, observation);
        }

        if (sendAsking)
        {
            return ClusterRouter.SendAskingBulkStreamAsync(
                connection, in command, cancellationToken, operation, preferredZone: GetTransportReadZone(in command),
                observation: observation);
        }

        return connection.SendBulkStreamAsync(in command, cancellationToken, operation,
            preferredZone: GetTransportReadZone(in command), observation: observation);
    }

#if NET
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
#endif
    private async ValueTask<Stream?> SendBulkStreamOnConnectionInstrumentedAsync<TCommand>(
        string operation,
        RespireConnection connection,
        TCommand command,
        CancellationToken cancellationToken,
        bool sendAsking, RespireTelemetry.ErrorObservation observation)
        where TCommand : struct, IRespCommand
    {
        var core = _core;
        var previousActivity = Activity.Current;
        var telemetry = RespireTelemetry.StartOperation(
            operation, connection, core.Options.Database);
        var telemetryCompleted = 0;
        CircuitStreamCompletion? circuitCompletion = null;
        var circuitHandedOver = false;
        void CompleteTelemetry(Exception? error)
        {
            if (Interlocked.Exchange(ref telemetryCompleted, 1) == 0)
            {
                circuitCompletion?.Complete(error);
                telemetry.Complete(operation, connection.Host, connection.Port, core.Options.Database,
                    error: error, connection: connection);
            }
        }

        try
        {
            Stream? stream;
            var commandDeadline = core.Circuits is not null ? CreateCircuitDeadline() : default;
            while (true)
            {
                try
                {
                    if (core.Circuits is not null)
                    {
                        connection.ThrowIfRetired();
                        circuitCompletion = new(AcquireCircuit(connection, cancellationToken), cancellationToken);
                    }
                    stream = sendAsking
                        ? await ClusterRouter.SendAskingBulkStreamAsync(
                            connection, in command, cancellationToken, operation, CompleteTelemetry,
                            GetTransportReadZone(in command), observation).ConfigureAwait(false)
                        : await connection.SendBulkStreamAsync(
                            in command, cancellationToken, operation, CompleteTelemetry,
                            GetTransportReadZone(in command), pinToConnection: core.Circuits is not null,
                            commandDeadline: commandDeadline, observation: observation).ConfigureAwait(false);
                    break;
                }
                catch (RespireConnectionRetiredException error) when (core.Circuits is not null
                    && connection.TryReroute(false, commandDeadline, out var target, out var rerouted, GetTransportReadZone(in command)))
                {
                    observation.Handled(error);
                    circuitCompletion?.Ignore();
                    circuitCompletion = null;
                    connection = target;
                    commandDeadline = rerouted;
                    telemetry.UpdateServerEndpoint(connection.Host, connection.Port);
                }
            }
            if (stream is null)
            {
                CompleteTelemetry(null);
            }

            if (stream is not null && circuitCompletion is not null)
            {
                var guarded = new CircuitCompletionStream(stream, circuitCompletion);
                circuitHandedOver = true;
                return guarded;
            }
            return stream;
        }
        catch (Exception ex)
        {
            CompleteTelemetry(ex);
            throw;
        }
        finally
        {
            if (!circuitHandedOver) circuitCompletion?.Ignore();
            if (!ReferenceEquals(Activity.Current, previousActivity))
            {
                Activity.Current = previousActivity;
            }
        }
    }

    // Endpoint-pinned fan-outs can retry a rejected target without replaying accepted peers.
    // Do not use this for WATCH or connection-scoped CLIENT operations, whose socket is part of their contract.
    internal async ValueTask<RespValue> SendToClusterTargetAsync<TCommand>(
        string operation, RespireConnection connection, TCommand command, CancellationToken cancellationToken,
        bool observeErrors = true, RespireTelemetry.ErrorObservation observation = default)
        where TCommand : struct, IRespCommand
    {
        var cluster = _core.Cluster;
        // Keep attempts through transport and target replacement, including late metric activation.
        var ownsObservation = observation.IsEmpty;
        if (observation.IsEmpty) observation = RespireTelemetry.ErrorObservation.Rent(force: true);
        ClusterRouter.DiscoveryRound? discovery = null;
        var discoveryPending = false;
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    return await SendOnConnectionAsync(operation, connection, command, cancellationToken,
                        observation: observation).ConfigureAwait(false);
                }
                catch (RespireConnectionRetiredException retirement) when (cluster is not null && cluster.CanRetryRetirement(attempt, cancellationToken))
                {
                    cluster.RecordRejection(ref discovery, connection, retirement);
                    observation.Handled(retirement);
                    discoveryPending = true;
                    connection = await cluster.GetReplacementConnectionAsync(connection, null, null, cancellationToken, discovery).ConfigureAwait(false);
                    discoveryPending = false;
                }
            }
        }
        catch (Exception error)
        {
            discovery?.RecordCommandFailure(error, discoveryPending, cancellationToken);
            if (observeErrors) observation.Final(error);
            throw;
        }
        finally
        {
            try { discovery?.Finish(); }
            finally { if (ownsObservation) observation.Dispose(); }
        }
    }

    // Physical-connection routes keep client-cache admission and borrow the caller's error owner.
    internal ValueTask<RespValue> SendAdmittedOnPinnedConnectionAsync<TCommand>(
        string operation, RespireConnection connection, TCommand command, CancellationToken cancellationToken,
        RespireTelemetry.ErrorObservation observation = default)
        where TCommand : struct, IRespCommand
    {
        var cache = _core.ClientCache;
        // Audited physical-connection inspections do not mutate application data.
        var mutationFence = cache is null || CommandDispatchAdmission<TCommand>.IsConnectionProtocol(in command)
            ? default : cache.BeforeCommand(operation, in command);
        if (!mutationFence.IsRequired)
            return SendOnPinnedConnectionAsync(operation, connection, command, cancellationToken, observation);
        try
        {
            return CompleteMutationAsync(
                SendOnPinnedConnectionAsync(operation, connection, new MutationCommand<TCommand>(command, mutationFence),
                    cancellationToken, observation),
                cache!, mutationFence);
        }
        catch
        {
            cache!.CompleteMutation(in mutationFence);
            throw;
        }
    }

    internal ValueTask<RespValue> SendOnConnectionAsync<TCommand>(
        string operation,
        RespireConnection connection,
        in TCommand command,
        CancellationToken cancellationToken,
        string? storedProcedureName = null,
        bool sendAsking = false,
        CommandDeadline commandDeadline = default,
        bool allowStreamingConnectionReroute = true,
        bool pinToConnection = false, RespireTelemetry.ErrorObservation observation = default)
        where TCommand : struct, IRespCommand
        => RespireTelemetry.IsOperationEnabled(operation)
            ? SendOnConnectionInstrumentedAsync(
                operation, connection, command, cancellationToken, storedProcedureName, sendAsking,
                commandDeadline, allowStreamingConnectionReroute, pinToConnection, observation: observation)
            : SendOnConnectionCoreAsync(operation, connection, command, cancellationToken, sendAsking,
                commandDeadline, allowStreamingConnectionReroute, pinToConnection, observation: observation);

    internal ValueTask<RespValue> SendMutationOnConnectionAsync<TCommand>(
        string operation, RespireConnection connection, in TCommand command,
        ClientSideCacheCoordinator.MutationFence mutationFence, CancellationToken cancellationToken,
        string? storedProcedureName = null, bool sendAsking = false,
        bool allowStreamingConnectionReroute = true, RespireTelemetry.ErrorObservation observation = default)
        where TCommand : struct, IRespCommand
        => mutationFence.IsRequired
            ? SendOnConnectionAsync(operation, connection, new MutationCommand<TCommand>(command, mutationFence),
                cancellationToken, storedProcedureName, sendAsking,
                allowStreamingConnectionReroute: allowStreamingConnectionReroute, observation: observation)
            : SendOnConnectionAsync(operation, connection, command, cancellationToken, storedProcedureName, sendAsking,
                allowStreamingConnectionReroute: allowStreamingConnectionReroute, observation: observation);

#if NET
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
#endif
    private async ValueTask<RespValue> SendOnConnectionInstrumentedAsync<TCommand>(
        string operation,
        RespireConnection connection,
        TCommand command,
        CancellationToken cancellationToken,
        string? storedProcedureName,
        bool sendAsking,
        CommandDeadline commandDeadline,
        bool allowStreamingConnectionReroute,
        bool pinToConnection = false, RespireTelemetry.ErrorObservation observation = default)
        where TCommand : struct, IRespCommand
    {
        var core = _core;
        var telemetry = RespireTelemetry.StartOperation(
            operation,
            connection,
            core.Options.Database,
            storedProcedureName: storedProcedureName);
        // The circuit retry owns the final endpoint, including rejection before dispatch.
        // Transfer telemetry with the command instead of completing against the source.
        if (core.Circuits is not null && !_snapshotPrefixedBinaryKeys && !pinToConnection
            && operation is not ("MULTI" or "WATCH"))
            return await SendCircuitResponseAsync(operation, connection, command, cancellationToken, sendAsking,
                commandDeadline, allowStreamingConnectionReroute, observation, telemetry, storedProcedureName)
                .ConfigureAwait(false);
        try
        {
            var response = await SendOnConnectionCoreAsync(
                    operation, connection, command, cancellationToken, sendAsking,
                    commandDeadline, allowStreamingConnectionReroute, pinToConnection, observation)
                .ConfigureAwait(false);
            telemetry.Complete(
                operation,
                connection.Host,
                connection.Port,
                core.Options.Database,
                storedProcedureName,
                connection: connection);
            return response;
        }
        catch (Exception ex)
        {
            telemetry.Complete(
                operation,
                connection.Host,
                connection.Port,
                core.Options.Database,
                storedProcedureName,
                ex,
                connection);
            throw;
        }
    }

    /// <summary>Sends a blocking command on a dedicated lease without a response timeout.</summary>
    internal async ValueTask<RespValue> SendBlockingAsync<TCommand>(
        string operation,
        TCommand command,
        CancellationToken cancellationToken,
        string? storedProcedureName = null,
        bool noRedirect = false,
        TimeSpan? cancellationTimeout = null, CancellationToken callerCancellationToken = default,
        bool allowReadFrom = true,
        bool observeErrors = true, RespireTelemetry.ErrorObservation observation = default)
        where TCommand : struct, IRespCommand
    {
        var core = _core;
        if (core.Disposed) ThrowIfDisposedForCommand(observeErrors);
        var readFrom = GetReadFromForCommand(in command, allowReadFrom);
        var cache = core.ClientCache;
        var mutationFence = cache is null ? default : cache.BeforeCommand(operation, in command, blocking: true);
        try
        {
            var started = RespireTelemetry.CaptureOperationStart(operation);
            if (core.Cluster is { } cluster)
            {
                return await SendBlockingClusterAsync(
                        operation, cluster, new MutationCommand<TCommand>(command, mutationFence), cancellationToken, storedProcedureName, noRedirect,
                        cancellationTimeout, callerCancellationToken, readFrom, started, observeErrors, observation)
                    .ConfigureAwait(false);
            }

            RespireTelemetry.OperationScope telemetry = default;
            var telemetryStarted = false;
            RespireConnection? connection = null;
            DedicatedConnectionPool? pool = null;
            var returned = false;
            var preferredZone = ReadFallbackPolicy.UsesAvailabilityZone(readFrom) ? core.Options.ClientAvailabilityZone : null;
            var fallback = new ReadFallbackPolicy.RoleFallback(readFrom);
            var errorAttempts = 0;
            try
            {
                RespValue response;
                while (true)
                {
                    var onReplica = false;
                    if (readFrom != RespireReadFrom.Primary)
                    {
                        try
                        {
                            (pool, connection, onReplica) = await core.ReadRouter.RentDedicatedConnectionAsync(
                                readFrom, cancellationToken, preferredZone, fallback.ReplicaOnly).ConfigureAwait(false);
                        }
                        catch (Exception error) when (fallback.OriginalFailure is not null && ReadEndpointRouter.IsReadCandidateFailure(error, cancellationToken))
                        {
                            RethrowPreservingStackTrace(fallback.OriginalFailure);
                            throw;
                        }
                    }
                    else
                    {
                        pool = await core.GetDedicatedPoolAsync(cancellationToken).ConfigureAwait(false);
                        (pool, connection) = await core.RentDedicatedConnectionAsync(pool, cancellationToken,
                            preferredZone: preferredZone).ConfigureAwait(false);
                    }
                    if (!telemetryStarted)
                    {
                        telemetry = RespireTelemetry.StartOperation(operation, connection,
                            core.Options.Database, storedProcedureName: storedProcedureName, started: started);
                        telemetryStarted = true;
                    }
                    else
                    {
                        // Keep one logical span while routing advances to a replacement lease.
                        telemetry.UpdateServerEndpoint(connection.Host, connection.Port);
                    }
                    response = mutationFence.IsRequired
                        ? await SendBlockingOnConnectionAsync(connection, new MutationCommand<TCommand>(command, mutationFence), cancellationToken,
                            observation.IsEmpty ? errorAttempts : observation.Attempts).ConfigureAwait(false)
                        : await SendBlockingOnConnectionAsync(connection, command, cancellationToken,
                            observation.IsEmpty ? errorAttempts : observation.Attempts).ConfigureAwait(false);
                    if (response.IsError && fallback.OriginalFailure is null && readFrom != RespireReadFrom.Primary)
                    {
                        var error = ResponseReader.ServerError(in response, operation);
                        if (fallback.TrySwitch(error, onReplica))
                        {
                            response.Dispose();
                            pool.Return(connection);
                            connection = null;
                            if (!observation.IsEmpty) observation.Handled(error);
                            else RespireTelemetry.RecordError(error, internallyHandled: true, errorAttempts);
                            errorAttempts++;
                            continue;
                        }
                    }
                    break;
                }
                pool.Return(connection);
                returned = true;
                if (response.IsError)
                {
                    var error = ResponseReader.ServerError(in response, operation);
                    response.Dispose();
                    throw error;
                }

                telemetry.Complete(core, operation, storedProcedureName, connection: connection);
                return response;
            }
            catch (Exception ex)
            {
                var timeoutError = cancellationTimeout is { } timeout && ex is OperationCanceledException cancelled
                    && RespireConnection.IsDeadlineCancellation(cancelled, cancellationToken, callerCancellationToken)
                    ? new RespireTimeoutException(operation, timeout, cancelled,
                        connection?.CaptureDedicatedTimeoutDiagnostics()
                        ?? RespireTimeoutDiagnostics.Capture(RespireCommandStage.Connecting,
                            core.Sentinel is null && readFrom == RespireReadFrom.Primary ? (RespireEndpoint?)core.Endpoint : null))
                    : null;
                if (!telemetryStarted)
                    RespireTelemetry.RecordUnroutedFailure(operation, core.Options.Database,
                        started, timeoutError ?? ex, storedProcedureName,
                        endpoint: core.Sentinel is null && readFrom == RespireReadFrom.Primary
                            ? pool?.Endpoint ?? core.Multiplexer.ActiveConnectionEndpoint : (RespireEndpoint?)null);
                telemetry.Complete(core, operation, storedProcedureName, timeoutError ?? ex, connection);
                if (connection is not null && !returned)
                {
                    if (ex is RespireCircuitOpenException) pool!.Return(connection);
                    else await pool!.DiscardAsync(connection).ConfigureAwait(false);
                }

                if (observeErrors)
                {
                    if (!observation.IsEmpty) observation.Final(timeoutError ?? ex);
                    else RespireTelemetry.RecordError(timeoutError ?? ex, internallyHandled: false, errorAttempts);
                }
                if (timeoutError is not null) throw timeoutError;
                throw;
            }
        }
        finally
        {
            if (mutationFence.IsRequired)
            {
                cache!.CompleteMutation(in mutationFence);
            }
        }
    }

    private async ValueTask<RespValue> SendBlockingClusterAsync<TCommand>(
        string operation,
        ClusterRouter cluster,
        TCommand command,
        CancellationToken cancellationToken,
        string? storedProcedureName,
        bool noRedirect,
        TimeSpan? cancellationTimeout, CancellationToken callerCancellationToken,
        RespireReadFrom readFrom,
        RespireTelemetry.OperationStart started,
        bool observeErrors, RespireTelemetry.ErrorObservation observation)
        where TCommand : struct, IRespCommand
    {
        var core = _core;
        // Role fallback narrows readFrom, but the physical-zone preference belongs to the whole read.
        var preferredZone = ReadFallbackPolicy.UsesAvailabilityZone(readFrom) ? core.Options.ClientAvailabilityZone : null;
        var slot = command.TryGetClusterSlot(out var commandSlot) ? commandSlot : (int?)null;
        DedicatedConnectionPool pool;
        RespireTelemetry.OperationScope telemetry = default;
        var telemetryStarted = false;
        RespireConnection? telemetryConnection = null;
        var sendAsking = false;
        RespireConnection? askingSource = null;
        RespireServerException? askRedirect = null;
        var fallback = new ReadFallbackPolicy.RoleFallback(readFrom);
        var errorAttempts = 0;

        ClusterRouter.DiscoveryRound? discovery = null;
        try
        {
            pool = await cluster.GetReadDedicatedPoolAsync(slot, readFrom, cancellationToken, discovery: null, preferredZone, observation: observation).ConfigureAwait(false);
            for (var attempt = 0; ; attempt++)
            {
                RespireConnection? connection = null;
                var returned = false;
                var acquiringRedirectPool = false;
                try
                {
                    try
                    {
                        (pool, connection) = await cluster.RentDedicatedConnectionAsync(
                            pool, new ClusterRouter.DedicatedRoute(slot, readFrom, askRedirect, askingSource),
                            cancellationToken, discovery, preferredZone: preferredZone).ConfigureAwait(false);
                    }
                    catch (Exception error) when (askRedirect is null && fallback.OriginalFailure is not null
                        && ReadEndpointRouter.IsReadCandidateFailure(error, cancellationToken))
                    {
                        // Endpoint selection returned a pool, but its private handshake can fail
                        // later. Preserve the original rejection through that acquisition phase too.
                        RethrowPreservingStackTrace(fallback.OriginalFailure);
                        throw;
                    }
                    telemetryConnection = connection;
                    if (!telemetryStarted)
                    {
                        telemetry = RespireTelemetry.StartOperation(
                            operation,
                            connection,
                            core.Options.Database,
                            storedProcedureName: storedProcedureName, started: started);
                        telemetryStarted = true;
                    }
                    else
                    {
                        // Keep one logical span while routing advances to a replacement lease.
                        telemetry.UpdateServerEndpoint(connection.Host, connection.Port);
                    }

                    // Errors from an ASK target must not switch roles during migration.
                    var sentAsking = sendAsking;
                    RespValue response = default;
                    RespireServerException? serverError = null;
                    try
                    {
                        response = await (sendAsking
                            ? ClusterRouter.SendBlockingAskingUncheckedAsync(connection, in command, cancellationToken,
                                observation.IsEmpty ? errorAttempts : observation.Attempts)
                            : connection.SendWithoutResponseTimeoutAsync(command, cancellationToken,
                                observation.IsEmpty ? errorAttempts : observation.Attempts))
                            .ConfigureAwait(false);
                    }
                    catch (RespireServerException error) { serverError = error; }
                    sendAsking = false;
                    if (serverError is not null || response.IsError)
                    {
                        var error = serverError ?? ResponseReader.ServerError(in response, operation);
                        if (serverError is null) response.Dispose();
                        var strictReplicaAsk = ReadFallbackPolicy.IsStrictReplicaAsk(error, readFrom);
                        if (!noRedirect && !strictReplicaAsk && attempt < ClusterRouter.RedirectLimit
                            && ClusterRouter.CanRecover(error, slot))
                        {
                            core.ClientCache?.FlushForContinuityLoss();
                            // The source reply completed; no redirected command has been accepted yet.
                            cluster.RecordRejection(ref discovery, connection, error);
                            if (!observation.IsEmpty) observation.Handled(error);
                            else RespireTelemetry.RecordError(error, internallyHandled: true, errorAttempts);
                            errorAttempts++;
                            acquiringRedirectPool = true;
                            var redirectedPool = await cluster.GetRedirectDedicatedPoolAsync(
                                    error, connection, cancellationToken, slot, discovery)
                                .ConfigureAwait(false);
                            if (error.Code != RespireErrorCodes.Ask && readFrom != RespireReadFrom.Primary)
                            {
                                redirectedPool = await cluster.GetReadDedicatedPoolAsync(
                                        slot, readFrom, cancellationToken, discovery, preferredZone, observation: observation)
                                    .ConfigureAwait(false);
                            }
                            acquiringRedirectPool = false;
                            pool.Return(connection);
                            returned = true;
                            pool = redirectedPool;
                            sendAsking = error.Code == RespireErrorCodes.Ask;
                            askingSource = sendAsking ? connection : null;
                            askRedirect = sendAsking ? error : null;
                            continue;
                        }

                        if (!sentAsking
                            && fallback.TrySwitch(error, slot, pool.IsReadOnly))
                        {
                            // Reads are idempotent; retry once on the other role's dedicated pool.
                            // NoRedirect only surfaces MOVED and ASK, so it does not suppress this retry.
                            if (!observation.IsEmpty) observation.Handled(error);
                            else RespireTelemetry.RecordError(error, internallyHandled: true, errorAttempts);
                            errorAttempts++;
                            acquiringRedirectPool = true;
                            var otherRolePool = await cluster.GetOtherRoleDedicatedPoolAsync(
                                    slot!.Value, fallback, cancellationToken, discovery, observation)
                                .ConfigureAwait(false);
                            readFrom = fallback.RecoveryPolicy;
                            acquiringRedirectPool = false;
                            pool.Return(connection);
                            returned = true;
                            pool = otherRolePool;
                            continue;
                        }

                        pool.Return(connection);
                        returned = true;
                        // NoRedirect callers handle redirects themselves and must see the ASK reply.
                        if (strictReplicaAsk && !noRedirect) throw ReadFallbackPolicy.CreateStrictReplicaAskException(error, slot);
                        throw error;
                    }

                    pool.Return(connection);
                    returned = true;
                    telemetry.Complete(core, operation, storedProcedureName, connection: connection);
                    return response;
                }
                catch (Exception ex)
                {
                    var timeoutError = cancellationTimeout is { } timeout && ex is OperationCanceledException cancelled
                        && RespireConnection.IsDeadlineCancellation(cancelled, cancellationToken, callerCancellationToken)
                        ? new RespireTimeoutException(operation, timeout, cancelled,
                            acquiringRedirectPool || connection is null
                                ? RespireTimeoutDiagnostics.Capture(RespireCommandStage.Connecting)
                                : connection.CaptureDedicatedTimeoutDiagnostics())
                        : null;
                    discovery?.RecordCommandFailure(timeoutError ?? ex,
                        acquiringRedirectPool || connection is null, slot, noRedirect, callerCancellationToken);
                    if (connection is not null && !returned)
                    {
                        await pool.DiscardAsync(connection).ConfigureAwait(false);
                    }

                    if (timeoutError is not null) throw timeoutError;
                    throw;
                }
            }
        }
        catch (Exception error)
        {
            if (!telemetryStarted)
                RespireTelemetry.RecordUnroutedFailure(operation, core.Options.Database, started, error, storedProcedureName);
            else
                telemetry.Complete(core, operation, storedProcedureName, error, telemetryConnection);
            if (observeErrors)
            {
                if (!observation.IsEmpty) observation.Final(error);
                else RespireTelemetry.RecordError(error, internallyHandled: false, errorAttempts);
            }
            throw;
        }
        finally { discovery?.Finish(); }
    }

    internal ValueTask<RespireConnection> AcquireConnectionAsync(CancellationToken cancellationToken)
        => AcquireConnectionAsync(slot: null, cancellationToken);

    // Transactions arm an acquisition timer only when connection discovery can suspend.
    private RespireConnection? TryAcquireReadyConnection(int? slot, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_core.Disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (_core.Cluster is { } cluster)
            return cluster.TryAcquireReadyConnection(slot, cancellationToken);

        var multiplexer = _core.Multiplexer;
        if (_core.Sentinel is { } sentinel)
        {
            if (sentinel.Current is not { IsRetired: false } generation) return null;
            multiplexer = generation.Multiplexer;
        }
        if (multiplexer is not { IsConnected: true }) return null;
        try { return multiplexer.GetConnection(); }
        catch (Exception error) when (error is RespireConnectionException or RespireConnectionRetiredException)
        {
            // Retirement can race the ready snapshot. The common cold path selects its replacement.
            return null;
        }
    }

    internal ValueTask<RespireConnection> AcquireConnectionAsync(
        int? slot, CancellationToken cancellationToken, RespireReadFrom readFrom,
        RespireTelemetry.ErrorObservation observation = default)
        => _core.Cluster is { } cluster
            ? cluster.GetReadConnectionAsync(slot, readFrom, cancellationToken, observation: observation)
            : AcquireConnectionAsync(slot, cancellationToken);

    internal ValueTask<RespireConnection> AcquireConnectionAsync(
        int? slot, CancellationToken cancellationToken)
    {
        var acquisition = new CommandAcquisitionScope(cancellationToken, default, timeout: null);
        return AcquireConnectionAsync(slot, ref acquisition);
    }

    internal ValueTask<RespireConnection> AcquireConnectionAsync(int? slot, ref CommandAcquisitionScope acquisition)
        => TryAcquireReadyConnection(slot, acquisition.CallerToken) is { } ready
            ? new(ready) : AcquireConnectionSlowAsync(slot, acquisition.Token);

    private async ValueTask<RespireConnection> AcquireConnectionSlowAsync(
        int? slot, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_core.Disposed, this);
        if (_core.Cluster is { } cluster)
        {
            return await cluster.GetConnectionAsync(slot, cancellationToken, discovery: null).ConfigureAwait(false);
        }

        await _core.EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        return _core.Multiplexer.GetConnection();
    }

    // Wire-level primitives for the caching package (see InternalsVisibleTo). Keyed operations
    // honor this view's key prefix.

    internal bool RequiresReliableCorrectionOrdering(CancellationToken cancellationToken)
        => cancellationToken.CanBeCanceled || _core.Options.CommandTimeout is not null;

    /// <summary>
    /// Returns this client when uncertain script outcomes can be fenced before a corrective
    /// command: required whenever the command can time out or be canceled, and opportunistic
    /// otherwise. Returns null when ordering cannot be established without that requirement.
    /// </summary>
    internal async ValueTask<RespireClient?> GetCorrectionTrackingClientAsync(CancellationToken cancellationToken)
    {
        if (RequiresReliableCorrectionOrdering(cancellationToken))
        {
            await EnsureReliableCorrectionOrderingAsync(cancellationToken).ConfigureAwait(false);
            return this;
        }

        return await TryEnsureReliableCorrectionOrderingAsync().ConfigureAwait(false) ? this : null;
    }

    internal async ValueTask<bool> TryEnsureReliableCorrectionOrderingAsync()
    {
        if (_core.Cluster is not null)
        {
            // The keyed tracked send initializes CLIENT ID on its routed node. Initializing
            // only the seed here would provide no ordering guarantee for that node.
            return true;
        }

        if (_core.Sentinel is not null)
            await _core.EnsureConnectedAsync(CancellationToken.None).ConfigureAwait(false);
        var multiplexer = _core.Multiplexer;
        if (multiplexer.IsReliableCorrectionOrderingUnavailable)
        {
            return false;
        }

        try
        {
            await multiplexer.EnsureReliableCorrectionOrderingAsync().ConfigureAwait(false);
            return true;
        }
        catch (RespireServerException)
        {
            // Normal non-cancellable access remains compatible with ACLs and RESP servers that
            // do not expose CLIENT commands. Its connection-loss guarantee is necessarily
            // best-effort when no server-side identity can be obtained.
            return false;
        }
    }

    /// <summary>
    /// Captures Redis client IDs before any cache command can become latent. A correction can
    /// then fence a locally dead socket server-side instead of assuming socket loss canceled
    /// bytes Redis may already have buffered.
    /// </summary>
    internal async ValueTask EnsureReliableCorrectionOrderingAsync(CancellationToken cancellationToken = default)
    {
        var core = _core;
        ObjectDisposedException.ThrowIf(core.Disposed, this);
        if (core.Cluster is not null)
        {
            // Deferred until the key selects a node in StartTrackedScriptExecutionAsync.
            return;
        }

        if ((core.Sentinel is null || core.Sentinel.IsConnected) && core.Multiplexer.HasReliableCorrectionOrdering)
        {
            return;
        }

        if (core.Options.CommandTimeout is not { } timeout)
        {
            if (core.Sentinel is not null)
                await core.EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
            await core.Multiplexer.EnsureReliableCorrectionOrderingAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        using var timeoutSource = CommandTimeoutCancellation.Create(cancellationToken, timeout);
        Infrastructure.RespireConnectionMultiplexer? selected = null;
        try
        {
            if (core.Sentinel is not null)
                await core.EnsureConnectedAsync(timeoutSource.Token).ConfigureAwait(false);
            selected = core.Multiplexer;
            await selected.EnsureReliableCorrectionOrderingAsync(timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // No cache command is sent until identity setup completes, so timing this stage out
            // leaves no cache mutation to correct.
            throw new RespireTimeoutException("CLIENT ID / CLIENT KILL", timeout, null,
                selected?.CaptureConnectionWait() ?? (core.Sentinel is null
                    ? core.Multiplexer.CaptureConnectionWait()
                    : RespireTimeoutDiagnostics.Capture(RespireCommandStage.Connecting)));
        }
        catch (RespireTimeoutException ex)
        {
            // The deadline sweep expired an individual setup command; relabel it as the
            // whole identity-setup stage.
            throw new RespireTimeoutException("CLIENT ID / CLIENT KILL", timeout, ex);
        }
    }

    internal readonly record struct TrackedConnectionIdentity(
        RespireEndpoint Endpoint,
        long ServerClientId,
        bool RequiresAsking = false,
        RespireConnection? Connection = null);

    internal async ValueTask<bool> HasDifferentSentinelGenerationAsync(TrackedConnectionIdentity identity)
    {
        if (identity.Connection is null || _core.Sentinel is not { } sentinel) return false;
        var current = await sentinel.GetGenerationAsync(CancellationToken.None).ConfigureAwait(false);
        return !ReferenceEquals(current.Multiplexer, identity.Connection.Multiplexer);
    }

    internal sealed class TrackedScriptExecution : ITrackedCorrectionExecution<RespireResult>
    {
        internal TrackedScriptExecution(
            RespireConnection connection, TrackedConnectionIdentity connectionIdentity,
            Action<long>? onSerialized = null, Action? onCommandNotApplied = null,
            RespireTelemetry.ErrorObservation errorObservation = default)
        {
            Connection = connection;
            ConnectionIdentity = connectionIdentity;
            OnSerialized = onSerialized;
            OnCommandNotApplied = onCommandNotApplied;
            ErrorObservation = errorObservation;
        }

        internal RespireConnection Connection { get; set; }

        internal TrackedConnectionIdentity ConnectionIdentity { get; set; }

        /// <summary>
        /// When the final attempt was serialized into the connection's write path. It is taken
        /// after any wait for in-flight capacity and before the bytes reach Redis, so it never
        /// postdates the server's execution of the script.
        /// </summary>
        internal long StartedTimestamp { get; set; }

        private long PendingSerializedTimestamp { get; set; }

        private Action<long>? OnSerialized { get; }

        private Action? OnCommandNotApplied { get; }

        internal void RecordSerialized(long timestamp)
        {
            PendingSerializedTimestamp = timestamp;
        }

        internal void RecordAccepted()
        {
            var timestamp = PendingSerializedTimestamp;
            StartedTimestamp = timestamp;
            OnSerialized?.Invoke(timestamp);
        }

        internal void RecordCommandNotApplied() => OnCommandNotApplied?.Invoke();

        private ValueTask<RespireResult> _response;
        internal RespireTelemetry.ErrorObservation ErrorObservation { get; }

        internal void SetResponse(ValueTask<RespireResult> response) => _response = response;

        // Consume once: a direct reader owns the observer; the correction interface borrows
        // the raw response and retains the same lease until its cleanup finishes.
        internal ValueTask<RespireResult> ConsumeResponseAsync()
            => ErrorObservation.IsEmpty ? _response : RespireTelemetry.ObserveFinalError(_response, ErrorObservation);
        ValueTask<RespireResult> ITrackedCorrectionExecution<RespireResult>.Response => _response;
        RespireTelemetry.ErrorObservation ITrackedCorrectionExecution<RespireResult>.ErrorObservation => ErrorObservation;
        TrackedConnectionIdentity ITrackedCorrectionExecution<RespireResult>.ConnectionIdentity => ConnectionIdentity;
        bool ITrackedCorrectionExecution<RespireResult>.CommandMayBeOutstanding => true;
    }

    /// <summary>
    /// Records <see cref="TrackedScriptExecution.StartedTimestamp"/> when the connection serializes
    /// the command. Serialization happens at enqueue, after any wait for in-flight ring capacity, so
    /// a lease measured from it does not count time parked behind other commands. A retried enqueue
    /// serializes again, so the last write wins.
    /// </summary>
    internal readonly struct SendTimestampCommand<TCommand>(TCommand command, TrackedScriptExecution execution) : IRespCommandWrapper
        where TCommand : struct, IRespCommand
    {
        public bool IsConnectionProtocol => CommandDispatchAdmission<TCommand>.IsConnectionProtocol(in command);
        public int GetWriteSizeHint() => command.GetWriteSizeHint();
        public void Write(ref RespWriter writer)
        {
            command.Write(ref writer);
            execution.RecordSerialized(Stopwatch.GetTimestamp());
        }

        public ReadCommandKind ReadKind => command.ReadKind;

        public ClientSideCacheCoordinator.MutationFence GetMutationFence() => CommandDispatchAdmission<TCommand>.GetMutationFence(in command);

        public void OnAccepted()
        {
            command.OnAccepted();
            execution.RecordAccepted();
        }

        public void ValidateAdmission() => command.ValidateAdmission();

        public CancellationToken GetResponseCancellationToken(CancellationToken admissionToken)
            => command.GetResponseCancellationToken(admissionToken);

        public int CursorArgumentIndex => command.CursorArgumentIndex;

        public RespireCacheMutation GetCacheMutation(string operation) => command.GetCacheMutation(operation);
        public ClientCacheCommandMetadata GetClientCacheMetadata(string operation) => command.GetClientCacheMetadata(operation);

        public bool TryGetArgument(int index, out RespireValue value) => command.TryGetArgument(index, out value);

        public bool TryGetPrimaryKey(out RespireValue key) => command.TryGetPrimaryKey(out key);

        public bool TryGetClusterSlot(out int slot) => command.TryGetClusterSlot(out slot);

        public bool TryGetClientCacheKey(string operation, out ClientCacheCommandKey key)
            => command.TryGetClientCacheKey(operation, out key);
    }

#if NET
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
#endif
    private async ValueTask<RespireResult> ExecuteScriptCoreAsync(
        RespireScript script,
        RespireValue[] tail,
        CancellationToken cancellationToken, ClientSideCacheCoordinator.MutationFence mutationFence,
        RespireTelemetry.ErrorObservation observation)
    {
        var core = _core;
        var started = RespireTelemetry.CaptureOperationStart(script.EvalShaOperation);
        if (core.Cluster is { } cluster)
        {
            var arguments = tail[1..];
            var scriptTelemetry = started.Timestamp == 0
                ? null : new ClusterScriptTelemetry(core, script, started);
            try
            {
                RespValue clusterReply;
                try
                {
                    clusterReply = await SendClusterAsync(
                        script.EvalShaOperation, cluster,
                        new MutationCommand<ReadOnlyCommand<Cmd2N>>(ReadOnlyCommand<Cmd2N>.ForAuditedScript(new Cmd2N(script.EvalShaVerb, script.Sha1, tail[0], arguments),
                            script.IsCacheReadOnly), mutationFence),
                        cancellationToken, script.Sha1, allowReadFrom: true,
                        suppressTelemetry: true, scriptTelemetry: scriptTelemetry, observation: observation).ConfigureAwait(false);
                }
                catch (RespireServerException ex) when (ex.Code == RespireErrorCodes.NoScript)
                {
                    observation.Handled(ex);
                    clusterReply = await SendClusterAsync(
                        script.EvalOperation, cluster,
                        new MutationCommand<ReadOnlyCommand<Cmd2N>>(ReadOnlyCommand<Cmd2N>.ForAuditedScript(new Cmd2N(script.EvalVerb, script.Source, tail[0], arguments),
                            script.IsCacheReadOnly), mutationFence),
                        cancellationToken, script.Sha1, allowReadFrom: true,
                        suppressTelemetry: true, scriptTelemetry: scriptTelemetry, observation: observation).ConfigureAwait(false);
                }
                scriptTelemetry?.Complete();
                return new RespireResult(in clusterReply, core.Options.Serializer);
            }
            catch (Exception error)
            {
                scriptTelemetry?.Complete(error);
                throw;
            }
        }

        var telemetry = default(RespireTelemetry.OperationScope);
        RespireConnection? connection = null;
        try
        {
            if (script.IsReadOnly && _readFrom != RespireReadFrom.Primary)
                connection = await core.ReadRouter.GetConnectionAsync(_readFrom, cancellationToken).ConfigureAwait(false);
            else
            {
                await core.EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
                connection = GetCircuitAwareConnection(core.Multiplexer, cancellationToken);
            }
            telemetry = RespireTelemetry.StartOperation(script.EvalShaOperation, connection,
                core.Options.Database, storedProcedureName: script.Sha1, started: started);
            var result = await ExecuteScriptOnConnectionCoreAsync(connection, script, tail, cancellationToken, mutationFence,
                    observation: observation)
                .ConfigureAwait(false);
            telemetry.Complete(core, script.EvalShaOperation, script.Sha1, connection: connection);
            return result;
        }
        catch (Exception ex)
        {
            if (connection is null)
            {
                RespireTelemetry.RecordUnroutedFailure(script.EvalShaOperation, core.Options.Database,
                    started, ex, script.Sha1, endpoint: core.Sentinel is null && _readFrom == RespireReadFrom.Primary
                        ? core.Endpoint : (RespireEndpoint?)null);
            }
            telemetry.Complete(core, script.EvalShaOperation, script.Sha1, ex, connection);
            throw;
        }
    }

    /// <summary>
    /// Starts a cache script on a known multiplexed connection. The caller keeps the Redis
    /// client ID even when the reply wait fails, so it can establish a server-side barrier for
    /// that exact command before surfacing the failure.
    /// </summary>
    private async ValueTask<TrackedScriptExecution> StartTrackedScriptExecutionCoreAsync(
        RespireScript script,
        RespireKey[] keys,
        RespireValue[] args,
        CancellationToken cancellationToken,
        bool requireReliableCorrectionOrdering,
        bool captureSendTimestampOnly,
        Action<long>? onSerialized,
        Action? onCommandNotApplied,
        RespireTelemetry.ErrorObservation observation)
    {
        var core = _core;
        ObjectDisposedException.ThrowIf(core.Disposed, this);
        var cache = core.ClientCache;
        var mutationFence = cache is null || script.IsReadOnly ? default : cache.BeginUnknownMutation();
        var responseOwnsFence = false;
        var started = RespireTelemetry.CaptureOperationStart(script.EvalShaOperation);
        var telemetryDispatched = false;
        try
        {
            var requiresIdentity = !captureSendTimestampOnly && (requireReliableCorrectionOrdering
                || RequiresReliableCorrectionOrdering(cancellationToken));
            var tail = BuildScriptTail(keys, args);

            RespireConnection connection;
            if (core.Cluster is { } cluster)
            {
                var command = new Cmd2N(script.EvalShaVerb, script.Sha1, tail[0], tail[1..]);
                var slot = command.TryGetClusterSlot(out var commandSlot) ? commandSlot : (int?)null;
                connection = await GetTrackedClusterConnectionAsync(
                        cluster, slot, requiresIdentity, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                var multiplexer = core.Sentinel is { } sentinel
                    ? (await sentinel.GetGenerationAsync(cancellationToken).ConfigureAwait(false)).Multiplexer
                    : core.Multiplexer;
                if (requiresIdentity && core.Sentinel is null && !multiplexer.HasReliableCorrectionOrdering)
                {
                    throw new InvalidOperationException(
                        "Reliable correction ordering must be initialized before a tracked script starts.");
                }

                connection = await GetTrackedConnectionAsync(multiplexer, cancellationToken, requiresIdentity)
                    .ConfigureAwait(false);
            }

            var identity = GetTrackedConnectionIdentity(
                connection, core.Cluster is null || ClusterRouter.HasReliableCorrectionOrdering(connection));
            var execution = new TrackedScriptExecution(connection, identity, onSerialized, onCommandNotApplied, observation);
            ValueTask<RespireResult> response;
            telemetryDispatched = true;
            if (core.Cluster is { } router)
            {
                response = ExecuteTrackedClusterScriptAsync(
                    execution, router, connection, script, tail, requiresIdentity, cancellationToken, started, mutationFence);
            }
            else
            {
                execution.StartedTimestamp = Stopwatch.GetTimestamp();
                response = ExecuteScriptOnConnectionAsync(connection, script, tail, cancellationToken, execution, started, mutationFence);
            }
            execution.SetResponse(mutationFence.IsRequired
                ? CompleteMutationAsync(response, cache!, mutationFence)
                : response);
            responseOwnsFence = mutationFence.IsRequired;
            return execution;
        }
        catch (Exception error)
        {
            if (!telemetryDispatched)
                RespireTelemetry.RecordUnroutedFailure(script.EvalShaOperation, core.Options.Database, started, error, script.Sha1,
                    endpoint: core.Cluster is null && core.Sentinel is null ? core.Endpoint : (RespireEndpoint?)null);
            throw;
        }
        finally
        {
            if (mutationFence.IsRequired && !responseOwnsFence)
            {
                cache!.CompleteMutation(in mutationFence);
            }
        }
    }

    private async ValueTask<RespireConnection> GetTrackedConnectionAsync(
        Infrastructure.RespireConnectionMultiplexer multiplexer,
        CancellationToken cancellationToken,
        bool requireIdentity = true)
    {
        if (_core.Options.CommandTimeout is not { } timeout)
        {
            // Initialize the captured generation: failover may have retired the preflight generation.
            if (requireIdentity && _core.Sentinel is not null)
                await multiplexer.EnsureReliableCorrectionOrderingAsync(cancellationToken).ConfigureAwait(false);
            return await multiplexer.GetHealthyConnectionAsync(cancellationToken).ConfigureAwait(false);
        }

        using var timeoutSource = CommandTimeoutCancellation.Create(cancellationToken, timeout);
        try
        {
            // Initialize the captured generation: failover may have retired the preflight generation.
            if (requireIdentity && _core.Sentinel is not null)
                await multiplexer.EnsureReliableCorrectionOrderingAsync(timeoutSource.Token).ConfigureAwait(false);
            return await multiplexer.GetHealthyConnectionAsync(timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new RespireTimeoutException(TrackedConnectionOperation(requireIdentity), timeout, null,
                multiplexer.CaptureConnectionWait());
        }
        catch (RespireTimeoutException ex)
        {
            throw new RespireTimeoutException(TrackedConnectionOperation(requireIdentity), timeout, ex);
        }
    }

    private ValueTask<RespireConnection> GetTrackedClusterConnectionAsync(
        ClusterRouter cluster,
        int? slot,
        bool requireIdentity,
        CancellationToken cancellationToken)
        => GetTrackedReplacementConnectionAsync(cluster, null, slot, requireIdentity, cancellationToken);

    private async ValueTask<RespireConnection> GetTrackedReplacementConnectionAsync(
        ClusterRouter cluster, RespireConnection? askingSource, int? slot,
        bool requireIdentity, CancellationToken cancellationToken, ClusterRouter.DiscoveryRound? discovery = null)
    {
        if (_core.Options.CommandTimeout is not { } timeout)
        {
            return await cluster.GetReplacementConnectionAsync(askingSource, slot, requireIdentity, cancellationToken, discovery)
                .ConfigureAwait(false);
        }

        using var timeoutSource = CommandTimeoutCancellation.Create(cancellationToken, timeout);
        try
        {
            return await cluster.GetReplacementConnectionAsync(askingSource, slot, requireIdentity, timeoutSource.Token, discovery)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new RespireTimeoutException(TrackedConnectionOperation(requireIdentity), timeout, null,
                RespireTimeoutDiagnostics.Capture(RespireCommandStage.Connecting));
        }
        catch (RespireTimeoutException ex)
        {
            throw new RespireTimeoutException(TrackedConnectionOperation(requireIdentity), timeout, ex);
        }
    }

    private async ValueTask<RespireConnection> GetTrackedRedirectConnectionAsync(
        ClusterRouter cluster,
        RespireServerException error,
        RespireConnection source,
        bool requireIdentity,
        CancellationToken cancellationToken,
        int? commandSlot, ClusterRouter.DiscoveryRound? discovery = null)
    {
        if (_core.Options.CommandTimeout is not { } timeout)
        {
            return await cluster.GetTrackedRedirectConnectionAsync(
                    error, source, requireIdentity, cancellationToken, commandSlot, discovery)
                .ConfigureAwait(false);
        }

        using var timeoutSource = CommandTimeoutCancellation.Create(cancellationToken, timeout);
        try
        {
            return await cluster.GetTrackedRedirectConnectionAsync(
                    error, source, requireIdentity, timeoutSource.Token, commandSlot, discovery)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new RespireTimeoutException(TrackedConnectionOperation(requireIdentity), timeout, null,
                RespireTimeoutDiagnostics.Capture(RespireCommandStage.Connecting));
        }
        catch (RespireTimeoutException ex)
        {
            throw new RespireTimeoutException(TrackedConnectionOperation(requireIdentity), timeout, ex);
        }
    }

    // Names the operation in a tracked-connection timeout. Without identity capture only the script
    // itself was waiting, so ACL guidance for CLIENT ID / CLIENT KILL would mislead.
    private static string TrackedConnectionOperation(bool requireIdentity)
        => requireIdentity ? "CLIENT ID / CLIENT KILL" : "script";

    private static TrackedConnectionIdentity GetTrackedConnectionIdentity(
        RespireConnection connection,
        bool reliable,
        bool requiresAsking = false)
        => new(
            new RespireEndpoint(connection.Host, connection.Port),
            reliable ? connection.ServerClientId : 0,
            requiresAsking,
            connection);

#if NET
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
#endif
    private async ValueTask<RespireResult> ExecuteTrackedClusterScriptAsync(
        TrackedScriptExecution execution,
        ClusterRouter cluster,
        RespireConnection connection,
        RespireScript script,
        RespireValue[] tail,
        bool requiresIdentity,
        CancellationToken cancellationToken,
        RespireTelemetry.OperationStart started, ClientSideCacheCoordinator.MutationFence mutationFence)
    {
        var telemetry = started.Timestamp == 0
            ? null : new ClusterScriptTelemetry(_core, script, started);
        try
        {
            RespireResult result;
            try
            {
                result = await ExecuteTrackedClusterCommandAsync(
                    execution, cluster, connection, script.EvalShaOperation, script.EvalShaVerb, script.Sha1, tail,
                    requiresIdentity, cancellationToken, telemetry, mutationFence).ConfigureAwait(false);
            }
            catch (RespireServerException ex) when (ex.Code == RespireErrorCodes.NoScript)
            {
                execution.ErrorObservation.Handled(ex);
                result = await ExecuteTrackedClusterCommandAsync(
                    execution, cluster, execution.Connection, script.EvalOperation, script.EvalVerb, script.Source, tail,
                    requiresIdentity, cancellationToken, telemetry, mutationFence).ConfigureAwait(false);
            }
            telemetry?.Complete();
            return result;
        }
        catch (Exception error)
        {
            telemetry?.Complete(error);
            throw;
        }
    }

#if NET
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
#endif
    private async ValueTask<RespireResult> ExecuteTrackedClusterCommandAsync(
        TrackedScriptExecution execution,
        ClusterRouter cluster,
        RespireConnection initialConnection,
        string operation,
        Verb verb,
        RespireValue body,
        RespireValue[] tail,
        bool requiresIdentity,
        CancellationToken cancellationToken,
        ClusterScriptTelemetry? telemetry, ClientSideCacheCoordinator.MutationFence mutationFence)
    {
        var command = new Cmd2N(verb, body, tail[0], tail[1..]);
        var slot = command.TryGetClusterSlot(out var commandSlot) ? commandSlot : (int?)null;
        ClusterRouter.DiscoveryRound? discovery = null;
        var discoveryPending = false;
        try
        {
            var connection = initialConnection;
            var sendAsking = execution.ConnectionIdentity.RequiresAsking;
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    execution.StartedTimestamp = Stopwatch.GetTimestamp();
                    telemetry?.UseConnection(connection);
                    var reply = await SendOnConnectionCoreAsync(
                            operation, connection, new MutationCommand<SendTimestampCommand<Cmd2N>>(
                                new SendTimestampCommand<Cmd2N>(command, execution), mutationFence),
                            cancellationToken, sendAsking, observation: execution.ErrorObservation)
                        .ConfigureAwait(false);
                    return new RespireResult(in reply, _core.Options.Serializer);
                }
                catch (RespireConnectionRetiredException retirement) when (cluster.CanRetryRetirement(attempt, cancellationToken))
                {
                    cluster.RecordRejection(ref discovery, connection, retirement);
                    execution.ErrorObservation.Handled(retirement);
                    discoveryPending = true;
                    connection = await GetTrackedReplacementConnectionAsync(
                        cluster, sendAsking ? connection : null, slot, requiresIdentity, cancellationToken, discovery).ConfigureAwait(false);
                    discoveryPending = false;
                    execution.Connection = connection;
                    execution.ConnectionIdentity = GetTrackedConnectionIdentity(
                        connection, ClusterRouter.HasReliableCorrectionOrdering(connection), sendAsking);
                }
                catch (RespireServerException error)
                    when (attempt < ClusterRouter.RedirectLimit && ClusterRouter.CanRecover(error, slot))
                {
                    execution.RecordCommandNotApplied();
                    cluster.RecordRejection(ref discovery, connection, error);
                    execution.ErrorObservation.Handled(error);
                    discoveryPending = true;
                    connection = await GetTrackedRedirectConnectionAsync(
                            cluster, error, connection, requiresIdentity, cancellationToken, slot, discovery)
                        .ConfigureAwait(false);
                    discoveryPending = false;
                    sendAsking = error.Code == RespireErrorCodes.Ask;
                    execution.Connection = connection;
                    execution.ConnectionIdentity = GetTrackedConnectionIdentity(
                        connection, ClusterRouter.HasReliableCorrectionOrdering(connection), sendAsking);
                }
            }
        }
        catch (Exception error)
        {
            discovery?.RecordCommandFailure(error, discoveryPending, slot, callerToken: cancellationToken);
            throw;
        }
        finally { discovery?.Finish(); }
    }

#if NET
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
#endif
    private async ValueTask<RespireResult> ExecuteScriptOnConnectionAsync(
        RespireConnection connection,
        RespireScript script,
        RespireValue[] tail,
        CancellationToken cancellationToken,
        TrackedScriptExecution execution,
        RespireTelemetry.OperationStart started, ClientSideCacheCoordinator.MutationFence mutationFence)
    {
        var core = _core;
        var telemetry = RespireTelemetry.StartOperation(
            script.EvalShaOperation,
            connection,
            core.Options.Database,
            storedProcedureName: script.Sha1, started: started);
        try
        {
            var result = await ExecuteScriptOnConnectionCoreAsync(
                    connection, script, tail, cancellationToken, mutationFence, execution.ErrorObservation, execution)
                .ConfigureAwait(false);
            telemetry.Complete(core, script.EvalShaOperation, script.Sha1, connection: connection);
            return result;
        }
        catch (Exception ex)
        {
            telemetry.Complete(core, script.EvalShaOperation, script.Sha1, ex, connection);
            throw;
        }
    }

#if NET
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
#endif
    private async ValueTask<RespireResult> ExecuteScriptOnConnectionCoreAsync(
        RespireConnection connection,
        RespireScript script,
        RespireValue[] tail,
        CancellationToken cancellationToken,
        ClientSideCacheCoordinator.MutationFence mutationFence,
        RespireTelemetry.ErrorObservation observation,
        TrackedScriptExecution? execution = null)
    {
        RespValue reply;
        try
        {
            reply = await SendScriptCommandAsync(
                script.EvalShaOperation, connection, new Cmd2N(script.EvalShaVerb, script.Sha1, tail[0], tail[1..]),
                cancellationToken, execution, mutationFence, script.IsCacheReadOnly, observation).ConfigureAwait(false);
        }
        catch (RespireServerException error) when (error.Code == RespireErrorCodes.NoScript)
        {
            execution?.RecordCommandNotApplied();
            if (execution is not null) execution.StartedTimestamp = Stopwatch.GetTimestamp();
            observation.Handled(error);
            reply = await SendScriptCommandAsync(
                script.EvalOperation, connection, new Cmd2N(script.EvalVerb, script.Source, tail[0], tail[1..]),
                cancellationToken, execution, mutationFence, script.IsCacheReadOnly, observation).ConfigureAwait(false);
        }
        return new RespireResult(in reply, _core.Options.Serializer);
    }

    private ValueTask<RespValue> SendScriptCommandAsync(
        string operation,
        RespireConnection connection,
        Cmd2N command,
        CancellationToken cancellationToken,
        TrackedScriptExecution? execution, ClientSideCacheCoordinator.MutationFence mutationFence, bool cacheReadOnly,
        RespireTelemetry.ErrorObservation observation)
    {
        if (!mutationFence.IsRequired)
        {
            var readOnly = ReadOnlyCommand<Cmd2N>.ForAuditedScript(command, cacheReadOnly);
            return execution is null
                ? SendOnConnectionCoreAsync(operation, connection, readOnly, cancellationToken, observation: observation)
                : SendOnConnectionCoreAsync(operation, connection,
                    new SendTimestampCommand<ReadOnlyCommand<Cmd2N>>(readOnly, execution), cancellationToken, observation: observation);
        }
        var bound = new MutationCommand<Cmd2N>(command, mutationFence);
        return execution is null
            ? SendOnConnectionCoreAsync(operation, connection, bound, cancellationToken, observation: observation)
            : SendOnConnectionCoreAsync(operation, connection,
                new SendTimestampCommand<MutationCommand<Cmd2N>>(bound, execution), cancellationToken, observation: observation);
    }

    /// <summary>
    /// Kills one multiplexed Redis client through a separate control connection and waits for
    /// the server acknowledgement. The acknowledged kill is an ordering barrier: no command
    /// from the target client can execute afterward.
    /// </summary>
    internal ValueTask FenceCorrectionConnectionAsync(
        TrackedConnectionIdentity identity, CancellationToken cancellationToken = default)
        => _core.Corrections.CreateFence(this, identity).EnsureAsync(cancellationToken);

    internal async ValueTask SendCorrectionFenceAsync(
        TrackedConnectionIdentity identity, CancellationToken cancellationToken, Action onAcknowledged)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(identity.ServerClientId);
        var core = _core;
        ObjectDisposedException.ThrowIf(core.Disposed, this);

        // Successful retirement already consumed every accepted reply and closed the socket.
        // A late fence must not kill a reused client ID on a replacement server at that address.
        if (identity.Connection is { DrainedSuccessfully: true }) return;

        await using var correction = core.Cluster is { } cluster && identity.Connection is { } original
            ? cluster.GetCorrectionLease(original) : null;
        await using var sentinelCorrection = core.Sentinel is { } sentinel
            ? sentinel.GetCorrectionLease(identity.Connection
                ?? throw new InvalidOperationException("Sentinel corrections require the original connection identity.")) : null;
        await using var standaloneCorrection = core.Cluster is null && core.Sentinel is null
            ? core.GetCorrectionLease(identity.Endpoint, identity.Connection) : null;
        var pool = standaloneCorrection?.Pool ?? sentinelCorrection?.Pool ?? correction?.Pool
            ?? core.Cluster!.GetDedicatedPool(identity.Endpoint);
        // Cleanup callers bound each attempt. They retire a canceled control connection and
        // retry; no caller may release the owner token before an acknowledged fence.
        var control = await pool.RentAsync(cancellationToken, armHandshakeDeadline: false).ConfigureAwait(false);
        try
        {
            // No command deadline applies. Cleanup cancellation still retires this control
            // connection before retrying the barrier on a replacement.
            var reply = await control.SendAsync(
                    new ClientKillIdCommand(identity.ServerClientId), cancellationToken,
                    armCommandDeadline: false)
                .ConfigureAwait(false);
            if (reply.IsError)
            {
                var error = ResponseReader.ServerError(in reply, "CLIENT KILL");
                reply.Dispose();
                throw error;
            }

            onAcknowledged();
            reply.Dispose();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (control.Multiplexer is { } controlMultiplexer)
                await controlMultiplexer.RetireConnectionAsync(control).ConfigureAwait(false);
            else
                await control.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        finally
        {
            pool.Return(control);
        }

        if (identity.Connection?.Multiplexer is { } originalMultiplexer)
        {
            await originalMultiplexer.RetireConnectionAsync(identity.Connection!).ConfigureAwait(false);
        }
        else if (core.Cluster is { } router)
        {
            await router.RetireConnectionAsync(identity.Endpoint, identity.ServerClientId).ConfigureAwait(false);
        }
        else
        {
            await core.Multiplexer.RetireConnectionAsync(identity.ServerClientId).ConfigureAwait(false);
        }
    }

    internal RespireValue[] BuildScriptTail(RespireKey[]? keys, RespireValue[]? args)
        => BuildScriptTailFromSpans(keys.AsSpan(), args.AsSpan());

    internal RespireValue[] BuildScriptTailFromSpans(
        ReadOnlySpan<RespireKey> keys, ReadOnlySpan<RespireValue> args)
    {
        var keyCount = keys.Length;
        var argCount = args.Length;
        var tail = new RespireValue[1 + keyCount + argCount];
        tail[0] = keyCount;
        for (var i = 0; i < keyCount; i++)
        {
            tail[1 + i] = Key(in keys[i]);
        }

        for (var i = 0; i < argCount; i++)
        {
            tail[1 + keyCount + i] = args[i];
        }

        return tail;
    }

    // The removal script: delete only while this removal's lease key is still alive. The lease
    // is placed — and its reply awaited — before this script is ever sent, and it carries a
    // TTL, so the script's authority to delete expires on the server's own duration clock: a
    // latent copy (flushed to the server, then abandoned) becomes harmless no later than lease
    // expiry, with no client action required. Returns whether the delete ran; 0 means the lease
    // was gone — expired in transit under a stall — and a still-waiting caller retries with a
    // fresh lease. Plain EVAL: removals are rare enough that EVALSHA probing isn't worth a
    // NOSCRIPT fallback on the dedicated connection.
    internal static readonly RespireScript LeasedUnlinkScript = RespireScript.Create("""
        if redis.call('EXISTS', KEYS[2]) == 1 then
          redis.call('UNLINK', KEYS[1])
          redis.call('UNLINK', KEYS[2])
          return 1
        end
        return 0
        """);

    // How long a removal's lease lives: long enough that no stall a successful removal should
    // survive expires it mid-flight (an in-transit expiry costs one retry round trip), short
    // enough to bound the failure path, which may have to outwait it. Mutable so tests can
    // shrink the bound.
    internal TimeSpan RemovalLeaseTtl = TimeSpan.FromSeconds(30);

    // Added to the client-side wait for lease expiry. The server counts the TTL from the
    // script's execution, the client from the lease reply's arrival — strictly later — so the
    // only error source is clock-*rate* drift between the hosts over the lease's lifetime,
    // which this covers by orders of magnitude. No wall-clock instants are ever compared.
    private static readonly TimeSpan LeaseExpiryMargin = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Removal on a dedicated pooled connection, with the wait bounded by the caller's token
    /// and either <see cref="RespireOptions.CommandTimeout"/> or the lease TTL when no command
    /// timeout is configured. Abandoning the wait discards the dedicated connection, killing
    /// the command when it is still queued client-side — but
    /// bytes already flushed can execute server-side after the failure is reported, and a plain
    /// UNLINK landing late would delete a replacement the caller wrote in response to that
    /// failure. So removal is leased (<see cref="LeasedUnlinkScript"/>): the delete only runs
    /// while its lease key lives, and the failure path never surfaces the failure until the
    /// latent script is provably harmless — either its lease was revoked and the revocation's
    /// reply seen, or the lease's TTL has certainly expired on the server (waited out locally;
    /// both are bounded by the TTL, so a wedged server cannot hang removal indefinitely). Only
    /// then can the caller observe the failure and write a replacement, which the latent script
    /// therefore cannot delete. On the multiplexed connections no wait bound could be honored
    /// at all: abandoning a wait there leaves the command queued indefinitely, and killing a
    /// shared connection would fault every innocent in-flight command on it.
    /// </summary>
    internal async ValueTask UnlinkGuardedAsync(RespireKey key, CancellationToken cancellationToken)
    {
        using var observation = RespireTelemetry.ErrorObservation.Rent(force: true);
        var timeout = _core.Options.CommandTimeout ?? RemovalLeaseTtl;
        using var timeoutSource = CommandTimeoutCancellation.Create(cancellationToken, timeout);
        try
        {
            await UnlinkLeasedAsync(key, timeoutSource.Token, timeout, cancellationToken, observation).ConfigureAwait(false);
        }
        catch (RespireTimeoutException ex)
        {
            var error = new RespireTimeoutException("UNLINK", timeout, ex);
            observation.Final(error);
            throw error;
        }
        catch (OperationCanceledException ex) when (
            RespireConnection.IsDeadlineCancellation(ex, timeoutSource.Token, cancellationToken))
        {
            var error = new RespireTimeoutException("UNLINK", timeout, ex,
                RespireTimeoutDiagnostics.Capture());
            observation.Final(error);
            throw error;
        }
        catch (Exception error)
        {
            observation.Final(error);
            throw;
        }
    }

    private async ValueTask UnlinkLeasedAsync(RespireKey key, CancellationToken cancellationToken,
        TimeSpan timeout, CancellationToken callerCancellationToken, RespireTelemetry.ErrorObservation observation)
    {
        while (true)
        {
            var redisKey = Key(in key);
            RespireValue lease;
            if (_core.Cluster is not null && redisKey.TryGetClusterSlot(out var slot))
            {
                lease = CreateClusterRemovalLeaseKey(slot).AsValue();
            }
            else
            {
                RespireKey leaseKey = "respire-rm-lease:" + Guid.NewGuid().ToString("N");
                lease = Key(in leaseKey);
            }

            // The lease must be on the server before the removal script is sent — its reply is
            // the proof. A failure here (including an abandoned wait) leaves nothing latent:
            // the script was never sent, and a lease-set that lands late just expires unused.
            await PlaceLeaseAsync(lease, cancellationToken, observation).ConfigureAwait(false);
            var leaseStart = Stopwatch.GetTimestamp();

            var command = new Cmd2N(Verbs.Eval, LeasedUnlinkScript.Source, 2, [redisKey, lease]);
            RespValue value;
            try
            {
                value = await SendBlockingAsync(
                    "EVAL", command, cancellationToken, LeasedUnlinkScript.Sha1,
                    cancellationTimeout: timeout, callerCancellationToken: callerCancellationToken,
                    observeErrors: false, observation: observation).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not RespireServerException and not ObjectDisposedException)
            {
                // The script's execution is undecided (a server error would prove it ran, and a
                // disposed client was rejected before sending), so the failure must not surface
                // until the latent copy can no longer delete anything written after it.
                await MakeLatentRemovalHarmlessAsync(lease, leaseStart).ConfigureAwait(false);
                throw;
            }

            var ran = ResponseReader.Integer(in value);
            value.Dispose();
            if (ran == 1)
            {
                return;
            }

            // The lease expired before the script executed — a stall longer than the TTL that
            // the caller's bounds chose to sit out. The delete never ran, so retry fresh; each
            // such pass costs at least a full lease lifetime, so the loop cannot spin.
        }
    }

    internal static RespireKey CreateClusterRemovalLeaseKey(int slot)
    {
        var bytes = new byte[20];
        bytes[0] = (byte)'{';
        ClusterHash.WriteRemovalLeaseTag(slot, bytes.AsSpan(1, 2));
        bytes[3] = (byte)'}';
        Guid.NewGuid().TryWriteBytes(bytes.AsSpan(4));
        return new RespireKey(bytes);
    }

    private async ValueTask PlaceLeaseAsync(RespireValue lease, CancellationToken cancellationToken,
        RespireTelemetry.ErrorObservation observation)
    {
        var command = new Cmd4(Verbs.Set, lease, 1, CommandOptionFrames.PXValue, (long)RemovalLeaseTtl.TotalMilliseconds);
        // The removal owner reports only after lease safety and timeout translation.
        var reply = await SendCoreAsync("SET", command, cancellationToken,
            RespireCommandFlags.None, allowReadFrom: false, cursorAffinity: null,
            observation: observation, observeErrors: false).ConfigureAwait(false);
        reply.Dispose();
    }

    /// <summary>
    /// Blocks until the latent removal script provably cannot run its delete: either its lease
    /// is revoked and the revocation's reply seen, or what remains of the lease's TTL (plus
    /// <see cref="LeaseExpiryMargin"/>) has been waited out, after which the server has
    /// certainly expired it. The revocation is uncancelable (no caller token, no
    /// <see cref="RespireOptions.CommandTimeout"/> — once owed it must not be abandonable) and
    /// its wait is bounded by the lease remainder, past which expiry has done its job anyway.
    /// A shorter command timeout can advance the expiry fallback; a caller-side timeout leaves
    /// the multiplexed send queued and observed in the background so a late fault is handled.
    /// </summary>
    private async ValueTask MakeLatentRemovalHarmlessAsync(RespireValue lease, long leaseStart)
    {
        var remaining = RemovalLeaseTtl - Stopwatch.GetElapsedTime(leaseStart);
        if (remaining > TimeSpan.Zero)
        {
            var revoke = RevokeLeaseAsync(new Cmd1(Verbs.Unlink, lease));
            try
            {
                await revoke.WaitAsync(remaining).ConfigureAwait(false);
                return;
            }
            catch (TimeoutException)
            {
                _ = ObserveRevokeAsync(revoke);
            }
            catch (Exception ex) when (ex is RespireException or ObjectDisposedException)
            {
                // The revocation could not be sent or died with its connection. Swallowed — it
                // must not mask the caller's real failure — and expiry below still bounds the
                // latent script's authority.
            }
        }

        var wait = RemovalLeaseTtl + LeaseExpiryMargin - Stopwatch.GetElapsedTime(leaseStart);
        if (wait > TimeSpan.Zero)
        {
            await Task.Delay(wait).ConfigureAwait(false);
        }
    }

    private async Task RevokeLeaseAsync(Cmd1 command)
    {
        try
        {
            var reply = await SendCoreAsync("UNLINK", command, CancellationToken.None,
                RespireCommandFlags.None, allowReadFrom: false, cursorAffinity: null,
                observeErrors: false).ConfigureAwait(false);
            reply.Dispose();
        }
        catch (Exception error)
        {
            // Revocation is cleanup: its failure is handled by lease expiry, not the caller.
            RespireTelemetry.RecordError(error, internallyHandled: true);
            throw;
        }
    }

    private static async Task ObserveRevokeAsync(Task revoke)
    {
        try
        {
            await revoke.ConfigureAwait(false);
        }
        catch
        {
            // An abandoned revocation that fails means its connection died and cannot deliver
            // more bytes; lease expiry decides the race either way.
        }
    }

    /// <summary>
    /// Executes a script on every connection of the original command's cluster node via
    /// <see cref="Infrastructure.RespireConnectionMultiplexer.SendToAllConnectionsAsync{TCommand}"/> — the copy
    /// sharing a connection with an earlier still-buffered command executes after it, so the
    /// script must be idempotent and safe out of order elsewhere. Locally dead connections are
    /// fenced by Redis client ID before a retry can complete. Plain EVAL (no EVALSHA
    /// probing: the callers are rare correction paths) and no caller token or command timeout —
    /// a correction, once owed, must not be abandonable.
    /// </summary>
    internal async ValueTask ExecuteOnAllConnectionsAsync(
        RespireScript script,
        RespireKey[] keys,
        RespireValue[] args,
        TrackedConnectionIdentity connectionIdentity = default,
        RespireTelemetry.ErrorObservation observation = default)
    {
        var core = _core;
        ObjectDisposedException.ThrowIf(core.Disposed, this);
        var cache = core.ClientCache;
        var mutationFence = cache is null ? default : cache.BeginUnknownMutation();
        try
        {
            var tail = new RespireValue[1 + keys.Length + args.Length];
            tail[0] = keys.Length;
            for (var i = 0; i < keys.Length; i++)
            {
                tail[1 + i] = Key(in keys[i]);
            }

            args.CopyTo(tail, 1 + keys.Length);
            if (core.Sentinel is not null && connectionIdentity.Connection is null)
                await core.EnsureConnectedAsync(CancellationToken.None).ConfigureAwait(false);
            var multiplexer = connectionIdentity.Connection?.Multiplexer;
            if (multiplexer is null)
            {
                if (core.Sentinel is { } sentinel)
                {
                    multiplexer = sentinel.Current?.Multiplexer
                        ?? throw new RespireConnectionException("Sentinel primary is unavailable for correction execution.");
                }
                else if (core.Cluster is { } cluster && connectionIdentity.Endpoint.Host is not null)
                {
                    multiplexer = cluster.GetMultiplexer(connectionIdentity.Endpoint);
                }
                else
                {
                    multiplexer = core.Multiplexer;
                }
            }
            var command = new MutationCommand<Cmd2N>(new Cmd2N(Verbs.Eval, script.Source, tail[0], tail[1..]), mutationFence);
            try
            {
                await multiplexer.SendToAllConnectionsAsync(command,
                    connectionIdentity.RequiresAsking, CancellationToken.None, observation).ConfigureAwait(false);
            }
            catch (RespireConnectionRetiredException retirement) when (
                (core.Cluster is not null || core.Sentinel is not null) && connectionIdentity.Connection is not null)
            {
                observation.Handled(retirement);
                // Retirement rejected new acceptance. Wait for the old FIFO and every owed
                // kill barrier before sending the idempotent correction on its original peer.
                try { await multiplexer.RetireAsync().ConfigureAwait(false); }
                catch (Exception) when (multiplexer.RetirementDrained)
                {
                    // A successful owner retry clears the fence IDs, but the original
                    // retirement task remains faulted. Earlier drain failures still propagate.
                    if (multiplexer.HasPendingCorrectionFences)
                        await multiplexer.FenceRetiredConnectionsAsync().ConfigureAwait(false);
                }
                await using var lease = core.Cluster?.GetCorrectionLease(connectionIdentity.Connection);
                await using var sentinelLease = core.Sentinel?.GetCorrectionLease(connectionIdentity.Connection);
                var pool = sentinelLease?.Pool ?? lease!.Pool;
                var control = await pool.RentAsync(CancellationToken.None, armHandshakeDeadline: false).ConfigureAwait(false);
                try
                {
                    using var reply = connectionIdentity.RequiresAsking
                        ? await ClusterRouter.SendAskingUncheckedAsync(control, command, CancellationToken.None, armCommandDeadline: false).ConfigureAwait(false)
                        : await control.SendAsync(command, CancellationToken.None, armCommandDeadline: false).ConfigureAwait(false);
                    if (reply.IsError) throw ResponseReader.ServerError(in reply, "EVAL");
                }
                finally { pool.Return(control); }
            }
        }
        finally
        {
            if (mutationFence.IsRequired)
            {
                cache!.CompleteMutation(in mutationFence);
            }
        }
    }

    // Typed send helpers — one per reply shape, shared by every facet.
    // Synchronous layers borrow the command. Async send/recovery paths still take an
    // owned value before returning, so no command reference survives an await.

    private ValueTask<TResult> ConvertAsync<TCommand, TResult>(
        string operation,
        in TCommand command,
        CancellationToken ct,
        ResponseConverter<RespireClient, TResult> converter,
        bool transferOwnership = false)
        where TCommand : struct, IRespCommand
        => ConvertResponseAsync(operation, command, ct, this, converter, transferOwnership);

    internal ValueTask<TResult> ConvertResponseAsync<TCommand, TState, TResult>(
        string operation,
        in TCommand command,
        CancellationToken ct,
        TState state,
        ResponseConverter<TState, TResult> converter,
        bool transferOwnership = false)
        where TCommand : struct, IRespCommand
    {
        var core = _core;
        if (core.Disposed) ThrowIfDisposedForCommand();
        if (TryDispatchReplica<TCommand, TResult, ConvertedReadySend<TState, TResult>>(
            operation, in command, ct, new(state, converter, transferOwnership), out var replicaResponse))
            return replicaResponse;
        if (CanUseDirectReplySource(operation, in command)
            && command is not IStreamingRespCommand)
        {
            if (core.Cluster is null && core.TryGetReadyPrimaryMultiplexer(out var readyMultiplexer))
            {
                // CommandTimeout is enforced by the connection's deadline sweep and covers the
                // Redis response, not user converter work (conversion runs at the caller).
                var cache = core.ClientCache;
                var mutationFence = cache is null ? default : cache.BeforeCommand(operation, in command);
                return SendOnReadyPrimaryAsync<TCommand, TResult, ConvertedReadySend<TState, TResult>>(
                    operation, readyMultiplexer, command, ct,
                    new ConvertedReadySend<TState, TResult>(state, converter, transferOwnership), cache, mutationFence);
            }
            else if (TryGetDirectReplyCluster(in command, out var cluster))
            {
                return SendOnReadyClusterAsync<TCommand, TResult, ClusterConvertedReadySend<TState, TResult>>(
                    operation, cluster, command, ct, new(state, converter, transferOwnership));
            }
        }

        return ConvertObservedResponseAsync(operation, command, ct, state, converter, transferOwnership);
    }

    internal ValueTask<long> IntegerAsync<TCommand>(string operation, in TCommand command, CancellationToken ct)
        where TCommand : struct, IRespCommand
        => ConvertAsync(
            operation, command, ct,
            static (RespireClient _, in RespValue value) => ResponseReader.Integer(in value));

    internal ValueTask<long> IntegerValuesAsync(
        string operation, Verb verb, RespireValue first, ReadOnlySpan<RespireValue> rest, CancellationToken ct)
        => rest.Length switch
        {
            0 => IntegerAsync(operation, new Cmd1(verb, first), ct),
            1 => IntegerAsync(operation, new Cmd2(verb, first, rest[0]), ct),
            2 => IntegerAsync(operation, new Cmd3(verb, first, rest[0], rest[1]), ct),
            3 => IntegerAsync(operation, new Cmd4(verb, first, rest[0], rest[1], rest[2]), ct),
            4 => IntegerAsync(operation, new Cmd5(verb, first, rest[0], rest[1], rest[2], rest[3]), ct),
            _ => IntegerAsync(operation, new Cmd1N(verb, first, MapValues(rest)), ct),
        };

    internal ValueTask<long> IntegerKeysAsync(
        string operation, Verb verb, ReadOnlySpan<RespireKey> keys, CancellationToken ct)
        => keys.Length switch
        {
            0 => IntegerAsync(operation, new CmdN(verb, []), ct),
            1 => IntegerAsync(operation, new Cmd1(verb, Key(in keys[0])), ct),
            2 => IntegerAsync(operation, new Cmd2(verb, Key(in keys[0]), Key(in keys[1])), ct),
            3 => IntegerAsync(operation, new Cmd3(verb, Key(in keys[0]), Key(in keys[1]), Key(in keys[2])), ct),
            4 => IntegerAsync(
                operation, new Cmd4(verb, Key(in keys[0]), Key(in keys[1]), Key(in keys[2]), Key(in keys[3])), ct),
            _ => IntegerAsync(operation, new CmdN(verb, MapKeys(keys)), ct),
        };

    internal ValueTask<bool> FlagAsync<TCommand>(string operation, in TCommand command, CancellationToken ct)
        where TCommand : struct, IRespCommand
        => ConvertAsync(
            operation, command, ct,
            static (RespireClient _, in RespValue value) => ResponseReader.Flag(in value));

    internal ValueTask<bool> OkOrNullAsync<TCommand>(string operation, in TCommand command, CancellationToken ct)
        where TCommand : struct, IRespCommand
        => ConvertAsync(
            operation, command, ct,
            static (RespireClient _, in RespValue value) => ResponseReader.OkOrNull(in value));

    internal ValueTask<bool> OkResultAsync<TCommand>(string operation, in TCommand command, CancellationToken ct)
        where TCommand : struct, IRespCommand
        => ConvertAsync(
            operation, command, ct,
            static (RespireClient _, in RespValue value) => ResponseReader.Ok(in value));

#if NET
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
#endif
    internal async ValueTask OkAsync<TCommand>(string operation, TCommand command, CancellationToken ct)
        where TCommand : struct, IRespCommand
    {
        _ = await ConvertAsync(operation, command, ct,
            static (RespireClient _, in RespValue value) => ResponseReader.Ok(in value)).ConfigureAwait(false);
    }

    internal ValueTask<string> StringAsync<TCommand>(string operation, in TCommand command, CancellationToken ct)
        where TCommand : struct, IRespCommand
        => ConvertAsync(
            operation, command, ct,
            static (RespireClient _, in RespValue value) => ResponseReader.String(in value));

    internal ValueTask<string?> StringOrNullAsync<TCommand>(string operation, in TCommand command, CancellationToken ct)
        where TCommand : struct, IRespCommand
    {
        var core = _core;
        if (core.Disposed) ThrowIfDisposedForCommand();
        if (TryDispatchReplica<TCommand, string?, StringReadySend>(
            operation, in command, ct, default, out var replicaResponse))
            return replicaResponse;
        if (CanUseDirectReplySource(operation, in command))
        {
            if (core.Cluster is null && core.TryGetReadyPrimaryMultiplexer(out var readyMultiplexer))
            {
                // Specialized bulk-string source: small buffered replies decode straight from the
                // receive buffer instead of round-tripping through a pooled RespValue payload.
                // CommandTimeout is enforced by the connection's deadline sweep.
                var cache = core.ClientCache;
                var mutationFence = cache is null ? default : cache.BeforeCommand(operation, in command);
                return SendOnReadyPrimaryAsync<TCommand, string?, StringReadySend>(
                    operation, readyMultiplexer, command, ct, default, cache, mutationFence);
            }
            else if (TryGetDirectReplyCluster(in command, out var cluster))
            {
                return SendOnReadyClusterAsync<TCommand, string?, StringReadySend>(operation, cluster, command, ct, default);
            }
        }

        return ConvertResponseAsync(
            operation, command, ct, this,
            static (RespireClient _, in RespValue value) => ResponseReader.StringOrNull(in value),
            transferOwnership: false);
    }

    internal ValueTask<byte[]?> BytesOrNullAsync<TCommand>(string operation, in TCommand command, CancellationToken ct)
        where TCommand : struct, IRespCommand
    {
        var core = _core;
        if (core.Disposed) ThrowIfDisposedForCommand();
        if (TryDispatchReplica<TCommand, byte[]?, BytesReadySend>(
            operation, in command, ct, default, out var replicaResponse))
            return replicaResponse;
        if (CanUseDirectReplySource(operation, in command)
            && command is not IStreamingRespCommand)
        {
            if (core.Cluster is null && core.TryGetReadyPrimaryMultiplexer(out var readyMultiplexer))
            {
                var cache = core.ClientCache;
                var mutationFence = cache is null ? default : cache.BeforeCommand(operation, in command);
                return SendOnReadyPrimaryAsync<TCommand, byte[]?, BytesReadySend>(
                    operation, readyMultiplexer, command, ct, default, cache, mutationFence);
            }
            else if (TryGetDirectReplyCluster(in command, out var cluster))
            {
                return SendOnReadyClusterAsync<TCommand, byte[]?, BytesReadySend>(operation, cluster, command, ct, default);
            }
        }
        return ConvertAsync(
            operation, command, ct,
            static (RespireClient _, in RespValue value) => ResponseReader.BytesOrNull(in value));
    }

    internal ValueTask<double> DoubleAsync<TCommand>(string operation, in TCommand command, CancellationToken ct)
        where TCommand : struct, IRespCommand
        => ConvertAsync(
            operation, command, ct,
            static (RespireClient _, in RespValue value) => ResponseReader.Double(in value));

    internal ValueTask<double?> DoubleOrNullAsync<TCommand>(string operation, in TCommand command, CancellationToken ct)
        where TCommand : struct, IRespCommand
        => ConvertAsync(
            operation, command, ct,
            static (RespireClient _, in RespValue value) => ResponseReader.DoubleOrNull(in value));

    internal ValueTask<long?> IntegerOrNullAsync<TCommand>(string operation, in TCommand command, CancellationToken ct)
        where TCommand : struct, IRespCommand
        => ConvertAsync(
            operation, command, ct,
            static (RespireClient _, in RespValue value) => ResponseReader.IntegerOrNull(in value));

    internal ValueTask<long?> IntegerMinusOneOrNullAsync<TCommand>(
        string operation, in TCommand command, CancellationToken ct)
        where TCommand : struct, IRespCommand
        => ConvertAsync(
            operation, command, ct,
            static (RespireClient _, in RespValue value) => ResponseReader.IntegerMinusOneOrNull(in value));

    internal ValueTask<RespireTtl[]> TtlArrayAsync<TCommand>(
        string operation, in TCommand command, CancellationToken ct)
        where TCommand : struct, IRespCommand
        => ConvertAsync(
            operation, command, ct,
            static (RespireClient _, in RespValue value) => ResponseReader.TtlArray(in value));

    internal ValueTask<RespireTtl> SingleTtlArrayAsync<TCommand>(
        string operation, in TCommand command, CancellationToken ct)
        where TCommand : struct, IRespCommand
        => ConvertAsync(
            operation, command, ct,
            static (RespireClient _, in RespValue value) =>
            {
                if (value.Type != RespDataType.Array)
                    throw new RespireProtocolException("A single hash field TTL reply must be an array.");
                var elements = value.AsArray();
                if (elements.Length != 1 || elements[0].Type != RespDataType.Integer)
                    throw new RespireProtocolException("A single hash field TTL reply must contain exactly one integer.");
                return RespireTtl.FromRedisMilliseconds(elements[0].AsInteger());
            });

    internal ValueTask<HashFieldExpiryResult[]> HashFieldExpiryResultArrayAsync<TCommand>(
        string operation, in TCommand command, CancellationToken ct)
        where TCommand : struct, IRespCommand
        => ConvertAsync(
            operation, command, ct,
            static (RespireClient _, in RespValue value) => ResponseReader.HashFieldExpiryResultArray(in value));

    internal ValueTask<bool[]> FlagArrayAsync<TCommand>(string operation, in TCommand command, CancellationToken ct)
        where TCommand : struct, IRespCommand
        => ConvertAsync(
            operation, command, ct,
            static (RespireClient _, in RespValue value) => ResponseReader.FlagArray(in value));

    internal ValueTask<string[]> StringArrayAsync<TCommand>(string operation, in TCommand command, CancellationToken ct)
        where TCommand : struct, IRespCommand
        => ConvertAsync(
            operation, command, ct,
            static (RespireClient _, in RespValue value) => ResponseReader.StringArray(in value));

    internal ValueTask<string?[]> NullableStringArrayAsync<TCommand>(string operation, in TCommand command, CancellationToken ct)
        where TCommand : struct, IRespCommand
        => ConvertAsync(
            operation, command, ct,
            static (RespireClient _, in RespValue value) => ResponseReader.NullableStringArray(in value));

    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    internal ValueTask<T[]> DeserializeArrayAsync<T, TCommand>(
        string operation, in TCommand command, CancellationToken ct)
        where TCommand : struct, IRespCommand
        => ConvertAsync<TCommand, T[]>(
            operation, command, ct,
            static (RespireClient client, in RespValue value) => client.DeserializeArray<T>(in value));

    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    internal ValueTask<T?[]> DeserializeNullableArrayAsync<T, TCommand>(
        string operation, in TCommand command, CancellationToken ct)
        where TCommand : struct, IRespCommand
        => ConvertAsync<TCommand, T?[]>(
            operation, command, ct,
            static (RespireClient client, in RespValue value) => client.DeserializeNullableArray<T>(in value));

    internal ValueTask<long?[]> NullableIntegerArrayAsync<TCommand>(string operation, in TCommand command, CancellationToken ct)
        where TCommand : struct, IRespCommand
        => ConvertResponseAsync(
            operation, command, ct, this,
            static (RespireClient _, in RespValue value) => ResponseReader.NullableIntegerArray(in value));

    internal ValueTask<double?[]> NullableDoubleArrayAsync<TCommand>(string operation, in TCommand command, CancellationToken ct)
        where TCommand : struct, IRespCommand
        => ConvertResponseAsync(
            operation, command, ct, this,
            static (RespireClient _, in RespValue value) => ResponseReader.NullableDoubleArray(in value));

    internal ValueTask<Dictionary<string, string>> StringMapAsync<TCommand>(string operation, in TCommand command, CancellationToken ct)
        where TCommand : struct, IRespCommand
        => ConvertAsync(
            operation, command, ct,
            static (RespireClient _, in RespValue value) => ResponseReader.StringMap(in value));

    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    internal ValueTask<Dictionary<string, T>> DeserializeMapAsync<T, TCommand>(
        string operation, in TCommand command, CancellationToken ct)
        where TCommand : struct, IRespCommand
        => ConvertAsync<TCommand, Dictionary<string, T>>(
            operation, command, ct,
            static (RespireClient client, in RespValue value) => client.DeserializeMap<T>(in value));

    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    internal ValueTask<T?> DeserializeAsync<T, TCommand>(string operation, in TCommand command, CancellationToken ct)
        where TCommand : struct, IRespCommand
        => ConvertAsync<TCommand, T?>(
            operation, command, ct,
            static (RespireClient client, in RespValue value) => client.DeserializeBorrowed<T>(in value));

    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    internal ValueTask<RespireGet<T>> TryDeserializeAsync<T, TCommand>(string operation, in TCommand command, CancellationToken ct)
        where TCommand : struct, IRespCommand
        => ConvertAsync<TCommand, RespireGet<T>>(
            operation, command, ct,
            static (RespireClient client, in RespValue value) => client.TryDeserializeBorrowed<T>(in value));

    internal ValueTask<RespireLease> LeaseAsync<TCommand>(string operation, in TCommand command, CancellationToken ct)
        where TCommand : struct, IRespCommand
        => ConvertAsync(
            operation, command, ct,
            static (RespireClient _, in RespValue value) => new RespireLease(in value),
            transferOwnership: true);

    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    internal T[] DeserializeArray<T>(in RespValue value)
    {
        var elements = value.AsArray();
        var result = new T[elements.Length];
        for (var i = 0; i < elements.Length; i++)
        {
            result[i] = DeserializeBorrowed<T>(in elements[i])!;
        }

        return result;
    }

    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    internal T?[] DeserializeNullableArray<T>(in RespValue value)
    {
        var elements = value.AsArray();
        var result = new T?[elements.Length];
        for (var i = 0; i < elements.Length; i++)
        {
            result[i] = DeserializeBorrowed<T>(in elements[i]);
        }

        return result;
    }

    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    internal Dictionary<string, T> DeserializeMap<T>(in RespValue value)
    {
        var elements = value.AsArray();
        var result = new Dictionary<string, T>(elements.Length / 2);
        for (var i = 0; i + 1 < elements.Length; i += 2)
        {
            result[elements[i].AsString()] = DeserializeBorrowed<T>(in elements[i + 1])!;
        }

        return result;
    }
}

