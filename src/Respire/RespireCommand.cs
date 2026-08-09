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
    }

    /// <summary>Canonical command name, including any subcommand (for example, <c>CONFIG GET</c>).</summary>
    public string Name { get; }

    /// <summary>Command references in which this command was found.</summary>
    public RespireCommandSource Sources { get; }

    internal Verb Verb => _verb;

    public override string ToString() => Name;
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
