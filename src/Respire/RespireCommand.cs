using Respire.Commands;

namespace Respire;

/// <summary>
/// A known RESP command whose verb is parsed and encoded once. Use entries from
/// <see cref="RespireCommands"/> with <see cref="IRespireClient.ExecuteAsync(RespireCommand, RespireValue[])"/>
/// for discoverable access to commands without a dedicated convenience method.
/// </summary>
public readonly struct RespireCommand
{
    private readonly Verb _verb;

    internal RespireCommand(string name, RespireCommandSource sources)
    {
        Name = name;
        Sources = sources;
        _verb = new Verb(name);
        Behavior = Classify(name);
    }

    /// <summary>Canonical command name, including any subcommand (for example, <c>CONFIG GET</c>).</summary>
    public string Name { get; }

    /// <summary>Command references in which this command was found.</summary>
    public RespireCommandSource Sources { get; }

    internal Verb Verb => _verb;

    internal RespireCommandBehavior Behavior { get; }

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

internal enum RespireCommandBehavior
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
    None = 0,
    Redis = 1,
    Valkey = 2,
    KeyDb = 4,
    Dragonfly = 8,
    RedisAndValkey = Redis | Valkey,
}
