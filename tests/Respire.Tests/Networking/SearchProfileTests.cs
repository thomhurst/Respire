using System.Globalization;
using System.Text;
using Respire.Protocol;
using Respire.Search;
using TUnit.Assertions.Enums;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class SearchProfileTests
{
    private static readonly RespireSearchExpression All = RespireSearchExpression.FromRaw("*");

    [Test]
    [Arguments(2, false)]
    [Arguments(2, true)]
    [Arguments(3, false)]
    [Arguments(3, true)]
    public async Task ProfileKeepsNormalQueryEncodingAndResultSemantics(int protocol, bool limited)
    {
        foreach (var mode in new[] { "SEARCH", "AGGREGATE", "HYBRID" })
        {
            await using var server = Server(command => Frame(command.StartsWith("FT.PROFILE", StringComparison.Ordinal)
                ? Envelope(protocol, mode, Profile(protocol)) : QueryResult(protocol, mode), protocol));
            await using var client = await RespireClient.ConnectAsync(Options(server, protocol));
            var search = client.Search;
            var query = new RespireSearchQuery(RespireSearchExpression.FromRaw("@title:{$term}"), new()
            {
                WithScores = true, ReturnFields = ["title with spaces"], Dialect = 3,
                Parameters = new Dictionary<string, RespireValue> { ["term"] = new byte[] { 0, 255, 13, 10 } },
            });
            var aggregate = new RespireSearchAggregateOptions
            {
                Stages = [RespireSearchAggregateStage.Load("@title with spaces"), RespireSearchAggregateStage.Limit(2, 4)],
                Dialect = 3,
            };
            var hybrid = new RespireHybridSearchQuery(All, "embedding", new byte[] { 0, 255, 13, 10 }, 2)
            { LoadFields = ["title with spaces"], RrfWindow = 3, TimeoutMilliseconds = 100 };
            if (mode == "SEARCH") await search.SearchAsync("index ü", query);
            else if (mode == "AGGREGATE") await search.AggregateAsync("index ü", All, aggregate);
            else await search.HybridSearchAsync("index ü", hybrid);
            var normal = server.ReceivedArguments.Last();
            if (mode == "SEARCH")
            {
                var result = await search.ProfileSearchAsync("index ü", query, limited);
                await Assert.That(result.Result.Documents[0].Score).IsEqualTo(0.5);
                await Assert.That(result.Result.Documents[0].Fields["title"]).IsEqualTo("hello");
            }
            else if (mode == "AGGREGATE")
            {
                var result = await search.ProfileAggregateAsync("index ü", All, aggregate, limited);
                await Assert.That(result.Result.Rows[0]["title"]).IsEqualTo("hello");
            }
            else
            {
                var result = await search.ProfileHybridSearchAsync("index ü", hybrid, limited);
                await Assert.That(result.Result.Documents[0].Id).IsEqualTo("doc");
                await Assert.That(result.Result.Documents[0].Score).IsEqualTo(0.5);
            }
            var profiled = server.ReceivedArguments.Last();
            var prefix = limited ? 5 : 4;
            string[] expectedPrefix = limited ? ["FT.PROFILE", "index ü", mode, "LIMITED", "QUERY"] : ["FT.PROFILE", "index ü", mode, "QUERY"];
            await Assert.That(profiled.Take(prefix).Select(Encoding.UTF8.GetString).ToArray()).IsEquivalentTo(
                expectedPrefix,
                CollectionOrdering.Matching);
            await Assert.That(profiled.Length - prefix).IsEqualTo(normal.Length - 2);
            for (var i = 2; i < normal.Length; i++)
                await Assert.That(profiled[prefix + i - 2].AsSpan().SequenceEqual(normal[i])).IsTrue();
        }
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task ProfileOwnsNestedIteratorsMetricsAndUnknownFields(int protocol)
    {
        await using var server = Server(_ => Frame(Envelope(protocol, "SEARCH", Profile(protocol)), protocol));
        await using var client = await RespireClient.ConnectAsync(Options(server, protocol));
        var result = await client.Search.ProfileSearchAsync("idx", new(All, new() { WithScores = true }));
        var shard = result.Profile.Children[0];
        await Assert.That(shard.Metrics["Total profile time"]).IsEqualTo(1.25);
        var iterator = shard.Children.Single(node => node.Name == "Iterators profile");
        await Assert.That(iterator.Type).IsEqualTo("INTERSECT");
        await Assert.That(iterator.Children.Select(node => node.Type!).ToArray()).IsEquivalentTo(
            ["TEXT", "NUMERIC"], CollectionOrdering.Matching);
        await Assert.That(iterator.Children[0].TimeMilliseconds).IsEqualTo(0.25);
        await Assert.That(iterator.Children[1].Metrics["Number of reading operations"]).IsEqualTo(3d);
        await Assert.That(shard.Properties["Future bytes"].Bytes!.Value.Span.SequenceEqual(new byte[] { 0, 255, 13, 10 })).IsTrue();
        await Assert.That(shard.Properties["Future list"].Items.Select(item => item.Scalar!).ToArray())
            .IsEquivalentTo(["a", "b", "a", "c"], CollectionOrdering.Matching);
        await client.Search.ProfileSearchAsync("idx", new(All, new() { WithScores = true }));
        await Assert.That(iterator.Children[0].Properties["Term"].Scalar).IsEqualTo("hello");
        await Assert.That(shard.Properties["Future bytes"].Bytes!.Value.Span.SequenceEqual(new byte[] { 0, 255, 13, 10 })).IsTrue();
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task NumericMetricsIgnoreTheCurrentCulture(int protocol)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            await using var server = Server(_ => Frame(Envelope(protocol, "SEARCH", Profile(protocol)), protocol));
            await using var client = await RespireClient.ConnectAsync(Options(server, protocol));
            var result = await client.Search.ProfileSearchAsync("idx", new(All, new() { WithScores = true }));
            var shard = result.Profile.Children[0];
            await Assert.That(shard.Metrics["Total profile time"]).IsEqualTo(1.25);
            await Assert.That(shard.Metrics["Future numeric"]).IsEqualTo(2.75);
            var iterator = shard.Children.Single(node => node.Name == "Iterators profile");
            await Assert.That(iterator.Children[0].TimeMilliseconds).IsEqualTo(0.25);
            await Assert.That(iterator.Children[1].Metrics["Number of reading operations"]).IsEqualTo(3d);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task HybridKeepsTextAndVectorProfilesAndWarnings(int protocol)
    {
        var profile = Object("Shards", new object[] { Object("SEARCH", Object("Time", 1d), "VSIM", Object("Time", 2d)) },
            "Coordinator", Object("Result processors profile", new object[] { Object("Type", "Hybrid Merger", "Results processed", 1L) }));
        await using var server = Server(_ => Frame(Envelope(protocol, "HYBRID", profile), protocol));
        await using var client = await RespireClient.ConnectAsync(Options(server, protocol));
        var result = await client.Search.ProfileHybridSearchAsync("idx", Hybrid());
        await Assert.That(result.Result.Warnings).IsEquivalentTo(["slow"], CollectionOrdering.Matching);
        await Assert.That(result.Profile.Children[0].Children.Select(child => child.Name).ToArray())
            .IsEquivalentTo(["SEARCH", "VSIM"], CollectionOrdering.Matching);
        await Assert.That(result.Profile.Children[1].Children[0].Type).IsEqualTo("Hybrid Merger");
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task EmptyQueryResultsKeepProfilesForEveryForm(int protocol)
    {
        foreach (var mode in new[] { "SEARCH", "AGGREGATE", "HYBRID" })
        {
            var result = protocol == 3 || mode == "HYBRID"
                ? (object)Object("total_results", 0L, "results", Array.Empty<object>()) : new object[] { 0L };
            await using var server = Server(_ => Frame(Envelope(protocol, mode, Profile(protocol), result), protocol));
            await using var client = await RespireClient.ConnectAsync(Options(server, protocol));
            if (mode == "SEARCH") await Assert.That((await client.Search.ProfileSearchAsync("idx", new(All))).Result.Documents.Count).IsEqualTo(0);
            else if (mode == "AGGREGATE") await Assert.That((await client.Search.ProfileAggregateAsync("idx", All)).Result.Rows.Count).IsEqualTo(0);
            else await Assert.That((await client.Search.ProfileHybridSearchAsync("idx", Hybrid())).Result.Documents.Count).IsEqualTo(0);
        }
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task HybridAlsoAcceptsTheResultProfileEnvelope(int protocol)
    {
        var reply = protocol == 2 ? (object)new object[] { QueryResult(protocol, "HYBRID"), Profile(protocol) }
            : Object("Results", QueryResult(protocol, "HYBRID"), "Profile", Profile(protocol));
        await using var server = Server(_ => Frame(reply, protocol));
        await using var client = await RespireClient.ConnectAsync(Options(server, protocol));
        var result = await client.Search.ProfileHybridSearchAsync("idx", Hybrid());
        await Assert.That(result.Result.Total).IsEqualTo(1);
        await Assert.That(result.Result.Documents[0].Score).IsEqualTo(0.5);
        await Assert.That(result.Profile.Children.Count).IsEqualTo(2);
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task MalformedProfilesAndEnvelopesThrow(int protocol)
    {
        object?[] invalidProfiles =
        [
            null, "profile", new object[] { "Time" }, Object("Time", "nope"), Object("Time", null),
            Object("Time", "nan"), Object("Time", "inf"), Object("Type", "A", "Type", "B"),
            Object("", 1L), Object(2L, 1L), Object("Shards", "bad"), Object("Shards", new object[] { "bad" }),
            Object("Iterators profile", null), Object("Iterators profile", Object("Child iterators", "bad")),
            Object("Result processors profile", Object("Type", "Index")),
            Object("Type", 1L), Object("Type", null), Object("Type", ""),
        ];
        var invalid = invalidProfiles.Select(profile => Envelope(protocol, "SEARCH", profile)).ToList();
        invalid.AddRange(["bad", new object[] { QueryResult(protocol, "SEARCH") },
            Object("Results", QueryResult(protocol, "SEARCH")), Object("Profile", Profile(protocol)),
            Object("Results", QueryResult(protocol, "SEARCH"), "Profile", Profile(protocol), "Profile", Profile(protocol))]);
        foreach (var reply in invalid)
        {
            await using var server = Server(_ => Frame(reply, protocol));
            await using var client = await RespireClient.ConnectAsync(Options(server, protocol));
            await Assert.That(async () => await client.Search.ProfileSearchAsync("idx", new(All, new() { WithScores = true })))
                .Throws<InvalidOperationException>();
        }
    }

    [Test]
    [Arguments(2, "scalar")]
    [Arguments(3, "scalar")]
    [Arguments(2, "missing-profile")]
    [Arguments(3, "missing-profile")]
    [Arguments(2, "extra-tail")]
    [Arguments(3, "extra-tail")]
    public async Task UnknownHybridEnvelopeReportsObservedShape(int protocol, string kind)
    {
        object envelope = kind switch
        {
            "scalar" => "bad",
            "missing-profile" => Object("total_results", 0L, "results", Array.Empty<object>()),
            _ => new object[] { "total_results", 0L, Object(), Object() },
        };
        var type = (kind, protocol) switch
        {
            ("scalar", _) => RespDataType.BulkString,
            ("missing-profile", 3) => RespDataType.Map,
            _ => RespDataType.Array,
        };
        var count = kind == "scalar" ? 0 : 4;
        await using var server = Server(_ => Frame(envelope, protocol));
        await using var client = await RespireClient.ConnectAsync(Options(server, protocol));
        var error = await Assert.That(async () => await client.Search.ProfileHybridSearchAsync("idx", Hybrid()))
            .Throws<InvalidOperationException>();
        await Assert.That(error!.Message).Contains($"type {type}, element count {count}");
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task ValidationCancellationAndPrefixesSendNoProfileCommand(int protocol)
    {
        await using var server = Server(_ => Frame(Envelope(protocol, "SEARCH", Profile(protocol)), protocol));
        await using var client = await RespireClient.ConnectAsync(Options(server, protocol));
        await Assert.That(async () => await client.Search.ProfileSearchAsync(" ", new(All))).Throws<ArgumentException>();
        await Assert.That(async () => await client.Search.ProfileSearchAsync("idx", null!)).Throws<ArgumentNullException>();
        await Assert.That(async () => await client.Search.ProfileAggregateAsync("idx", default)).Throws<ArgumentException>();
        await Assert.That(async () => await client.Search.ProfileHybridSearchAsync("idx", Hybrid() with { K = 0 })).Throws<ArgumentOutOfRangeException>();
        await Assert.That(async () => await client.Search.ProfileSearchAsync("idx", new(All, new() { Dialect = 0 }))).Throws<ArgumentOutOfRangeException>();
        var prefixed = client.WithKeyPrefix("tenant:").Search;
        await Assert.That(async () => await prefixed.ProfileSearchAsync("idx", new(All))).Throws<NotSupportedException>();
        await Assert.That(async () => await prefixed.ProfileAggregateAsync("idx", All)).Throws<NotSupportedException>();
        await Assert.That(async () => await prefixed.ProfileHybridSearchAsync("idx", Hybrid())).Throws<NotSupportedException>();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.That(async () => await client.Search.ProfileSearchAsync("idx", new(All), cancellationToken: cancelled.Token)).Throws<OperationCanceledException>();
        await Assert.That(server.ReceivedCommands.Any(command => command.StartsWith("FT.PROFILE", StringComparison.Ordinal))).IsFalse();
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task ServerErrorsPassThroughWithoutCapabilityProbe(int protocol)
    {
        foreach (var mode in new[] { "SEARCH", "AGGREGATE", "HYBRID" })
        {
            await using var server = Server(_ => "-NOPERM denied\r\n"u8.ToArray());
            await using var client = await RespireClient.ConnectAsync(Options(server, protocol));
            var error = await Assert.That(async () => await RunAsync(client.Search, mode)).Throws<RespireServerException>();
            await Assert.That(error!.Code).IsEqualTo("NOPERM");
            await Assert.That(server.ReceivedCommands.Any(command => command.StartsWith("COMMAND", StringComparison.Ordinal))).IsFalse();
        }
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task AcceptedCancellationDrainsProfileReplyBeforeNextCommand(int protocol)
    {
        foreach (var mode in new[] { "SEARCH", "AGGREGATE", "HYBRID" })
        {
            await using var server = Server(_ => Frame(Envelope(protocol, mode, Profile(protocol)), protocol));
            server.DelayCommand("FT.PROFILE", 100);
            await using var client = await RespireClient.ConnectAsync(Options(server, protocol));
            using var cancelled = new CancellationTokenSource();
            var pending = RunAsync(client.Search, mode, cancelled.Token);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!server.ReceivedCommands.Any(command => command.StartsWith("FT.PROFILE", StringComparison.Ordinal)))
                await Task.Delay(1, deadline.Token);
            cancelled.Cancel();
            var error = await Assert.That(async () => await pending).Throws<OperationCanceledException>();
            await Assert.That(error!.CancellationToken).IsEqualTo(cancelled.Token);
            await RunAsync(client.Search, mode);
            await Assert.That(server.ReceivedCommands.Count(command => command.StartsWith("FT.PROFILE", StringComparison.Ordinal))).IsEqualTo(2);
        }
    }

    [Test]
    public async Task ProfileDescriptorIsReadOnlyAndRoutesByIndex()
    {
        await Assert.That(RespireCommands.Search.FT_PROFILE.CacheMutation).IsEqualTo(RespireCacheMutation.ReadOnly);
        var command = new Respire.Commands.CmdN(RespireCommand.Create("FT.PROFILE", RespireCacheMutation.ReadOnly).Verb,
            ["idx{slot}", "SEARCH", "QUERY", "*"]);
        await Assert.That(command.TryGetClusterSlot(out var slot)).IsTrue();
        await Assert.That(slot).IsEqualTo(new RespireKey("idx{slot}").ClusterSlot);
    }

    private static async Task RunAsync(RespireSearchClient client, string mode, CancellationToken token = default)
    {
        if (mode == "SEARCH") await client.ProfileSearchAsync("idx", new(All, new() { WithScores = true }), cancellationToken: token);
        else if (mode == "AGGREGATE") await client.ProfileAggregateAsync("idx", All, cancellationToken: token);
        else await client.ProfileHybridSearchAsync("idx", Hybrid(), cancellationToken: token);
    }

    private static RespireHybridSearchQuery Hybrid() => new(All, "embedding", new byte[] { 1, 2 }, 1);
    private sealed record Fields(object?[] Values);
    private static Fields Object(params object?[] values) => new(values);
    private static object Profile(int protocol) => Object("Shards", new object[]
    {
        Object("Total profile time", protocol == 2 ? "1.25" : 1.25,
            "Iterators profile", Object("Type", "INTERSECT", "Child iterators", new object[]
            {
                Object("Type", "TEXT", "Term", "hello", "Time", "0.25"),
                Object("Type", "NUMERIC", "Number of reading operations", 3L),
            }), "Future bytes", new byte[] { 0, 255, 13, 10 }, "Future numeric", "2.75", "Future list", new object[] { "a", "b", "a", "c" }),
    }, "Coordinator", Object());

    private static object QueryResult(int protocol, string mode) => mode switch
    {
        "HYBRID" => Object("total_results", 1L, "results", new object[] { Object("__key", "doc", "__score", "0.5") }, "warnings", new object[] { "slow" }),
        "SEARCH" when protocol == 2 => new object[] { 1L, "doc", "0.5", new object[] { "title", "hello" } },
        "AGGREGATE" when protocol == 2 => new object[] { 1L, new object[] { "title", "hello" } },
        _ => Object("total_results", 1L, "results", new object[] { Object("id", "doc", "score", 0.5, "extra_attributes", Object("title", "hello")) }),
    };

    private static object Envelope(int protocol, string mode, object? profile, object? result = null)
    {
        result ??= QueryResult(protocol, mode);
        if (mode != "HYBRID") return protocol == 2 ? new object?[] { result, profile } : Object("Results", result, "Profile", profile);
        var fields = ((Fields)result).Values;
        return protocol == 2 ? fields.Concat(new[] { profile }).ToArray() : Object([.. fields, "Profile", profile]);
    }

    private static byte[] Frame(object? value, int protocol)
    {
        using var stream = new MemoryStream();
        Write(value);
        return stream.ToArray();
        void Text(string text) => stream.Write(Encoding.UTF8.GetBytes(text));
        void Write(object? item)
        {
            switch (item)
            {
                case null: Text(protocol == 2 ? "$-1\r\n" : "_\r\n"); break;
                case Fields fields:
                    Text((protocol == 3 ? "%" + (fields.Values.Length / 2) : "*" + fields.Values.Length) + "\r\n");
                    foreach (var entry in fields.Values) Write(entry);
                    break;
                case object?[] array:
                    Text("*" + array.Length + "\r\n");
                    foreach (var entry in array) Write(entry);
                    break;
                case long number: Text(":" + number.ToString(CultureInfo.InvariantCulture) + "\r\n"); break;
                case double number when protocol == 3: Text("," + number.ToString(CultureInfo.InvariantCulture) + "\r\n"); break;
                default:
                    var bytes = item is byte[] binary ? binary : Encoding.UTF8.GetBytes(Convert.ToString(item, CultureInfo.InvariantCulture)!);
                    Text("$" + bytes.Length + "\r\n"); stream.Write(bytes); Text("\r\n"); break;
            }
        }
    }

    private static FakeRespServer Server(Func<string, byte[]> reply) => new(1, FakeRespServer.PongReply)
    {
        ReplyOverride = (_, command) => command == "HELLO 3" ? "%1\r\n$5\r\nproto\r\n:3\r\n"u8.ToArray()
            : command.StartsWith("FT.", StringComparison.Ordinal) ? reply(command) : null,
    };

    private static RespireOptions Options(FakeRespServer server, int protocol) => new()
    {
        Endpoints = { new("127.0.0.1", server.Port) }, Protocol = (RespProtocol)protocol, ThreadPoolMonitoring = false,
    };
}
