using Respire.Commands;

namespace Respire;

/// <summary>
/// A RESP command whose verb is parsed and encoded once. Use entries from
/// <see cref="RespireCommands"/> for discoverable access, or pass a command name as a string for
/// an experimental or server-specific command.
/// </summary>
public readonly struct RespireCommand
{
    private const int SourceMask = 0xFF;
    private const int CacheMutationShift = 8;
    private const int CacheMutationMask = 0xF << CacheMutationShift;
    private const int ReadOnlyMetadataFlag = 1 << 12;
    private const int ExplicitCacheMutationFlag = 1 << 13;
    private readonly Verb _verb;
    private readonly int _sourceAndMutationMetadata;

    internal RespireCommand(string name, RespireCommandSource sources,
        RespireCacheMutation cacheMutation = RespireCacheMutation.Unknown, bool isReadOnly = false,
        bool hasExplicitCacheMutation = false)
    {
        Name = name;
        _sourceAndMutationMetadata = (int)sources | ((int)cacheMutation << CacheMutationShift)
            | (isReadOnly ? ReadOnlyMetadataFlag : 0)
            | (hasExplicitCacheMutation ? ExplicitCacheMutationFlag : 0);
        var readKind = Verb.GetReadKind(name, isReadOnly);
        _verb = new Verb(name, readKind);
        Behavior = Classify(name);
    }

    private RespireCommand(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
        _sourceAndMutationMetadata = 0;
        _verb = default;
        Behavior = default;
    }

    /// <summary>Canonical command name, including any subcommand (for example, <c>CONFIG GET</c>).</summary>
    public string Name { get; }

    /// <summary>Command references in which this command was found.</summary>
    public RespireCommandSource Sources => (RespireCommandSource)(_sourceAndMutationMetadata & SourceMask);

    /// <summary>
    /// Whether all audited providers explicitly mark this command read-only.
    /// False includes unknown metadata and caller-supplied commands; it does not prove a command writes.
    /// This metadata does not change routing, blocking behavior, or connection affinity.
    /// </summary>
    public bool IsReadOnly => (_sourceAndMutationMetadata & ReadOnlyMetadataFlag) != 0;

    /// <summary>How this command affects keys tracked by client-side caching.</summary>
    public RespireCacheMutation CacheMutation
        => (RespireCacheMutation)((_sourceAndMutationMetadata & CacheMutationMask) >> CacheMutationShift);

    internal Verb Verb => _verb;

    internal ReadCommandKind ReadKind => _verb.ReadKind;

    internal int CursorArgumentIndex => _verb.Bulk is null ? -1 : _verb.CursorArgumentIndex;

    internal RespireCommandBehavior Behavior { get; }

    internal bool IsCallerSupplied => _verb.Bulk is null;

    internal bool HasExplicitCacheMutation => (_sourceAndMutationMetadata & ExplicitCacheMutationFlag) != 0;

    /// <summary>
    /// Encodes a single command token once for repeated execution, including custom module commands.
    /// The token is normalized to uppercase ASCII. Pass subcommands and options as arguments.
    /// </summary>
    /// <remarks>
    /// Uses the same safety, routing, and cache invalidation policies as catalog commands.
    /// Key-prefixed views are unsupported because arbitrary commands have no known key layout.
    /// This does not declare the command read-only or associate it with an official command source.
    /// </remarks>
    /// <exception cref="ArgumentException">The name is empty or contains spaces, control characters, or non-ASCII characters.</exception>
    public static RespireCommand Create(string name)
        => Create(name, RespireCacheMutation.Unknown, hasExplicitCacheMutation: false);

    /// <summary>Creates a caller-supplied command descriptor with an explicit cache mutation policy.</summary>
    public static RespireCommand Create(string name, RespireCacheMutation cacheMutation)
        => Create(name, cacheMutation, hasExplicitCacheMutation: true);

    private static RespireCommand Create(string name, RespireCacheMutation cacheMutation, bool hasExplicitCacheMutation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        foreach (var character in name)
        {
            if (character is < '!' or > '~')
            {
                throw new ArgumentException("A command name must be one printable ASCII token.", nameof(name));
            }
        }

        return new RespireCommand(name.ToUpperInvariant(), RespireCommandSource.None, cacheMutation,
            hasExplicitCacheMutation: hasExplicitCacheMutation);
    }

    /// <summary>Creates a caller-supplied command descriptor from a command name.</summary>
    public static implicit operator RespireCommand(string name)
        => new(name);

    /// <inheritdoc/>
    public override string ToString() => Name;

    internal static RespireCommandBehavior Classify(string name) => name switch
    {
        "BLMOVE" or "BLMOVEM" or "BLMPOP" or "BLPOP" or "BRPOP" or "BRPOPLPUSH" or
        "BZMPOP" or "BZPOPMAX" or "BZPOPMIN" => RespireCommandBehavior.Blocking,

        "TS.READ" or "XREAD" or "XREADGROUP" => RespireCommandBehavior.BlockingWhenRequested,

        "ASKING" or "AUTH" or "CLIENT" or "CLIENT CACHING" or "CLIENT CAPA" or "CLIENT GETNAME" or
        "CLIENT GETREDIR" or "CLIENT ID" or "CLIENT IMPORT-SOURCE" or "CLIENT INFO" or
        "CLIENT MAINT_NOTIFICATIONS" or "CLIENT NO-EVICT" or "CLIENT NO-TOUCH" or "CLIENT REPLY" or
        "CLIENT SETINFO" or "CLIENT SETNAME" or "CLIENT TRACKING" or "CLIENT TRACKINGINFO" or
        "DISCARD" or "EXEC" or "HELLO" or
        "MONITOR" or "MULTI" or "PSUBSCRIBE" or "PSYNC" or "PUNSUBSCRIBE" or "QUIT" or
        "READONLY" or "READWRITE" or "REPLCONF" or "RESET" or "SCRIPT" or "SCRIPT DEBUG" or "SELECT" or
        "SSUBSCRIBE" or "SUBSCRIBE" or "SUNSUBSCRIBE" or "SYNC" or "UNSUBSCRIBE" or
        "UNWATCH" or "WAIT" or "WAITAOF" or "WATCH" => RespireCommandBehavior.ConnectionScoped,

        _ => RespireCommandBehavior.Multiplexed,
    };

    internal static bool MayCloseWithoutReply(string name) => name == "SHUTDOWN";

    internal bool IsBlocking(RespireValue[] args) => IsBlocking(Name, Behavior, args);

    internal static bool IsBlocking(
        string name,
        RespireCommandBehavior behavior,
        ReadOnlySpan<RespireValue> args)
        => IsBlocking(name, behavior, ReadOnlySpan<string>.Empty, args);

    internal static bool IsBlocking(
        string name,
        RespireCommandBehavior behavior,
        ReadOnlySpan<string> inlineArguments,
        ReadOnlySpan<RespireValue> arguments)
    {
        if (behavior == RespireCommandBehavior.Blocking)
        {
            return true;
        }

        if (behavior != RespireCommandBehavior.BlockingWhenRequested)
        {
            return false;
        }

        var firstOptionIndex = name == "XREADGROUP" ? 3 : 0;
        var stopsAtStreams = name is "XREAD" or "XREADGROUP";
        var index = 0;
        foreach (var argument in inlineArguments)
        {
            if (index++ < firstOptionIndex)
            {
                continue;
            }

            if (stopsAtStreams && argument.Equals("STREAMS", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (argument.Equals("BLOCK", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        foreach (var argument in arguments)
        {
            if (index++ < firstOptionIndex)
            {
                continue;
            }

            if (stopsAtStreams && argument.EqualsAsciiIgnoreCase("STREAMS"))
            {
                return false;
            }

            if (argument.EqualsAsciiIgnoreCase("BLOCK"))
            {
                return true;
            }
        }

        return false;
    }
}

[AttributeUsage(AttributeTargets.Field, Inherited = false)]
internal sealed class RespireCommandCatalogNameAttribute(string name) : Attribute
{
    public string Name { get; } = name;
}

internal enum ReadCommandKind : byte
{
    None,
    Read,
    CursorRead,
}

// Keep the behavior and read-only metadata within the descriptor's existing footprint.
internal enum RespireCommandBehavior : byte
{
    Multiplexed,
    Blocking,
    BlockingWhenRequested,
    ConnectionScoped,
}

/// <summary>Official command-reference sources. Flags can be combined.</summary>
[Flags]
public enum RespireCommandSource
{
    /// <summary>No official source reference.</summary>
    None = 0,
    /// <summary>Documented by Redis.</summary>
    Redis = 1,
    /// <summary>Documented by Valkey.</summary>
    Valkey = 2,
    /// <summary>Documented by KeyDB.</summary>
    KeyDb = 4,
    /// <summary>Documented by Dragonfly.</summary>
    Dragonfly = 8,
    /// <summary>Documented by both Redis and Valkey.</summary>
    RedisAndValkey = Redis | Valkey,
}
