using System.Buffers;
using System.Diagnostics;
using System.Runtime.CompilerServices;
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
    private readonly bool _ownsCore;

    private RespireClient(ClientCore core, string? keyPrefix, bool ownsCore)
    {
        _core = core;
        _keyPrefix = keyPrefix;
        _ownsCore = ownsCore;
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
        Scripts = new ScriptCommands(this);
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

    public static async ValueTask<RespireClient> ConnectAsync(RespireOptions options, CancellationToken cancellationToken = default)
    {
        return await SentinelResolver.ResolveAndConnectPrimaryAsync(
            options ?? throw new ArgumentNullException(nameof(options)),
            ConnectPrimaryAsync,
            cancellationToken).ConfigureAwait(false);
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
            ? options.PrimaryEndpoint.ToString()
            : string.Join(", ", options.Endpoints);

    /// <summary>
    /// Creates a client without connecting; the first command connects. Useful for dependency
    /// injection, where construction should not block on the network.
    /// </summary>
    public static RespireClient Create(string connectionString) => Create(RespireOptions.Parse(connectionString));

    public static RespireClient Create(RespireOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!string.IsNullOrWhiteSpace(options.ServiceName))
        {
            throw new RespireConfigurationException(
                "Redis Sentinel discovery requires RespireClient.ConnectAsync because it must query Sentinel " +
                "before Redis connections are created. Lazy Sentinel failover is not supported yet.");
        }

        return new RespireClient(new ClientCore(options), keyPrefix: null, ownsCore: true);
    }

    public RespireEndpoint Endpoint => new(_core.Multiplexer.Host, _core.Multiplexer.Port);

    public bool IsConnected
        => !_core.Disposed && (_core.Cluster?.IsConnected ?? _core.Multiplexer.IsConnected);

    /// <summary>Raised when connection health changes, including endpoint and error context.</summary>
    public event Action<RespireConnectionStateChange>? ConnectionStateChanged
    {
        add => _core.ConnectionStateChanged += value;
        remove => _core.ConnectionStateChanged -= value;
    }

    public IStringCommands Strings { get; }
    public IKeyCommands Keys { get; }
    public ILockCommands Locks { get; }
    public IHashCommands Hashes { get; }
    public IListCommands Lists { get; }
    public ISetCommands Sets { get; }
    public ISortedSetCommands SortedSets { get; }
    public IStreamCommands Streams { get; }
    public IBitmapCommands Bitmaps { get; }
    public IHyperLogLogCommands HyperLogLog { get; }
    public IGeoCommands Geo { get; }
    public IScriptCommands Scripts { get; }
    public IServerCommands Server { get; }

    /// <summary>
    /// A view of this client that prepends <paramref name="prefix"/> to every key (channels and
    /// server-level commands are untouched). Views share this client's connections; disposing a
    /// view is a no-op — dispose the root client.
    /// </summary>
    public IRespireClient WithKeyPrefix(string prefix)
    {
        ArgumentException.ThrowIfNullOrEmpty(prefix);
        return new RespireClient(_core, _keyPrefix is null ? prefix : _keyPrefix + prefix, ownsCore: false);
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

    // Raw escape hatch

    /// <summary>
    /// Sends a command from <see cref="RespireCommands"/>. Unlike the string overload, command
    /// words are pre-encoded once and never split or allocated per call. The result is a lease.
    /// </summary>
    public ValueTask<RespireResult> ExecuteAsync(RespireCommand command, params RespireValue[] args)
        => ExecuteCatalogAsync(command, args, RespireCommandFlags.None, CancellationToken.None);

    /// <summary>
    /// Sends a command from <see cref="RespireCommands"/> with optional policy flags and
    /// cancellation. Blocking descriptors use a dedicated pooled connection; cancellation
    /// abandons that connection without stalling multiplexed traffic.
    /// </summary>
    public ValueTask<RespireResult> ExecuteAsync(
        RespireCommand command,
        RespireValue[] args,
        RespireCommandFlags flags = RespireCommandFlags.None,
        CancellationToken cancellationToken = default)
        => ExecuteCatalogAsync(command, args, flags, cancellationToken);

    /// <summary>
    /// Queues a catalog command and discards its reply once it arrives.
    /// </summary>
    /// <remarks>
    /// Standalone execution returns after writing. Cluster execution may await replies to process
    /// <c>MOVED</c> and <c>ASK</c> redirects, adding round-trip latency.
    /// </remarks>
    public ValueTask ExecuteFireAndForgetAsync(RespireCommand command, params RespireValue[] args)
        => ExecuteCatalogFireAndForgetAsync(command, args, CancellationToken.None);

    /// <summary>
    /// Queues a catalog command and discards its reply once it arrives.
    /// </summary>
    /// <remarks>
    /// Standalone execution returns after writing. Cluster execution may await replies to process
    /// <c>MOVED</c> and <c>ASK</c> redirects, adding round-trip latency.
    /// </remarks>
    public ValueTask ExecuteFireAndForgetAsync(
        RespireCommand command, RespireValue[] args, CancellationToken cancellationToken = default)
        => ExecuteCatalogFireAndForgetAsync(command, args, cancellationToken);

    private async ValueTask<RespireResult> ExecuteCatalogAsync(
        RespireCommand command,
        RespireValue[] args,
        RespireCommandFlags flags,
        CancellationToken cancellationToken)
    {
        ValidateResultFlags(flags);
        ValidateCatalogCommand(command);

        var storedProcedureName = StoredProcedureName(command.Name, args);
        var commandValue = new CatalogCommand(command, args);
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

        return new RespireResult(in response);
    }

    private async ValueTask ExecuteCatalogFireAndForgetAsync(
        RespireCommand command,
        RespireValue[] args,
        CancellationToken cancellationToken)
    {
        ValidateCatalogCommand(command);
        if (command.IsBlocking(args))
        {
            throw new NotSupportedException(
                $"{command.Name} can block and cannot run through ExecuteFireAndForgetAsync.");
        }

        var storedProcedureName = StoredProcedureName(command.Name, args);
        var commandValue = new CatalogCommand(command, args);
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

    /// <summary>
    /// Sends any command. The command may contain spaces ("CONFIG GET"); each arg is exactly one
    /// argument. The result is a lease — dispose it.
    /// </summary>
    public ValueTask<RespireResult> ExecuteAsync(string command, params RespireValue[] args)
        => ExecuteRawAsync(command, args, RespireCommandFlags.None, CancellationToken.None);

    /// <summary>
    /// Sends any command with optional policy flags and cancellation.
    /// </summary>
    public ValueTask<RespireResult> ExecuteAsync(
        string command,
        RespireValue[] args,
        RespireCommandFlags flags = RespireCommandFlags.None,
        CancellationToken cancellationToken = default)
        => ExecuteRawAsync(command, args, flags, cancellationToken);

    /// <summary>
    /// Queues any command and discards its reply once it arrives.
    /// </summary>
    /// <remarks>
    /// Standalone execution returns after writing. Cluster execution may await replies to process
    /// <c>MOVED</c> and <c>ASK</c> redirects, adding round-trip latency.
    /// </remarks>
    public ValueTask ExecuteFireAndForgetAsync(string command, params RespireValue[] args)
        => ExecuteRawFireAndForgetAsync(command, args, CancellationToken.None);

    /// <summary>
    /// Queues any command and discards its reply once it arrives.
    /// </summary>
    /// <remarks>
    /// Standalone execution returns after writing. Cluster execution may await replies to process
    /// <c>MOVED</c> and <c>ASK</c> redirects, adding round-trip latency.
    /// </remarks>
    public ValueTask ExecuteFireAndForgetAsync(
        string command, RespireValue[] args, CancellationToken cancellationToken = default)
        => ExecuteRawFireAndForgetAsync(command, args, cancellationToken);

    private async ValueTask<RespireResult> ExecuteRawAsync(
        string command,
        RespireValue[] args,
        RespireCommandFlags flags,
        CancellationToken cancellationToken)
    {
        ValidateResultFlags(flags);
        var (operation, words, firstArgumentIndex) = ParseRawCommand(command);
        var (storedProcedureName, commandValue) = CreateRawCommand(
            operation, words, firstArgumentIndex, args);
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

        return new RespireResult(in response);
    }

    private async ValueTask ExecuteRawFireAndForgetAsync(
        string command,
        RespireValue[] args,
        CancellationToken cancellationToken)
    {
        var (operation, words, firstArgumentIndex) = ParseRawCommand(command);
        ValidateRawFireAndForgetCommand(
            operation, words.AsSpan(firstArgumentIndex), args);
        var (storedProcedureName, commandValue) = CreateRawCommand(
            operation, words, firstArgumentIndex, args);

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

    private async ValueTask<RespireResult> ExecuteInterpolatedAsync(
        RespireCommandInterpolatedStringHandler command,
        RespireCommandFlags flags,
        CancellationToken cancellationToken)
    {
        ValidateResultFlags(flags);
        var (operation, tokens) = command.Build();
        var storedProcedureName = StoredProcedureName(operation, tokens.AsSpan(1));
        var routingKeyIndex = DynamicCommandRouting.GetRoutingKeyIndex(operation, tokens, firstArgumentIndex: 1);
        var commandValue = new DynamicCommand(tokens, routingKeyIndex);
        var isBlocking = RespireCommand.IsBlocking(
            operation, RespireCommand.Classify(operation), tokens.AsSpan(1));
        RespValue response;
        if (_core.Cluster is { } cluster
            && DynamicCommandRouting.IsClusterWideMutation(operation, tokens.AsSpan(1)))
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

        return new RespireResult(in response);
    }

    private void ValidateCatalogCommand(RespireCommand command)
    {
        if (string.IsNullOrEmpty(command.Name))
        {
            throw new ArgumentException("Command must be an entry from RespireCommands.", nameof(command));
        }

        if (_keyPrefix is not null)
        {
            throw new NotSupportedException(
                "Catalog commands cannot run through a key-prefixed view because descriptors do not identify key arguments. " +
                "Use the typed command facets instead.");
        }

        if (command.Behavior == RespireCommandBehavior.ConnectionScoped)
        {
            throw new NotSupportedException(
                $"{command.Name} requires connection affinity and cannot run through ExecuteAsync. " +
                "Use RespireOptions, CreateTransaction, or the subscription APIs instead.");
        }
    }

    private static void ValidateResultFlags(RespireCommandFlags flags)
    {
        if (HasFlag(flags, RespireCommandFlags.FireAndForget))
        {
            throw new ArgumentException(
                $"{nameof(RespireCommandFlags.FireAndForget)} commands do not return a {nameof(RespireResult)}. " +
                $"Use {nameof(ExecuteFireAndForgetAsync)} instead.",
                nameof(flags));
        }

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

    private static (string? StoredProcedureName, DynamicCommand Command) CreateRawCommand(
        string operation,
        string[] words,
        int firstArgumentIndex,
        RespireValue[] args)
    {
        var tokens = new RespireValue[words.Length + args.Length];
        for (var i = 0; i < words.Length; i++)
        {
            tokens[i] = words[i];
        }

        args.CopyTo(tokens, words.Length);
        var storedProcedureName = words.Length == 1 ? StoredProcedureName(operation, args) : null;
        var routingKeyIndex = DynamicCommandRouting.GetRoutingKeyIndex(
            operation, tokens, firstArgumentIndex);
        return (storedProcedureName, new DynamicCommand(tokens, routingKeyIndex));
    }

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

        var subcommand = KnownRawSubcommand(command, words[1]);
        firstArgumentIndex = subcommand is null ? 1 : 2;
        return subcommand is null ? command : $"{command} {subcommand}";
    }

    private static string? KnownRawSubcommand(string command, string candidate)
        => command switch
        {
            "CONFIG" when candidate.Equals("GET", StringComparison.OrdinalIgnoreCase) => "GET",
            "CONFIG" when candidate.Equals("SET", StringComparison.OrdinalIgnoreCase) => "SET",
            "FUNCTION" when candidate.Equals("DELETE", StringComparison.OrdinalIgnoreCase) => "DELETE",
            "FUNCTION" when candidate.Equals("FLUSH", StringComparison.OrdinalIgnoreCase) => "FLUSH",
            "FUNCTION" when candidate.Equals("LOAD", StringComparison.OrdinalIgnoreCase) => "LOAD",
            "FUNCTION" when candidate.Equals("RESTORE", StringComparison.OrdinalIgnoreCase) => "RESTORE",
            "SCRIPT" when candidate.Equals("DEBUG", StringComparison.OrdinalIgnoreCase) => "DEBUG",
            "SCRIPT" when candidate.Equals("EXISTS", StringComparison.OrdinalIgnoreCase) => "EXISTS",
            "SCRIPT" when candidate.Equals("FLUSH", StringComparison.OrdinalIgnoreCase) => "FLUSH",
            "SCRIPT" when candidate.Equals("HELP", StringComparison.OrdinalIgnoreCase) => "HELP",
            "SCRIPT" when candidate.Equals("KILL", StringComparison.OrdinalIgnoreCase) => "KILL",
            "SCRIPT" when candidate.Equals("LOAD", StringComparison.OrdinalIgnoreCase) => "LOAD",
            "SCRIPT" when candidate.Equals("SHOW", StringComparison.OrdinalIgnoreCase) => "SHOW",
            "XGROUP" when candidate.Equals("CREATE", StringComparison.OrdinalIgnoreCase) => "CREATE",
            _ => null,
        };

    // Pub/sub

    /// <summary>Publishes to a channel; returns the number of subscribers that received it. Redis: PUBLISH.</summary>
    public ValueTask<long> PublishAsync(string channel, RespireValue message, CancellationToken cancellationToken = default)
        => IntegerAsync("PUBLISH", new Cmd2(Verbs.Publish, channel, message), cancellationToken);

    /// <summary>Publishes to a sharded channel (Redis 7+). Redis: SPUBLISH.</summary>
    public ValueTask<long> PublishShardedAsync(string channel, RespireValue message, CancellationToken cancellationToken = default)
        => IntegerAsync("SPUBLISH", new Cmd2(Verbs.SPublish, channel, message), cancellationToken);

    /// <summary>
    /// Subscribes to a channel and returns once the server has acknowledged the SUBSCRIBE, so the
    /// next publish is guaranteed to reach this subscriber — no readiness polling. Messages are
    /// buffered from that moment, whenever enumeration starts; disposing the subscription
    /// unsubscribes. Redis: SUBSCRIBE.
    /// </summary>
    public ValueTask<RespireSubscription> SubscribeAsync(string channel, CancellationToken cancellationToken = default)
        => SubscribeCoreAsync(SubscriptionKind.Channel, [channel], cancellationToken);

    /// <inheritdoc cref="SubscribeAsync(string, CancellationToken)"/>
    public ValueTask<RespireSubscription> SubscribeAsync(string[] channels, CancellationToken cancellationToken = default)
        => SubscribeCoreAsync(SubscriptionKind.Channel, channels, cancellationToken);

    /// <summary>
    /// Subscribes to a glob pattern ("news.*") and returns once the server has acknowledged.
    /// Redis: PSUBSCRIBE.
    /// </summary>
    public ValueTask<RespireSubscription> SubscribePatternAsync(string pattern, CancellationToken cancellationToken = default)
        => SubscribeCoreAsync(SubscriptionKind.Pattern, [pattern], cancellationToken);

    /// <inheritdoc cref="SubscribePatternAsync(string, CancellationToken)"/>
    public ValueTask<RespireSubscription> SubscribePatternAsync(string[] patterns, CancellationToken cancellationToken = default)
        => SubscribeCoreAsync(SubscriptionKind.Pattern, patterns, cancellationToken);

    /// <summary>
    /// Subscribes to a sharded channel (Redis 7+) and returns once the server has acknowledged.
    /// Redis: SSUBSCRIBE.
    /// </summary>
    public ValueTask<RespireSubscription> SubscribeShardedAsync(string channel, CancellationToken cancellationToken = default)
        => SubscribeCoreAsync(SubscriptionKind.Sharded, [channel], cancellationToken);

    /// <inheritdoc cref="SubscribeShardedAsync(string, CancellationToken)"/>
    public ValueTask<RespireSubscription> SubscribeShardedAsync(string[] channels, CancellationToken cancellationToken = default)
        => SubscribeCoreAsync(SubscriptionKind.Sharded, channels, cancellationToken);

    private ValueTask<RespireSubscription> SubscribeCoreAsync(
        SubscriptionKind kind, string[] names, CancellationToken cancellationToken)
        => _core.Hub.SubscribeAsync(kind, names, cancellationToken);

    // Batches and transactions

    /// <summary>
    /// Starts an explicit pipeline: queue commands, then <see cref="RespireBatch.ExecuteAsync"/>
    /// flushes them together. Queued results are unreadable until the batch is sent — awaiting
    /// early throws instead of deadlocking. Dispose an unsent batch to fault its queued pendings.
    /// </summary>
    public RespireBatch CreateBatch() => new(this);

    /// <summary>
    /// Starts a MULTI/EXEC transaction. Queue commands, then
    /// <see cref="RespireTransaction.CommitAsync"/>. Always commit or dispose the transaction.
    /// </summary>
    public RespireTransaction CreateTransaction() => new(this, watchConnection: null);

    /// <summary>
    /// Starts a transaction that WATCHes keys first: if any watched key changes before commit,
    /// <see cref="RespireTransaction.CommitAsync"/> returns false. Runs on a dedicated
    /// connection for correct WATCH isolation; always commit or dispose the transaction.
    /// For read-modify-write loops, prefer a Lua script (<see cref="Scripts"/>) — one round
    /// trip, no retry loop.
    /// </summary>
    public async ValueTask<RespireTransaction> CreateTransactionAsync(
        RespireKey[] watchKeys, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(watchKeys);
        if (watchKeys.Length == 0)
        {
            return CreateTransaction();
        }

        ObjectDisposedException.ThrowIf(_core.Disposed, this);
        if (_core.Cluster is not null)
        {
            throw new NotSupportedException(
                "WATCH transactions are not supported in Redis Cluster mode. " +
                "Use a same-slot Lua script for atomic read-modify-write operations.");
        }

        // Rented from the tracked dedicated pool (not raw-connected) so client disposal can
        // see and abort this connection even if it opens mid-disposal.
        var connection = await _core.DedicatedPool.RentAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var command = new Cmd1N(Verbs.Watch, Key(watchKeys[0]), MapKeys(watchKeys.AsSpan(1)));
            var reply = await SendOnConnectionAsync("WATCH", connection, command, cancellationToken).ConfigureAwait(false);
            reply.Dispose();
            return new RespireTransaction(this, connection);
        }
        catch
        {
            await _core.DedicatedPool.DiscardAsync(connection).ConfigureAwait(false);
            throw;
        }
    }

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

        if (typeof(T) == typeof(char))
        {
            return (ushort)(char)(object)value!;
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

    internal RespireValue SerializeCollectionMember<T>(T value)
        => value is bool boolean ? boolean : SerializeRawCompatible(value);

    /// <summary>
    /// Reads a value the caller does not own, keeping "reply was null" distinct from a
    /// deserialized <c>default(T)</c>.
    /// </summary>
    internal RespireGet<T> TryDeserializeBorrowed<T>(in RespValue value)
        => value.IsNull ? default : new RespireGet<T>(true, DeserializeBorrowed<T>(in value)!);

    /// <summary>Reads a value the caller does not own (e.g. a transaction-array element).</summary>
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

        if (PrimitiveCodec.TryDeserialize<T>(value.AsSpan(), out var primitive))
        {
            return primitive;
        }

        return _core.Options.Serializer.Deserialize<T>(value.AsSpan());
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
        if (core.Cluster is { } cluster)
        {
            return SendClusterAsync(
                operation,
                cluster,
                command,
                cancellationToken,
                noRedirect: HasFlag(flags, RespireCommandFlags.NoRedirect));
        }

        if (!core.Multiplexer.IsInitialized)
        {
            return SendAfterConnectAsync(operation, command, cancellationToken);
        }

        var connection = core.Multiplexer.GetConnection();
        return SendOnConnectionAsync(operation, connection, command, cancellationToken);
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

        await core.Multiplexer.EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);

        var connection = core.Multiplexer.GetConnection();
        return await SendOnConnectionAsync(
                operation, connection, command, cancellationToken, storedProcedureName)
            .ConfigureAwait(false);
    }

#if NET
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
#endif
    private async ValueTask<RespValue> SendClusterAsync<TCommand>(
        string operation,
        ClusterRouter cluster,
        TCommand command,
        CancellationToken cancellationToken,
        string? storedProcedureName = null,
        bool noRedirect = false)
        where TCommand : struct, IRespCommand
    {
        var slot = command.TryGetClusterSlot(out var commandSlot) ? commandSlot : (int?)null;
        var connection = await cluster.GetConnectionAsync(slot, cancellationToken).ConfigureAwait(false);
        var sendAsking = false;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await SendOnConnectionAsync(
                        operation, connection, command, cancellationToken, storedProcedureName, sendAsking)
                    .ConfigureAwait(false);
            }
            catch (RespireServerException error)
                when (!noRedirect && attempt < ClusterRouter.RedirectLimit && ClusterRouter.IsRedirect(error))
            {
                connection = await cluster.GetRedirectConnectionAsync(error, connection, cancellationToken)
                    .ConfigureAwait(false);
                sendAsking = error.Code == RespireErrorCodes.Ask;
            }
        }
    }

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
        var connections = await cluster.GetMasterConnectionsAsync(cancellationToken).ConfigureAwait(false);
        var retainedReply = default(RespValue);
        var hasRetainedReply = false;
        try
        {
            foreach (var connection in connections)
            {
                var reply = await SendOnConnectionAsync(
                        operation, connection, command, cancellationToken)
                    .ConfigureAwait(false);
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

    private ValueTask SendFireAndForgetAsync<TCommand>(
        string operation,
        TCommand command,
        CancellationToken cancellationToken,
        string? storedProcedureName = null)
        where TCommand : struct, IRespCommand
    {
        var core = _core;
        ObjectDisposedException.ThrowIf(core.Disposed, this);
        if (core.Cluster is { } cluster)
        {
            return SendFireAndForgetClusterAsync(
                operation, cluster, command, cancellationToken, storedProcedureName);
        }

        if (!core.Multiplexer.IsInitialized)
        {
            return SendFireAndForgetAfterConnectAsync(
                operation, command, cancellationToken, storedProcedureName);
        }

        var connection = core.Multiplexer.GetConnection();
        return SendFireAndForgetOnConnectionAsync(
            operation, connection, command, cancellationToken, storedProcedureName);
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
            : connection.SendFireAndForgetAsync(in command, cancellationToken);

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
            await connection.SendFireAndForgetAsync(in command, cancellationToken).ConfigureAwait(false);
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

    private async ValueTask SendFireAndForgetClusterAsync<TCommand>(
        string operation,
        ClusterRouter cluster,
        TCommand command,
        CancellationToken cancellationToken,
        string? storedProcedureName)
        where TCommand : struct, IRespCommand
    {
        if (RespireCommand.MayCloseWithoutReply(operation))
        {
            var slot = command.TryGetClusterSlot(out var commandSlot) ? commandSlot : (int?)null;
            var connection = await cluster.GetConnectionAsync(slot, cancellationToken).ConfigureAwait(false);
            await SendFireAndForgetOnConnectionAsync(
                    operation, connection, command, cancellationToken, storedProcedureName)
                .ConfigureAwait(false);
            return;
        }

        try
        {
            using var response = await SendClusterAsync(
                    operation, cluster, command, cancellationToken, storedProcedureName)
                .ConfigureAwait(false);
        }
        catch (RespireServerException)
        {
            // Fire-and-forget still discards ordinary server errors after processing redirects.
        }
    }

    private async ValueTask SendClusterWideFireAndForgetAsync<TCommand>(
        string operation,
        ClusterRouter cluster,
        TCommand command,
        CancellationToken cancellationToken,
        string? storedProcedureName = null)
        where TCommand : struct, IRespCommand
    {
        var connections = await cluster.GetMasterConnectionsAsync(cancellationToken).ConfigureAwait(false);
        List<Exception>? failures = null;
        foreach (var connection in connections)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await SendFireAndForgetOnConnectionAsync(
                        operation,
                        connection,
                        command,
                        cancellationToken,
                        storedProcedureName)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                (failures ??= []).Add(ex);
            }
        }

        if (failures is not null)
        {
            throw new AggregateException(
                "One or more Redis Cluster masters did not accept the command.", failures);
        }
    }

    private ValueTask<RespValue> SendOnConnectionCoreAsync<TCommand>(
        string operation,
        RespireConnection connection,
        TCommand command,
        CancellationToken cancellationToken,
        bool sendAsking = false)
        where TCommand : struct, IRespCommand
    {
        if (_core.Options.CommandTimeout is not { } timeout)
        {
            return sendAsking
                ? ClusterRouter.SendAskingAsync(connection, in command, cancellationToken, operation)
                : connection.SendCheckedAsync(in command, cancellationToken, operation);
        }

        return SendWithTimeoutAsync(operation, connection, command, cancellationToken, timeout, sendAsking);
    }

#if NET
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
#endif
    private static async ValueTask<RespValue> SendWithTimeoutAsync<TCommand>(
        string operation,
        RespireConnection connection,
        TCommand command,
        CancellationToken cancellationToken,
        TimeSpan timeout,
        bool sendAsking)
        where TCommand : struct, IRespCommand
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            return await (sendAsking
                    ? ClusterRouter.SendAskingAsync(connection, in command, timeoutSource.Token, operation)
                    : connection.SendCheckedAsync(in command, timeoutSource.Token, operation))
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new RespireTimeoutException(operation, timeout);
        }
    }

    internal ValueTask<RespValue> SendOnConnectionAsync<TCommand>(
        string operation,
        RespireConnection connection,
        TCommand command,
        CancellationToken cancellationToken,
        string? storedProcedureName = null,
        bool sendAsking = false)
        where TCommand : struct, IRespCommand
        => RespireTelemetry.IsEnabled
            ? SendOnConnectionInstrumentedAsync(
                operation, connection, command, cancellationToken, storedProcedureName, sendAsking)
            : SendOnConnectionCoreAsync(operation, connection, command, cancellationToken, sendAsking);

    private async ValueTask<RespValue> SendOnConnectionInstrumentedAsync<TCommand>(
        string operation,
        RespireConnection connection,
        TCommand command,
        CancellationToken cancellationToken,
        string? storedProcedureName,
        bool sendAsking)
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
                    operation, connection, command, cancellationToken, sendAsking)
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
        bool noRedirect = false)
        where TCommand : struct, IRespCommand
    {
        var core = _core;
        ObjectDisposedException.ThrowIf(core.Disposed, this);
        if (core.Cluster is { } cluster)
        {
            return await SendBlockingClusterAsync(
                    operation, cluster, command, cancellationToken, storedProcedureName, noRedirect)
                .ConfigureAwait(false);
        }

        var telemetry = RespireTelemetry.StartOperation(
            operation,
            core.Multiplexer.Host,
            core.Multiplexer.Port,
            core.Options.Database,
            storedProcedureName: storedProcedureName);
        RespireConnection? connection = null;
        var returned = false;
        try
        {
            connection = await core.DedicatedPool.RentAsync(cancellationToken).ConfigureAwait(false);
            var response = await connection.SendWithoutResponseTimeoutAsync(command, cancellationToken)
                .ConfigureAwait(false);
            core.DedicatedPool.Return(connection);
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
            telemetry.Complete(core, operation, storedProcedureName, ex, connection);
            if (connection is not null && !returned)
            {
                // The connection may still be mid-block server-side; don't return it to the pool.
                await core.DedicatedPool.DiscardAsync(connection).ConfigureAwait(false);
            }

            throw;
        }
    }

    private async ValueTask<RespValue> SendBlockingClusterAsync<TCommand>(
        string operation,
        ClusterRouter cluster,
        TCommand command,
        CancellationToken cancellationToken,
        string? storedProcedureName,
        bool noRedirect)
        where TCommand : struct, IRespCommand
    {
        var core = _core;
        var slot = command.TryGetClusterSlot(out var commandSlot) ? commandSlot : (int?)null;
        var pool = await cluster.GetDedicatedPoolAsync(slot, cancellationToken).ConfigureAwait(false);
        RespireTelemetry.OperationScope telemetry = default;
        var telemetryStarted = false;
        var sendAsking = false;

        for (var attempt = 0; ; attempt++)
        {
            RespireConnection? connection = null;
            var returned = false;
            try
            {
                connection = await pool.RentAsync(cancellationToken).ConfigureAwait(false);
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
                    if (!noRedirect && attempt < ClusterRouter.RedirectLimit && ClusterRouter.IsRedirect(error))
                    {
                        var redirectedPool = await cluster.GetRedirectDedicatedPoolAsync(
                                error, connection, cancellationToken)
                            .ConfigureAwait(false);
                        pool.Return(connection);
                        returned = true;
                        pool = redirectedPool;
                        sendAsking = error.Code == RespireErrorCodes.Ask;
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
                telemetry.Complete(core, operation, storedProcedureName, ex, connection);
                if (connection is not null && !returned)
                {
                    await pool.DiscardAsync(connection).ConfigureAwait(false);
                }

                throw;
            }
        }
    }

    internal ValueTask<RespireConnection> AcquireConnectionAsync(CancellationToken cancellationToken)
        => AcquireConnectionAsync(slot: null, cancellationToken);

    internal async ValueTask<RespireConnection> AcquireConnectionAsync(
        int? slot, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_core.Disposed, this);
        if (_core.Cluster is { } cluster)
        {
            return await cluster.GetConnectionAsync(slot, cancellationToken).ConfigureAwait(false);
        }

        await _core.Multiplexer.EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        return _core.Multiplexer.GetConnection();
    }

    // Wire-level primitives for the caching package (see InternalsVisibleTo). Keyed operations
    // honor this view's key prefix.

    internal bool RequiresReliableCorrectionOrdering(CancellationToken cancellationToken)
        => cancellationToken.CanBeCanceled || _core.Options.CommandTimeout is not null;

    internal async ValueTask<bool> TryEnsureReliableCorrectionOrderingAsync()
    {
        if (_core.Cluster is not null)
        {
            // The keyed tracked send initializes CLIENT ID on its routed node. Initializing
            // only the seed here would provide no ordering guarantee for that node.
            return true;
        }

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

        if (core.Multiplexer.HasReliableCorrectionOrdering)
        {
            return;
        }

        if (core.Options.CommandTimeout is not { } timeout)
        {
            await core.Multiplexer.EnsureReliableCorrectionOrderingAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            await core.Multiplexer.EnsureReliableCorrectionOrderingAsync(timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // No cache command is sent until identity setup completes, so timing this stage out
            // leaves no cache mutation to correct.
            throw new RespireTimeoutException("CLIENT ID / CLIENT KILL", timeout);
        }
    }

    internal readonly record struct TrackedConnectionIdentity(
        RespireEndpoint Endpoint,
        long ServerClientId,
        bool RequiresAsking = false);

    internal sealed class TrackedScriptExecution
    {
        internal TrackedScriptExecution(
            RespireConnection connection, TrackedConnectionIdentity connectionIdentity)
        {
            Connection = connection;
            ConnectionIdentity = connectionIdentity;
        }

        internal RespireConnection Connection { get; set; }

        internal TrackedConnectionIdentity ConnectionIdentity { get; set; }

        internal ValueTask<RespireResult> Response { get; set; }
    }

    internal async ValueTask<RespireResult> ExecuteScriptAsync(
        RespireScript script,
        RespireValue[] tail,
        CancellationToken cancellationToken)
    {
        var core = _core;
        ObjectDisposedException.ThrowIf(core.Disposed, this);
        if (core.Cluster is { } cluster)
        {
            var arguments = tail[1..];
            RespValue clusterReply;
            try
            {
                clusterReply = await SendClusterAsync(
                        "EVALSHA", cluster,
                        new Cmd2N(Verbs.EvalSha, script.Sha1, tail[0], arguments),
                        cancellationToken, script.Sha1)
                    .ConfigureAwait(false);
            }
            catch (RespireServerException ex) when (ex.Code == RespireErrorCodes.NoScript)
            {
                clusterReply = await SendClusterAsync(
                        "EVAL", cluster,
                        new Cmd2N(Verbs.Eval, script.Source, tail[0], arguments),
                        cancellationToken, script.Sha1)
                    .ConfigureAwait(false);
            }

            return new RespireResult(in clusterReply);
        }

        var telemetry = RespireTelemetry.StartOperation(
            "EVALSHA",
            core.Multiplexer.Host,
            core.Multiplexer.Port,
            core.Options.Database,
            storedProcedureName: script.Sha1);
        RespireConnection? connection = null;
        try
        {
            await core.Multiplexer.EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
            connection = core.Multiplexer.GetConnection();
            var result = await ExecuteScriptOnConnectionCoreAsync(connection, script, tail, cancellationToken)
                .ConfigureAwait(false);
            telemetry.Complete(core, "EVALSHA", script.Sha1, connection: connection);
            return result;
        }
        catch (Exception ex)
        {
            telemetry.Complete(core, "EVALSHA", script.Sha1, ex, connection);
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
        CancellationToken cancellationToken)
    {
        var core = _core;
        ObjectDisposedException.ThrowIf(core.Disposed, this);
        var requiresIdentity = RequiresReliableCorrectionOrdering(cancellationToken);
        var tail = BuildScriptTail(keys, args);

        RespireConnection connection;
        if (core.Cluster is { } cluster)
        {
            var command = new Cmd2N(Verbs.EvalSha, script.Sha1, tail[0], tail[1..]);
            var slot = command.TryGetClusterSlot(out var commandSlot) ? commandSlot : (int?)null;
            connection = await GetTrackedClusterConnectionAsync(
                    cluster, slot, requiresIdentity, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            if (!core.Multiplexer.HasReliableCorrectionOrdering)
            {
                throw new InvalidOperationException(
                    "Reliable correction ordering must be initialized before a tracked script starts.");
            }

            connection = core.Multiplexer.GetConnection();
        }

        var identity = GetTrackedConnectionIdentity(
            connection, core.Cluster?.HasReliableCorrectionOrdering(connection) ?? true);
        var execution = new TrackedScriptExecution(connection, identity);
        execution.Response = core.Cluster is { } router
            ? ExecuteTrackedClusterScriptAsync(
                execution, router, connection, script, tail, requiresIdentity, cancellationToken)
            : ExecuteScriptOnConnectionAsync(connection, script, tail, cancellationToken);
        return execution;
    }

    private async ValueTask<RespireConnection> GetTrackedClusterConnectionAsync(
        ClusterRouter cluster,
        int? slot,
        bool requireIdentity,
        CancellationToken cancellationToken)
    {
        if (_core.Options.CommandTimeout is not { } timeout)
        {
            return await cluster.GetTrackedConnectionAsync(slot, requireIdentity, cancellationToken)
                .ConfigureAwait(false);
        }

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            return await cluster.GetTrackedConnectionAsync(slot, requireIdentity, timeoutSource.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new RespireTimeoutException("CLIENT ID / CLIENT KILL", timeout);
        }
    }

    private async ValueTask<RespireConnection> GetTrackedRedirectConnectionAsync(
        ClusterRouter cluster,
        RespireServerException error,
        RespireConnection source,
        bool requireIdentity,
        CancellationToken cancellationToken)
    {
        if (_core.Options.CommandTimeout is not { } timeout)
        {
            return await cluster.GetTrackedRedirectConnectionAsync(
                    error, source, requireIdentity, cancellationToken)
                .ConfigureAwait(false);
        }

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            return await cluster.GetTrackedRedirectConnectionAsync(
                    error, source, requireIdentity, timeoutSource.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new RespireTimeoutException("CLIENT ID / CLIENT KILL", timeout);
        }
    }

    private static TrackedConnectionIdentity GetTrackedConnectionIdentity(
        RespireConnection connection,
        bool reliable,
        bool requiresAsking = false)
        => reliable && connection.ServerClientId > 0
            ? new TrackedConnectionIdentity(
                new RespireEndpoint(connection.Host, connection.Port),
                connection.ServerClientId,
                requiresAsking)
            : default;

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
                    execution, cluster, connection, "EVALSHA", Verbs.EvalSha, script.Sha1, script.Sha1, tail,
                    requiresIdentity, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (RespireServerException ex) when (ex.Code == RespireErrorCodes.NoScript)
        {
            return await ExecuteTrackedClusterCommandAsync(
                    execution, cluster, execution.Connection, "EVAL", Verbs.Eval, script.Source, script.Sha1, tail,
                    requiresIdentity, cancellationToken)
                .ConfigureAwait(false);
        }
    }

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
        var connection = initialConnection;
        var sendAsking = execution.ConnectionIdentity.RequiresAsking;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var reply = await SendOnConnectionAsync(
                        operation, connection, new Cmd2N(verb, body, tail[0], tail[1..]),
                        cancellationToken, storedProcedureName, sendAsking)
                    .ConfigureAwait(false);
                return new RespireResult(in reply);
            }
            catch (RespireServerException error)
                when (attempt < ClusterRouter.RedirectLimit && ClusterRouter.IsRedirect(error))
            {
                connection = await GetTrackedRedirectConnectionAsync(
                        cluster, error, connection, requiresIdentity, cancellationToken)
                    .ConfigureAwait(false);
                sendAsking = error.Code == RespireErrorCodes.Ask;
                execution.Connection = connection;
                execution.ConnectionIdentity = GetTrackedConnectionIdentity(
                    connection, cluster.HasReliableCorrectionOrdering(connection), sendAsking);
            }
        }
    }

    private async ValueTask<RespireResult> ExecuteScriptOnConnectionAsync(
        RespireConnection connection,
        RespireScript script,
        RespireValue[] tail,
        CancellationToken cancellationToken)
    {
        var core = _core;
        var telemetry = RespireTelemetry.StartOperation(
            "EVALSHA",
            connection.Host,
            connection.Port,
            core.Options.Database,
            storedProcedureName: script.Sha1);
        try
        {
            var result = await ExecuteScriptOnConnectionCoreAsync(connection, script, tail, cancellationToken)
                .ConfigureAwait(false);
            telemetry.Complete(core, "EVALSHA", script.Sha1, connection: connection);
            return result;
        }
        catch (Exception ex)
        {
            telemetry.Complete(core, "EVALSHA", script.Sha1, ex, connection);
            throw;
        }
    }

    private async ValueTask<RespireResult> ExecuteScriptOnConnectionCoreAsync(
        RespireConnection connection,
        RespireScript script,
        RespireValue[] tail,
        CancellationToken cancellationToken)
    {
        try
        {
            var reply = await SendOnConnectionCoreAsync(
                    "EVALSHA", connection, new Cmd2N(Verbs.EvalSha, script.Sha1, tail[0], tail[1..]), cancellationToken)
                .ConfigureAwait(false);
            return new RespireResult(in reply);
        }
        catch (RespireServerException ex) when (ex.Code == RespireErrorCodes.NoScript)
        {
            var reply = await SendOnConnectionCoreAsync(
                    "EVAL", connection, new Cmd2N(Verbs.Eval, script.Source, tail[0], tail[1..]), cancellationToken)
                .ConfigureAwait(false);
            return new RespireResult(in reply);
        }
    }

    /// <summary>
    /// Kills one multiplexed Redis client through a separate control connection and waits for
    /// the server acknowledgement. The acknowledged kill is an ordering barrier: no command
    /// from the target client can execute afterward.
    /// </summary>
    internal async ValueTask FenceCorrectionConnectionAsync(TrackedConnectionIdentity identity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(identity.ServerClientId);
        var core = _core;
        ObjectDisposedException.ThrowIf(core.Disposed, this);

        var pool = core.Cluster is { } cluster
            ? cluster.GetDedicatedPool(identity.Endpoint)
            : core.DedicatedPool;
        var control = await pool.RentAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            var reply = await control.SendAsync(
                new ClientKillIdCommand(identity.ServerClientId), CancellationToken.None).ConfigureAwait(false);
            if (reply.IsError)
            {
                var error = ResponseReader.ServerError(in reply, "CLIENT KILL");
                reply.Dispose();
                throw error;
            }

            reply.Dispose();
        }
        finally
        {
            pool.Return(control);
        }

        if (core.Cluster is { } router)
        {
            await router.RetireConnectionAsync(identity.Endpoint, identity.ServerClientId).ConfigureAwait(false);
        }
        else
        {
            await core.Multiplexer.RetireConnectionAsync(identity.ServerClientId).ConfigureAwait(false);
        }
    }

    internal RespireValue[] BuildScriptTail(RespireKey[]? keys, RespireValue[]? args)
    {
        var keyCount = keys?.Length ?? 0;
        var argCount = args?.Length ?? 0;
        var tail = new RespireValue[1 + keyCount + argCount];
        tail[0] = keyCount;
        for (var i = 0; i < keyCount; i++)
        {
            tail[1 + i] = Key(in keys![i]);
        }

        for (var i = 0; i < argCount; i++)
        {
            tail[1 + keyCount + i] = args![i];
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
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            await UnlinkLeasedAsync(key, timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new RespireTimeoutException("UNLINK", timeout);
        }
    }

    private async ValueTask UnlinkLeasedAsync(RespireKey key, CancellationToken cancellationToken)
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
                    "EVAL", command, cancellationToken, LeasedUnlinkScript.Sha1).ConfigureAwait(false);
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
        var tail = new RespireValue[1 + keys.Length + args.Length];
        tail[0] = keys.Length;
        for (var i = 0; i < keys.Length; i++)
        {
            tail[1 + i] = Key(in keys[i]);
        }

        args.CopyTo(tail, 1 + keys.Length);
        var multiplexer = core.Cluster is { } cluster && connectionIdentity.Endpoint.Host is not null
            ? cluster.GetMultiplexer(connectionIdentity.Endpoint)
            : core.Multiplexer;
        await multiplexer.SendToAllConnectionsAsync(
            new Cmd2N(Verbs.Eval, script.Source, tail[0], tail[1..]),
            connectionIdentity.RequiresAsking,
            CancellationToken.None).ConfigureAwait(false);
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

    private ValueTask<TResult> ConvertResponseAsync<TCommand, TState, TResult>(
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
        if (core.Options.CommandTimeout is null
            && !RespireTelemetry.IsEnabled
            && core.Cluster is null
            && core.Multiplexer.IsInitialized)
        {
            var connection = core.Multiplexer.GetConnection();
            return connection.SendConvertedAsync(
                in command, state, converter, transferOwnership, ct, operation);
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

    internal ValueTask<bool> ConfirmedOkAsync<TCommand>(string operation, TCommand command, CancellationToken ct)
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
        => ConvertAsync(
            operation, command, ct,
            static (RespireClient _, in RespValue value) => ResponseReader.StringOrNull(in value));

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

    internal ValueTask<long?[]> NullableIntegerArrayAsync<TCommand>(string operation, TCommand command, CancellationToken ct)
        where TCommand : struct, IRespCommand
        => ConvertResponseAsync(
            operation, command, ct, this,
            static (RespireClient _, in RespValue value) => ResponseReader.NullableIntegerArray(in value));

    internal ValueTask<Dictionary<string, string>> StringMapAsync<TCommand>(string operation, TCommand command, CancellationToken ct)
        where TCommand : struct, IRespCommand
        => ConvertAsync(
            operation, command, ct,
            static (RespireClient _, in RespValue value) => ResponseReader.StringMap(in value));

    internal ValueTask<T?> DeserializeAsync<T, TCommand>(string operation, TCommand command, CancellationToken ct)
        where TCommand : struct, IRespCommand
        => ConvertAsync<TCommand, T?>(
            operation, command, ct,
            static (RespireClient client, in RespValue value) => client.DeserializeBorrowed<T>(in value));

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
}
