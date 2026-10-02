using System.Collections.Frozen;

namespace Respire.Internal;

/// <summary>Resolves catalog metadata only for raw string commands that have no descriptor.</summary>
internal static class RawCommandDescriptorLookup
{
    private static readonly FrozenDictionary<string, RespireCommand> s_readCommands = BuildReadCommands();

    internal static ReadCommandKind GetReadKind(string operation)
        => s_readCommands.TryGetValue(operation, out var command) ? command.ReadKind : ReadCommandKind.None;

    private static FrozenDictionary<string, RespireCommand> BuildReadCommands()
    {
        var commands = new Dictionary<string, RespireCommand>(StringComparer.OrdinalIgnoreCase);
        foreach (var command in RespireCommands.All)
        {
            if (command.ReadKind != ReadCommandKind.None)
                commands[command.Name] = command;
        }

        return commands.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }
}
