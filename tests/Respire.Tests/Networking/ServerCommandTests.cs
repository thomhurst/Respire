using System.Text;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class ServerCommandTests
{
    [Test]
    public async Task ServerIntrospectionCommands_WriteExpectedFramesAndParseReplies()
    {
        const long slowLogTimestamp = 1_700_000_000;
        const long latencyTimestamp = 1_700_000_100;
        const long lastSaveTimestamp = 1_700_000_200;

        var clientList =
            "id=3 addr=127.0.0.1:6379 fd=8 name=worker age=9 idle=2 flags=N db=1 " +
            "sub=0 psub=0 multi=-1 qbuf=0 qbuf-free=20474 argv-mem=10 obl=0 oll=0 omem=0 " +
            "events=r cmd=get user=default redir=-1 resp=3\n" +
            "id=4 addr=10.0.0.5:1111 name= age=1 idle=0 flags=x db=0 cmd=client|list user=alice\n";

        await using var server = new FakeRespServer(
            Bulk(clientList),
            Array(Array(
                Integer(7),
                Integer(slowLogTimestamp),
                Integer(1234),
                Array(Bulk("SET"), Bulk("key"), Bulk("value")),
                Bulk("127.0.0.1:6379"),
                Bulk("worker"))),
            SimpleString("OK"),
            Array(Array(Bulk("command"), Integer(latencyTimestamp), Integer(12), Integer(34))),
            Integer(1),
            Integer(2048),
            Array(
                Bulk("peak.allocated"), Integer(1024),
                Bulk("dataset.bytes"), Integer(700),
                Bulk("db.0"), Array(
                    Bulk("keys.count"), Integer(2),
                    Bulk("overhead.hashtable"), Integer(128))),
            Array(
                Bulk("master"),
                Integer(55),
                Array(Array(Bulk("127.0.0.2"), Bulk("6380"), Bulk("50")))),
            Integer(lastSaveTimestamp),
            Integer(240),
            Array(Bulk("get"), Bulk("set")),
            Integer(1));
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);

        var clients = await client.Server.ClientsAsync();
        await Assert.That(clients).Count().IsEqualTo(2);
        await Assert.That(clients[0].Id).IsEqualTo(3);
        await Assert.That(clients[0].Address).IsEqualTo("127.0.0.1:6379");
        await Assert.That(clients[0].Name).IsEqualTo("worker");
        await Assert.That(clients[0].Database).IsEqualTo(1);
        await Assert.That(clients[0].Command).IsEqualTo("get");
        await Assert.That(clients[0].Attributes["resp"]).IsEqualTo("3");
        await Assert.That(clients[1].Name).IsNull();

        var slowLog = await client.Server.SlowLogAsync(5);
        await Assert.That(slowLog).Count().IsEqualTo(1);
        await Assert.That(slowLog[0].Id).IsEqualTo(7);
        await Assert.That(slowLog[0].Timestamp).IsEqualTo(DateTimeOffset.FromUnixTimeSeconds(slowLogTimestamp));
        await Assert.That(slowLog[0].Duration).IsEqualTo(TimeSpan.FromTicks(12_340));
        await Assert.That(slowLog[0].Command).IsEquivalentTo(["SET", "key", "value"]);
        await Assert.That(slowLog[0].ClientAddress).IsEqualTo("127.0.0.1:6379");
        await Assert.That(slowLog[0].ClientName).IsEqualTo("worker");

        await client.Server.ResetSlowLogAsync();

        var latency = await client.Server.LatestLatencyAsync();
        await Assert.That(latency).Count().IsEqualTo(1);
        await Assert.That(latency[0].Event).IsEqualTo("command");
        await Assert.That(latency[0].Latest).IsEqualTo(DateTimeOffset.FromUnixTimeSeconds(latencyTimestamp));
        await Assert.That(latency[0].LatestLatency).IsEqualTo(TimeSpan.FromMilliseconds(12));
        await Assert.That(latency[0].MaxLatency).IsEqualTo(TimeSpan.FromMilliseconds(34));

        await Assert.That(await client.Server.ResetLatencyAsync("command")).IsEqualTo(1);
        await Assert.That(await client.Server.MemoryUsageAsync("payload", samples: 0)).IsEqualTo(2048);

        var memory = await client.Server.MemoryStatsAsync();
        await Assert.That(memory.Values["peak.allocated"].AsInt64).IsEqualTo(1024);
        await Assert.That(memory.Values["dataset.bytes"].AsInt64).IsEqualTo(700);
        var db0 = memory.Values["db.0"].Children!;
        await Assert.That(db0["keys.count"].AsInt64).IsEqualTo(2);
        await Assert.That(db0["overhead.hashtable"].AsInt64).IsEqualTo(128);

        var role = await client.Server.RoleAsync();
        await Assert.That(role.Kind).IsEqualTo(RespireServerRoleKind.Master);
        await Assert.That(role.ReplicationOffset).IsEqualTo(55);
        await Assert.That(role.Replicas).IsEquivalentTo([new RespireReplicaInfo("127.0.0.2", 6380, 50)]);

        await Assert.That(await client.Server.LastSaveAsync())
            .IsEqualTo(DateTimeOffset.FromUnixTimeSeconds(lastSaveTimestamp));
        await Assert.That(await client.Server.CommandCountAsync()).IsEqualTo(240);
        await Assert.That(await client.Server.CommandListAsync()).IsEquivalentTo(["get", "set"]);
        await Assert.That(await client.Server.KillClientAsync(42, skipMe: false)).IsTrue();

        await Assert.That(server.ReceivedCommands).IsEquivalentTo(new[]
        {
            "CLIENT LIST",
            "SLOWLOG GET 5",
            "SLOWLOG RESET",
            "LATENCY LATEST",
            "LATENCY RESET command",
            "MEMORY USAGE payload SAMPLES 0",
            "MEMORY STATS",
            "ROLE",
            "LASTSAVE",
            "COMMAND COUNT",
            "COMMAND LIST",
            "CLIENT KILL ID 42 SKIPME no",
        });
    }

    [Test]
    public async Task ServerIntrospectionParsers_HandleResp3MapsAndLegacyShapes()
    {
        await using var server = new FakeRespServer(
            Array(Array(
                Integer(8),
                Bulk("1700000001"),
                Bulk("2000"),
                Array(Bulk("PING")))),
            Map(
                Bulk("peak.allocated"), Integer(1000),
                Bulk("fragmentation"), Bulk("1.25"),
                Bulk("db.1"), Map(
                    Bulk("keys.count"), Integer(3))));
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);

        var slowLog = await client.Server.SlowLogAsync();
        await Assert.That(slowLog).Count().IsEqualTo(1);
        await Assert.That(slowLog[0].Id).IsEqualTo(8);
        await Assert.That(slowLog[0].Timestamp).IsEqualTo(DateTimeOffset.FromUnixTimeSeconds(1_700_000_001));
        await Assert.That(slowLog[0].Duration).IsEqualTo(TimeSpan.FromMilliseconds(2));
        await Assert.That(slowLog[0].Command).IsEquivalentTo(["PING"]);
        await Assert.That(slowLog[0].ClientAddress).IsNull();
        await Assert.That(slowLog[0].ClientName).IsNull();

        var memory = await client.Server.MemoryStatsAsync();
        await Assert.That(memory.Values["peak.allocated"].AsInt64).IsEqualTo(1000);
        await Assert.That(memory.Values["fragmentation"].AsDouble).IsEqualTo(1.25);
        await Assert.That(memory.Values["db.1"].Children!["keys.count"].AsInt64).IsEqualTo(3);

        await Assert.That(server.ReceivedCommands).IsEquivalentTo(["SLOWLOG GET", "MEMORY STATS"]);
    }

    [Test]
    public async Task ServerIntrospectionCommands_ValidateBeforeSending()
    {
        await using var server = new FakeRespServer();
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);

        await Assert.That(async () => await client.Server.KillClientAsync(0))
            .Throws<ArgumentOutOfRangeException>();
        await Assert.That(async () => await client.Server.SlowLogAsync(-1))
            .Throws<ArgumentOutOfRangeException>();
        await Assert.That(async () => await client.Server.MemoryUsageAsync("key", samples: -1))
            .Throws<ArgumentOutOfRangeException>();
        await Assert.That(async () => await client.Server.ResetLatencyAsync(""))
            .Throws<ArgumentException>();
        await Assert.That(server.ReceivedCommands).IsEmpty();
    }

    [Test]
    public async Task ConfigAsync_ReturnsMatchingConfiguration()
    {
        await using var server = new FakeRespServer(
            Array(Bulk("maxmemory"), Bulk("0")));
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);

        var config = await client.Server.ConfigAsync("max*");

        await Assert.That(config).IsEquivalentTo(new Dictionary<string, string>
        {
            ["maxmemory"] = "0",
        });
        await Assert.That(server.ReceivedCommands.Single()).IsEqualTo("CONFIG GET max*");
    }

    private static byte[] SimpleString(string value)
        => Encoding.ASCII.GetBytes($"+{value}\r\n");

    private static byte[] Integer(long value)
        => Encoding.ASCII.GetBytes($":{value}\r\n");

    private static byte[] Bulk(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        using var stream = new MemoryStream();
        WriteAscii(stream, $"${bytes.Length}\r\n");
        stream.Write(bytes);
        WriteAscii(stream, "\r\n");
        return stream.ToArray();
    }

    private static byte[] Array(params byte[][] elements)
        => Aggregate('*', elements.Length, elements);

    private static byte[] Map(params byte[][] elements)
    {
        if (elements.Length % 2 != 0)
        {
            throw new ArgumentException("RESP maps require key/value pairs.", nameof(elements));
        }

        return Aggregate('%', elements.Length / 2, elements);
    }

    private static byte[] Aggregate(char type, int count, byte[][] elements)
    {
        using var stream = new MemoryStream();
        WriteAscii(stream, $"{type}{count}\r\n");
        foreach (var element in elements)
        {
            stream.Write(element);
        }

        return stream.ToArray();
    }

    private static void WriteAscii(Stream stream, string value)
    {
        var bytes = Encoding.ASCII.GetBytes(value);
        stream.Write(bytes);
    }
}
