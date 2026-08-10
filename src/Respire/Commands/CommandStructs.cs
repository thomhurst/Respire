using Respire.Protocol;

namespace Respire.Commands;

// Generic command shapes: an array header, the pre-encoded verb, then N bulk-string arguments.
// Readonly structs so the connection's generic send path is fully monomorphized.

internal readonly struct Cmd(Verb verb) : IRespCommand
{
    public bool TryGetClusterSlot(out int slot)
    {
        slot = 0;
        return false;
    }

    public void Write(ref RespWriter writer)
    {
        writer.WriteArrayHeader(verb.Tokens);
        writer.WriteRaw(verb.Bulk);
    }
}

internal readonly struct Cmd1(Verb verb, RespireValue a1) : IRespCommand
{
    public bool TryGetClusterSlot(out int slot)
        => CommandRouting.TryGetClusterSlot(verb, 0, a1, out slot);

    public void Write(ref RespWriter writer)
    {
        writer.WriteArrayHeader(verb.Tokens + 1);
        writer.WriteRaw(verb.Bulk);
        a1.WriteTo(ref writer);
    }
}

internal readonly struct Cmd2(Verb verb, RespireValue a1, RespireValue a2) : IRespCommand
{
    public bool TryGetClusterSlot(out int slot)
        => CommandRouting.TryGetClusterSlot(verb, 0, a1, out slot)
            || CommandRouting.TryGetClusterSlot(verb, 1, a2, out slot);

    public void Write(ref RespWriter writer)
    {
        writer.WriteArrayHeader(verb.Tokens + 2);
        writer.WriteRaw(verb.Bulk);
        a1.WriteTo(ref writer);
        a2.WriteTo(ref writer);
    }
}

internal readonly struct Cmd3(Verb verb, RespireValue a1, RespireValue a2, RespireValue a3) : IRespCommand
{
    public bool TryGetClusterSlot(out int slot)
        => CommandRouting.TryGetClusterSlot(verb, 0, a1, out slot)
            || CommandRouting.TryGetClusterSlot(verb, 1, a2, out slot)
            || CommandRouting.TryGetClusterSlot(verb, 2, a3, out slot);

    public void Write(ref RespWriter writer)
    {
        writer.WriteArrayHeader(verb.Tokens + 3);
        writer.WriteRaw(verb.Bulk);
        a1.WriteTo(ref writer);
        a2.WriteTo(ref writer);
        a3.WriteTo(ref writer);
    }
}

internal readonly struct Cmd4(Verb verb, RespireValue a1, RespireValue a2, RespireValue a3, RespireValue a4) : IRespCommand
{
    public bool TryGetClusterSlot(out int slot)
        => CommandRouting.TryGetClusterSlot(verb, 0, a1, out slot)
            || CommandRouting.TryGetClusterSlot(verb, 1, a2, out slot)
            || CommandRouting.TryGetClusterSlot(verb, 2, a3, out slot)
            || CommandRouting.TryGetClusterSlot(verb, 3, a4, out slot);

    public void Write(ref RespWriter writer)
    {
        writer.WriteArrayHeader(verb.Tokens + 4);
        writer.WriteRaw(verb.Bulk);
        a1.WriteTo(ref writer);
        a2.WriteTo(ref writer);
        a3.WriteTo(ref writer);
        a4.WriteTo(ref writer);
    }
}

internal readonly struct Cmd5(Verb verb, RespireValue a1, RespireValue a2, RespireValue a3, RespireValue a4, RespireValue a5) : IRespCommand
{
    public bool TryGetClusterSlot(out int slot)
        => CommandRouting.TryGetClusterSlot(verb, 0, a1, out slot)
            || CommandRouting.TryGetClusterSlot(verb, 1, a2, out slot)
            || CommandRouting.TryGetClusterSlot(verb, 2, a3, out slot)
            || CommandRouting.TryGetClusterSlot(verb, 3, a4, out slot)
            || CommandRouting.TryGetClusterSlot(verb, 4, a5, out slot);

    public void Write(ref RespWriter writer)
    {
        writer.WriteArrayHeader(verb.Tokens + 5);
        writer.WriteRaw(verb.Bulk);
        a1.WriteTo(ref writer);
        a2.WriteTo(ref writer);
        a3.WriteTo(ref writer);
        a4.WriteTo(ref writer);
        a5.WriteTo(ref writer);
    }
}

/// <summary>VERB args… — fully dynamic argument list.</summary>
internal readonly struct CmdN(Verb verb, RespireValue[] args) : IRespCommand
{
    public bool TryGetClusterSlot(out int slot)
    {
        if ((uint)verb.RoutingKeyIndex < (uint)args.Length)
        {
            return args[verb.RoutingKeyIndex].TryGetClusterSlot(out slot);
        }

        slot = 0;
        return false;
    }

    public void Write(ref RespWriter writer)
    {
        writer.WriteArrayHeader(verb.Tokens + args.Length);
        writer.WriteRaw(verb.Bulk);
        foreach (var arg in args)
        {
            arg.WriteTo(ref writer);
        }
    }
}

/// <summary>VERB fixed rest… (e.g. SADD key member…).</summary>
internal readonly struct Cmd1N(Verb verb, RespireValue a1, RespireValue[] rest) : IRespCommand
{
    public bool TryGetClusterSlot(out int slot)
    {
        if (verb.RoutingKeyIndex == 0)
        {
            return a1.TryGetClusterSlot(out slot);
        }

        if ((uint)(verb.RoutingKeyIndex - 1) < (uint)rest.Length)
        {
            return rest[verb.RoutingKeyIndex - 1].TryGetClusterSlot(out slot);
        }

        slot = 0;
        return false;
    }

    public void Write(ref RespWriter writer)
    {
        writer.WriteArrayHeader(verb.Tokens + 1 + rest.Length);
        writer.WriteRaw(verb.Bulk);
        a1.WriteTo(ref writer);
        foreach (var arg in rest)
        {
            arg.WriteTo(ref writer);
        }
    }
}

internal static class CommandRouting
{
    internal static bool TryGetClusterSlot(
        Verb verb,
        int argumentIndex,
        RespireValue argument,
        out int slot)
    {
        if (verb.RoutingKeyIndex == argumentIndex)
        {
            return argument.TryGetClusterSlot(out slot);
        }

        slot = 0;
        return false;
    }
}

/// <summary>VERB a1 a2 rest… (e.g. XACK key group id…).</summary>
internal readonly struct Cmd2N(Verb verb, RespireValue a1, RespireValue a2, RespireValue[] rest) : IRespCommand
{
    public bool TryGetClusterSlot(out int slot)
    {
        var index = verb.RoutingKeyIndex;
        if (index == 0)
        {
            return a1.TryGetClusterSlot(out slot);
        }

        if (index == 1)
        {
            return a2.TryGetClusterSlot(out slot);
        }

        if ((uint)(index - 2) < (uint)rest.Length)
        {
            return rest[index - 2].TryGetClusterSlot(out slot);
        }

        slot = 0;
        return false;
    }

    public void Write(ref RespWriter writer)
    {
        writer.WriteArrayHeader(verb.Tokens + 2 + rest.Length);
        writer.WriteRaw(verb.Bulk);
        a1.WriteTo(ref writer);
        a2.WriteTo(ref writer);
        foreach (var arg in rest)
        {
            arg.WriteTo(ref writer);
        }
    }
}

/// <summary>An entire command whose tokens (verb included) are caller-supplied — the raw escape hatch.</summary>
internal readonly struct DynamicCommand(RespireValue[] tokens, int routingKeyIndex) : IRespCommand
{
    public bool TryGetClusterSlot(out int slot)
    {
        if ((uint)routingKeyIndex < (uint)tokens.Length)
        {
            return tokens[routingKeyIndex].TryGetClusterSlot(out slot);
        }

        slot = 0;
        return false;
    }

    public void Write(ref RespWriter writer)
    {
        writer.WriteArrayHeader(tokens.Length);
        foreach (var token in tokens)
        {
            token.WriteTo(ref writer);
        }
    }
}

internal static class DynamicCommandRouting
{
    internal static int GetRoutingKeyIndex(
        string operation,
        RespireValue[] tokens,
        int firstArgumentIndex)
    {
        if ((uint)firstArgumentIndex >= (uint)tokens.Length)
        {
            return -1;
        }

        return operation switch
        {
            "EVAL" or "EVAL_RO" or "EVALSHA" or "EVALSHA_RO" or "FCALL" or "FCALL_RO" =>
                HasKeys(tokens, firstArgumentIndex + 1) ? firstArgumentIndex + 2 : -1,
            "BITOP" => firstArgumentIndex + 1,
            "BLMPOP" or "BZMPOP" =>
                HasKeys(tokens, firstArgumentIndex + 1) ? firstArgumentIndex + 2 : -1,
            "LMPOP" or "MSETEX" or "ZMPOP" or "SINTERCARD" or
            "ZDIFF" or "ZINTER" or "ZINTERCARD" or "ZUNION" =>
                HasKeys(tokens, firstArgumentIndex) ? firstArgumentIndex + 1 : -1,
            "MIGRATE" => MigrateKeyIndex(tokens, firstArgumentIndex),
            "OBJECT" or "MEMORY" or "XGROUP" or "XINFO" =>
                firstArgumentIndex == 1 ? firstArgumentIndex + 1 : firstArgumentIndex,
            "XREAD" or "XREADGROUP" => IndexAfter(tokens, firstArgumentIndex, "STREAMS"),
            "ACL" or "ASKING" or "AUTH" or "BGSAVE" or "BGREWRITEAOF" or "CLIENT" or "CLUSTER" or
            "COMMAND" or "CONFIG" or "CONFIG GET" or "CONFIG SET" or "DBSIZE" or "DISCARD" or "ECHO" or
            "EXEC" or "FAILOVER" or "FLUSHALL" or "FLUSHDB" or "FUNCTION" or "HELLO" or "INFO" or
            "KEYS" or "LASTSAVE" or "LATENCY" or "LOLWUT" or "MODULE" or "MONITOR" or "MULTI" or
            "PING" or "PSUBSCRIBE" or "PUBSUB" or "PUNSUBSCRIBE" or "QUIT" or "RANDOMKEY" or
            "READONLY" or "READWRITE" or "REPLICAOF" or "RESET" or "ROLE" or "SAVE" or "SCAN" or
            "SCRIPT" or "SCRIPT DEBUG" or "SCRIPT EXISTS" or "SCRIPT FLUSH" or "SCRIPT HELP" or "SCRIPT KILL" or
            "SCRIPT LOAD" or "SCRIPT SHOW" or "SELECT" or "SHUTDOWN" or "SLOWLOG" or "SUBSCRIBE" or
            "SUNSUBSCRIBE" or "SWAPDB" or "SYNC" or "TIME" or "UNSUBSCRIBE" or "UNWATCH" or "WAIT" or
            "WAITAOF" => -1,
            _ => firstArgumentIndex,
        };
    }

    private static bool HasKeys(RespireValue[] tokens, int keyCountIndex)
        => (uint)keyCountIndex < (uint)tokens.Length
           && tokens[keyCountIndex].TryGetInt64(out var count)
           && count > 0;

    internal static int GetCatalogRoutingKeyIndex(string operation, RespireValue[] args)
        => IsUnkeyedCatalogSubcommand(operation)
            ? -1
            : GetRoutingKeyIndex(operation, args, 0);

    internal static bool IsClusterWideMutation(
        string operation,
        ReadOnlySpan<RespireValue> arguments)
    {
        if (operation.Equals("FUNCTION", StringComparison.OrdinalIgnoreCase))
        {
            return arguments.Length > 0 && IsFunctionMutation(arguments[0]);
        }

        const string prefix = "FUNCTION ";
        if (operation.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return IsFunctionMutation(operation.AsSpan(prefix.Length));
        }

        if (operation.Equals("SCRIPT", StringComparison.OrdinalIgnoreCase))
        {
            return arguments.Length > 0 && IsScriptMutation(arguments[0]);
        }

        const string scriptPrefix = "SCRIPT ";
        return operation.StartsWith(scriptPrefix, StringComparison.OrdinalIgnoreCase)
               && IsScriptMutation(operation.AsSpan(scriptPrefix.Length));
    }

    private static bool IsFunctionMutation(RespireValue subcommand)
        => subcommand.EqualsAsciiIgnoreCase("LOAD")
           || subcommand.EqualsAsciiIgnoreCase("DELETE")
           || subcommand.EqualsAsciiIgnoreCase("FLUSH")
           || subcommand.EqualsAsciiIgnoreCase("RESTORE");

    private static bool IsFunctionMutation(ReadOnlySpan<char> subcommand)
        => subcommand.Equals("LOAD", StringComparison.OrdinalIgnoreCase)
           || subcommand.Equals("DELETE", StringComparison.OrdinalIgnoreCase)
           || subcommand.Equals("FLUSH", StringComparison.OrdinalIgnoreCase)
           || subcommand.Equals("RESTORE", StringComparison.OrdinalIgnoreCase);

    private static bool IsScriptMutation(RespireValue subcommand)
        => subcommand.EqualsAsciiIgnoreCase("LOAD")
           || subcommand.EqualsAsciiIgnoreCase("FLUSH");

    private static bool IsScriptMutation(ReadOnlySpan<char> subcommand)
        => subcommand.Equals("LOAD", StringComparison.OrdinalIgnoreCase)
           || subcommand.Equals("FLUSH", StringComparison.OrdinalIgnoreCase);

    private static bool IsUnkeyedCatalogSubcommand(string operation)
    {
        if (operation.Length < 4)
        {
            return false;
        }

        return operation[0] switch
        {
            'A' => operation.StartsWith("ACL ", StringComparison.Ordinal),
            'C' => operation.StartsWith("CLIENT ", StringComparison.Ordinal)
                   || operation.StartsWith("CLUSTER ", StringComparison.Ordinal)
                   || operation.StartsWith("COMMAND ", StringComparison.Ordinal)
                   || operation.StartsWith("CONFIG ", StringComparison.Ordinal),
            'F' => operation.StartsWith("FUNCTION ", StringComparison.Ordinal),
            'L' => operation.StartsWith("LATENCY ", StringComparison.Ordinal),
            'M' => operation.StartsWith("MODULE ", StringComparison.Ordinal),
            'P' => operation.StartsWith("PUBSUB ", StringComparison.Ordinal),
            'S' => operation.StartsWith("SCRIPT ", StringComparison.Ordinal)
                   || operation.StartsWith("SLOWLOG ", StringComparison.Ordinal),
            _ => false,
        };
    }

    private static int MigrateKeyIndex(RespireValue[] tokens, int firstArgumentIndex)
    {
        var keyIndex = firstArgumentIndex + 2;
        if ((uint)keyIndex < (uint)tokens.Length && !tokens[keyIndex].IsEmpty)
        {
            return keyIndex;
        }

        return IndexAfter(tokens, firstArgumentIndex + 3, "KEYS");
    }

    private static int IndexAfter(RespireValue[] tokens, int startIndex, string marker)
    {
        for (var index = startIndex; index + 1 < tokens.Length; index++)
        {
            if (tokens[index].EqualsAsciiIgnoreCase(marker))
            {
                return index + 1;
            }
        }

        return -1;
    }
}

/// <summary>A pre-encoded catalog command followed by caller-supplied arguments.</summary>
internal readonly struct CatalogCommand(RespireCommand command, RespireValue[] args) : IRespCommand
{
    public bool TryGetClusterSlot(out int slot)
    {
        var routingKeyIndex = DynamicCommandRouting.GetCatalogRoutingKeyIndex(command.Name, args);
        if ((uint)routingKeyIndex < (uint)args.Length)
        {
            return args[routingKeyIndex].TryGetClusterSlot(out slot);
        }

        slot = 0;
        return false;
    }

    public void Write(ref RespWriter writer)
    {
        writer.WriteArrayHeader(command.Verb.Tokens + args.Length);
        writer.WriteRaw(command.Verb.Bulk);
        foreach (var arg in args)
        {
            arg.WriteTo(ref writer);
        }
    }
}

/// <summary>MSETEX numkeys key value... options — routes by the first key after numkeys.</summary>
internal readonly struct MSetExCommand(Verb verb, RespireValue[] args) : IRespCommand
{
    public bool TryGetClusterSlot(out int slot)
    {
        if (args.Length > 1)
        {
            return args[1].TryGetClusterSlot(out slot);
        }

        slot = 0;
        return false;
    }

    public void Write(ref RespWriter writer)
    {
        writer.WriteArrayHeader(verb.Tokens + args.Length);
        writer.WriteRaw(verb.Bulk);
        foreach (var arg in args)
        {
            arg.WriteTo(ref writer);
        }
    }
}

/// <summary>
/// INCR/DECR key when the delta is 1 (the two-token form StackExchange.Redis sends), INCRBY/DECRBY
/// key delta otherwise. The wire-form choice lives here so the facet, batch, and transaction layers
/// all share it.
/// </summary>
internal readonly struct IncrementCommand(Verb one, Verb by, RespireValue key, long delta) : IRespCommand
{
    public bool TryGetClusterSlot(out int slot) => key.TryGetClusterSlot(out slot);

    public void Write(ref RespWriter writer)
    {
        if (delta == 1)
        {
            writer.WriteArrayHeader(one.Tokens + 1);
            writer.WriteRaw(one.Bulk);
            key.WriteTo(ref writer);
        }
        else
        {
            writer.WriteArrayHeader(by.Tokens + 2);
            writer.WriteRaw(by.Bulk);
            key.WriteTo(ref writer);
            writer.WriteBulkInteger(delta);
        }
    }
}

/// <summary>SET key value [PX ms | PXAT unix-ms] [NX|XX] [KEEPTTL] [GET].</summary>
internal readonly struct SetCommand(
    RespireValue key, RespireValue value, RespireTtl expiry, SetWhen when, bool returnOld) : IRespCommand
{
    public bool TryGetClusterSlot(out int slot) => key.TryGetClusterSlot(out slot);

    public void Write(ref RespWriter writer)
    {
        var count = 3
            + expiry.TokenCount
            + (when != SetWhen.Always ? 1 : 0)
            + (returnOld ? 1 : 0);
        writer.WriteArrayHeader(count);
        writer.WriteRaw(Verbs.Set.Bulk);
        key.WriteTo(ref writer);
        value.WriteTo(ref writer);

        if (expiry.TryGetRelativeMilliseconds(out var milliseconds))
        {
            writer.WriteBulkString("PX"u8);
            writer.WriteBulkInteger(milliseconds);
        }
        else if (expiry.TryGetAbsoluteUnixMilliseconds(out var unixMilliseconds))
        {
            writer.WriteBulkString("PXAT"u8);
            writer.WriteBulkInteger(unixMilliseconds);
        }

        if (when == SetWhen.NotExists)
        {
            writer.WriteBulkString("NX"u8);
        }
        else if (when == SetWhen.Exists)
        {
            writer.WriteBulkString("XX"u8);
        }

        if (expiry.IsKeep)
        {
            writer.WriteBulkString("KEEPTTL"u8);
        }

        if (returnOld)
        {
            writer.WriteBulkString("GET"u8);
        }
    }
}
