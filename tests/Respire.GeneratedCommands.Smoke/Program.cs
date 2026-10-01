using Respire;

var endpoint = args.Length > 0 ? args[0] : "127.0.0.1:6379";
foreach (var protocol in new[] { RespProtocol.Resp2, RespProtocol.Resp3 })
{
    await using var client = await RespireClient.ConnectAsync($"redis://{endpoint}?protocol={(int)protocol}");
    var commands = new ISmokeCommandsImplementation(client);
    var key = "respire:generator-smoke:" + Guid.NewGuid().ToString("N");
    try
    {
        await commands.Set(key, "generated");
        if (await commands.Get(key) != "generated") throw new InvalidOperationException("Generated GET failed.");
        var values = await commands.MultiGet(key, key + ":missing");
        if (values.Length != 2 || values[0] != "generated" || values[1] is not null)
            throw new InvalidOperationException("Generated aggregate reply failed.");
        using var raw = await commands.RawGet(key);
        if (raw.AsString() != "generated") throw new InvalidOperationException("Generated raw reply failed.");
    }
    finally
    {
        await commands.Delete(key);
    }
}
Console.WriteLine("Generated commands passed with RESP2 and RESP3.");

[RespireCommands]
public interface ISmokeCommands
{
    [RespireCommand("SET")]
    ValueTask Set(RespireKey key, string value, CancellationToken cancellationToken = default);
    [RespireCommand("GET")]
    ValueTask<string?> Get(RespireKey key);
    [RespireCommand("MGET")]
    ValueTask<string?[]> MultiGet(params RespireKey[] keys);
    [RespireCommand("GET")]
    ValueTask<RespireResult> RawGet(RespireKey key);
    [RespireCommand("DEL")]
    ValueTask<long> Delete(RespireKey key);
}
