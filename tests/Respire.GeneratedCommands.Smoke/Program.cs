using System.Text.Json.Serialization;
using Respire;
using Respire.Extensions.Json;

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

    // Respire.Json under Native AOT: caller-supplied metadata, generated module commands, and key prefixes.
    var jsonPrefix = "respire:json-smoke:" + Guid.NewGuid().ToString("N") + ":";
    var json = new RespireJsonClient(client.WithKeyPrefix(jsonPrefix));
    try
    {
        if (!await json.SetAsync("doc", new SmokeDocument("generated", 1), SmokeJsonContext.Default.SmokeDocument))
            throw new InvalidOperationException("Respire.Json SET failed.");
        var document = await json.GetAsync("doc", SmokeJsonContext.Default.SmokeDocument, RespireJsonPath.JsonPathRoot);
        if (!document.Found || document.Value != new SmokeDocument("generated", 1))
            throw new InvalidOperationException("Respire.Json GET failed.");
        await json.MultiSetAsync([new("doc", new SmokeDocument("multi", 2))], SmokeJsonContext.Default.SmokeDocument);
        var documents = await json.MultiGetAsync(["doc", "missing"], SmokeJsonContext.Default.SmokeDocument);
        if (documents[0]?[0].Value?.Count != 2 || documents[1] is not null)
            throw new InvalidOperationException("Respire.Json MGET failed.");
    }
    finally
    {
        await json.DeleteAsync("doc");
    }
}
Console.WriteLine("Generated commands and Respire.Json passed with RESP2 and RESP3.");

public sealed record SmokeDocument(string Name, int Count);

[JsonSerializable(typeof(SmokeDocument))]
internal sealed partial class SmokeJsonContext : JsonSerializerContext;

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
