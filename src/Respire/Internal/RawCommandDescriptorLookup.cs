namespace Respire.Internal;

/// <summary>Resolves catalog metadata only for raw string commands that have no descriptor.</summary>
internal static class RawCommandDescriptorLookup
{
    internal static ReadCommandKind GetReadKind(
        string operation, ReadOnlySpan<RespireValue> args = default)
    {
        var kind = CommandReadMetadata.Get(operation).Kind;
        if (kind != ReadCommandKind.None) return kind;
        if (args.Length > 0
            && RespireClient.KnownRawOperation(operation, args[0]) is { } normalized)
            return CommandReadMetadata.Get(normalized).Kind;
        return ReadCommandKind.None;
    }
}
