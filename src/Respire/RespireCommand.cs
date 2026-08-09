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

    private static RespireCommandBehavior Classify(string name) => name switch
    {
        "BLMOVE" or "BLMPOP" or "BLPOP" or "BRPOP" or "BRPOPLPUSH" or
        "BZMPOP" or "BZPOPMAX" or "BZPOPMIN" => RespireCommandBehavior.Blocking,

        "XREAD" or "XREADGROUP" => RespireCommandBehavior.BlockingWhenRequested,

        "ASKING" or "AUTH" or "CLIENT" or "CLIENT CACHING" or "CLIENT CAPA" or "CLIENT IMPORT-SOURCE" or
        "CLIENT MAINT_NOTIFICATIONS" or "CLIENT NO-EVICT" or "CLIENT NO-TOUCH" or "CLIENT REPLY" or
        "CLIENT SETINFO" or "CLIENT SETNAME" or "CLIENT TRACKING" or "DISCARD" or "EXEC" or "HELLO" or
        "MONITOR" or "MULTI" or "PSUBSCRIBE" or "PSYNC" or "PUNSUBSCRIBE" or "QUIT" or
        "READONLY" or "READWRITE" or "REPLCONF" or "RESET" or "SCRIPT" or "SCRIPT DEBUG" or "SELECT" or
        "SSUBSCRIBE" or "SUBSCRIBE" or "SUNSUBSCRIBE" or "SYNC" or "UNSUBSCRIBE" or
        "UNWATCH" or "WAIT" or "WAITAOF" or "WATCH" => RespireCommandBehavior.ConnectionScoped,

        _ => RespireCommandBehavior.Multiplexed,
    };

    internal bool IsBlocking(RespireValue[] args)
    {
        if (Behavior == RespireCommandBehavior.Blocking)
        {
            return true;
        }

        if (Behavior != RespireCommandBehavior.BlockingWhenRequested)
        {
            return false;
        }

        foreach (var arg in args)
        {
            if (arg.EqualsAsciiIgnoreCase("BLOCK"))
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
