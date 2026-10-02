using System.Collections.Frozen;

namespace Respire.Internal;

/// <summary>Resolves catalog metadata only for raw string commands that have no descriptor.</summary>
internal static class RawCommandDescriptorLookup
{
    private static readonly FrozenDictionary<string, RespireCommand> s_readCommands = BuildReadCommands();

    internal static ReadCommandKind GetReadKind(
        string operation, ReadOnlySpan<RespireValue> args = default)
    {
        if (s_readCommands.TryGetValue(operation, out var command)) return command.ReadKind;
        if (args.Length > 0
            && RespireClient.KnownRawOperation(operation, args[0]) is { } normalized
            && s_readCommands.TryGetValue(normalized, out command))
            return command.ReadKind;
        return ReadCommandKind.None;
    }

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
