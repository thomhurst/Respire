using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using Respire.Commands;
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
    private readonly string? _keyPrefix;
    private readonly byte[]? _keyPrefixBytes;
    private readonly bool _ownsCore;
    private readonly bool _broadcastTracking;
    private readonly RespireReadFrom _readFrom;
    private static readonly bool s_getIsReadOnly = RespireCommands.String.GET.IsReadOnly;
    private static readonly bool s_mgetIsReadOnly = RespireCommands.String.MGET.IsReadOnly;

    private RespireClient(ClientCore core, string? keyPrefix, bool ownsCore, RespireReadFrom? readFrom = null)
    {
        _core = core;
        _keyPrefix = keyPrefix;
        _keyPrefixBytes = keyPrefix is null ? null : System.Text.Encoding.UTF8.GetBytes(keyPrefix);
        _ownsCore = ownsCore;
        _readFrom = readFrom ?? core.Options.ReadFrom;
        _broadcastTracking = core.Options.ClientSideCache?.TrackingMode == RespireClientTrackingMode.Broadcast;
        Strings = new StringCommands(this);
        Keys = new KeyCommands(this);
        Locks = new LockCommands(this);
        Hashes = new HashCommands(this);
        Lists = new ListCommands(this);
        Sets = new SetCommands(this);
        SortedSets = new SortedSetCommands(this);
        Streams = new StreamCommands(this);
        Bitmaps = new BitmapCommands(this);
        HyperLogLog = new HyperLogLogCommands(this);
        Geo = new GeoCommands(this);
        VectorSets = new VectorSetCommands(this);
        Scripts = new ScriptCommands(this);
        Functions = new FunctionCommands(this);
        Server = new ServerCommands(this);
    }

    /// <summary>
    /// Connects using a connection string: "host", "host:port", a single-endpoint
    /// StackExchange.Redis-compatible comma-delimited string, or a
    /// <c>redis://[user[:password]@]host[:port][/db]</c> URI
    /// (see <see cref="RespireOptions.Parse"/>).
    /// </summary>
    public static ValueTask<RespireClient> ConnectAsync(string connectionString, CancellationToken cancellationToken = default)
        => ConnectAsync(RespireOptions.Parse(connectionString), cancellationToken);

    /// <summary>Connects eagerly using structured client options.</summary>
    public static async ValueTask<RespireClient> ConnectAsync(RespireOptions options, CancellationToken cancellationToken = default)
    {
        options = (options ?? throw new ArgumentNullException(nameof(options))).ValidateAndSnapshot();
        return await ConnectPrimaryAsync(options, cancellationToken).ConfigureAwait(false);
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
                return await ConnectAsync(candidate, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                (failures ??= []).Add(ex);
                (failureMessages ??= []).Add(
                    $"{candidateIndex}. {FormatCandidateEndpoints(candidate)}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        if (candidateIndex == 0)
        {
            throw new RespireConnectionException("No Redis connection candidates were provided.");
        }

        var aggregate = new AggregateException("All Redis connection candidates failed.", failures!);
        throw new RespireConnectionException(
            "Unable to connect to any Redis endpoint candidate. Attempts:" +
            Environment.NewLine +
            string.Join(Environment.NewLine, failureMessages!),
            aggregate);
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
        return new RespireClient(new ClientCore(options), keyPrefix: null, ownsCore: true);
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
    public IStringCommands Strings { get; }
    /// <inheritdoc/>
    public IKeyCommands Keys { get; }
    /// <inheritdoc/>
    public ILockCommands Locks { get; }
    /// <inheritdoc/>
    public IHashCommands Hashes { get; }
    /// <inheritdoc/>
    public IListCommands Lists { get; }
    /// <inheritdoc/>
    public ISetCommands Sets { get; }
    /// <inheritdoc/>
    public ISortedSetCommands SortedSets { get; }
    /// <inheritdoc/>
    public IStreamCommands Streams { get; }
    /// <inheritdoc/>
    public IBitmapCommands Bitmaps { get; }
    /// <inheritdoc/>
    public IHyperLogLogCommands HyperLogLog { get; }
    /// <inheritdoc/>
    public IGeoCommands Geo { get; }
    /// <inheritdoc/>
    public IVectorSetCommands VectorSets { get; }
    /// <inheritdoc/>
    public IScriptCommands Scripts { get; }

    /// <summary>Redis Functions (Redis 7+).</summary>
    public IFunctionCommands Functions { get; }
    /// <inheritdoc/>
    public IServerCommands Server { get; }

    /// <summary>
    /// A view of this client that prepends <paramref name="prefix"/> to every key (channels and
    /// server-level commands are untouched). Views share this client's connections; disposing a
    /// view is a no-op — dispose the root client.
    /// </summary>
    public IRespireClient WithKeyPrefix(string prefix)
    {
        ArgumentException.ThrowIfNullOrEmpty(prefix);
        return new RespireClient(_core, _keyPrefix is null ? prefix : _keyPrefix + prefix,
            ownsCore: false, readFrom: _readFrom);
    }

    /// <summary>Creates a view that routes eligible read-only commands using <paramref name="readFrom"/>.</summary>
    public IRespireClient WithReadFrom(RespireReadFrom readFrom)
    {
        if (!Enum.IsDefined(readFrom)) throw new ArgumentOutOfRangeException(nameof(readFrom));
        if (readFrom != RespireReadFrom.Primary && _core.Options.UseCluster)
            throw new NotSupportedException("Replica read routing is not supported with Redis Cluster yet.");
        if (readFrom != RespireReadFrom.Primary && _core.Options.ReplicaEndpoints.Count == 0
            && string.IsNullOrWhiteSpace(_core.Options.SentinelPrimaryName))
            throw new InvalidOperationException("Replica read routing requires Sentinel discovery or configured ReplicaEndpoints.");
        return new RespireClient(_core, _keyPrefix, ownsCore: false, readFrom: readFrom);
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
            return ExecuteRawAsync(
                command.Name, args, flags, cancellationToken,
                cacheMutation: command.CacheMutation,
                hasExplicitCacheMutation: command.HasExplicitCacheMutation,
                readKind: RawCommandDescriptorLookup.GetReadKind(command.Name, args));
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
        if (_keyPrefix is null) return ExecuteRawAsync(
            operation, rawArguments, flags, cancellationToken,
            cacheMutation: command.CacheMutation,
            hasExplicitCacheMutation: command.HasExplicitCacheMutation,
            readKind: readKind);
        var prefixError = PrefixModuleKeysOrError(operation, rawArguments, out var prefixedArguments);
        return prefixError is null
            ? ExecuteRawAsync(operation, prefixedArguments, flags, cancellationToken,
                cacheMutation: command.CacheMutation,
                hasExplicitCacheMutation: command.HasExplicitCacheMutation,
                readKind: readKind)
            : ValueTask.FromException<RespireResult>(prefixError);
    }

    private ValueTask ExecuteCommandFireAndForgetAsync(
        RespireCommand command,
        RespireValue[] args,
        CancellationToken cancellationToken)
    {
        if (command.IsCallerSupplied)
        {
            return ExecuteRawFireAndForgetAsync(
                command.Name, args, cancellationToken,
                cacheMutation: command.CacheMutation,
                hasExplicitCacheMutation: command.HasExplicitCacheMutation,
                readKind: RawCommandDescriptorLookup.GetReadKind(command.Name, args));
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
        if (_keyPrefix is null) return ExecuteRawFireAndForgetAsync(
            operation, rawArguments, cancellationToken,
            cacheMutation: command.CacheMutation,
            hasExplicitCacheMutation: command.HasExplicitCacheMutation,
            readKind: readKind);
        var prefixError = PrefixModuleKeysOrError(operation, rawArguments, out var prefixedArguments);
        return prefixError is null
            ? ExecuteRawFireAndForgetAsync(
                operation, prefixedArguments, cancellationToken,
                cacheMutation: command.CacheMutation,
                hasExplicitCacheMutation: command.HasExplicitCacheMutation,
                readKind: readKind)
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
        ValidateResultFlags(flags);
        ValidateCatalogCommand(command);
        args = PrefixCatalogKeys(command.Name, args);

        var storedProcedureName = StoredProcedureName(command.Name, args);
        var commandValue = new CatalogCommand(command, args, ValidateClusterRawKeys(command.Name, args));
        RespValue response;
        if (_core.Cluster is { } cluster
            && DynamicCommandRouting.IsClusterWideMutation(command.Name, args))
        {
            ValidateClusterWideFlags(command.Name, flags);
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
                    noRedirect: HasFlag(flags, RespireCommandFlags.NoRedirect))
                .ConfigureAwait(false);
        }
        else if (storedProcedureName is null)
        {
            response = await SendAsync(command.Name, commandValue, cancellationToken, flags).ConfigureAwait(false);
        }
        else
        {
            response = await SendStoredProcedureAsync(
                    command.Name, commandValue, cancellationToken, storedProcedureName, flags)
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
                command.Name, commandValue, cancellationToken, storedProcedureName)
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
        ReadCommandKind readKind = ReadCommandKind.None)
    {
        ValidateResultFlags(flags);
        var (operation, words, firstArgumentIndex) = ParseRawCommand(command);
        var (storedProcedureName, commandValue) = CreateRawCommand(
            operation, words, firstArgumentIndex, args, cacheMutation, hasExplicitCacheMutation, readKind);
        var isBlocking = RespireCommand.IsBlocking(
            operation,
            RespireCommand.Classify(operation),
            words.AsSpan(firstArgumentIndex),
            args);
        RespValue response;
        if (_core.Cluster is { } cluster
            && DynamicCommandRouting.IsClusterWideMutation(operation, args))
        {
            ValidateClusterWideFlags(operation, flags);
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
                    noRedirect: HasFlag(flags, RespireCommandFlags.NoRedirect))
                .ConfigureAwait(false);
        }
        else if (storedProcedureName is null)
        {
            response = await SendAsync(operation, commandValue, cancellationToken, flags).ConfigureAwait(false);
        }
        else
        {
            response = await SendStoredProcedureAsync(
                    operation, commandValue, cancellationToken, storedProcedureName, flags)
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
        ReadCommandKind readKind = ReadCommandKind.None)
    {
        var (operation, words, firstArgumentIndex) = ParseRawCommand(command);
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
                operation, commandValue, cancellationToken, storedProcedureName)
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
        ValidateResultFlags(flags);
        var (initialOperation, tokens) = command.Build();
        var (operation, firstArgumentIndex) = NormalizeInterpolatedOperation(initialOperation, tokens);
        var arguments = tokens.AsSpan(firstArgumentIndex);
        var storedProcedureName = StoredProcedureName(operation, arguments);
        var routingKeyIndex = GetRawRoutingKeyIndex(operation, tokens, firstArgumentIndex);
        var commandValue = new DynamicCommand(
            tokens, routingKeyIndex, firstArgumentIndex,
            readKind: RawCommandDescriptorLookup.GetReadKind(operation, arguments),
            cursorArgumentIndex: Verb.GetCursorArgumentIndex(operation));
        var isBlocking = RespireCommand.IsBlocking(
            operation, RespireCommand.Classify(operation), arguments);
        RespValue response;
        if (_core.Cluster is { } cluster
            && DynamicCommandRouting.IsClusterWideMutation(operation, arguments))
        {
            ValidateClusterWideFlags(operation, flags);
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
                    noRedirect: HasFlag(flags, RespireCommandFlags.NoRedirect))
                .ConfigureAwait(false);
        }
        else if (storedProcedureName is null)
        {
            response = await SendAsync(operation, commandValue, cancellationToken, flags).ConfigureAwait(false);
        }
        else
        {
            response = await SendStoredProcedureAsync(
                    operation, commandValue, cancellationToken, storedProcedureName, flags)
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
            cursorArgumentIndex: Verb.GetCursorArgumentIndex(operation));
        if (_core.Cluster is { } cluster
            && DynamicCommandRouting.IsClusterWideMutation(operation, arguments))
        {
            await SendClusterWideFireAndForgetAsync(
                    operation, cluster, commandValue, cancellationToken, storedProcedureName)
                .ConfigureAwait(false);
            return;
        }

        await SendFireAndForgetAsync(
                operation, commandValue, cancellationToken, storedProcedureName)
            .ConfigureAwait(false);
    }

    private void ValidateCatalogCommand(RespireCommand command)
    {
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

    private static (string Operation, string[] Words, int FirstArgumentIndex) ParseRawCommand(string command)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        var words = command.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var operation = RawOperationName(words, out var firstArgumentIndex);
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
                Verb.GetCursorArgumentIndex(operation), hasExplicitCacheMutation));
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
        => operation.StartsWith("BF.", StringComparison.Ordinal)
            || operation.StartsWith("CF.", StringComparison.Ordinal)
            || operation.StartsWith("CMS.", StringComparison.Ordinal)
            || operation.StartsWith("TOPK.", StringComparison.Ordinal)
            || operation.StartsWith("TDIGEST.", StringComparison.Ordinal)
            || operation.StartsWith("JSON.", StringComparison.Ordinal)
            || operation.StartsWith("TS.", StringComparison.Ordinal);

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
            "CLIENT" when candidate.EqualsAsciiIgnoreCase("CACHING") => "CLIENT CACHING",
            "CLIENT" when candidate.EqualsAsciiIgnoreCase("TRACKING") => "CLIENT TRACKING",
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
        => SubscribeAsync(new RespireChannel(channel), cancellationToken);

    /// <summary>Subscribes to one channel with per-subscription buffer settings. Redis: SUBSCRIBE.</summary>
    public ValueTask<RespireSubscription> SubscribeAsync(
        string channel, RespireSubscriptionOptions options, CancellationToken cancellationToken)
        => SubscribeAsync(new RespireChannel(channel), options, cancellationToken);

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
        => SubscribeAsync(new RespireChannel(pattern).WithKind(SubscriptionKind.Pattern), cancellationToken);

    /// <summary>Subscribes to one pattern with per-subscription buffer settings. Redis: PSUBSCRIBE.</summary>
    public ValueTask<RespireSubscription> SubscribePatternAsync(
        string pattern, RespireSubscriptionOptions options, CancellationToken cancellationToken)
        => SubscribeAsync(new RespireChannel(pattern).WithKind(SubscriptionKind.Pattern), options, cancellationToken);

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
        => SubscribeAsync(new RespireChannel(channel).WithKind(SubscriptionKind.Sharded), cancellationToken);

    /// <summary>Subscribes to one sharded channel with per-subscription buffer settings. Redis: SSUBSCRIBE.</summary>
    public ValueTask<RespireSubscription> SubscribeShardedAsync(
        string channel, RespireSubscriptionOptions options, CancellationToken cancellationToken)
        => SubscribeAsync(new RespireChannel(channel).WithKind(SubscriptionKind.Sharded), options, cancellationToken);

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
        => _core.Hub.SubscribeAsync(kind, names, options, cancellationToken);

    /// <summary>Publishes raw channel bytes; sharded metadata selects SPUBLISH. Patterns cannot be published.</summary>
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
            : IntegerAsync("PUBLISH", new Cmd2(Verbs.Publish, channel.AsValue(), message), cancellationToken);
    }

    /// <summary>Publishes raw bytes with SPUBLISH. Channel names are not prefixed.</summary>
    public ValueTask<long> PublishShardedAsync(RespireChannel channel, RespireValue message, CancellationToken cancellationToken = default)
    {
        if (channel.IsNotification)
            throw new ArgumentException("Notification descriptors are server-owned and cannot be published.", nameof(channel));
        if (channel.Kind == SubscriptionKind.Pattern)
        {
            throw new ArgumentException("A pattern cannot be published; use a sharded channel.", nameof(channel));
        }
        return IntegerAsync("SPUBLISH", new Cmd2(Verbs.SPublish, channel.AsValue(), message), cancellationToken);
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
                throw new ArgumentException("All targets in one subscription must have the same kind.", nameof(channels));
            }
        }
        return SubscribeCoreAsync(kind, channels.ToArray(), options, cancellationToken);
    }

    /// <summary>Subscribes to raw bytes using the pattern command family.</summary>
    public ValueTask<RespireSubscription> SubscribePatternAsync(RespireChannel channel, CancellationToken cancellationToken = default)
        => SubscribeAsync(channel.WithKind(SubscriptionKind.Pattern), cancellationToken);

    /// <summary>Subscribes to raw bytes with per-subscription buffer settings.</summary>
    public ValueTask<RespireSubscription> SubscribePatternAsync(
        RespireChannel channel, RespireSubscriptionOptions options, CancellationToken cancellationToken)
        => SubscribeAsync(channel.WithKind(SubscriptionKind.Pattern), options, cancellationToken);

    /// <summary>Subscribes to binary targets using the pattern command family.</summary>
    public ValueTask<RespireSubscription> SubscribePatternAsync(ReadOnlySpan<RespireChannel> channels, CancellationToken cancellationToken)
        => SubscribePatternAsync(channels, default, cancellationToken);

    /// <summary>Subscribes to binary targets with per-subscription buffer settings.</summary>
    public ValueTask<RespireSubscription> SubscribePatternAsync(
        ReadOnlySpan<RespireChannel> channels, RespireSubscriptionOptions options, CancellationToken cancellationToken)
        => SubscribeCoreAsync(SubscriptionKind.Pattern, MapChannels(channels, SubscriptionKind.Pattern), options, cancellationToken);

    /// <summary>Subscribes to raw bytes using the sharded command family.</summary>
    public ValueTask<RespireSubscription> SubscribeShardedAsync(RespireChannel channel, CancellationToken cancellationToken = default)
        => SubscribeAsync(channel.WithKind(SubscriptionKind.Sharded), cancellationToken);

    /// <summary>Subscribes to raw bytes with per-subscription buffer settings.</summary>
    public ValueTask<RespireSubscription> SubscribeShardedAsync(
        RespireChannel channel, RespireSubscriptionOptions options, CancellationToken cancellationToken)
        => SubscribeAsync(channel.WithKind(SubscriptionKind.Sharded), options, cancellationToken);

    /// <summary>Subscribes to binary targets using the sharded command family.</summary>
    public ValueTask<RespireSubscription> SubscribeShardedAsync(ReadOnlySpan<RespireChannel> channels, CancellationToken cancellationToken)
        => SubscribeShardedAsync(channels, default, cancellationToken);

    /// <summary>Subscribes to binary targets with per-subscription buffer settings.</summary>
    public ValueTask<RespireSubscription> SubscribeShardedAsync(
        ReadOnlySpan<RespireChannel> channels, RespireSubscriptionOptions options, CancellationToken cancellationToken)
        => SubscribeCoreAsync(SubscriptionKind.Sharded, MapChannels(channels, SubscriptionKind.Sharded), options, cancellationToken);

    private static RespireChannel[] MapChannels(ReadOnlySpan<RespireChannel> channels, SubscriptionKind kind)
    {
        var names = channels.ToArray();
        for (var i = 0; i < names.Length; i++)
        {
            names[i] = names[i].WithKind(kind);
        }
        return names;
    }

    private static RespireChannel[] MapChannels(ReadOnlySpan<string> names, SubscriptionKind kind)
    {
        var channels = new RespireChannel[names.Length];
        for (var i = 0; i < names.Length; i++)
        {
            channels[i] = new RespireChannel(names[i]).WithKind(kind);
        }
        return channels;
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

        // Resolve and own keys before the first await so routing and WATCH use the same bytes.
        var keys = MapKeys(watchKeys);
        int? slot = null;
        for (var i = 0; i < keys.Length; i++)
        {
            keys[i] = keys[i].Snapshot();
            if (_core.Cluster is not null && keys[i].TryGetClusterSlot(out var keySlot))
                RespireTransactionBase.ValidateClusterSlot(keySlot, ref slot);
        }
        return CreateWatchedTransactionAsync(keys, slot, cancellationToken);
    }

    private async ValueTask<RespireWatchedTransaction> CreateWatchedTransactionAsync(
        RespireValue[] watchKeys, int? slot, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_core.Disposed, this);
        var cluster = _core.Cluster;
        var pool = cluster is null ? await _core.GetDedicatedPoolAsync(cancellationToken).ConfigureAwait(false)
            : await cluster.GetDedicatedPoolAsync(slot, cancellationToken, discovery: null).ConfigureAwait(false);
        // The owning pool must follow the lease through commit/disposal, even if topology changes.
        RespireConnection connection;
        if (cluster is null)
            connection = await pool.RentAsync(cancellationToken).ConfigureAwait(false);
        else
            (pool, connection) = await cluster.RentDedicatedConnectionAsync(pool, slot, cancellationToken, discovery: null).ConfigureAwait(false);
        try
        {
            var command = new CmdN(Verbs.Watch, watchKeys);
            using var reply = await SendOnConnectionAsync("WATCH", connection, command, cancellationToken).ConfigureAwait(false);
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

    internal string? KeyPrefix => _keyPrefix;
    internal ReadOnlySpan<byte> KeyPrefixBytes => _keyPrefixBytes;

    /// <inheritdoc/>
    public RespireKey ResolveKey(RespireKey key)
        => _keyPrefix is null ? key : key.Prepend(_keyPrefix);

    /// <summary>Resolves a user key to a command argument, applying this view's key prefix.</summary>
    internal RespireValue Key(in RespireKey key)
        => _keyPrefix is null ? key.AsValue() : key.Prepend(_keyPrefix).AsValue();

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
        ArgumentNullException.ThrowIfNull(value);
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

        var buffer = new ArrayBufferWriter<byte>(256);
        _core.Options.Serializer.Serialize(buffer, value);
        return buffer.WrittenMemory;
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

    internal ValueTask<TResult> CachedGetAsync<TResult>(
        RespireKey resolvedKey,
        CancellationToken cancellationToken,
        ResponseConverter<RespireClient, TResult> converter)
    {
        var cache = _core.ClientCache;
        var command = new Cmd1(Verbs.Get, resolvedKey.AsValue());
        if (_readFrom != RespireReadFrom.Primary && s_getIsReadOnly)
            return ConvertResponseAsync("GET", command, cancellationToken, this, converter);
        if (cache is null)
        {
            return ConvertResponseAsync("GET", command, cancellationToken, this, converter);
        }

        var generation = _core.Sentinel?.Current;
        if (cache.TryGet(in resolvedKey, out var cached) && IsCacheGenerationCurrent(generation))
        {
            return new ValueTask<TResult>(converter(this, in cached));
        }

        return GetAndCacheAsync(resolvedKey, cache, cancellationToken, converter);
    }

    internal ValueTask<TResult[]> CachedGetManyAsync<TResult>(
        ReadOnlySpan<RespireKey> keys,
        CancellationToken cancellationToken,
        ResponseConverter<RespireClient, TResult> converter,
        bool keysResolved = false)
    {
        var cache = _core.ClientCache;
        if (_readFrom != RespireReadFrom.Primary && s_mgetIsReadOnly)
        {
            var arguments = new RespireValue[keys.Length];
            int? clusterSlot = null;
            for (var i = 0; i < keys.Length; i++)
            {
                var resolvedKey = keysResolved ? keys[i] : ResolveKey(keys[i]);
                arguments[i] = resolvedKey.AsValue();
                ValidateMGetClusterSlot(in resolvedKey, ref clusterSlot);
            }
            return ConvertResponseAsync("MGET", new CmdN(Verbs.MGet, arguments), cancellationToken, this,
                (RespireClient client, in RespValue response) =>
                {
                    var values = response.AsArray();
                    var converted = new TResult[values.Length];
                    for (var i = 0; i < values.Length; i++) converted[i] = converter(client, in values[i]);
                    return converted;
                });
        }
        if (keys.Length == 0)
        {
            return ConvertResponseAsync(
                "MGET",
                new CmdN(Verbs.MGet, MapKeys(keys)),
                cancellationToken,
                this,
                (RespireClient client, in RespValue response) =>
                {
                    var values = response.AsArray();
                    var result = new TResult[values.Length];
                    for (var i = 0; i < values.Length; i++)
                    {
                        result[i] = converter(client, in values[i]);
                    }

                    return result;
                });
        }

        if (cache is null)
        {
            var arguments = new RespireValue[keys.Length];
            int? clusterSlot = null;
            for (var i = 0; i < keys.Length; i++)
            {
                var resolvedKey = keysResolved ? keys[i] : ResolveKey(keys[i]);
                arguments[i] = resolvedKey.AsValue();
                ValidateMGetClusterSlot(in resolvedKey, ref clusterSlot);
            }

            return ConvertResponseAsync(
                "MGET",
                new CmdN(Verbs.MGet, arguments),
                cancellationToken,
                this,
                (RespireClient client, in RespValue response) =>
                {
                    var values = response.AsArray();
                    var converted = new TResult[values.Length];
                    for (var i = 0; i < values.Length; i++)
                    {
                        converted[i] = converter(client, in values[i]);
                    }

                    return converted;
                });
        }

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
                generation);
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
        ResponseConverter<RespireClient, TResult> converter)
        => cache.CoalesceConcurrentMisses
            ? GetSharedAndCacheAsync(resolvedKey, cache, cancellationToken, converter)
            : FetchGetAndCacheAsync(resolvedKey, cache, cancellationToken, converter);

#if NET
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
#endif
    private async ValueTask<TResult> GetSharedAndCacheAsync<TResult>(
        RespireKey resolvedKey, ClientSideCacheCoordinator cache, CancellationToken cancellationToken,
        ResponseConverter<RespireClient, TResult> converter)
    {
        var identity = new ClientCacheCommandKey("GET", resolvedKey.AsValue());
        using var response = await cache.CoalesceReadAsync(
            identity, (Client: this, Key: resolvedKey, Cache: cache),
            static (state, token) => state.Client.FetchGetAndCacheAsync(state.Key, state.Cache, token,
                static (RespireClient _, in RespValue value) => value, transferResponse: true),
            cancellationToken).ConfigureAwait(false);
        return converter(this, in response);
    }

#if NET
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
#endif
    private async ValueTask<TResult> FetchGetAndCacheAsync<TResult>(
        RespireKey resolvedKey, ClientSideCacheCoordinator cache, CancellationToken cancellationToken,
        ResponseConverter<RespireClient, TResult> converter, bool transferResponse = false)
    {
        var generation = _core.Sentinel?.Current;
        if (cache.CoalesceConcurrentMisses && cache.TryPeek(in resolvedKey, out var cached)
            && IsCacheGenerationCurrent(generation))
            return converter(this, in cached);
        var token = cache.BeginRead(in resolvedKey);
        var command = new Cmd1(Verbs.Get, token.State.Key.AsValue());
        var response = default(RespValue);
        var released = false;
        var returned = false;
        Action? onRedirect = null;
        if (_core.Cluster is not null)
        {
            onRedirect = () =>
            {
                token = cache.RebaseRead(in token);
            };
        }

        try
        {
            response = await SendTrackedAsync(
                "GET", command, cancellationToken, onRedirect).ConfigureAwait(false);
            released = true;
            cache.CompleteRead(in token, in response, allowInsert: true);
            var result = converter(this, in response);
            returned = transferResponse;
            return result;
        }
        finally
        {
            if (!released)
            {
                cache.CompleteRead(in token, in response, allowInsert: false);
            }
            if (!returned) response.Dispose();
        }
    }

    private async ValueTask<TResult[]> GetManyAndCacheAsync<TResult>(
        RespireKey[] missingKeys,
        TResult[] result,
        int[] missingIndexes,
        int missingCount,
        ClientSideCacheCoordinator cache,
        CancellationToken cancellationToken,
        ResponseConverter<RespireClient, TResult> converter,
        RespireKey[]? allKeys,
        SentinelRouter.Generation? generation)
    {
        var fetchedResult = await (cache.CoalesceConcurrentMisses
            ? GetManySharedAndCacheAsync(missingKeys, result, missingIndexes, missingCount, cache, cancellationToken, converter)
            : FetchManyAndCacheAsync(missingKeys, missingCount, cache, cancellationToken,
                (Client: this, Result: result, Indexes: missingIndexes, Converter: converter),
                static ((RespireClient Client, TResult[] Result, int[] Indexes, ResponseConverter<RespireClient, TResult> Converter) state, in RespValue response) =>
                {
                    var values = response.AsArray();
                    for (var index = 0; index < values.Length; index++)
                        state.Result[state.Indexes[index]] = state.Converter(state.Client, in values[index]);
                    return state.Result;
                })).ConfigureAwait(false);

        return allKeys is null || IsCacheGenerationCurrent(generation)
            ? fetchedResult
            : await FetchManyForCurrentGenerationAsync(allKeys, cache, cancellationToken, converter).ConfigureAwait(false);
    }

    private async ValueTask<TResult[]> FetchManyForCurrentGenerationAsync<TResult>(
        RespireKey[] keys, ClientSideCacheCoordinator cache, CancellationToken cancellationToken,
        ResponseConverter<RespireClient, TResult> converter)
    {
        while (true)
        {
            var generation = _core.Sentinel?.Current;
            var result = new TResult[keys.Length];
            var indexes = new int[keys.Length];
            for (var index = 0; index < indexes.Length; index++) indexes[index] = index;
            var fetchedResult = await (cache.CoalesceConcurrentMisses
                ? GetManySharedAndCacheAsync(keys, result, indexes, keys.Length, cache, cancellationToken, converter)
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
                    })).ConfigureAwait(false);
            if (IsCacheGenerationCurrent(generation)) return fetchedResult;
        }
    }

#if NET
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
#endif
    private async ValueTask<TResult[]> GetManySharedAndCacheAsync<TResult>(
        RespireKey[] missingKeys, TResult[] result, int[] missingIndexes, int missingCount,
        ClientSideCacheCoordinator cache, CancellationToken cancellationToken,
        ResponseConverter<RespireClient, TResult> converter)
    {
        var identityArguments = new RespireValue[missingCount];
        for (var i = 0; i < missingCount; i++) identityArguments[i] = missingKeys[i].AsValue();
        var identity = new ClientCacheCommandKey("MGET", identityArguments);
        using var response = await cache.CoalesceReadAsync(
            identity, (Client: this, Keys: missingKeys, Count: missingCount, Cache: cache),
            static (state, token) => state.Client.FetchManyAndCacheAsync(
                state.Keys, state.Count, state.Cache, token, state.Client,
                static (RespireClient _, in RespValue value) => value, transferResponse: true), cancellationToken).ConfigureAwait(false);
        var values = response.AsArray();
        if (values.Length != missingCount)
            throw new RespireProtocolException($"MGET returned {values.Length} values for {missingCount} keys.");
        for (var i = 0; i < missingCount; i++)
            result[missingIndexes[i]] = converter(this, in values[i]);
        return result;
    }

#if NET
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
#endif
    private async ValueTask<TResult> FetchManyAndCacheAsync<TState, TResult>(
        RespireKey[] missingKeys, int missingCount, ClientSideCacheCoordinator cache,
        CancellationToken cancellationToken, TState state, ResponseConverter<TState, TResult> converter,
        bool transferResponse = false)
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
                "MGET", new CmdN(Verbs.MGet, arguments), cancellationToken, onRedirect).ConfigureAwait(false);
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

    private async ValueTask<RespValue> SendTrackedAsync<TCommand>(
        string operation,
        TCommand command,
        CancellationToken cancellationToken,
        Action? onRedirect = null)
        where TCommand : struct, IRespCommand
    {
        var core = _core;
        ObjectDisposedException.ThrowIf(core.Disposed, this);
        if (core.Cluster is { } cluster)
        {
            return await SendTrackedClusterAsync(
                operation, cluster, command, cancellationToken, onRedirect).ConfigureAwait(false);
        }

        await core.EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        var connection = core.Multiplexer.GetConnection();
        var response = await SendTrackedOnConnectionAsync(
            operation, connection, command, cancellationToken, sendAsking: false).ConfigureAwait(false);
        if (response.IsError)
        {
            var error = ResponseReader.ServerError(in response, operation);
            response.Dispose();
            throw error;
        }

        return response;
    }

    private async ValueTask<RespValue> SendTrackedClusterAsync<TCommand>(
        string operation,
        ClusterRouter cluster,
        TCommand command,
        CancellationToken cancellationToken,
        Action? onRedirect)
        where TCommand : struct, IRespCommand
    {
        var slot = command.TryGetClusterSlot(out var commandSlot) ? commandSlot : (int?)null;
        ClusterRouter.DiscoveryRound? discovery = null;
        // Keep the budget across sends; successful selection does not imply the final route accepts the command.
        var discoveryPending = false;
        try
        {
            var connection = await cluster.GetConnectionAsync(slot, cancellationToken, discovery: null).ConfigureAwait(false);
            var sendAsking = false;
            for (var attempt = 0; ; attempt++)
            {
                RespValue response;
                try
                {
                    response = await SendTrackedOnConnectionAsync(
                        operation, connection, command, cancellationToken, sendAsking).ConfigureAwait(false);
                }
                catch (RespireConnectionRetiredException retirement) when (cluster.CanRetryRetirement(attempt, cancellationToken))
                {
                    cluster.RecordRejection(ref discovery, connection, retirement);
                    _core.ClientCache?.FlushForContinuityLoss();
                    discoveryPending = true;
                    connection = await cluster.GetReplacementConnectionAsync(
                        sendAsking ? connection : null, slot, null, cancellationToken, discovery).ConfigureAwait(false);
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

                _core.ClientCache?.FlushForContinuityLoss();
                cluster.RecordRejection(ref discovery, connection, error);
                discoveryPending = true;
                connection = await cluster.GetRedirectConnectionAsync(error, connection, cancellationToken, slot, discovery)
                    .ConfigureAwait(false);
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
        bool sendAsking)
        where TCommand : struct, IRespCommand
        => RespireTelemetry.IsEnabled
            ? SendTrackedOnConnectionInstrumentedAsync(
                operation, connection, command, cancellationToken, sendAsking)
            : SendTrackedOnConnectionCoreAsync(
                operation, connection, command, cancellationToken, sendAsking);

    private ValueTask<RespValue> SendTrackedOnConnectionCoreAsync<TCommand>(
        string operation,
        RespireConnection connection,
        TCommand command,
        CancellationToken cancellationToken,
        bool sendAsking)
        where TCommand : struct, IRespCommand
    {
        if (sendAsking)
        {
            if (!_broadcastTracking)
            {
                return ClusterRouter.SendTrackedAskingAsync(
                    connection, in command, cancellationToken, operation);
            }
            return ClusterRouter.SendAskingAsync(
                connection, in command, cancellationToken, operation);
        }

        if (_broadcastTracking)
            return connection.SendAsync(command, cancellationToken, commandName: operation);
        var caching = new ClientCachingCommand();
        return connection.SendValidatedPrefixedAsync(
            in caching, in command, cancellationToken, operation);
    }

#if NET
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
#endif
    private async ValueTask<RespValue> SendTrackedOnConnectionInstrumentedAsync<TCommand>(
        string operation,
        RespireConnection connection,
        TCommand command,
        CancellationToken cancellationToken,
        bool sendAsking)
        where TCommand : struct, IRespCommand
    {
        var telemetry = RespireTelemetry.StartOperation(
            operation, connection.Host, connection.Port, _core.Options.Database);
        try
        {
            var response = await SendTrackedOnConnectionCoreAsync(
                operation, connection, command, cancellationToken, sendAsking).ConfigureAwait(false);
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
        TCommand command,
        CancellationToken cancellationToken)
        where TCommand : struct, IRespCommand
        => SendAsync(operation, command, cancellationToken, RespireCommandFlags.None);

    private ValueTask<RespValue> SendAsync<TCommand>(
        string operation,
        TCommand command,
        CancellationToken cancellationToken,
        RespireCommandFlags flags)
        where TCommand : struct, IRespCommand
    {
        var core = _core;
        ObjectDisposedException.ThrowIf(core.Disposed, this);
        var cache = core.ClientCache;
        var readKind = _readFrom == RespireReadFrom.Primary ? ReadCommandKind.None : command.ReadKind;
        var routeRead = readKind != ReadCommandKind.None;
        if (!routeRead && flags == RespireCommandFlags.None
            && cache is not null
            && cache.TryCreateQuery(operation, in command, out var query))
        {
            // Shared GET/MGET producers must populate the same per-key representation,
            // regardless of whether a typed or raw caller wins the miss.
            if (cache.CoalesceConcurrentMisses)
            {
                if (operation == "GET" && query.Query.ArgumentCount == 1)
                    return CachedGetAsync(query.PrimaryKey, cancellationToken,
                        static (RespireClient _, in RespValue value) => value.ToOwned());
                if (operation == "MGET" && query.Query.ArgumentCount > 0)
                    return CachedRawGetManyAsync(query.Query, cancellationToken);
            }

            if (cache.ReuseHashFields && operation == "HMGET" && query.Query.ArgumentCount >= 2
                && cache.CanTrack(query.PrimaryKey))
                return CachedHashGetManyAsync(cache, query, cancellationToken);
            var generation = core.Sentinel?.Current;
            if (cache.TryGet(in query, out var cached) && IsCacheGenerationCurrent(generation))
            {
                return new ValueTask<RespValue>(cached);
            }

            return QueryAndCacheAsync(operation, command, cache, query, cancellationToken);
        }

        var mutationFence = cache is null ? default : cache.BeforeCommand(operation, in command);
        ValueTask<RespValue> response;
        if (routeRead)
        {
            response = SendReadFromAsync(operation, command, readKind, affinity: null, cancellationToken);
        }
        else if (core.Cluster is { } cluster)
        {
            response = SendClusterAsync(
                operation,
                cluster,
                command,
                cancellationToken,
                noRedirect: HasFlag(flags, RespireCommandFlags.NoRedirect)
                    || command is IStreamingRespCommand
                        && command is not IReplayableStreamingRespCommand { CanReplay: true });
        }
        else if (core.Sentinel is not null || !core.Multiplexer.IsInitialized)
        {
            response = SendAfterConnectAsync(operation, command, cancellationToken);
        }
        else
        {
            var connection = core.Multiplexer.GetConnection();
            response = SendOnConnectionAsync(operation, connection, command, cancellationToken);
        }

        return mutationFence.IsRequired
            ? CompleteMutationAsync(response, cache!, mutationFence)
            : response;
    }

    private async ValueTask<RespValue> SendReadFromAsync<TCommand>(
        string operation, TCommand command, ReadCommandKind readKind, ReadAffinity? affinity,
        CancellationToken cancellationToken)
        where TCommand : struct, IRespCommand
    {
        // Scan cursors are server-local, so successive pages must reach the server that issued them.
        // A raw command's own cursor argument says whether it starts a scan or continues one.
        var connection = readKind == ReadCommandKind.CursorRead
            ? await _core.ReadRouter.GetCursorConnectionAsync(_readFrom, affinity,
                affinity is null && CursorCommandMetadata.IsCursorContinuation(in command),
                cancellationToken).ConfigureAwait(false)
            : await _core.ReadRouter.GetConnectionAsync(_readFrom, cancellationToken).ConfigureAwait(false);
        return await SendOnConnectionAsync(operation, connection, command, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends one page of a cursor enumeration. Under a replica read policy, every page of the
    /// enumeration owning <paramref name="affinity"/> reaches the server that issued its cursor.
    /// </summary>
    internal ValueTask<RespValue> SendCursorPageAsync<TCommand>(
        string operation, TCommand command, ReadAffinity affinity, CancellationToken cancellationToken)
        where TCommand : struct, IRespCommand
    {
        ObjectDisposedException.ThrowIf(_core.Disposed, this);
        return _readFrom != RespireReadFrom.Primary
            && command.ReadKind == ReadCommandKind.CursorRead
            ? SendReadFromAsync(operation, command, ReadCommandKind.CursorRead, affinity, cancellationToken)
            : SendAsync(operation, command, cancellationToken);
    }

    /// <summary>This client, or a view of it that sends every command to the primary.</summary>
    internal RespireClient PrimaryReadView
        => _readFrom == RespireReadFrom.Primary
            ? this
            : new RespireClient(_core, _keyPrefix, ownsCore: false, readFrom: RespireReadFrom.Primary);

#if NET
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
#endif
    private async ValueTask<RespValue> CachedRawGetManyAsync(
        ClientCacheCommandKey query, CancellationToken cancellationToken)
    {
        var keys = new RespireKey[query.ArgumentCount];
        for (var index = 0; index < keys.Length; index++) keys[index] = query.GetArgument(index).AsKey();
        var values = await CachedGetManyAsync(keys, cancellationToken,
            static (RespireClient _, in RespValue value) => value.ToOwned(), keysResolved: true).ConfigureAwait(false);
        return RespValue.Array(values);
    }

    private ValueTask<RespValue> QueryAndCacheAsync<TCommand>(
        string operation, TCommand command, ClientSideCacheCoordinator cache,
        ClientSideCacheCoordinator.QueryRequest request, CancellationToken cancellationToken)
        where TCommand : struct, IRespCommand
        => !cache.CoalesceConcurrentMisses
            ? FetchQueryAndCacheAsync(operation, command, cache, request, cancellationToken)
            : cache.CoalesceReadAsync(
                request.Query, (Client: this, Operation: operation, Command: command, Cache: cache, Request: request),
                static (state, token) => state.Client.FetchQueryAndCacheAsync(
                    state.Operation, state.Command, state.Cache, state.Request, token), cancellationToken);

#if NET
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
#endif
    private async ValueTask<RespValue> FetchQueryAndCacheAsync<TCommand>(
        string operation,
        TCommand command,
        ClientSideCacheCoordinator cache,
        ClientSideCacheCoordinator.QueryRequest request,
        CancellationToken cancellationToken)
        where TCommand : struct, IRespCommand
    {
        var generation = _core.Sentinel?.Current;
        if (cache.CoalesceConcurrentMisses && cache.TryPeek(in request, out var cached)
            && IsCacheGenerationCurrent(generation)) return cached;
        var snapshot = SnapshotCommand.Create(in command);
        var token = cache.BeginRead(operation, in request);
        var completed = false;
        Action? onRedirect = null;
        if (_core.Cluster is not null)
        {
            onRedirect = () =>
            {
                token = cache.RebaseRead(in token);
            };
        }

        var response = default(RespValue);
        try
        {
            response = await SendTrackedAsync(
                operation, snapshot, cancellationToken, onRedirect).ConfigureAwait(false);
            cache.CompleteRead(in token, in response, allowInsert: true);
            completed = true;
            return response;
        }
        finally
        {
            if (!completed)
            {
                cache.CompleteRead(in token, in response, allowInsert: false);
                response.Dispose();
            }
        }
    }

#if NET
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
#endif
    private static async ValueTask<TResult> CompleteMutationAsync<TResult>(
        ValueTask<TResult> response,
        ClientSideCacheCoordinator cache,
        ClientSideCacheCoordinator.MutationFence fence)
    {
        try
        {
            return await response.ConfigureAwait(false);
        }
        finally
        {
            cache.CompleteMutation(in fence);
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
        try
        {
            await response.ConfigureAwait(false);
        }
        finally
        {
            cache.CompleteMutation(in fence);
        }
    }

#if NET
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
#endif
    private async ValueTask<RespValue> SendAfterConnectAsync<TCommand>(
        string operation,
        TCommand command,
        CancellationToken cancellationToken)
        where TCommand : struct, IRespCommand
    {
        var core = _core;
        await core.EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        var connection = core.Multiplexer.GetConnection();
        return await SendOnConnectionAsync(operation, connection, command, cancellationToken)
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
        RespireCommandFlags flags = RespireCommandFlags.None)
        where TCommand : struct, IRespCommand
    {
        var core = _core;
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
                        command,
                        cancellationToken,
                        storedProcedureName,
                        HasFlag(flags, RespireCommandFlags.NoRedirect))
                    .ConfigureAwait(false);
            }

            RespireConnection connection;
            if (command.ReadKind != ReadCommandKind.None && _readFrom != RespireReadFrom.Primary)
            {
                connection = await core.ReadRouter.GetConnectionAsync(_readFrom, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await core.EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
                connection = core.Multiplexer.GetConnection();
            }
            return await SendOnConnectionAsync(
                    operation, connection, command, cancellationToken, storedProcedureName)
                .ConfigureAwait(false);
        }
        finally
        {
            if (mutationFence.IsRequired)
            {
                cache!.CompleteMutation(in mutationFence);
            }
        }
    }

    internal ValueTask<RespValue> ResumeRetiredClusterSendAsync<TCommand>(
        string operation, TCommand command, RespireConnection source,
        RespireConnectionRetiredException error, CancellationToken cancellationToken)
        where TCommand : struct, IRespCommand
        => SendClusterAsync(operation, _core.Cluster!, command, cancellationToken,
            initialConnection: source, firstAttempt: 1, initialRetirement: error);

    internal ValueTask<RespValue> ResumeRejectedClusterSendAsync<TCommand>(
        string operation, TCommand command, RespireConnection source,
        RespireServerException error, CancellationToken cancellationToken)
        where TCommand : struct, IRespCommand
        => SendClusterAsync(operation, _core.Cluster!, command, cancellationToken,
            initialConnection: source, firstAttempt: 1, initialRejection: error);

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
        RespireServerException? initialRejection = null)
        where TCommand : struct, IRespCommand
    {
        var slot = command.TryGetClusterSlot(out var commandSlot) ? commandSlot : (int?)null;
        ClusterRouter.DiscoveryRound? discovery = null;
        var discoveryPending = false;
        try
        {
            var connection = initialConnection
                ?? await cluster.GetConnectionAsync(slot, cancellationToken, discovery: null).ConfigureAwait(false);
            if (initialRetirement is not null)
            {
                cluster.RecordRejection(ref discovery, connection, initialRetirement);
                discoveryPending = true;
                connection = await cluster.GetReplacementConnectionAsync(null, slot, null, cancellationToken, discovery)
                    .ConfigureAwait(false);
                discoveryPending = false;
            }
            if (initialRejection is not null)
            {
                cluster.RecordRejection(ref discovery, connection, initialRejection);
                _core.ClientCache?.FlushForContinuityLoss();
                discoveryPending = true;
                connection = await cluster.GetRedirectConnectionAsync(
                    initialRejection, connection, cancellationToken, slot, discovery).ConfigureAwait(false);
                discoveryPending = false;
            }
            var commandDeadline = command is IStreamingRespCommand && _core.Options.CommandTimeout is { } streamTimeout
                ? CommandDeadline.After(Math.Max(1L, (long)streamTimeout.TotalMilliseconds))
                : CommandDeadline.None;
            var sendAsking = initialRejection?.Code == RespireErrorCodes.Ask;
            for (var attempt = firstAttempt; ; attempt++)
            {
                try
                {
                    return await SendOnConnectionAsync(
                            operation, connection, command, cancellationToken, storedProcedureName, sendAsking,
                            commandDeadline, allowStreamingConnectionReroute: false)
                        .ConfigureAwait(false);
                }
                // Retirement rejects a streamed SET before its header is written. Its source is
                // untouched, or its consumed first chunk was restored in front of the source, so
                // the command can move to the replacement connection without losing bytes.
                catch (RespireConnectionRetiredException retirement) when (cluster.CanRetryRetirement(attempt, cancellationToken))
                {
                    commandDeadline = connection.GetReroutedCommandDeadline(commandDeadline);
                    cluster.RecordRejection(ref discovery, connection, retirement);
                    _core.ClientCache?.FlushForContinuityLoss();
                    discoveryPending = true;
                    connection = await cluster.GetReplacementConnectionAsync(
                        sendAsking ? connection : null, slot, null, cancellationToken, discovery).ConfigureAwait(false);
                    discoveryPending = false;
                }
                catch (RespireServerException error)
                    when (!noRedirect && attempt < ClusterRouter.RedirectLimit && ClusterRouter.CanRecover(error, slot))
                {
                    // Learn the new owner before touching the caller-owned stream. A broken seek
                    // must not leave later commands pinned to the stale slot owner.
                    _core.ClientCache?.FlushForContinuityLoss();
                    cluster.RecordRejection(ref discovery, connection, error);
                    discoveryPending = true;
                    connection = await cluster.GetRedirectConnectionAsync(error, connection, cancellationToken, slot, discovery)
                        .ConfigureAwait(false);
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
            }
        }
        catch (Exception error)
        {
            discovery?.RecordCommandFailure(error, discoveryPending, slot, noRedirect, cancellationToken);
            throw;
        }
        finally { discovery?.Finish(); }
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
                                reply = await SendOnConnectionAsync(operation, target, command, cancellationToken)
                                    .ConfigureAwait(false);
                                break;
                            }
                            catch (RespireConnectionRetiredException retirement) when (cluster.CanRetryRetirement(attempt, cancellationToken))
                            {
                                cluster.RecordRejection(ref discovery, target, retirement);
                                // Keep this snapshot endpoint; earlier targets have already accepted the mutation.
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
            throw;
        }
        finally { discovery?.Finish(); }
    }

    private ValueTask SendFireAndForgetAsync<TCommand>(
        string operation,
        TCommand command,
        CancellationToken cancellationToken,
        string? storedProcedureName = null)
        where TCommand : struct, IRespCommand
    {
        var core = _core;
        ObjectDisposedException.ThrowIf(core.Disposed, this);
        if (_readFrom != RespireReadFrom.Primary && command.ReadKind != ReadCommandKind.None)
        {
            // A read discards its reply here, but the policy still decides which server serves it.
            return SendFireAndForgetViaReadRouterAsync(operation, command, cancellationToken, storedProcedureName);
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
                mutationFence);
        }

        if (core.Cluster is { } cluster)
        {
            return SendFireAndForgetClusterAsync(
                operation, cluster, command, cancellationToken, storedProcedureName);
        }

        if (core.Sentinel is not null || !core.Multiplexer.IsInitialized)
        {
            return SendFireAndForgetAfterConnectAsync(
                operation, command, cancellationToken, storedProcedureName);
        }

        var connection = core.Multiplexer.GetConnection();
        return SendFireAndForgetOnConnectionAsync(
            operation, connection, command, cancellationToken, storedProcedureName);
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
        ClientSideCacheCoordinator.MutationFence mutationFence)
        where TCommand : struct, IRespCommand
    {
        try
        {
            var core = _core;
            if (core.Cluster is { } cluster)
            {
                await SendFireAndForgetClusterAsync(
                        operation, cluster, command, cancellationToken, storedProcedureName)
                    .ConfigureAwait(false);
                return;
            }

            await core.EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
            var connection = core.Multiplexer.GetConnection();
            if (RespireCommand.MayCloseWithoutReply(operation))
            {
                await SendFireAndForgetOnConnectionAsync(
                        operation, connection, command, cancellationToken, storedProcedureName)
                    .ConfigureAwait(false);
                return;
            }

            try
            {
                using var response = await SendOnConnectionAsync(
                        operation, connection, command, cancellationToken, storedProcedureName)
                    .ConfigureAwait(false);
            }
            catch (RespireServerException)
            {
                // Fire-and-forget preserves its contract by discarding ordinary server errors.
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
        string? storedProcedureName = null)
        where TCommand : struct, IRespCommand
        => RespireTelemetry.IsEnabled
            ? SendFireAndForgetOnConnectionInstrumentedAsync(
                operation, connection, command, cancellationToken, storedProcedureName)
            : connection.SendFireAndForgetAsync(in command, cancellationToken, operation);

#if NET
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
#endif
    private async ValueTask SendFireAndForgetOnConnectionInstrumentedAsync<TCommand>(
        string operation,
        RespireConnection connection,
        TCommand command,
        CancellationToken cancellationToken,
        string? storedProcedureName)
        where TCommand : struct, IRespCommand
    {
        var core = _core;
        var telemetry = RespireTelemetry.StartOperation(
            operation,
            connection.Host,
            connection.Port,
            core.Options.Database,
            storedProcedureName: storedProcedureName);
        try
        {
            await connection.SendFireAndForgetAsync(in command, cancellationToken, operation).ConfigureAwait(false);
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
        string? storedProcedureName)
        where TCommand : struct, IRespCommand
    {
        var connection = await _core.ReadRouter.GetConnectionAsync(_readFrom, cancellationToken).ConfigureAwait(false);
        await SendFireAndForgetOnConnectionAsync(
                operation, connection, command, cancellationToken, storedProcedureName)
            .ConfigureAwait(false);
    }

    private async ValueTask SendFireAndForgetAfterConnectAsync<TCommand>(
        string operation,
        TCommand command,
        CancellationToken cancellationToken,
        string? storedProcedureName)
        where TCommand : struct, IRespCommand
    {
        var core = _core;
        await core.EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        var connection = core.Multiplexer.GetConnection();
        await SendFireAndForgetOnConnectionAsync(
                operation, connection, command, cancellationToken, storedProcedureName)
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
        string? storedProcedureName)
        where TCommand : struct, IRespCommand
    {
        ClusterRouter.DiscoveryRound? discovery = null;
        var discoveryPending = false;
        try
        {
            if (RespireCommand.MayCloseWithoutReply(operation))
            {
                var slot = command.TryGetClusterSlot(out var commandSlot) ? commandSlot : (int?)null;
                var connection = await cluster.GetConnectionAsync(slot, cancellationToken, discovery: null).ConfigureAwait(false);
                for (var attempt = 0; ; attempt++)
                {
                    try
                    {
                        await SendFireAndForgetOnConnectionAsync(
                                operation, connection, command, cancellationToken, storedProcedureName)
                            .ConfigureAwait(false);
                        return;
                    }
                    catch (RespireConnectionRetiredException retirement) when (cluster.CanRetryRetirement(attempt, cancellationToken))
                    {
                        cluster.RecordRejection(ref discovery, connection, retirement);
                        discoveryPending = true;
                        connection = await cluster.GetReplacementConnectionAsync(null, slot, null, cancellationToken, discovery)
                            .ConfigureAwait(false);
                        discoveryPending = false;
                    }
                }
            }

            try
            {
                using var response = await SendClusterAsync(
                        operation, cluster, command, cancellationToken, storedProcedureName)
                    .ConfigureAwait(false);
            }
            catch (RespireServerException error) when (!ClusterRouter.CanRecover(
                error, command.TryGetClusterSlot(out var failedSlot) ? failedSlot : null))
            {
                // Discard ordinary errors, but surface exhausted redirect or READONLY recovery.
            }
        }
        catch (Exception error)
        {
            discovery?.RecordCommandFailure(error, discoveryPending, cancellationToken);
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
                                        operation, target, command, cancellationToken, storedProcedureName)
                                    .ConfigureAwait(false);
                                break;
                            }
                            catch (RespireConnectionRetiredException retirement) when (cluster.CanRetryRetirement(attempt, cancellationToken))
                            {
                                cluster.RecordRejection(ref discovery, target, retirement);
                                // Retry only this rejected target, never a previously accepted send.
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
            throw;
        }
        finally { discovery?.Finish(); }
    }

    // CommandTimeout is enforced by the connection's deadline sweep (commands are stamped at
    // enqueue), so no per-command CancellationTokenSource or timer is created here.
    private ValueTask<RespValue> SendOnConnectionCoreAsync<TCommand>(
        string operation,
        RespireConnection connection,
        TCommand command,
        CancellationToken cancellationToken,
        bool sendAsking = false,
        CommandDeadline commandDeadline = default,
        bool allowStreamingConnectionReroute = true)
        where TCommand : struct, IRespCommand
        => sendAsking
            ? ClusterRouter.SendAskingAsync(connection, in command, cancellationToken, operation,
                commandDeadline, allowStreamingConnectionReroute)
            : connection.SendCheckedAsync(in command, cancellationToken, operation,
                commandDeadline, allowStreamingConnectionReroute);

    /// <summary>Sends a streaming GET through the current standalone or Cluster route.</summary>
    internal ValueTask<Stream?> SendBulkStreamAsync<TCommand>(
        string operation,
        TCommand command,
        CancellationToken cancellationToken)
        where TCommand : struct, IRespCommand
    {
        var core = _core;
        ObjectDisposedException.ThrowIf(core.Disposed, this);
        if (core.Cluster is { } cluster)
        {
            return SendClusterBulkStreamAsync(operation, cluster, command, cancellationToken);
        }

        if (_readFrom != RespireReadFrom.Primary && command.ReadKind != ReadCommandKind.None)
            return SendBulkStreamViaReadRouterAsync(operation, command, cancellationToken);

        if (!core.Multiplexer.IsInitialized)
        {
            return SendBulkStreamAfterConnectAsync(operation, command, cancellationToken);
        }

        return SendBulkStreamOnConnectionAsync(
            operation, core.Multiplexer.GetConnection(), command, cancellationToken);
    }

    private async ValueTask<Stream?> SendBulkStreamViaReadRouterAsync<TCommand>(
        string operation, TCommand command, CancellationToken cancellationToken)
        where TCommand : struct, IRespCommand
    {
        var connection = await _core.ReadRouter.GetConnectionAsync(_readFrom, cancellationToken).ConfigureAwait(false);
        return await SendBulkStreamOnConnectionAsync(operation, connection, command, cancellationToken).ConfigureAwait(false);
    }

#if NET
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
#endif
    private async ValueTask<Stream?> SendBulkStreamAfterConnectAsync<TCommand>(
        string operation,
        TCommand command,
        CancellationToken cancellationToken)
        where TCommand : struct, IRespCommand
    {
        await _core.Multiplexer.EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        return await SendBulkStreamOnConnectionAsync(
            operation, _core.Multiplexer.GetConnection(), command, cancellationToken).ConfigureAwait(false);
    }

#if NET
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
#endif
    private async ValueTask<Stream?> SendClusterBulkStreamAsync<TCommand>(
        string operation,
        ClusterRouter cluster,
        TCommand command,
        CancellationToken cancellationToken)
        where TCommand : struct, IRespCommand
    {
        var slot = command.TryGetClusterSlot(out var commandSlot) ? commandSlot : (int?)null;
        ClusterRouter.DiscoveryRound? discovery = null;
        var discoveryPending = false;
        try
        {
            var connection = await cluster.GetConnectionAsync(slot, cancellationToken, discovery: null).ConfigureAwait(false);
            var sendAsking = false;
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    return await SendBulkStreamOnConnectionAsync(
                        operation, connection, command, cancellationToken, sendAsking).ConfigureAwait(false);
                }
                catch (RespireConnectionRetiredException retirement)
                    when (cluster.CanRetryRetirement(attempt, cancellationToken))
                {
                    cluster.RecordRejection(ref discovery, connection, retirement);
                    _core.ClientCache?.FlushForContinuityLoss();
                    discoveryPending = true;
                    connection = await cluster.GetReplacementConnectionAsync(
                        sendAsking ? connection : null, slot, null, cancellationToken, discovery).ConfigureAwait(false);
                    discoveryPending = false;
                }
                catch (RespireServerException error)
                    when (attempt < ClusterRouter.RedirectLimit && ClusterRouter.CanRecover(error, slot))
                {
                    _core.ClientCache?.FlushForContinuityLoss();
                    cluster.RecordRejection(ref discovery, connection, error);
                    discoveryPending = true;
                    connection = await cluster.GetRedirectConnectionAsync(error, connection, cancellationToken, slot, discovery)
                        .ConfigureAwait(false);
                    discoveryPending = false;
                    sendAsking = error.Code == RespireErrorCodes.Ask;
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
        bool sendAsking = false)
        where TCommand : struct, IRespCommand
    {
        if (RespireTelemetry.IsEnabled)
        {
            return SendBulkStreamOnConnectionInstrumentedAsync(
                operation, connection, command, cancellationToken, sendAsking);
        }

        if (sendAsking)
        {
            return ClusterRouter.SendAskingBulkStreamAsync(
                connection, in command, cancellationToken, operation);
        }

        return connection.SendBulkStreamAsync(in command, cancellationToken, operation);
    }

#if NET
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
#endif
    private async ValueTask<Stream?> SendBulkStreamOnConnectionInstrumentedAsync<TCommand>(
        string operation,
        RespireConnection connection,
        TCommand command,
        CancellationToken cancellationToken,
        bool sendAsking)
        where TCommand : struct, IRespCommand
    {
        var core = _core;
        var previousActivity = Activity.Current;
        var telemetry = RespireTelemetry.StartOperation(
            operation, connection.Host, connection.Port, core.Options.Database);
        var telemetryCompleted = 0;
        void CompleteTelemetry(Exception? error)
        {
            if (Interlocked.Exchange(ref telemetryCompleted, 1) == 0)
            {
                telemetry.Complete(operation, connection.Host, connection.Port, core.Options.Database,
                    error: error, connection: connection);
            }
        }

        try
        {
            var stream = sendAsking
                ? await ClusterRouter.SendAskingBulkStreamAsync(
                    connection, in command, cancellationToken, operation, CompleteTelemetry).ConfigureAwait(false)
                : await connection.SendBulkStreamAsync(
                    in command, cancellationToken, operation, CompleteTelemetry).ConfigureAwait(false);
            if (stream is null)
            {
                CompleteTelemetry(null);
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
            if (!ReferenceEquals(Activity.Current, previousActivity))
            {
                Activity.Current = previousActivity;
            }
        }
    }

    // Endpoint-pinned fan-outs can retry a rejected target without replaying accepted peers.
    // Do not use this for WATCH or connection-scoped CLIENT operations, whose socket is part of their contract.
    internal async ValueTask<RespValue> SendToClusterTargetAsync<TCommand>(
        string operation, RespireConnection connection, TCommand command, CancellationToken cancellationToken)
        where TCommand : struct, IRespCommand
    {
        var cluster = _core.Cluster;
        ClusterRouter.DiscoveryRound? discovery = null;
        var discoveryPending = false;
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    return await SendOnConnectionAsync(operation, connection, command, cancellationToken).ConfigureAwait(false);
                }
                catch (RespireConnectionRetiredException retirement) when (cluster is not null && cluster.CanRetryRetirement(attempt, cancellationToken))
                {
                    cluster.RecordRejection(ref discovery, connection, retirement);
                    discoveryPending = true;
                    connection = await cluster.GetReplacementConnectionAsync(connection, null, null, cancellationToken, discovery).ConfigureAwait(false);
                    discoveryPending = false;
                }
            }
        }
        catch (Exception error)
        {
            discovery?.RecordCommandFailure(error, discoveryPending, cancellationToken);
            throw;
        }
        finally { discovery?.Finish(); }
    }

    internal ValueTask<RespValue> SendOnConnectionAsync<TCommand>(
        string operation,
        RespireConnection connection,
        TCommand command,
        CancellationToken cancellationToken,
        string? storedProcedureName = null,
        bool sendAsking = false,
        CommandDeadline commandDeadline = default,
        bool allowStreamingConnectionReroute = true)
        where TCommand : struct, IRespCommand
        => RespireTelemetry.IsEnabled
            ? SendOnConnectionInstrumentedAsync(
                operation, connection, command, cancellationToken, storedProcedureName, sendAsking,
                commandDeadline, allowStreamingConnectionReroute)
            : SendOnConnectionCoreAsync(operation, connection, command, cancellationToken, sendAsking,
                commandDeadline, allowStreamingConnectionReroute);

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
        bool allowStreamingConnectionReroute)
        where TCommand : struct, IRespCommand
    {
        var core = _core;
        var telemetry = RespireTelemetry.StartOperation(
            operation,
            connection.Host,
            connection.Port,
            core.Options.Database,
            storedProcedureName: storedProcedureName);
        try
        {
            var response = await SendOnConnectionCoreAsync(
                    operation, connection, command, cancellationToken, sendAsking,
                    commandDeadline, allowStreamingConnectionReroute)
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

    /// <summary>
    /// Sends an intentionally blocking command (BLPOP, blocking XREADGROUP, …) on a dedicated
    /// pooled connection so it cannot stall multiplexed traffic. No command timeout applies —
    /// blocking is the point; cancel via the token (which abandons the connection).
    /// </summary>
    internal async ValueTask<RespValue> SendBlockingAsync<TCommand>(
        string operation,
        TCommand command,
        CancellationToken cancellationToken,
        string? storedProcedureName = null,
        bool noRedirect = false,
        TimeSpan? cancellationTimeout = null, CancellationToken callerCancellationToken = default)
        where TCommand : struct, IRespCommand
    {
        var core = _core;
        ObjectDisposedException.ThrowIf(core.Disposed, this);
        var cache = core.ClientCache;
        var mutationFence = cache is null ? default : cache.BeforeCommand(operation, in command);
        try
        {
            if (core.Cluster is { } cluster)
            {
                return await SendBlockingClusterAsync(
                        operation, cluster, command, cancellationToken, storedProcedureName, noRedirect,
                        cancellationTimeout, callerCancellationToken)
                    .ConfigureAwait(false);
            }

            var sentinelStarted = core.Sentinel is null ? 0 : RespireTelemetry.CaptureStartTimestamp();
            var telemetry = core.Sentinel is null ? RespireTelemetry.StartOperation(
                operation,
                core.Endpoint,
                core.Options.Database,
                storedProcedureName: storedProcedureName) : default;
            RespireConnection? connection = null;
            DedicatedConnectionPool? pool = null;
            var returned = false;
            try
            {
                pool = await core.GetDedicatedPoolAsync(cancellationToken).ConfigureAwait(false);
                connection = await pool.RentAsync(cancellationToken).ConfigureAwait(false);
                if (core.Sentinel is not null)
                    telemetry = RespireTelemetry.StartOperation(operation, connection.Host, connection.Port,
                        core.Options.Database, storedProcedureName: storedProcedureName, started: sentinelStarted);
                var response = await connection.SendWithoutResponseTimeoutAsync(command, cancellationToken)
                    .ConfigureAwait(false);
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
                            core.Sentinel is null ? (RespireEndpoint?)core.Endpoint : null))
                    : null;
                if (connection is null)
                    RespireTelemetry.RecordUnroutedFailure(operation, core.Options.Database,
                        sentinelStarted, timeoutError ?? ex, storedProcedureName);
                telemetry.Complete(core, operation, storedProcedureName, timeoutError ?? ex, connection);
                if (connection is not null && !returned)
                {
                    // The connection may still be mid-block server-side; don't return it to the pool.
                    await pool!.DiscardAsync(connection).ConfigureAwait(false);
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
        TimeSpan? cancellationTimeout, CancellationToken callerCancellationToken)
        where TCommand : struct, IRespCommand
    {
        var core = _core;
        var slot = command.TryGetClusterSlot(out var commandSlot) ? commandSlot : (int?)null;
        var pool = await cluster.GetDedicatedPoolAsync(slot, cancellationToken, discovery: null).ConfigureAwait(false);
        RespireTelemetry.OperationScope telemetry = default;
        var telemetryStarted = false;
        var sendAsking = false;
        RespireConnection? askingSource = null;
        RespireServerException? askRedirect = null;

        ClusterRouter.DiscoveryRound? discovery = null;
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                RespireConnection? connection = null;
                var returned = false;
                var acquiringRedirectPool = false;
                try
                {
                    (pool, connection) = await cluster.RentDedicatedConnectionAsync(
                        pool, slot, cancellationToken, discovery, askRedirect: askRedirect, redirectSource: askingSource).ConfigureAwait(false);
                    if (!telemetryStarted)
                    {
                        telemetry = RespireTelemetry.StartOperation(
                            operation,
                            connection.Host,
                            connection.Port,
                            core.Options.Database,
                            storedProcedureName: storedProcedureName);
                        telemetryStarted = true;
                    }

                    var response = await (sendAsking
                            ? ClusterRouter.SendBlockingAskingUncheckedAsync(
                                connection, in command, cancellationToken)
                            : connection.SendWithoutResponseTimeoutAsync(command, cancellationToken))
                        .ConfigureAwait(false);
                    sendAsking = false;
                    if (response.IsError)
                    {
                        var error = ResponseReader.ServerError(in response, operation);
                        response.Dispose();
                        if (!noRedirect && attempt < ClusterRouter.RedirectLimit && ClusterRouter.CanRecover(error, slot))
                        {
                            core.ClientCache?.FlushForContinuityLoss();
                            // The source reply completed; no redirected command has been accepted yet.
                            cluster.RecordRejection(ref discovery, connection, error);
                            acquiringRedirectPool = true;
                            var redirectedPool = await cluster.GetRedirectDedicatedPoolAsync(
                                    error, connection, cancellationToken, slot, discovery)
                                .ConfigureAwait(false);
                            acquiringRedirectPool = false;
                            pool.Return(connection);
                            returned = true;
                            pool = redirectedPool;
                            sendAsking = error.Code == RespireErrorCodes.Ask;
                            askingSource = sendAsking ? connection : null;
                            askRedirect = sendAsking ? error : null;
                            continue;
                        }

                        pool.Return(connection);
                        returned = true;
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
                    telemetry.Complete(core, operation, storedProcedureName, timeoutError ?? ex, connection);
                    if (connection is not null && !returned)
                    {
                        await pool.DiscardAsync(connection).ConfigureAwait(false);
                    }

                    if (timeoutError is not null) throw timeoutError;
                    throw;
                }
            }
        }
        finally { discovery?.Finish(); }
    }

    internal ValueTask<RespireConnection> AcquireConnectionAsync(CancellationToken cancellationToken)
        => AcquireConnectionAsync(slot: null, cancellationToken);

    internal async ValueTask<RespireConnection> AcquireConnectionAsync(
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

    internal sealed class TrackedScriptExecution
    {
        internal TrackedScriptExecution(
            RespireConnection connection, TrackedConnectionIdentity connectionIdentity,
            Action<long>? onSerialized = null, Action? onCommandNotApplied = null)
        {
            Connection = connection;
            ConnectionIdentity = connectionIdentity;
            OnSerialized = onSerialized;
            OnCommandNotApplied = onCommandNotApplied;
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

        internal ValueTask<RespireResult> Response { get; set; }
    }

    /// <summary>
    /// Records <see cref="TrackedScriptExecution.StartedTimestamp"/> when the connection serializes
    /// the command. Serialization happens at enqueue, after any wait for in-flight ring capacity, so
    /// a lease measured from it does not count time parked behind other commands. A retried enqueue
    /// serializes again, so the last write wins.
    /// </summary>
    private readonly struct SendTimestampCommand<TCommand>(TCommand command, TrackedScriptExecution execution) : IRespCommand
        where TCommand : struct, IRespCommand
    {
        public void Write(ref RespWriter writer)
        {
            command.Write(ref writer);
            execution.RecordSerialized(Stopwatch.GetTimestamp());
        }

        public ReadCommandKind ReadKind => command.ReadKind;

        public void OnAccepted() => execution.RecordAccepted();

        public bool TryGetPrimaryKey(out RespireValue key) => command.TryGetPrimaryKey(out key);

        public bool TryGetClusterSlot(out int slot) => command.TryGetClusterSlot(out slot);

        public bool TryGetClientCacheKey(string operation, out ClientCacheCommandKey key)
            => command.TryGetClientCacheKey(operation, out key);
    }

    internal ValueTask<RespireResult> ExecuteScriptAsync(
        RespireScript script,
        RespireValue[] tail,
        CancellationToken cancellationToken)
    {
        var core = _core;
        ObjectDisposedException.ThrowIf(core.Disposed, this);
        var cache = core.ClientCache;
        var mutationFence = cache is null || script.IsCacheReadOnly ? default : cache.BeginUnknownMutation();
        var response = ExecuteScriptCoreAsync(script, tail, cancellationToken);
        return mutationFence.IsRequired
            ? CompleteMutationAsync(response, cache!, mutationFence)
            : response;
    }

#if NET
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
#endif
    private async ValueTask<RespireResult> ExecuteScriptCoreAsync(
        RespireScript script,
        RespireValue[] tail,
        CancellationToken cancellationToken)
    {
        var core = _core;
        if (core.Cluster is { } cluster)
        {
            var arguments = tail[1..];
            RespValue clusterReply;
            try
            {
                clusterReply = await SendClusterAsync(
                        script.EvalShaOperation, cluster,
                        new Cmd2N(script.EvalShaVerb, script.Sha1, tail[0], arguments),
                        cancellationToken, script.Sha1)
                    .ConfigureAwait(false);
            }
            catch (RespireServerException ex) when (ex.Code == RespireErrorCodes.NoScript)
            {
                clusterReply = await SendClusterAsync(
                        script.EvalOperation, cluster,
                        new Cmd2N(script.EvalVerb, script.Source, tail[0], arguments),
                        cancellationToken, script.Sha1)
                    .ConfigureAwait(false);
            }

            return new RespireResult(in clusterReply, _core.Options.Serializer);
        }

        var started = RespireTelemetry.CaptureStartTimestamp();
        var telemetry = default(RespireTelemetry.OperationScope);
        RespireConnection? connection = null;
        try
        {
            if (script.IsReadOnly && _readFrom != RespireReadFrom.Primary)
                connection = await core.ReadRouter.GetConnectionAsync(_readFrom, cancellationToken).ConfigureAwait(false);
            else
            {
                await core.EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
                connection = core.Multiplexer.GetConnection();
            }
            telemetry = RespireTelemetry.StartOperation(script.EvalShaOperation, connection.Host, connection.Port,
                core.Options.Database, storedProcedureName: script.Sha1, started: started);
            var result = await ExecuteScriptOnConnectionCoreAsync(connection, script, tail, cancellationToken)
                .ConfigureAwait(false);
            telemetry.Complete(core, script.EvalShaOperation, script.Sha1, connection: connection);
            return result;
        }
        catch (Exception ex)
        {
            if (connection is null)
                RespireTelemetry.RecordUnroutedFailure(script.EvalShaOperation, core.Options.Database,
                    started, ex, script.Sha1, endpoint: core.Sentinel is null && _readFrom == RespireReadFrom.Primary
                        ? core.Endpoint : (RespireEndpoint?)null);
            telemetry.Complete(core, script.EvalShaOperation, script.Sha1, ex, connection);
            throw;
        }
    }

    /// <summary>
    /// Starts a cache script on a known multiplexed connection. The caller keeps the Redis
    /// client ID even when the reply wait fails, so it can establish a server-side barrier for
    /// that exact command before surfacing the failure.
    /// </summary>
    internal async ValueTask<TrackedScriptExecution> StartTrackedScriptExecutionAsync(
        RespireScript script,
        RespireKey[] keys,
        RespireValue[] args,
        CancellationToken cancellationToken,
        bool requireReliableCorrectionOrdering = false,
        bool captureSendTimestampOnly = false,
        Action<long>? onSerialized = null,
        Action? onCommandNotApplied = null)
    {
        var core = _core;
        ObjectDisposedException.ThrowIf(core.Disposed, this);
        var cache = core.ClientCache;
        var mutationFence = cache is null || script.IsReadOnly ? default : cache.BeginUnknownMutation();
        var responseOwnsFence = false;
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
                connection, core.Cluster?.HasReliableCorrectionOrdering(connection) ?? true);
            var execution = new TrackedScriptExecution(connection, identity, onSerialized, onCommandNotApplied);
            ValueTask<RespireResult> response;
            if (core.Cluster is { } router)
            {
                response = ExecuteTrackedClusterScriptAsync(
                    execution, router, connection, script, tail, requiresIdentity, cancellationToken);
            }
            else
            {
                execution.StartedTimestamp = Stopwatch.GetTimestamp();
                response = ExecuteScriptOnConnectionAsync(connection, script, tail, cancellationToken, execution);
            }
            execution.Response = mutationFence.IsRequired
                ? CompleteMutationAsync(response, cache!, mutationFence)
                : response;
            responseOwnsFence = mutationFence.IsRequired;
            return execution;
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
        CancellationToken cancellationToken)
    {
        try
        {
            return await ExecuteTrackedClusterCommandAsync(
                    execution, cluster, connection, script.EvalShaOperation, script.EvalShaVerb, script.Sha1, script.Sha1, tail,
                    requiresIdentity, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (RespireServerException ex) when (ex.Code == RespireErrorCodes.NoScript)
        {
            return await ExecuteTrackedClusterCommandAsync(
                    execution, cluster, execution.Connection, script.EvalOperation, script.EvalVerb, script.Source, script.Sha1, tail,
                    requiresIdentity, cancellationToken)
                .ConfigureAwait(false);
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
        string storedProcedureName,
        RespireValue[] tail,
        bool requiresIdentity,
        CancellationToken cancellationToken)
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
                    var reply = await SendOnConnectionAsync(
                            operation, connection, new SendTimestampCommand<Cmd2N>(command, execution),
                            cancellationToken, storedProcedureName, sendAsking)
                        .ConfigureAwait(false);
                    return new RespireResult(in reply, _core.Options.Serializer);
                }
                catch (RespireConnectionRetiredException retirement) when (cluster.CanRetryRetirement(attempt, cancellationToken))
                {
                    cluster.RecordRejection(ref discovery, connection, retirement);
                    discoveryPending = true;
                    connection = await GetTrackedReplacementConnectionAsync(
                        cluster, sendAsking ? connection : null, slot, requiresIdentity, cancellationToken, discovery).ConfigureAwait(false);
                    discoveryPending = false;
                    execution.Connection = connection;
                    execution.ConnectionIdentity = GetTrackedConnectionIdentity(
                        connection, cluster.HasReliableCorrectionOrdering(connection), sendAsking);
                }
                catch (RespireServerException error)
                    when (attempt < ClusterRouter.RedirectLimit && ClusterRouter.CanRecover(error, slot))
                {
                    execution.RecordCommandNotApplied();
                    cluster.RecordRejection(ref discovery, connection, error);
                    discoveryPending = true;
                    connection = await GetTrackedRedirectConnectionAsync(
                            cluster, error, connection, requiresIdentity, cancellationToken, slot, discovery)
                        .ConfigureAwait(false);
                    discoveryPending = false;
                    sendAsking = error.Code == RespireErrorCodes.Ask;
                    execution.Connection = connection;
                    execution.ConnectionIdentity = GetTrackedConnectionIdentity(
                        connection, cluster.HasReliableCorrectionOrdering(connection), sendAsking);
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
        TrackedScriptExecution execution)
    {
        var core = _core;
        var telemetry = RespireTelemetry.StartOperation(
            script.EvalShaOperation,
            connection.Host,
            connection.Port,
            core.Options.Database,
            storedProcedureName: script.Sha1);
        try
        {
            var result = await ExecuteScriptOnConnectionCoreAsync(
                    connection, script, tail, cancellationToken, execution)
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
        TrackedScriptExecution? execution = null)
    {
        try
        {
            var reply = await SendScriptCommandAsync(
                    script.EvalShaOperation, connection, new Cmd2N(script.EvalShaVerb, script.Sha1, tail[0], tail[1..]),
                    cancellationToken, execution)
                .ConfigureAwait(false);
            return new RespireResult(in reply, _core.Options.Serializer);
        }
        catch (RespireServerException ex) when (ex.Code == RespireErrorCodes.NoScript)
        {
            execution?.RecordCommandNotApplied();
            if (execution is not null) execution.StartedTimestamp = Stopwatch.GetTimestamp();
            var reply = await SendScriptCommandAsync(
                    script.EvalOperation, connection, new Cmd2N(script.EvalVerb, script.Source, tail[0], tail[1..]),
                    cancellationToken, execution)
                .ConfigureAwait(false);
            return new RespireResult(in reply, _core.Options.Serializer);
        }
    }

    private ValueTask<RespValue> SendScriptCommandAsync(
        string operation,
        RespireConnection connection,
        Cmd2N command,
        CancellationToken cancellationToken,
        TrackedScriptExecution? execution)
        => execution is null
            ? SendOnConnectionCoreAsync(operation, connection, command, cancellationToken)
            : SendOnConnectionCoreAsync(
                operation, connection, new SendTimestampCommand<Cmd2N>(command, execution), cancellationToken);

    /// <summary>
    /// Kills one multiplexed Redis client through a separate control connection and waits for
    /// the server acknowledgement. The acknowledged kill is an ordering barrier: no command
    /// from the target client can execute afterward.
    /// </summary>
    internal ValueTask FenceCorrectionConnectionAsync(TrackedConnectionIdentity identity)
        => FenceCorrectionConnectionAsync(identity, CancellationToken.None, null);

    internal async ValueTask FenceCorrectionConnectionAsync(
        TrackedConnectionIdentity identity, CancellationToken cancellationToken, Action? onAcknowledged = null)
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
        var pool = sentinelCorrection?.Pool ?? correction?.Pool ?? (core.Cluster is { } routerPool
            ? routerPool.GetDedicatedPool(identity.Endpoint) : core.DedicatedPool);
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

            onAcknowledged?.Invoke();
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
        var timeout = _core.Options.CommandTimeout ?? RemovalLeaseTtl;
        using var timeoutSource = CommandTimeoutCancellation.Create(cancellationToken, timeout);
        try
        {
            await UnlinkLeasedAsync(key, timeoutSource.Token, timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (RespireTimeoutException ex)
        {
            throw new RespireTimeoutException("UNLINK", timeout, ex);
        }
        catch (OperationCanceledException ex) when (
            RespireConnection.IsDeadlineCancellation(ex, timeoutSource.Token, cancellationToken))
        {
            throw new RespireTimeoutException("UNLINK", timeout, ex,
                RespireTimeoutDiagnostics.Capture());
        }
    }

    private async ValueTask UnlinkLeasedAsync(RespireKey key, CancellationToken cancellationToken,
        TimeSpan timeout, CancellationToken callerCancellationToken)
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
            await PlaceLeaseAsync(lease, cancellationToken).ConfigureAwait(false);
            var leaseStart = Stopwatch.GetTimestamp();

            var command = new Cmd2N(Verbs.Eval, LeasedUnlinkScript.Source, 2, [redisKey, lease]);
            RespValue value;
            try
            {
                value = await SendBlockingAsync(
                    "EVAL", command, cancellationToken, LeasedUnlinkScript.Sha1,
                    cancellationTimeout: timeout, callerCancellationToken: callerCancellationToken).ConfigureAwait(false);
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

    private async ValueTask PlaceLeaseAsync(RespireValue lease, CancellationToken cancellationToken)
    {
        var command = new Cmd4(Verbs.Set, lease, 1, "PX", (long)RemovalLeaseTtl.TotalMilliseconds);
        var reply = await SendAsync("SET", command, cancellationToken).ConfigureAwait(false);
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
        var reply = await SendAsync("UNLINK", command, CancellationToken.None).ConfigureAwait(false);
        reply.Dispose();
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
        TrackedConnectionIdentity connectionIdentity = default)
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
            var command = new Cmd2N(Verbs.Eval, script.Source, tail[0], tail[1..]);
            try
            {
                await multiplexer.SendToAllConnectionsAsync(command,
                    connectionIdentity.RequiresAsking, CancellationToken.None).ConfigureAwait(false);
            }
            catch (RespireConnectionRetiredException) when (
                (core.Cluster is not null || core.Sentinel is not null) && connectionIdentity.Connection is not null)
            {
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

    private ValueTask<TResult> ConvertAsync<TCommand, TResult>(
        string operation,
        TCommand command,
        CancellationToken ct,
        ResponseConverter<RespireClient, TResult> converter,
        bool transferOwnership = false)
        where TCommand : struct, IRespCommand
        => ConvertResponseAsync(operation, command, ct, this, converter, transferOwnership);

    internal ValueTask<TResult> ConvertResponseAsync<TCommand, TState, TResult>(
        string operation,
        TCommand command,
        CancellationToken ct,
        TState state,
        ResponseConverter<TState, TResult> converter,
        bool transferOwnership = false)
        where TCommand : struct, IRespCommand
    {
        var core = _core;
        ObjectDisposedException.ThrowIf(core.Disposed, this);
        if (!RespireTelemetry.IsEnabled
            && (_readFrom == RespireReadFrom.Primary || command.ReadKind == ReadCommandKind.None)
            && command is not IStreamingRespCommand
            && core.Cluster is null
            && core.Sentinel is null
            && core.Multiplexer.IsInitialized
            && (core.ClientCache is null
                || !ClientSideCacheCoordinator.CanCacheOperation(operation)))
        {
            // CommandTimeout is enforced by the connection's deadline sweep and covers the
            // Redis response, not user converter work (conversion runs at the caller).
            var cache = core.ClientCache;
            var mutationFence = cache is null ? default : cache.BeforeCommand(operation, in command);
            var connection = core.Multiplexer.GetConnection();
            var response = connection.SendConvertedAsync(
                in command, state, converter, transferOwnership, ct, operation);
            return mutationFence.IsRequired
                ? CompleteMutationAsync(response, cache!, mutationFence)
                : response;
        }

        return PooledResponseSource<TState, TResult>.Create(
            SendAsync(operation, command, ct), state, converter, transferOwnership);
    }

    internal ValueTask<long> IntegerAsync<TCommand>(string operation, TCommand command, CancellationToken ct)
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

    internal ValueTask<bool> FlagAsync<TCommand>(string operation, TCommand command, CancellationToken ct)
        where TCommand : struct, IRespCommand
        => ConvertAsync(
            operation, command, ct,
            static (RespireClient _, in RespValue value) => ResponseReader.Flag(in value));

    internal ValueTask<bool> OkOrNullAsync<TCommand>(string operation, TCommand command, CancellationToken ct)
        where TCommand : struct, IRespCommand
        => ConvertAsync(
            operation, command, ct,
            static (RespireClient _, in RespValue value) => ResponseReader.OkOrNull(in value));

    internal ValueTask<bool> OkResultAsync<TCommand>(string operation, TCommand command, CancellationToken ct)
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
        var value = await SendAsync(operation, command, ct).ConfigureAwait(false);
        try
        {
            ResponseReader.ExpectOk(in value);
        }
        finally
        {
            value.Dispose();
        }
    }

    internal ValueTask<string> StringAsync<TCommand>(string operation, TCommand command, CancellationToken ct)
        where TCommand : struct, IRespCommand
        => ConvertAsync(
            operation, command, ct,
            static (RespireClient _, in RespValue value) => ResponseReader.String(in value));

    internal ValueTask<string?> StringOrNullAsync<TCommand>(string operation, TCommand command, CancellationToken ct)
        where TCommand : struct, IRespCommand
    {
        var core = _core;
        ObjectDisposedException.ThrowIf(core.Disposed, this);
        if (!RespireTelemetry.IsEnabled
            && core.Cluster is null
            && core.Sentinel is null
            && (_readFrom == RespireReadFrom.Primary || command.ReadKind == ReadCommandKind.None)
            && core.Multiplexer.IsInitialized
            && (core.ClientCache is null
                || !ClientSideCacheCoordinator.CanCacheOperation(operation)))
        {
            // Specialized bulk-string source: small buffered replies decode straight from the
            // receive buffer instead of round-tripping through a pooled RespValue payload.
            // CommandTimeout is enforced by the connection's deadline sweep.
            var cache = core.ClientCache;
            var mutationFence = cache is null ? default : cache.BeforeCommand(operation, in command);
            var connection = core.Multiplexer.GetConnection();
            var response = connection.SendStringAsync(in command, ct, operation);
            return mutationFence.IsRequired
                ? CompleteMutationAsync(response, cache!, mutationFence)
                : response;
        }

        return PooledResponseSource<RespireClient, string?>.Create(
            SendAsync(operation, command, ct), this,
            static (RespireClient _, in RespValue value) => ResponseReader.StringOrNull(in value),
            transferOwnership: false);
    }

    internal ValueTask<byte[]?> BytesOrNullAsync<TCommand>(string operation, TCommand command, CancellationToken ct)
        where TCommand : struct, IRespCommand
        => ConvertAsync(
            operation, command, ct,
            static (RespireClient _, in RespValue value) => ResponseReader.BytesOrNull(in value));

    internal ValueTask<double> DoubleAsync<TCommand>(string operation, TCommand command, CancellationToken ct)
        where TCommand : struct, IRespCommand
        => ConvertAsync(
            operation, command, ct,
            static (RespireClient _, in RespValue value) => ResponseReader.Double(in value));

    internal ValueTask<double?> DoubleOrNullAsync<TCommand>(string operation, TCommand command, CancellationToken ct)
        where TCommand : struct, IRespCommand
        => ConvertAsync(
            operation, command, ct,
            static (RespireClient _, in RespValue value) => ResponseReader.DoubleOrNull(in value));

    internal ValueTask<long?> IntegerOrNullAsync<TCommand>(string operation, TCommand command, CancellationToken ct)
        where TCommand : struct, IRespCommand
        => ConvertAsync(
            operation, command, ct,
            static (RespireClient _, in RespValue value) => ResponseReader.IntegerOrNull(in value));

    internal ValueTask<long?> IntegerMinusOneOrNullAsync<TCommand>(
        string operation, TCommand command, CancellationToken ct)
        where TCommand : struct, IRespCommand
        => ConvertAsync(
            operation, command, ct,
            static (RespireClient _, in RespValue value) => ResponseReader.IntegerMinusOneOrNull(in value));

    internal ValueTask<RespireTtl[]> TtlArrayAsync<TCommand>(
        string operation, TCommand command, CancellationToken ct)
        where TCommand : struct, IRespCommand
        => ConvertAsync(
            operation, command, ct,
            static (RespireClient _, in RespValue value) => ResponseReader.TtlArray(in value));

    internal ValueTask<HashFieldExpiryResult[]> HashFieldExpiryResultArrayAsync<TCommand>(
        string operation, TCommand command, CancellationToken ct)
        where TCommand : struct, IRespCommand
        => ConvertAsync(
            operation, command, ct,
            static (RespireClient _, in RespValue value) => ResponseReader.HashFieldExpiryResultArray(in value));

    internal ValueTask<bool[]> FlagArrayAsync<TCommand>(string operation, TCommand command, CancellationToken ct)
        where TCommand : struct, IRespCommand
        => ConvertAsync(
            operation, command, ct,
            static (RespireClient _, in RespValue value) => ResponseReader.FlagArray(in value));

    internal ValueTask<string[]> StringArrayAsync<TCommand>(string operation, TCommand command, CancellationToken ct)
        where TCommand : struct, IRespCommand
        => ConvertAsync(
            operation, command, ct,
            static (RespireClient _, in RespValue value) => ResponseReader.StringArray(in value));

    internal ValueTask<string?[]> NullableStringArrayAsync<TCommand>(string operation, TCommand command, CancellationToken ct)
        where TCommand : struct, IRespCommand
        => ConvertAsync(
            operation, command, ct,
            static (RespireClient _, in RespValue value) => ResponseReader.NullableStringArray(in value));

    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    internal ValueTask<T[]> DeserializeArrayAsync<T, TCommand>(
        string operation, TCommand command, CancellationToken ct)
        where TCommand : struct, IRespCommand
        => ConvertAsync<TCommand, T[]>(
            operation, command, ct,
            static (RespireClient client, in RespValue value) => client.DeserializeArray<T>(in value));

    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    internal ValueTask<T?[]> DeserializeNullableArrayAsync<T, TCommand>(
        string operation, TCommand command, CancellationToken ct)
        where TCommand : struct, IRespCommand
        => ConvertAsync<TCommand, T?[]>(
            operation, command, ct,
            static (RespireClient client, in RespValue value) => client.DeserializeNullableArray<T>(in value));

    internal ValueTask<long?[]> NullableIntegerArrayAsync<TCommand>(string operation, TCommand command, CancellationToken ct)
        where TCommand : struct, IRespCommand
        => ConvertResponseAsync(
            operation, command, ct, this,
            static (RespireClient _, in RespValue value) => ResponseReader.NullableIntegerArray(in value));

    internal ValueTask<double?[]> NullableDoubleArrayAsync<TCommand>(string operation, TCommand command, CancellationToken ct)
        where TCommand : struct, IRespCommand
        => ConvertResponseAsync(
            operation, command, ct, this,
            static (RespireClient _, in RespValue value) => ResponseReader.NullableDoubleArray(in value));

    internal ValueTask<Dictionary<string, string>> StringMapAsync<TCommand>(string operation, TCommand command, CancellationToken ct)
        where TCommand : struct, IRespCommand
        => ConvertAsync(
            operation, command, ct,
            static (RespireClient _, in RespValue value) => ResponseReader.StringMap(in value));

    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    internal ValueTask<Dictionary<string, T>> DeserializeMapAsync<T, TCommand>(
        string operation, TCommand command, CancellationToken ct)
        where TCommand : struct, IRespCommand
        => ConvertAsync<TCommand, Dictionary<string, T>>(
            operation, command, ct,
            static (RespireClient client, in RespValue value) => client.DeserializeMap<T>(in value));

    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    internal ValueTask<T?> DeserializeAsync<T, TCommand>(string operation, TCommand command, CancellationToken ct)
        where TCommand : struct, IRespCommand
        => ConvertAsync<TCommand, T?>(
            operation, command, ct,
            static (RespireClient client, in RespValue value) => client.DeserializeBorrowed<T>(in value));

    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    internal ValueTask<RespireGet<T>> TryDeserializeAsync<T, TCommand>(string operation, TCommand command, CancellationToken ct)
        where TCommand : struct, IRespCommand
        => ConvertAsync<TCommand, RespireGet<T>>(
            operation, command, ct,
            static (RespireClient client, in RespValue value) => client.TryDeserializeBorrowed<T>(in value));

    internal ValueTask<RespireLease> LeaseAsync<TCommand>(string operation, TCommand command, CancellationToken ct)
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

