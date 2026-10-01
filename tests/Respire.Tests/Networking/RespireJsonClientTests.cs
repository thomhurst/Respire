using System.Text;
using System.Text.Json.Serialization;
using Respire;
using Respire.Commands;
using Respire.Extensions.Json;
using Respire.Protocol;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public partial class RespireJsonClientTests
{
    [Test]
    [Arguments(RespProtocol.Resp2)]
    [Arguments(RespProtocol.Resp3)]
    public async Task GetAsyncDeserializesJsonAndPrefixesGeneratedCommandKey(RespProtocol protocol)
    {
        var jsonReply = "$9\r\n{\"Age\":1}\r\n"u8.ToArray();
        await using var server = new FakeRespServer(1, FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => command switch
            {
                "HELLO 3" => "%1\r\n+proto\r\n:3\r\n"u8.ToArray(),
                "CLIENT MAINT_NOTIFICATIONS ON" => FakeRespServer.OkReply,
                "JSON.GET tenant:profile ." => jsonReply,
                _ => null,
            },
        };
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = protocol,
            MaintenanceNotifications = protocol == RespProtocol.Resp3
                ? RespireMaintenanceNotificationMode.Enabled
                : RespireMaintenanceNotificationMode.Disabled,
            Endpoints = { new RespireEndpoint("127.0.0.1", server.Port) },
            ThreadPoolMonitoring = false,
        });
        var json = new RespireJsonClient(client.WithKeyPrefix("tenant:"));

        var result = await json.GetAsync("profile", JsonTestContext.Default.Profile);

        await Assert.That(result.Found).IsTrue();
        await Assert.That(result.Value!.Age).IsEqualTo(1);
        await Assert.That(server.ReceivedCommands).Contains("JSON.GET tenant:profile .");
    }

    [Test]
    public async Task CatalogCommandsPrefixEveryKnownJsonDocumentKey()
    {
        await using var server = new FakeRespServer(FakeRespServer.OkReply);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var prefixed = client.WithKeyPrefix("tenant:");
        (RespireCommand Command, RespireValue[] Arguments)[] commands =
        [
            (RespireCommands.Json.JSON_ARRLEN, ["profile", "."]),
            (RespireCommands.Json.JSON_MERGE, ["profile", ".", "{}"]),
            (RespireCommands.Json.JSON_NUMPOWBY, ["profile", ".", 2]),
            (RespireCommands.Json.JSON_MSET, ["first", ".", "{}", "second", ".", "{}"]),
            (RespireCommands.Json.JSON_DEBUG_MEMORY, ["profile", "."]),
        ];

        foreach (var (command, arguments) in commands)
        {
            using var result = await prefixed.ExecuteAsync(command, arguments);
        }

        await Assert.That(server.ReceivedCommands).Contains("JSON.ARRLEN tenant:profile .");
        await Assert.That(server.ReceivedCommands).Contains("JSON.MERGE tenant:profile . {}");
        await Assert.That(server.ReceivedCommands).Contains("JSON.NUMPOWBY tenant:profile . 2");
        await Assert.That(server.ReceivedCommands).Contains("JSON.MSET tenant:first . {} tenant:second . {}");
        await Assert.That(server.ReceivedCommands).Contains("JSON.DEBUG MEMORY tenant:profile .");
    }

    [Test]
    public async Task PrefixedDebugCatalogCommandsPrefixFieldsAndKeepHelpKeyless()
    {
        await using var server = new FakeRespServer(FakeRespServer.OkReply);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var prefixed = client.WithKeyPrefix("tenant:");

        using (await prefixed.ExecuteAsync(RespireCommands.Dragonfly.JSON_DEBUG_FIELDS, ["profile", "."])) { }
        using (await prefixed.ExecuteAsync(RespireCommands.Dragonfly.JSON_DEBUG_HELP, [])) { }

        await Assert.That(server.ReceivedCommands).Contains("JSON.DEBUG FIELDS tenant:profile .");
        await Assert.That(server.ReceivedCommands).Contains("JSON.DEBUG HELP");
    }

    [Test]
    public async Task PrefixedJsonCommandsRejectNullKeysAndReportLayoutErrorsThroughTheTask()
    {
        await using var server = new FakeRespServer(FakeRespServer.OkReply);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var prefixed = client.WithKeyPrefix("tenant:");

        // A null key must not become the bare prefix.
        await Assert.That(async () => await prefixed.ExecuteAsync(RespireCommands.Json.JSON_GET, [RespireValue.Null, "."]))
            .Throws<ArgumentNullException>();

        // Malformed layouts fault the returned task instead of throwing synchronously.
        ValueTask<RespireResult> malformed = default;
        await Assert.That(() => { malformed = prefixed.ExecuteAsync(RespireCommands.Json.JSON_MGET, ["."]); }).ThrowsNothing();
        await Assert.That(async () => { using var result = await malformed; }).Throws<Exception>();
        await Assert.That(server.ReceivedCommands.Any(command => command.StartsWith("JSON.", StringComparison.Ordinal))).IsFalse();
    }

    [Test]
    public async Task MultiSetRejectsCrossSlotKeysBeforeConnecting()
    {
        await using var client = RespireClient.Create(new RespireOptions
        {
            UseCluster = true,
            Endpoints = { new RespireEndpoint("127.0.0.1", 1) },
        });
        var json = new RespireJsonClient(client);
        RespireJsonSetEntry<Profile>[] entries =
        [
            new("{left}:profile", new(1)),
            new("{right}:profile", new(2)),
        ];

        var error = await Assert.That(async () => await json.MultiSetAsync(entries, JsonTestContext.Default.Profile))
            .Throws<RespireServerException>();
        await Assert.That(error!.Message).Contains("CROSSSLOT");
    }

    [Test]
    public async Task MultiSetWritesFlattenedKeyPathValueTriples()
    {
        await using var server = new FakeRespServer(FakeRespServer.OkReply);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var json = new RespireJsonClient(client);
        RespireJsonSetEntry<Profile>[] entries =
        [
            new("{same}:one", new(1), "$.field"),
            new("{same}:two", new(2), "$.field"),
        ];

        await json.MultiSetAsync(entries, JsonTestContext.Default.Profile);

        await Assert.That(server.ReceivedCommands)
            .Contains("JSON.MSET {same}:one $.field {\"Age\":1} {same}:two $.field {\"Age\":2}");
    }

    [Test]
    public async Task JsonPathRepliesDeserializeEveryMatchAndLegacyNullIsFound()
    {
        await using var server = new FakeRespServer(FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => command switch
            {
                "JSON.GET many $..Age" => Bulk("[{\"Age\":1},{\"Age\":2}]"),
                "JSON.GET single $" => Bulk("[{\"Age\":3}]"),
                "JSON.GET none $.missing" => Bulk("[]"),
                "JSON.GET stored-null ." => Bulk("null"),
                "JSON.GET missing ." => "$-1\r\n"u8.ToArray(),
                _ => null,
            },
        };
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var json = new RespireJsonClient(client);

        var many = await json.GetManyAsync("many", JsonTestContext.Default.Profile, "$..Age");
        await Assert.That(many.Select(value => value.Value!.Age).ToArray()).IsEquivalentTo(new[] { 1, 2 });
        await Assert.That(async () => await json.GetAsync("many", JsonTestContext.Default.Profile, "$..Age"))
            .Throws<InvalidOperationException>();
        var single = await json.GetAsync("single", JsonTestContext.Default.Profile, RespireJsonPath.JsonPathRoot);
        await Assert.That(single.Value!.Age).IsEqualTo(3);
        var none = await json.GetAsync("none", JsonTestContext.Default.Profile, "$.missing");
        await Assert.That(none.Found).IsFalse();
        var storedNull = await json.GetAsync("stored-null", JsonTestContext.Default.Profile);
        await Assert.That(storedNull.Found).IsTrue();
        await Assert.That(storedNull.Value).IsNull();
        var missing = await json.GetAsync("missing", JsonTestContext.Default.Profile);
        await Assert.That(missing.Found).IsFalse();
    }

    [Test]
    public async Task MultiGetReturnsNullForMissingKeys()
    {
        await using var server = new FakeRespServer(FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => command == "JSON.MGET {t}:a {t}:b $"
                ? Encoding.ASCII.GetBytes("*2\r\n$11\r\n[{\"Age\":7}]\r\n$-1\r\n")
                : null,
        };
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var json = new RespireJsonClient(client);

        var values = await json.MultiGetAsync(
            new RespireKey[] { "{t}:a", "{t}:b" }, JsonTestContext.Default.Profile, RespireJsonPath.JsonPathRoot);

        await Assert.That(values.Length).IsEqualTo(2);
        await Assert.That(values[0]![0].Value!.Age).IsEqualTo(7);
        await Assert.That(values[1]).IsNull();
    }

    [Test]
    public async Task SetSendsConditionTokensAndReportsRejectedWrites()
    {
        await using var server = new FakeRespServer(FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => command.EndsWith(" NX", StringComparison.Ordinal) ? "$-1\r\n"u8.ToArray() : null,
        };
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var json = new RespireJsonClient(client.WithKeyPrefix("tenant:"));

        await Assert.That(await json.SetAsync("profile", new Profile(5), JsonTestContext.Default.Profile)).IsTrue();
        await Assert.That(await json.SetAsync("profile", new Profile(6), JsonTestContext.Default.Profile,
            condition: RespireJsonSetCondition.Nx)).IsFalse();
        await Assert.That(await json.SetJsonAsync("profile", new ReadOnlyMemory<byte>("{\"Age\":7}"u8.ToArray()),
            condition: RespireJsonSetCondition.Xx)).IsTrue();
        await Assert.That(async () => await json.SetJsonAsync("profile", "{}", condition: (RespireJsonSetCondition)42))
            .Throws<ArgumentOutOfRangeException>();

        await Assert.That(server.ReceivedCommands).Contains("JSON.SET tenant:profile . {\"Age\":5}");
        await Assert.That(server.ReceivedCommands).Contains("JSON.SET tenant:profile . {\"Age\":6} NX");
        await Assert.That(server.ReceivedCommands).Contains("JSON.SET tenant:profile . {\"Age\":7} XX");
    }

    [Test]
    public async Task SetFailuresAreReportedThroughTheReturnedTask()
    {
        await using var server = new FakeRespServer(FakeRespServer.OkReply);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var json = new RespireJsonClient(client);

        ValueTask<bool> badCondition = default;
        ValueTask<bool> missingMetadata = default;
        ValueTask<bool> blankJson = default;
        ValueTask<bool> emptyUtf8 = default;
        await Assert.That(() =>
        {
            badCondition = json.SetAsync("profile", new Profile(1), JsonTestContext.Default.Profile,
                condition: (RespireJsonSetCondition)42);
            missingMetadata = json.SetAsync("profile", new Profile(1), null!);
            blankJson = json.SetJsonAsync("profile", " ");
            emptyUtf8 = json.SetJsonAsync("profile", ReadOnlyMemory<byte>.Empty);
        }).ThrowsNothing();

        await Assert.That(async () => await badCondition).Throws<ArgumentOutOfRangeException>();
        await Assert.That(async () => await missingMetadata).Throws<ArgumentNullException>();
        await Assert.That(async () => await blankJson).Throws<ArgumentException>();
        await Assert.That(async () => await emptyUtf8).Throws<ArgumentException>();
        await Assert.That(server.ReceivedCommands.Any(command => command.StartsWith("JSON.", StringComparison.Ordinal))).IsFalse();
    }

    [Test]
    public async Task JsonPathRepliesHonorTheMetadataMaxDepthAndCollectManyMatches()
    {
        const int depth = 100;
        var nested = "null";
        for (var level = 0; level < depth; level++) nested = "{\"Next\":" + nested + "}";
        var matches = string.Join(",", Enumerable.Range(1, 10).Select(age => "{\"Age\":" + age + "}"));
        await using var server = new FakeRespServer(FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => command switch
            {
                "JSON.GET deep $" => Bulk("[" + nested + "]"),
                "JSON.GET deep ." => Bulk(nested),
                "JSON.GET many $..Age" => Bulk("[" + matches + "]"),
                _ => null,
            },
        };
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var json = new RespireJsonClient(client);

        // The JSONPath wrapper array must not push a value that fits the caller's MaxDepth over the limit.
        var viaJsonPath = await json.GetAsync("deep", DeepJsonTestContext.Default.Node, RespireJsonPath.JsonPathRoot);
        var viaLegacyPath = await json.GetAsync("deep", DeepJsonTestContext.Default.Node);
        await Assert.That(Depth(viaJsonPath.Value)).IsEqualTo(depth);
        await Assert.That(Depth(viaLegacyPath.Value)).IsEqualTo(depth);

        // The default limit of 64 still applies when the metadata does not raise it.
        await Assert.That(async () => await json.GetAsync("deep", JsonTestContext.Default.Node, RespireJsonPath.JsonPathRoot))
            .Throws<System.Text.Json.JsonException>();

        var many = await json.GetManyAsync("many", JsonTestContext.Default.Profile, "$..Age");
        await Assert.That(many.Select(value => value.Value!.Age).ToArray()).IsEquivalentTo(Enumerable.Range(1, 10).ToArray());

        static int Depth(Node? node)
        {
            var count = 0;
            for (; node is not null; node = node.Next) count++;
            return count;
        }
    }

    [Test]
    public async Task GetMemoryUsageRoutesByDocumentKeyAndReadsBothReplyShapes()
    {
        await using var server = new FakeRespServer(FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => command switch
            {
                "JSON.DEBUG MEMORY tenant:profile ." => ":42\r\n"u8.ToArray(),
                "JSON.DEBUG MEMORY tenant:profile $..Age" => "*2\r\n:8\r\n:9\r\n"u8.ToArray(),
                _ => null,
            },
        };
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var json = new RespireJsonClient(client.WithKeyPrefix("tenant:"));

        await Assert.That(await json.GetMemoryUsageAsync("profile")).IsEquivalentTo(new[] { 42L });
        await Assert.That(await json.GetMemoryUsageAsync("profile", "$..Age")).IsEquivalentTo(new[] { 8L, 9L });
    }

    [Test]
    public async Task PathEqualityUsesTheValueSentToRedis()
    {
        await Assert.That(default(RespireJsonPath)).IsEqualTo(RespireJsonPath.Root);
        await Assert.That(default(RespireJsonPath).GetHashCode()).IsEqualTo(RespireJsonPath.Root.GetHashCode());
        await Assert.That(default(RespireJsonPath) == RespireJsonPath.Root).IsTrue();
        await Assert.That(RespireJsonPath.Root != RespireJsonPath.JsonPathRoot).IsTrue();
        await Assert.That(RespireJsonPath.From("$.a")).IsEqualTo((RespireJsonPath)"$.a");
        await Assert.That(default(RespireJsonPath).UsesJsonPath).IsFalse();
        await Assert.That(RespireJsonPath.Root.UsesJsonPath).IsFalse();
        await Assert.That(RespireJsonPath.JsonPathRoot.UsesJsonPath).IsTrue();
        await Assert.That(((RespireJsonPath)"$..name").UsesJsonPath).IsTrue();
        await Assert.That(((RespireJsonPath)".name").UsesJsonPath).IsFalse();
        await Assert.That(() => (RespireJsonPath)" ").Throws<ArgumentException>();
        await Assert.That(() => RespireJsonPath.From(null!)).Throws<ArgumentNullException>();
    }

    [Test]
    public async Task GetOptionsRejectMissingPathsWhenAssigned()
    {
        await Assert.That(() => new RespireJsonGetOptions { Paths = [] }).Throws<ArgumentException>();
        await Assert.That(() => new RespireJsonGetOptions { Paths = null! }).Throws<ArgumentNullException>();
    }

    /// <summary>
    /// RedisJSON command names live in the generated interfaces, the key-layout table, and the client-side
    /// cache classification. This fails when a command is added to one and not the others.
    /// </summary>
    [Test]
    public async Task EveryJsonCommandHasAKeyLayoutAndACacheClassification()
    {
        var operations = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var type in new[] { typeof(RespireCommands.Json), typeof(RespireCommands.Dragonfly) })
        {
            foreach (var field in type.GetFields())
            {
                if (field.GetValue(null) is RespireCommand command && command.Name.StartsWith("JSON.", StringComparison.Ordinal))
                    operations.Add(command.Name);
            }
        }
        var modifierCommands = typeof(RespireJsonClient).Assembly.GetType("Respire.Extensions.Json.IRespireJsonModifierCommands")!;
        foreach (var type in new[] { typeof(IRespireJsonCommands), modifierCommands })
        {
            foreach (var method in type.GetMethods())
            {
                foreach (var attribute in method.GetCustomAttributes(typeof(RespireCommandAttribute), false))
                    operations.Add(((RespireCommandAttribute)attribute).Name);
            }
        }

        // JSON.MSET writes several keys and deliberately takes the conservative full-cache flush.
        string[] flushesWholeCache = ["JSON.MSET"];
        var missingLayout = operations.Where(operation => !RawCommandKeyLayouts.HasLayout(operation)).ToArray();
        var unclassified = operations.Where(operation =>
            !ClientSideCacheCoordinator.IsReadOnly(operation)
            && !ClientSideCacheCoordinator.IsSingleKeyMutation(operation)
            && !flushesWholeCache.Contains(operation)).ToArray();

        await Assert.That(operations.Count).IsGreaterThan(20);
        await Assert.That(missingLayout).IsEmpty();
        await Assert.That(unclassified).IsEmpty();
    }

    private static byte[] Bulk(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        return Encoding.UTF8.GetBytes("$" + bytes.Length + "\r\n" + value + "\r\n");
    }

    private sealed record Profile(int Age);

    private sealed class Node
    {
        public Node? Next { get; set; }
    }

    [JsonSerializable(typeof(Profile))]
    [JsonSerializable(typeof(Node))]
    private sealed partial class JsonTestContext : JsonSerializerContext;

    [JsonSourceGenerationOptions(MaxDepth = 128)]
    [JsonSerializable(typeof(Node))]
    private sealed partial class DeepJsonTestContext : JsonSerializerContext;
}
