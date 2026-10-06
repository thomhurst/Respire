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
        foreach (var property in typeof(RespireClientFilterOptions).GetProperties())
        {
            if (property.Name is nameof(RespireClientFilterOptions.Include) or nameof(RespireClientFilterOptions.Exclude)
                or nameof(RespireClientFilterOptions.SkipMe) or nameof(RespireClientFilterOptions.AllowUnfilteredKill))
                continue;
            var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            object[] values = type == typeof(string) ? ["selected", ""] : [SelectorValue(type)];
            foreach (var value in values)
            {
                var options = new RespireClientFilterOptions { Include = new(), Exclude = new() };
                property.SetValue(options, value);
                var error = await Assert.That(() => { _ = client.Server.ClientsAsync(options, default); }).ThrowsExactly<ArgumentException>();
                await Assert.That(error!.Message).Contains("cannot be combined");
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
        }
        await Assert.That(server.ReceivedCommands.ToArray()).IsEquivalentTo(["CLIENT ID"]);

        static object SelectorValue(Type type)
        {
            if (type == typeof(long)) return 1L;
            if (type == typeof(int)) return 0;
            if (type == typeof(RespireClientType)) return RespireClientType.Normal;
            if (type == typeof(IReadOnlyList<long>)) return new long[] { 1 };
            throw new InvalidOperationException($"Add a selector value for {type}.");
        }
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task NullFlatIdsReportTheirPropertyEvenWithGroups(bool exclude, bool grouped)
    {
        var options = exclude ? new RespireClientFilterOptions { ExcludedIds = null! }
            : new RespireClientFilterOptions { Ids = null! };
        if (grouped) options = options with { Include = new(), Exclude = new() };
        foreach (var kill in new[] { false, true })
        {
            var error = await Assert.That(() => ClientFilterArguments.Build(options, kill)).ThrowsExactly<ArgumentNullException>();
            await Assert.That(error!.ParamName).IsEqualTo(exclude ? "ExcludedIds" : "Ids");
        }
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
