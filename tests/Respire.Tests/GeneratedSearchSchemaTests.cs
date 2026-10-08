using System.Text;
using Respire.Json;
using Respire.Search;
using Respire.Tests.Models;
using Respire.Tests.Networking;
using TUnit.Assertions;
using TUnit.Core;

namespace Respire.Tests;

public class GeneratedSearchSchemaTests
{
    [Test]
    public async Task HashVectorsRoundTripWithoutUtf8ConversionAndRejectWrongDimensions()
    {
        var vector = new byte[] { 0, 255, 128, 1, 0, 0, 192, 127 };
        var value = new SearchHashModel("42", "Hello", "tools", 2, vector);
        var fields = SearchHashModelHashMapper.ToFields(value);
        vector[0] = 5;
        await Assert.That(fields["Embedding"][0]).IsEqualTo((byte)0);
        var copy = SearchHashModelHashMapper.FromFields(fields);
        await Assert.That(copy.Embedding).IsEquivalentTo(fields["Embedding"]);
        fields["Embedding"][0] = 7;
        await Assert.That(copy.Embedding[0]).IsEqualTo((byte)0);
        await Assert.That(() => SearchHashModelHashMapper.ToFields(value with { Embedding = [1] })).Throws<ArgumentException>();
        await Assert.That(() => SearchHashModelHashMapper.FromFields(new Dictionary<string, byte[]>(fields) { ["Embedding"] = [1] })).Throws<ArgumentException>();
    }

    [Test]
    public async Task JsonVectorsRoundTripAndValidateNumbersDimensionsAndNulls()
    {
        var value = new SearchJsonModel("42", "Hello", "tools", 2, [1, 2]);
        var json = SearchJsonModelJsonMapper.ToJson(value);
        var copy = SearchJsonModelJsonMapper.FromJson(json)!;
        await Assert.That(copy.Embedding!).IsEquivalentTo(value.Embedding!);
        await Assert.That(copy.Title).IsEqualTo(value.Title);
        await Assert.That(SearchJsonModelJsonMapper.FromJson(SearchJsonModelJsonMapper.ToJson(value with { Embedding = null }))!.Embedding).IsNull();
        await Assert.That(() => SearchJsonModelJsonMapper.ToJson(value with { Embedding = [1] })).Throws<ArgumentException>();
        await Assert.That(() => SearchJsonModelJsonMapper.ToJson(value with { Embedding = [float.NaN, 2] })).Throws<ArgumentException>();
        await Assert.That(() => SearchJsonModelJsonMapper.FromJson("{\"Id\":\"42\",\"document.title\":\"Hello\",\"Category\":\"tools\",\"Rating\":2,\"vector\":[1]}"u8)).Throws<ArgumentException>();
        var doubles = new SearchDoubleModel("42", [1.25, double.MaxValue]);
        await Assert.That(SearchDoubleModelJsonMapper.FromJson(SearchDoubleModelJsonMapper.ToJson(doubles))!.Embedding).IsEquivalentTo(doubles.Embedding);
    }

    [Test]
    public async Task ConnectorCanConsumeGeneratedMetadataThroughPublicSeam()
    {
        var definition = Schema<SearchHashModel, SearchHashModelSearchSchema>();
        await Assert.That(definition.Source).IsEqualTo(RespireSearchSource.Hash);
        await Assert.That(definition.Prefixes.Single()).IsEqualTo("generated-hash:");
        await Assert.That(SearchJsonModelSearchSchema.Fields.Title.Identifier).IsEqualTo("$[\"document.title\"]");
        await Assert.That(SearchJsonModelSearchSchema.Fields.Embedding.Identifier).IsEqualTo("$[\"vector\"]");
        await Assert.That(SearchHashModelSearchSchema.Fields.Embedding.Vector!.Dimensions).IsEqualTo(2);
    }

    [Test]
    [Arguments(2, false)]
    [Arguments(3, false)]
    [Arguments(2, true)]
    [Arguments(3, true)]
    public async Task GeneratedDefinitionsProduceExactCreateWireTokens(int protocol, bool json)
    {
        await using var server = Server();
        await using var client = await RespireClient.ConnectAsync(Options(server, protocol));
        var definition = json ? SearchJsonModelSearchSchema.Definition : SearchHashModelSearchSchema.Definition;
        await client.Search.CreateIndexAsync("models", definition);
        var tokens = server.ReceivedArguments.Last().Select(Encoding.UTF8.GetString).ToArray();
        var expected = json
            ? "FT.CREATE models ON JSON PREFIX 1 generated-json: SCHEMA $[\"document.title\"] AS title TEXT WEIGHT 1 NOSTEM $[\"Category\"] AS category TAG CASESENSITIVE $[\"Rating\"] AS rating NUMERIC SORTABLE $[\"vector\"] AS embedding VECTOR FLAT 6 TYPE FLOAT32 DIM 2 DISTANCE_METRIC COSINE"
            : "FT.CREATE models ON HASH PREFIX 1 generated-hash: SCHEMA Title AS title TEXT WEIGHT 1 NOSTEM Category AS category TAG SEPARATOR | Rating AS rating NUMERIC SORTABLE Embedding AS embedding VECTOR FLAT 6 TYPE FLOAT32 DIM 2 DISTANCE_METRIC COSINE";
        await Assert.That(string.Join(" ", tokens)).IsEqualTo(expected);
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task GeneratedVectorWritesSendBinaryHashAndNumericJson(int protocol)
    {
        await using var server = Server();
        await using var client = await RespireClient.ConnectAsync(Options(server, protocol));
        var vector = new byte[] { 255, 128, 0, 1, 2, 3, 4, 5 };
        await SearchHashModelHashMapper.SetAsync(client, new("42", "Hello", "tools", 2, vector));
        await Assert.That(server.ReceivedArguments.Last()[^1]).IsEquivalentTo(vector);
        await SearchJsonModelJsonMapper.SetAsync(new RespireJsonClient(client), new("42", "Hello", "tools", 2, [1, 2]));
        await Assert.That(Encoding.UTF8.GetString(server.ReceivedArguments.Last()[^1])).Contains("\"vector\":[1,2]");
    }

    private static RespireSearchIndexDefinition Schema<TModel, TSchema>() where TSchema : IRespireSearchSchema<TModel> => TSchema.Definition;

    [Test]
    public async Task BinaryTrackerOwnsBaselineAndWritesOnlyMutatedVectorBytes()
    {
        await using var server = Server();
        server.ReplyOverride = (_, command) => command.StartsWith("HSET ", StringComparison.Ordinal) ? ":1\r\n"u8.ToArray() : null;
        await using var client = await RespireClient.ConnectAsync(Options(server, 2));
        var value = new SearchHashModel("42", "Hello", "tools", 2, new byte[8]);
        var tracker = SearchHashModelHashMapper.Track(client, value);
        await tracker.UpdateAsync(value);
        await Assert.That(server.ReceivedCommands).IsEmpty();
        value.Embedding[0] = 255;
        await tracker.UpdateAsync(value);
        await tracker.UpdateAsync(value);
        await Assert.That(server.ReceivedArguments.Count).IsEqualTo(1);
        await Assert.That(Encoding.UTF8.GetString(server.ReceivedArguments[0][2])).IsEqualTo("Embedding");
        await Assert.That(server.ReceivedArguments[0][3]).IsEquivalentTo(value.Embedding);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task BinaryExpiryAndTrackerSendOwnedHSetExPayload(bool tracked)
    {
        await using var server = new FakeRespServer(3, ":1\r\n"u8.ToArray())
        {
            ReplyOverride = (_, command) => command == "COMMAND INFO HSETEX"
                ? "*1\r\n*6\r\n$6\r\nhsetex\r\n:-6\r\n*0\r\n:1\r\n:1\r\n:1\r\n"u8.ToArray() : null,
        };
        await using var client = await RespireClient.ConnectAsync(Options(server, 2));
        var vector = new byte[] { 255, 128, 0, 1, 2, 3, 4, 5 };
        var value = new SearchExpiringHashModel("42", vector);
        if (tracked)
        {
            var tracker = SearchExpiringHashModelHashMapper.Track(client, "expiring");
            await tracker.UpdateAsync(value);
            await tracker.UpdateAsync(value);
        }
        else await SearchExpiringHashModelHashMapper.SetAsync(client, "expiring", value);
        var write = server.ReceivedArguments.Single(arguments => Encoding.UTF8.GetString(arguments[0]) == "HSETEX");
        await Assert.That(write[^1]).IsEquivalentTo(vector);
        await Assert.That(Encoding.UTF8.GetString(write[3])).IsEqualTo("60000");
    }

    [Test]
    public async Task BinaryCancellationSettlesOwnedSnapshotBeforeAdvancingBaseline()
    {
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = Server();
        server.ReplyOverride = (_, command) => command.StartsWith("HSET ", StringComparison.Ordinal) ? ":1\r\n"u8.ToArray() : null;
        server.SuppressReply = _ => { received.TrySetResult(); return true; };
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var value = new SearchHashModel("42", "Hello", "tools", 2, new byte[8]);
        var tracker = SearchHashModelHashMapper.Track(client, value);
        value.Embedding[0] = 1;
        using var cancellation = new CancellationTokenSource();
        var pending = tracker.UpdateAsync(value, cancellation.Token).AsTask();
        await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
        value.Embedding[0] = 2;
        cancellation.Cancel();
        await Assert.That(pending.IsCompleted).IsFalse();
        await Assert.That(async () => await tracker.UpdateAsync(value)).Throws<InvalidOperationException>();
        server.SuppressReply = null;
        await server.SendRawAsync(":1\r\n"u8.ToArray(), server.ReceivedConnectionIds.Single());
        await Assert.That(async () => await pending).Throws<OperationCanceledException>();
        await tracker.UpdateAsync(value);
        await tracker.UpdateAsync(value);
        await Assert.That(server.ReceivedArguments.Count).IsEqualTo(2);
        await Assert.That(server.ReceivedArguments[0][3][0]).IsEqualTo((byte)1);
        await Assert.That(server.ReceivedArguments[1][3][0]).IsEqualTo((byte)2);
    }

    [Test]
    public async Task BinaryGroupedFailureRetriesOwnedVectorAfterCallerMutation()
    {
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new FakeRespServer(3, ":1\r\n"u8.ToArray())
        {
            ReplyOverride = (_, command) => command == "COMMAND INFO HSETEX"
                ? "*1\r\n*6\r\n$6\r\nhsetex\r\n:-6\r\n*0\r\n:1\r\n:1\r\n:1\r\n"u8.ToArray() : null,
            SuppressReply = command =>
            {
                if (!command.StartsWith("HSET ", StringComparison.Ordinal)) return false;
                received.TrySetResult();
                return true;
            },
        };
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var value = new SearchExpiringHashModel("42", new byte[8]);
        value.Embedding[0] = 1;
        var tracker = SearchExpiringHashModelHashMapper.Track(client, "fixed");
        var pending = tracker.UpdateAsync(value).AsTask();
        await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
        value.Embedding[0] = 2;
        server.SuppressReply = null;
        await server.SendRawAsync("-ERR ordinary group failed\r\n"u8.ToArray(), server.ReceivedConnectionIds.Last());
        await Assert.That(async () => await pending).Throws<RespireServerException>();
        await tracker.UpdateAsync(value);
        await tracker.UpdateAsync(value);
        var vectors = server.ReceivedArguments.Where(arguments => Encoding.UTF8.GetString(arguments[0]) == "HSETEX").ToArray();
        await Assert.That(vectors.Length).IsEqualTo(2);
        await Assert.That(vectors[0][^1][0]).IsEqualTo((byte)1);
        await Assert.That(vectors[1][^1][0]).IsEqualTo((byte)2);
        await Assert.That(server.ReceivedCommands.Count(command => command.StartsWith("HSET ", StringComparison.Ordinal))).IsEqualTo(2);
    }

    [Test]
    public async Task BinaryWriteTimeoutKeepsTrackerInvalidAfterLateReply()
    {
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = Server();
        server.SuppressReply = _ => { received.TrySetResult(); return true; };
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, Connections = 1,
            Endpoints = [new("127.0.0.1", server.Port)],
            CommandTimeout = TimeSpan.FromMilliseconds(200), ThreadPoolMonitoring = false,
        });
        var value = new SearchHashModel("42", "Hello", "tools", 2, new byte[8]);
        var tracker = SearchHashModelHashMapper.Track(client, value);
        value.Embedding[0] = 1;
        var pending = tracker.UpdateAsync(value).AsTask();
        await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
        value.Embedding[0] = 2;
        await Assert.That(async () => await pending).Throws<RespireTimeoutException>();
        await Assert.That(async () => await tracker.UpdateAsync(value)).Throws<InvalidOperationException>();
        server.SuppressReply = null;
        await server.SendRawAsync(":1\r\n"u8.ToArray(), server.ReceivedConnectionIds.Single());
        await Assert.That(async () => await tracker.UpdateAsync(value)).Throws<InvalidOperationException>();
        await Assert.That(server.ReceivedArguments.Count).IsEqualTo(1);
        await Assert.That(server.ReceivedArguments[0][3][0]).IsEqualTo((byte)1);
    }

    [Test]
    public async Task PartialHashVectorReadsValidateDimensionsAndOwnReplyBytes()
    {
        var vector = new byte[] { 255, 128, 0, 1, 2, 3, 4, 5 };
        await using var server = Server();
        server.ReplyOverride = (_, command) => command switch
        {
            "HMGET good Embedding" => [.. "*1\r\n$8\r\n"u8.ToArray(), .. vector, .. "\r\n"u8.ToArray()],
            "HMGET bad Embedding" => "*1\r\n$1\r\nx\r\n"u8.ToArray(),
            _ => null,
        };
        await using var client = await RespireClient.ConnectAsync(Options(server, 2));
        var read = await SearchHashModelHashMapper.GetPartialAsync(client, "good", ["Embedding"]);
        await Assert.That(read.Embedding.Value!).IsEquivalentTo(vector);
        await Assert.That(async () => await SearchHashModelHashMapper.GetPartialAsync(client, "bad", ["Embedding"])).Throws<ArgumentException>();
        await Assert.That(read.Embedding.Value!).IsEquivalentTo(vector);
    }

    [Test]
    [Arguments(RespireSearchVectorType.Float32, 4)]
    [Arguments(RespireSearchVectorType.Float64, 8)]
    [Arguments(RespireSearchVectorType.Float16, 2)]
    [Arguments(RespireSearchVectorType.BFloat16, 2)]
    [Arguments(RespireSearchVectorType.Int8, 1)]
    [Arguments(RespireSearchVectorType.UInt8, 1)]
    public async Task HashVectorSeamValidatesEveryElementSize(RespireSearchVectorType type, int elementSize)
    {
        var options = new RespireSearchVectorOptions(RespireSearchVectorAlgorithm.Flat, type, 2, RespireSearchDistanceMetric.Cosine);
        RespireSearchVectorValidation.ValidateHash(new byte[elementSize * 2], options);
        await Assert.That(() => RespireSearchVectorValidation.ValidateHash(new byte[elementSize], options)).Throws<ArgumentException>();
    }
    private static FakeRespServer Server() => new(FakeRespServer.OkReply)
    {
        ReplyOverride = (_, command) => command switch
        {
            "HELLO 3" => "%1\r\n$5\r\nproto\r\n:3\r\n"u8.ToArray(),
            _ when command.StartsWith("HSET ", StringComparison.Ordinal) => ":5\r\n"u8.ToArray(),
            _ => null,
        },
    };
    private static RespireOptions Options(FakeRespServer server, int protocol) => new()
    {
        Endpoints = { new("127.0.0.1", server.Port) }, Protocol = (RespProtocol)protocol, ThreadPoolMonitoring = false,
    };
}
