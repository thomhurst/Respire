namespace Respire.Commands;

/// <summary>Builds commands that require all physical keys to share a Cluster slot.</summary>
internal static class SingleSlotCommand
{
    internal static CmdN Create(RespireClient client, string operation, Verb verb, ReadOnlySpan<RespireKey> keys)
    {
        var arguments = client.MapKeys(keys);
        ValidateKeys(client, operation, arguments);
        return new(verb, arguments);
    }

    internal static Cmd1N Create(RespireClient client, string operation, Verb verb,
        RespireKey destination, ReadOnlySpan<RespireKey> keys)
    {
        RespireValue first = client.Key(in destination);
        var rest = client.MapKeys(keys);
        ValidateKeys(client, operation, rest, first);
        return new(verb, first, rest);
    }

    internal static void ValidateKeys(RespireClient client, string operation, ReadOnlySpan<RespireValue> keys,
        RespireValue? first = null)
    {
        if (client.Core.Cluster is null || keys.Length < (first.HasValue ? 1 : 2))
        {
            return;
        }

        bool includeKeyContext = client.Core.Options.IncludeCrossSlotKeyContext;
        int? slot = null;

        if (first is { } firstKey)
        {
            RawCommandKeyLayouts.ValidateKey(firstKey, operation, ref slot, includeKeyContext);
        }

        foreach (ref readonly var key in keys)
        {
            RawCommandKeyLayouts.ValidateKey(key, operation, ref slot, includeKeyContext);
        }
    }
}
