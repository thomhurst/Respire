using System.Text;
using Respire.Commands;
using Respire.Search;
using Respire.Protocol;
using TUnit.Assertions;
using TUnit.Assertions.Enums;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class SearchConfigurationTests
{
    private static readonly byte[] Hello = "%1\r\n$5\r\nproto\r\n:3\r\n"u8.ToArray();

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task ReadsWildcardAndPatternAsOwnedNullableValues(int protocol)
    {
        IReadOnlyDictionary<string, string?> owned;
        await using (var server = Server(ReadReply(protocol)))
        await using (var client = await RespireClient.ConnectAsync(Options(server, protocol)))
        {
            owned = await client.Search.GetConfigurationAsync();
            await Assert.That(LastArguments(server)).IsEquivalentTo(
                ["FT.CONFIG", "GET", "*"], CollectionOrdering.Matching);
            var filtered = await client.Search.GetConfigurationAsync("TIME*");
            await Assert.That(LastArguments(server)).IsEquivalentTo(
                ["FT.CONFIG", "GET", "TIME*"], CollectionOrdering.Matching);
            await Assert.That(filtered["TIMEOUT"]).IsEqualTo("500");
            await client.PingAsync();
        }

        await Assert.That(owned.Count).IsEqualTo(3);
        await Assert.That(owned["TIMEOUT"]).IsEqualTo("500");
        await Assert.That(owned["EXTLOAD"]).IsNull();
        await Assert.That(owned["EMPTY"]).IsEqualTo(string.Empty);
        await Assert.That(() => ((IDictionary<string, string?>)owned).Add("new", "value"))
            .Throws<NotSupportedException>();
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task WritesExactlyOneOptionAndValueToken(int protocol)
    {
        await using var server = Server(FakeRespServer.OkReply);
        await using var client = await RespireClient.ConnectAsync(Options(server, protocol));
        await client.Search.SetConfigurationAsync("option with spaces", "value with spaces\r\nand unicode é");
        await Assert.That(LastArguments(server)).IsEquivalentTo(
            ["FT.CONFIG", "SET", "option with spaces", "value with spaces\r\nand unicode é"], CollectionOrdering.Matching);
        await client.Search.SetConfigurationAsync("EMPTY", "");
        await Assert.That(LastArguments(server)[^1]).IsEqualTo(string.Empty);
    }

    public static IEnumerable<(int Protocol, string Reply)> MalformedReads()
    {
        string[] replies =
        [
            "+OK\r\n", ":1\r\n", "_\r\n",
            "*2\r\n$7\r\nTIMEOUT\r\n$3\r\n500\r\n",
            "*1\r\n*1\r\n$7\r\nTIMEOUT\r\n",
            "*1\r\n*3\r\n$7\r\nTIMEOUT\r\n$3\r\n500\r\n+extra\r\n",
            "*1\r\n*2\r\n:1\r\n$3\r\n500\r\n",
            "*1\r\n*2\r\n$-1\r\n$3\r\n500\r\n",
            "*1\r\n*2\r\n$0\r\n\r\n$3\r\n500\r\n",
            "*1\r\n*2\r\n$7\r\nTIMEOUT\r\n*0\r\n",
            "%1\r\n$7\r\nTIMEOUT\r\n:500\r\n",
            "%1\r\n$7\r\nTIMEOUT\r\n#t\r\n",
            "%2\r\n$7\r\nTIMEOUT\r\n$3\r\n500\r\n$7\r\nTIMEOUT\r\n$3\r\n600\r\n",
            "*2\r\n*2\r\n$7\r\nTIMEOUT\r\n$3\r\n500\r\n*2\r\n$7\r\nTIMEOUT\r\n$3\r\n600\r\n",
        ];
        foreach (var protocol in new[] { 2, 3 })
        foreach (var reply in replies) yield return (protocol, reply);
    }

    [Test]
    [MethodDataSource(nameof(MalformedReads))]
    public async Task RejectsMalformedConfigurationReads(int protocol, string reply)
    {
        await using var server = Server(Encoding.UTF8.GetBytes(reply));
        await using var client = await RespireClient.ConnectAsync(Options(server, protocol));
        var error = await Assert.That(async () => await client.Search.GetConfigurationAsync())
            .Throws<InvalidOperationException>();
        await Assert.That(error!.Message).StartsWith("Unexpected FT.CONFIG GET reply:");
    }

    [Test]
    [Arguments("+YES\r\n")]
    [Arguments("$2\r\nOK\r\n")]
    [Arguments("*0\r\n")]
    [Arguments(":1\r\n")]
    [Arguments("_\r\n")]
    public async Task RejectsConfigurationWritesWithoutSimpleStringOk(string reply)
    {
        foreach (var protocol in new[] { 2, 3 })
        {
            await using var server = Server(Encoding.UTF8.GetBytes(reply));
            await using var client = await RespireClient.ConnectAsync(Options(server, protocol));
            var error = await Assert.That(async () => await client.Search.SetConfigurationAsync("TIMEOUT", "501"))
                .Throws<InvalidOperationException>();
            await Assert.That(error!.Message).StartsWith("Unexpected FT.CONFIG SET reply:");
        }
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task EmptyConfigurationResultsAreValid(int protocol)
    {
        await using var server = Server(protocol == 2 ? "*0\r\n"u8.ToArray() : "%0\r\n"u8.ToArray());
        await using var client = await RespireClient.ConnectAsync(Options(server, protocol));
        var values = await client.Search.GetConfigurationAsync("DOES_NOT_EXIST");
        await Assert.That(values.Count).IsEqualTo(0);
    }

    [Test]
    [Arguments(2, false)]
    [Arguments(3, false)]
    [Arguments(2, true)]
    [Arguments(3, true)]
    public async Task PreservesServerErrorsWithoutCapabilityProbes(int protocol, bool write)
    {
        foreach (var message in new[] { "ERR unknown configuration option", "NOPERM configuration denied" })
        {
            await using var server = Server(Encoding.UTF8.GetBytes("-" + message + "\r\n"));
            await using var client = await RespireClient.ConnectAsync(Options(server, protocol));
            var error = await Assert.That(async () =>
            {
                if (write) await client.Search.SetConfigurationAsync("UNKNOWN", "value");
                else await client.Search.GetConfigurationAsync("UNKNOWN");
            }).Throws<RespireServerException>();
            await Assert.That(error!.Message).IsEqualTo(message);
            await Assert.That(server.ReceivedCommands.Any(command => command.StartsWith("COMMAND", StringComparison.Ordinal))).IsFalse();
        }
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task ValidatesArgumentsAndPreCanceledCallsBeforeSending(int protocol)
    {
        await using var server = Server(ReadReply(protocol));
        await using var client = await RespireClient.ConnectAsync(Options(server, protocol));
        foreach (var option in new[] { null, "", " " })
        {
            await Assert.That(async () => await client.Search.GetConfigurationAsync(option!)).Throws<ArgumentException>();
            await Assert.That(async () => await client.Search.SetConfigurationAsync(option!, "500")).Throws<ArgumentException>();
        }
        await Assert.That(async () => await client.Search.SetConfigurationAsync("TIMEOUT", null!)).Throws<ArgumentNullException>();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.That(async () => await client.Search.GetConfigurationAsync(cancellationToken: cancellation.Token))
            .Throws<OperationCanceledException>();
        await Assert.That(async () => await client.Search.SetConfigurationAsync("TIMEOUT", "500", cancellation.Token))
            .Throws<OperationCanceledException>();
        await Assert.That(server.ReceivedCommands.Any(command => command.StartsWith("FT.CONFIG", StringComparison.Ordinal))).IsFalse();
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task WritesRequireAdminBeforeSending(int protocol)
    {
        await using var server = Server(ReadReply(protocol));
        await using var disabled = await RespireClient.ConnectAsync(Options(server, protocol, allowAdmin: false));
        await disabled.Search.GetConfigurationAsync();
        await Assert.That(async () => await disabled.Search.SetConfigurationAsync("TIMEOUT", "500"))
            .Throws<NotSupportedException>();
        await Assert.That(server.ReceivedCommands.Any(command => command.StartsWith("FT.CONFIG SET", StringComparison.Ordinal))).IsFalse();
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task BothCommandsRejectPrefixViewsBeforeSending(int protocol)
    {
        await using var server = Server(ReadReply(protocol));
        await using var enabled = await RespireClient.ConnectAsync(Options(server, protocol));
        var prefix = enabled.WithKeyPrefix("tenant:").Search;
        await Assert.That(async () => await prefix.GetConfigurationAsync()).Throws<NotSupportedException>();
        await Assert.That(async () => await prefix.SetConfigurationAsync("TIMEOUT", "500")).Throws<NotSupportedException>();
        await Assert.That(server.ReceivedCommands.Any(command => command.StartsWith("FT.CONFIG", StringComparison.Ordinal))).IsFalse();
    }

    [Test]
    public async Task ConfigurationMetadataDoesNotTreatOptionsAsClusterKeys()
    {
        foreach (var descriptor in new[] { RespireCommands.Search.FT_CONFIG_GET, RespireCommands.Search.FT_CONFIG_SET })
        {
            var command = new CatalogCommand(descriptor, ["TIMEOUT", "501"]);
            await Assert.That(command.TryGetPrimaryKey(out _)).IsFalse();
            await Assert.That(command.TryGetClusterSlot(out _)).IsFalse();
        }
        foreach (var operation in new[] { "FT.CONFIG", "FT.CONFIG GET", "FT.CONFIG SET" })
            await Assert.That(DynamicCommandRouting.GetRoutingKeyIndex(operation, ["FT.CONFIG", "GET", "TIMEOUT"], 1)).IsEqualTo(-1);
        await Assert.That(RespireCommands.Search.FT_CONFIG_GET.CacheMutation).IsEqualTo(RespireCacheMutation.ReadOnly);
        await Assert.That(RespireCommands.Search.FT_CONFIG_SET.CacheMutation).IsEqualTo(RespireCacheMutation.Mutation);
    }

    [Test]
    public async Task ConfigurationCallsNeverFanOutOnCluster()
    {
        await using var first = Server(ReadReply(2));
        await using var second = Server(ReadReply(2));
        byte[] slots = Encoding.ASCII.GetBytes(
            $"*2\r\n*3\r\n:0\r\n:8191\r\n*2\r\n$9\r\n127.0.0.1\r\n:{first.Port}\r\n" +
            $"*3\r\n:8192\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{second.Port}\r\n");
        foreach (var server in new[] { first, second })
            server.ReplyOverride = (_, command) => command switch
            {
                "CLUSTER SLOTS" => slots,
                "FT.CONFIG SET TIMEOUT 501" => FakeRespServer.OkReply,
                "FT.CONFIG GET TIMEOUT" => ReadReply(2),
                _ => null,
            };
        await using var client = await RespireClient.ConnectAsync(Options(first, 2) with { UseCluster = true });
        await client.Search.GetConfigurationAsync("TIMEOUT");
        await client.Search.SetConfigurationAsync("TIMEOUT", "501");
        var commands = first.ReceivedCommands.Concat(second.ReceivedCommands).ToArray();
        await Assert.That(commands.Count(command => command == "FT.CONFIG GET TIMEOUT")).IsEqualTo(1);
        await Assert.That(commands.Count(command => command == "FT.CONFIG SET TIMEOUT 501")).IsEqualTo(1);
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task ReadsPreserveCacheAndWritesInvalidateIt(int protocol)
    {
        await using var server = Server(ReadReply(protocol));
        server.ReplyOverride = (_, command) => command switch
        {
            "HELLO 3" => Hello,
            "FT.CONFIG SET TIMEOUT 501" => FakeRespServer.OkReply,
            _ when command.StartsWith("FT.CONFIG GET", StringComparison.Ordinal) => ReadReply(protocol),
            "GET cache-key" => "$5\r\nalive\r\n"u8.ToArray(),
            _ => null,
        };
        await using var client = await RespireClient.ConnectAsync(Options(server, protocol) with { ClientSideCache = new() });
        await client.GetStringAsync("cache-key");
        await client.Search.GetConfigurationAsync();
        using (await client.ExecuteAsync("FT.CONFIG GET", ["TIMEOUT"])) { }
        await client.GetStringAsync("cache-key");
        await Assert.That(server.ReceivedCommands.Count(command => command == "GET cache-key")).IsEqualTo(1);
        await client.Search.SetConfigurationAsync("TIMEOUT", "501");
        await client.GetStringAsync("cache-key");
        await Assert.That(server.ReceivedCommands.Count(command => command == "GET cache-key")).IsEqualTo(2);
    }

    [Test]
    [Arguments(2, false)]
    [Arguments(3, false)]
    [Arguments(2, true)]
    [Arguments(3, true)]
    public async Task InFlightCancellationDrainsTheAcceptedReply(int protocol, bool write)
    {
        var accepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = Server(ReadReply(protocol));
        server.SuppressReply = command =>
        {
            if (!command.StartsWith("FT.CONFIG", StringComparison.Ordinal)) return false;
            accepted.TrySetResult();
            return true;
        };
        await using var client = await RespireClient.ConnectAsync(Options(server, protocol));
        using var cancellation = new CancellationTokenSource();
        Task pending = write
            ? client.Search.SetConfigurationAsync("TIMEOUT", "501", cancellation.Token).AsTask()
            : client.Search.GetConfigurationAsync(cancellationToken: cancellation.Token).AsTask();
        await accepted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        var error = await Assert.That(async () => await pending).Throws<OperationCanceledException>();
        await Assert.That(error!.CancellationToken).IsEqualTo(cancellation.Token);
        await server.SendRawAsync(write ? FakeRespServer.OkReply : ReadReply(protocol));
        var next = await client.GetStringAsync("after-cancel").AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(next).IsEqualTo("alive");
        await Assert.That(client.IsConnected).IsTrue();
    }

    private static FakeRespServer Server(byte[] reply) => new(FakeRespServer.PongReply)
    {
        ReplyOverride = (_, command) => command switch
        {
            "HELLO 3" => Hello,
            "GET after-cancel" => "$5\r\nalive\r\n"u8.ToArray(),
            _ when command.StartsWith("FT.CONFIG", StringComparison.Ordinal) => reply,
            _ => null,
        },
    };

    private static byte[] ReadReply(int protocol) => protocol == 2
        ? "*3\r\n*2\r\n$7\r\nTIMEOUT\r\n$3\r\n500\r\n*2\r\n$7\r\nEXTLOAD\r\n$-1\r\n*2\r\n$5\r\nEMPTY\r\n$0\r\n\r\n"u8.ToArray()
        : "%3\r\n$7\r\nTIMEOUT\r\n$3\r\n500\r\n$7\r\nEXTLOAD\r\n_\r\n$5\r\nEMPTY\r\n$0\r\n\r\n"u8.ToArray();

    private static string[] LastArguments(FakeRespServer server)
        => server.ReceivedArguments.Last().Select(Encoding.UTF8.GetString).ToArray();

    private static RespireOptions Options(FakeRespServer server, int protocol, bool allowAdmin = true) => new()
    {
        Endpoints = [new("127.0.0.1", server.Port)],
        Protocol = (RespProtocol)protocol,
        Connections = 1,
        ThreadPoolMonitoring = false,
        AllowAdmin = allowAdmin,
    };
}
