using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Respire.Json;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public partial class RespireJsonClientTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task PooledWritesPreserveMetadataWriterOptionsAndGrowth(bool indented)
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = indented,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            MaxDepth = 128,
#if NET9_0_OR_GREATER
            IndentCharacter = '\t',
            IndentSize = 1,
            NewLine = "\r\n",
#endif
        };
        var info = new JsonWriteContext(options).TextDocument;
        TextDocument[] values = [new("<one>"), new(new string('x', 16384)), new(new string('y', 98304)), new("last")];
        var expected = values.Select(value => JsonSerializer.SerializeToUtf8Bytes(value, info)).ToArray();
        await using var server = new FakeRespServer(FakeRespServer.OkReply);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        await client.Json.SetAsync("set", values[1], info);
        await client.Json.MergeAsync("merge", values[0], info);
        var entries = values.Select((value, index) => new RespireJsonSetEntry<TextDocument>($"{{same}}:{index}", value)).ToArray();
        await client.Json.MultiSetAsync(entries, info);
        var frames = server.ReceivedArguments.Where(frame => Encoding.UTF8.GetString(frame[0]).StartsWith("JSON.", StringComparison.Ordinal)).ToArray();
        await Assert.That(frames.Length).IsEqualTo(3);
        await Assert.That(frames[0][3]).IsEquivalentTo(expected[1]);
        await Assert.That(frames[1][3]).IsEquivalentTo(expected[0]);
        for (var index = 0; index < values.Length; index++)
            await Assert.That(frames[2][index * 3 + 3]).IsEquivalentTo(expected[index]);
    }

    [Test]
    public async Task MultiSetSerializationFailureSendsNothingAfterEarlierValues()
    {
        await using var server = new FakeRespServer(FakeRespServer.OkReply);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var cycle = new Node();
        cycle.Next = cycle;
        RespireJsonSetEntry<Node>[] entries = [new("{same}:one", new Node()), new("{same}:two", cycle)];
        await Assert.That(() => JsonSerializer.SerializeToUtf8Bytes(cycle, JsonTestContext.Default.Node))
            .Throws<InvalidOperationException>();
        await Assert.That(async () => await client.Json.MultiSetAsync(entries, JsonTestContext.Default.Node))
            .Throws<InvalidOperationException>();
        await Assert.That(server.ReceivedCommands.Any(command => command.StartsWith("JSON.MSET", StringComparison.Ordinal))).IsFalse();
        await client.Json.SetAsync("after-error", new Node(), JsonTestContext.Default.Node);
        await Assert.That(server.ReceivedCommands).Contains("JSON.SET after-error . {\"Next\":null}");
    }

    [Test]
    public async Task ConcurrentWritesAndChangedOptionsNeverShareActiveBuffers()
    {
        await using var server = new FakeRespServer(FakeRespServer.OkReply);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var json = new RespireJsonClient(client);
        var contexts = new[] { new JsonWriteContext(), new JsonWriteContext(new JsonSerializerOptions { WriteIndented = true }) };
        var values = Enumerable.Range(0, 16).Select(index => new TextDocument(new string((char)('a' + index), 4096))).ToArray();
        await Task.WhenAll(values.Select((value, index) => json.SetAsync($"key:{index}", value, contexts[index % 2].TextDocument).AsTask()));
        // Exercise reuse after concurrent returns, including an options mismatch.
        for (var index = 0; index < 2; index++)
            await json.SetAsync($"repeat:{index}", values[index], contexts[index].TextDocument);
        var frames = server.ReceivedArguments.Where(frame => Encoding.UTF8.GetString(frame[0]) == "JSON.SET").ToArray();
        await Assert.That(frames.Length).IsEqualTo(18);
        foreach (var frame in frames)
        {
            var key = Encoding.UTF8.GetString(frame[1]);
            var index = int.Parse(key[(key.IndexOf(':') + 1)..], System.Globalization.CultureInfo.InvariantCulture);
            await Assert.That(frame[3]).IsEquivalentTo(JsonSerializer.SerializeToUtf8Bytes(values[index], contexts[index % 2].TextDocument));
        }
    }

    private sealed record TextDocument(string Text);
    [JsonSerializable(typeof(TextDocument))]
    private sealed partial class JsonWriteContext : JsonSerializerContext;
}
