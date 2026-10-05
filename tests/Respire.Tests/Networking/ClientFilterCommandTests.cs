using System.Text;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class ClientFilterCommandTests
{
    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(3)]
    public async Task MatchAllFiltersFailSynchronouslyAcrossAllPaths(int filter)
    {
        await using var server = new FakeRespServer(":7\r\n"u8.ToArray());
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, Connections = 1, AllowAdmin = true,
            Endpoints = [new("127.0.0.1", server.Port)],
        });
        var handle = await client.Server.GetClientConnectionAsync();
        using var batch = client.CreateBatch();
        await using var tx = client.CreateTransaction();
        var options = filter switch
        {
            0 => new RespireClientFilterOptions { SkipMe = true },
            1 => new RespireClientFilterOptions { SkipMe = false },
            2 => new RespireClientFilterOptions { Flags = "" },
            _ => new RespireClientFilterOptions { Capabilities = "" },
        };
        await Assert.That(() => { _ = client.Server.KillClientsAsync(options); }).ThrowsExactly<ArgumentException>();
        await Assert.That(() => { _ = handle.KillClientsAsync(options); }).ThrowsExactly<ArgumentException>();
        await Assert.That(() => batch.Server.KillClients(options)).ThrowsExactly<ArgumentException>();
        await Assert.That(() => tx.Server.KillClients(options)).ThrowsExactly<ArgumentException>();
        var invalid = new RespireClientFilterOptions { Ids = [0] };
        await Assert.That(() => { _ = client.Server.ClientsAsync(invalid, default); }).ThrowsExactly<ArgumentOutOfRangeException>();
        await Assert.That(() => { _ = client.Server.ClientsOnAllNodesAsync(invalid, default); }).ThrowsExactly<ArgumentOutOfRangeException>();
        await Assert.That(() => { _ = handle.ClientsAsync(invalid); }).ThrowsExactly<ArgumentOutOfRangeException>();
        await Assert.That(() => batch.Server.Clients(invalid)).ThrowsExactly<ArgumentOutOfRangeException>();
        await Assert.That(() => tx.Server.Clients(invalid)).ThrowsExactly<ArgumentOutOfRangeException>();
        await Assert.That(server.ReceivedCommands.ToArray()).IsEquivalentTo(["CLIENT ID"]);
    }

    [Test]
    public async Task ExplicitUnfilteredKillAndReplicaTokensUseCompatibleWireForms()
    {
        await using var server = new FakeRespServer(4, ":0\r\n"u8.ToArray());
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, Connections = 1, AllowAdmin = true,
            Endpoints = [new("127.0.0.1", server.Port)],
        });
        await client.Server.KillClientsAsync(new() { AllowUnfilteredKill = true });
        await client.Server.KillClientsAsync(new() { AllowUnfilteredKill = true, SkipMe = false });
        await client.Server.KillClientsAsync(new() { Type = RespireClientType.Replica });
        await client.Server.KillClientsAsync(new() { ExcludedType = RespireClientType.Replica });
        await Assert.That(server.ReceivedCommands.ToArray()).IsEquivalentTo([
            "CLIENT KILL SKIPME yes", "CLIENT KILL SKIPME no", "CLIENT KILL TYPE slave", "CLIENT KILL NOT-TYPE slave"]);
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(3)]
    public async Task FiltersSnapshotIdsAndPreserveTypedReplies(int execution)
    {
        const string row = "id=42 addr=127.0.0.1:4567 name=worker db=0 flags=N cmd=ping user=default age=9 idle=1 future=kept\n";
        var list = Encoding.UTF8.GetBytes($"${Encoding.UTF8.GetByteCount(row)}\r\n{row}\r\n");
        byte[][] replies = execution switch
        {
            2 => [FakeRespServer.OkReply, "+QUEUED\r\n"u8.ToArray(), "+QUEUED\r\n"u8.ToArray(), [.. "*2\r\n"u8, .. list, .. ":2\r\n"u8]],
            3 => [":7\r\n"u8.ToArray(), list, ":2\r\n"u8.ToArray()],
            _ => [list, ":2\r\n"u8.ToArray()],
        };
        await using var server = new FakeRespServer(replies);
        await using var owner = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, Connections = 1, AllowAdmin = true,
            Endpoints = [new("127.0.0.1", server.Port)],
        });
        var client = owner.WithKeyPrefix("tenant:");
        long[] ids = [42, 43];
        long[] excluded = [44, 45];
        var options = new RespireClientFilterOptions
        {
            Type = RespireClientType.Normal, Ids = ids, User = "default", Address = "remote:1", LocalAddress = "local:2",
            SkipMe = false, MaximumAgeSeconds = 3, Name = "worker", IdleSeconds = 4, Flags = "N", LibraryName = "lib",
            LibraryVersion = "1", Database = 0, Capabilities = "r", Ip = "127.0.0.1", ExcludedType = RespireClientType.Primary,
            ExcludedIds = excluded, ExcludedUser = "other", ExcludedAddress = "remote:3", ExcludedLocalAddress = "local:4",
            ExcludedName = "admin", ExcludedFlags = "b", ExcludedLibraryName = "otherlib", ExcludedLibraryVersion = "2",
            ExcludedDatabase = 1, ExcludedCapabilities = "x", ExcludedIp = "127.0.0.2",
        };
        RespireServerClientInfo[] rows;
        long killed;
        if (execution == 0)
        {
            rows = await client.Server.ClientsAsync(options, default);
            killed = await client.Server.KillClientsAsync(options);
        }
        else if (execution == 3)
        {
            var handle = await client.Server.GetClientConnectionAsync();
            rows = await handle.ClientsAsync(options);
            killed = await handle.KillClientsAsync(options);
        }
        else
        {
            using var batch = client.CreateBatch();
            await using var tx = client.CreateTransaction();
            IRespireCommandQueue queue = execution == 1 ? batch : tx;
            var pendingRows = queue.Server.Clients(options);
            var pendingKill = queue.Server.KillClients(options);
            ids[0] = 999;
            excluded[0] = 998;
            if (execution == 1) await batch.ExecuteAsync(); else await tx.CommitAsync();
            rows = pendingRows.Result;
            killed = pendingKill.Result;
        }
        await Assert.That(rows.Length).IsEqualTo(1);
        await Assert.That(rows[0].Id).IsEqualTo(42);
        await Assert.That(rows[0].Attributes["future"]).IsEqualTo("kept");
        await Assert.That(killed).IsEqualTo(2);
        const string suffix = " TYPE normal ID 42 43 USER default ADDR remote:1 LADDR local:2 SKIPME no MAXAGE 3 NAME worker IDLE 4 FLAGS N LIB-NAME lib LIB-VER 1 DB 0 CAPA r IP 127.0.0.1 NOT-TYPE master NOT-ID 44 45 NOT-USER other NOT-ADDR remote:3 NOT-LADDR local:4 NOT-NAME admin NOT-FLAGS b NOT-LIB-NAME otherlib NOT-LIB-VER 2 NOT-DB 1 NOT-CAPA x NOT-IP 127.0.0.2";
        await Assert.That(server.ReceivedCommands.Where(c => c.StartsWith("CLIENT LIST") || c.StartsWith("CLIENT KILL")))
            .IsEquivalentTo(["CLIENT LIST" + suffix, "CLIENT KILL" + suffix]);
    }

    [Test]
    public async Task InvalidFiltersFailBeforeSendingOrQueueing()
    {
        await using var server = new FakeRespServer(FakeRespServer.OkReply);
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, Connections = 1, AllowAdmin = true,
            Endpoints = [new("127.0.0.1", server.Port)],
        });
        using var batch = client.CreateBatch();
        await using var tx = client.CreateTransaction();
        (RespireClientFilterOptions Options, string Property)[] invalid =
        [
            (new() { Ids = [0] }, "Ids"), (new() { ExcludedIds = [-1] }, "ExcludedIds"),
            (new() { Type = (RespireClientType)99 }, "Type"), (new() { ExcludedType = (RespireClientType)99 }, "ExcludedType"),
            (new() { MaximumAgeSeconds = 0 }, "MaximumAgeSeconds"), (new() { IdleSeconds = 0 }, "IdleSeconds"),
            (new() { IdleSeconds = -1 }, "IdleSeconds"), (new() { Database = -1 }, "Database"),
            (new() { ExcludedDatabase = -1 }, "ExcludedDatabase"),
        ];
        foreach (var (options, property) in invalid)
        {
            var error = await Assert.That(() => { _ = client.Server.ClientsAsync(options, default); })
                .ThrowsExactly<ArgumentOutOfRangeException>();
            await Assert.That(error!.Message).Contains(property);
            await Assert.That(error.ParamName).IsEqualTo("options");
            await Assert.That(async () => await client.Server.ClientsAsync(options, default)).ThrowsExactly<ArgumentOutOfRangeException>();
            await Assert.That(async () => await client.Server.KillClientsAsync(options)).ThrowsExactly<ArgumentOutOfRangeException>();
            foreach (IRespireCommandQueue queue in new IRespireCommandQueue[] { batch, tx })
            {
                await Assert.That(() => queue.Server.Clients(options)).ThrowsExactly<ArgumentOutOfRangeException>();
                await Assert.That(() => queue.Server.KillClients(options)).ThrowsExactly<ArgumentOutOfRangeException>();
            }
        }
        await Assert.That(async () => await client.Server.KillClientsAsync(new())).ThrowsExactly<ArgumentException>();
        await Assert.That(() => batch.Server.KillClients(new())).ThrowsExactly<ArgumentException>();
        await Assert.That(() => tx.Server.KillClients(new())).ThrowsExactly<ArgumentException>();
        await Assert.That(server.ReceivedCommands).IsEmpty();
    }

    [Test]
    public async Task KillFiltersRequireAdminPermission()
    {
        await using var server = new FakeRespServer(FakeRespServer.OkReply);
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, Endpoints = [new("127.0.0.1", server.Port)],
        });
        var options = new RespireClientFilterOptions { Ids = [42] };
        using var batch = client.CreateBatch();
        await using var tx = client.CreateTransaction();
        await Assert.That(async () => await client.Server.KillClientsAsync(options)).ThrowsExactly<NotSupportedException>();
        await Assert.That(() => batch.Server.KillClients(options)).ThrowsExactly<NotSupportedException>();
        await Assert.That(() => tx.Server.KillClients(options)).ThrowsExactly<NotSupportedException>();
        await Assert.That(server.ReceivedCommands).IsEmpty();
    }
}
