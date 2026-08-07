using Respire.Protocol;

namespace Respire.Commands;

// Generic command shapes: an array header, the pre-encoded verb, then N bulk-string arguments.
// Readonly structs so the connection's generic send path is fully monomorphized.

internal readonly struct Cmd(Verb verb) : IRespCommand
{
    public void Write(ref RespWriter writer)
    {
        writer.WriteArrayHeader(verb.Tokens);
        writer.WriteRaw(verb.Bulk);
    }
}

internal readonly struct Cmd1(Verb verb, RespireValue a1) : IRespCommand
{
    public void Write(ref RespWriter writer)
    {
        writer.WriteArrayHeader(verb.Tokens + 1);
        writer.WriteRaw(verb.Bulk);
        a1.WriteTo(ref writer);
    }
}

internal readonly struct Cmd2(Verb verb, RespireValue a1, RespireValue a2) : IRespCommand
{
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

/// <summary>VERB a1 a2 rest… (e.g. XACK key group id…).</summary>
internal readonly struct Cmd2N(Verb verb, RespireValue a1, RespireValue a2, RespireValue[] rest) : IRespCommand
{
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
internal readonly struct DynamicCommand(RespireValue[] tokens) : IRespCommand
{
    public void Write(ref RespWriter writer)
    {
        writer.WriteArrayHeader(tokens.Length);
        foreach (var token in tokens)
        {
            token.WriteTo(ref writer);
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

/// <summary>SET key value [PX ms] [NX|XX] [KEEPTTL] [GET].</summary>
internal readonly struct SetCommand(
    RespireValue key, RespireValue value, TimeSpan? expiry, SetWhen when, bool keepTtl, bool returnOld) : IRespCommand
{
    public void Write(ref RespWriter writer)
    {
        var count = 3
            + (expiry.HasValue ? 2 : 0)
            + (when != SetWhen.Always ? 1 : 0)
            + (keepTtl ? 1 : 0)
            + (returnOld ? 1 : 0);
        writer.WriteArrayHeader(count);
        writer.WriteRaw(Verbs.Set.Bulk);
        key.WriteTo(ref writer);
        value.WriteTo(ref writer);

        if (expiry is { } ttl)
        {
            writer.WriteBulkString("PX"u8);
            writer.WriteBulkInteger((long)ttl.TotalMilliseconds);
        }

        if (when == SetWhen.NotExists)
        {
            writer.WriteBulkString("NX"u8);
        }
        else if (when == SetWhen.Exists)
        {
            writer.WriteBulkString("XX"u8);
        }

        if (keepTtl)
        {
            writer.WriteBulkString("KEEPTTL"u8);
        }

        if (returnOld)
        {
            writer.WriteBulkString("GET"u8);
        }
    }
}
