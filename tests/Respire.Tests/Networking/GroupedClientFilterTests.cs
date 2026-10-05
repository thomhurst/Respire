using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class GroupedClientFilterTests
{
    [Test]
    public async Task MixedSelectorFormsFailSynchronouslyAcrossEveryPath()
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
        RespireClientFilterOptions[] flat =
        [
            new() { Type = RespireClientType.Normal }, new() { Ids = [1] },
            new() { Ids = null! }, new() { User = "worker" }, new() { Address = "remote:1" },
            new() { LocalAddress = "local:1" }, new() { MaximumAgeSeconds = 1 },
            new() { Name = "worker" }, new() { IdleSeconds = 1 }, new() { Flags = "N" },
            new() { LibraryName = "lib" }, new() { LibraryVersion = "1" },
            new() { Database = 0 }, new() { Capabilities = "r" }, new() { Ip = "127.0.0.1" },
            new() { ExcludedType = RespireClientType.Normal }, new() { ExcludedIds = [1] },
            new() { ExcludedIds = null! }, new() { ExcludedUser = "worker" },
            new() { ExcludedAddress = "remote:1" }, new() { ExcludedLocalAddress = "local:1" },
            new() { ExcludedName = "worker" }, new() { ExcludedFlags = "" },
            new() { ExcludedLibraryName = "lib" }, new() { ExcludedLibraryVersion = "1" },
            new() { ExcludedDatabase = 0 }, new() { ExcludedCapabilities = "" }, new() { ExcludedIp = "127.0.0.1" },
        ];
        foreach (var legacy in flat)
        {
            var options = legacy with { Include = new(), Exclude = new() };
            await Assert.That(() => { _ = client.Server.ClientsAsync(options, default); }).ThrowsExactly<ArgumentException>();
            await Assert.That(() => { _ = client.Server.ClientsOnAllNodesAsync(options, default); }).ThrowsExactly<ArgumentException>();
            await Assert.That(() => { _ = client.Server.KillClientsAsync(options); }).ThrowsExactly<ArgumentException>();
            await Assert.That(() => { _ = handle.ClientsAsync(options); }).ThrowsExactly<ArgumentException>();
            await Assert.That(() => { _ = handle.KillClientsAsync(options); }).ThrowsExactly<ArgumentException>();
            foreach (IRespireCommandQueue queue in new IRespireCommandQueue[] { batch, tx })
            {
                await Assert.That(() => queue.Server.Clients(options)).ThrowsExactly<ArgumentException>();
                await Assert.That(() => queue.Server.KillClients(options)).ThrowsExactly<ArgumentException>();
            }
        }
        await Assert.That(server.ReceivedCommands.ToArray()).IsEquivalentTo(["CLIENT ID"]);
    }

    [Test]
    public async Task GroupedIdsSelectTheSameWireCommandAsFlatIds()
    {
        await using var server = new FakeRespServer(2, ":0\r\n"u8.ToArray());
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, Connections = 1, AllowAdmin = true,
            Endpoints = [new("127.0.0.1", server.Port)],
        });
        await client.Server.KillClientsAsync(new() { Ids = [42], SkipMe = true });
        await client.Server.KillClientsAsync(new() { Include = new() { Ids = [42] }, SkipMe = true });
        await Assert.That(server.ReceivedCommands.ToArray()).IsEquivalentTo([
            "CLIENT KILL ID 42 SKIPME yes", "CLIENT KILL ID 42 SKIPME yes"]);
    }

    [Test]
    [Arguments(2, false)]
    [Arguments(3, false)]
    [Arguments(2, true)]
    [Arguments(3, true)]
    public async Task EmptyExclusionSetsRemainSelectors(int protocol, bool capabilities)
    {
        await using var server = new FakeRespServer(":0\r\n"u8.ToArray())
        {
            ReplyOverride = (_, command) => command == "HELLO 3"
                ? "%1\r\n$5\r\nproto\r\n:3\r\n"u8.ToArray() : null,
        };
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = (RespProtocol)protocol, Connections = 1, AllowAdmin = true,
            Endpoints = [new("127.0.0.1", server.Port)],
        });
        var filter = new RespireClientFilterOptions
        {
            Exclude = capabilities ? new() { Capabilities = "" } : new() { Flags = "" },
        };
        await client.Server.KillClientsAsync(filter);
        var frame = server.ReceivedArguments.Last();
        await Assert.That(frame.Length).IsEqualTo(4);
        await Assert.That(System.Text.Encoding.UTF8.GetString(frame[2])).IsEqualTo(capabilities ? "NOT-CAPA" : "NOT-FLAGS");
        await Assert.That(frame[3].Length).IsEqualTo(0);
    }

    [Test]
    public async Task EmptyGroupsStillRequireExplicitUnfilteredKill()
    {
        await using var server = new FakeRespServer(FakeRespServer.OkReply);
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, Connections = 1, AllowAdmin = true,
            Endpoints = [new("127.0.0.1", server.Port)],
        });
        RespireClientFilterOptions[] empty =
        [
            new() { Include = new() }, new() { Exclude = new() },
            new() { Include = new(), Exclude = new(), SkipMe = true },
        ];
        foreach (var filter in empty)
            await Assert.That(() => { _ = client.Server.KillClientsAsync(filter); }).ThrowsExactly<ArgumentException>();
        await Assert.That(server.ReceivedCommands).IsEmpty();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task NullGroupedIdsFailBeforeSendingOrQueueing(bool exclude)
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
        var options = exclude ? new RespireClientFilterOptions { Exclude = new() { Ids = null! } }
            : new RespireClientFilterOptions { Include = new() { Ids = null! } };
        await Assert.That(() => { _ = client.Server.ClientsAsync(options, default); }).ThrowsExactly<ArgumentNullException>();
        await Assert.That(() => { _ = client.Server.ClientsOnAllNodesAsync(options, default); }).ThrowsExactly<ArgumentNullException>();
        await Assert.That(() => { _ = client.Server.KillClientsAsync(options); }).ThrowsExactly<ArgumentNullException>();
        await Assert.That(() => { _ = handle.ClientsAsync(options); }).ThrowsExactly<ArgumentNullException>();
        await Assert.That(() => { _ = handle.KillClientsAsync(options); }).ThrowsExactly<ArgumentNullException>();
        foreach (IRespireCommandQueue queue in new IRespireCommandQueue[] { batch, tx })
        {
            await Assert.That(() => queue.Server.Clients(options)).ThrowsExactly<ArgumentNullException>();
            await Assert.That(() => queue.Server.KillClients(options)).ThrowsExactly<ArgumentNullException>();
        }
        await Assert.That(server.ReceivedCommands.ToArray()).IsEquivalentTo(["CLIENT ID"]);
    }
}
