using System.Text;
using Respire.Internal;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class ReadEndpointRoutingTests
{
    private static readonly byte[] PrimaryRole = "*3\r\n$6\r\nmaster\r\n:0\r\n*0\r\n"u8.ToArray();
    private static readonly byte[] ReplicaRole = "*5\r\n$5\r\nslave\r\n$9\r\n127.0.0.1\r\n:6379\r\n$9\r\nconnected\r\n:0\r\n"u8.ToArray();

    [Test]
    public async Task ReadViewUsesReplicaWithoutTelemetryAndKeepsWritesOnPrimary()
    {
        await using var primary = new FakeRespServer(FakeRespServer.OkReply);
        await using var replica = new FakeRespServer(ReplicaRole, Bulk("replica"));
        await using var client = RespireClient.Create(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            Connections = 1,
            Endpoints = [new("127.0.0.1", primary.Port)],
            ReplicaEndpoints = [new("127.0.0.1", replica.Port)],
        });

        var view = client.WithReadFrom(RespireReadFrom.Replica);
        await Assert.That(await view.GetStringAsync("key")).IsEqualTo("replica");
        await Assert.That(await view.SetAsync("key", "new-value")).IsTrue();

        await Assert.That(replica.ReceivedCommands).IsEquivalentTo(["ROLE", "GET key"]);
        await Assert.That(primary.ReceivedCommands).IsEquivalentTo(["SET key new-value"]);
    }

    [Test]
    public async Task ReplicaPolicyRejectsWrongRoleAndReplicaPreferredFallsBackBeforeSending()
    {
        await using var primary = new FakeRespServer(Bulk("primary"));
        await using var wrongRole = new FakeRespServer(2, PrimaryRole, PrimaryRole);
        await using var client = RespireClient.Create(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            Connections = 1,
            Endpoints = [new("127.0.0.1", primary.Port)],
            ReplicaEndpoints = [new("127.0.0.1", wrongRole.Port)],
        });

        await Assert.That(async () => await client.WithReadFrom(RespireReadFrom.Replica).GetStringAsync("key"))
            .Throws<RespireConnectionException>();
        await Assert.That(await client.WithReadFrom(RespireReadFrom.ReplicaPreferred).GetStringAsync("key"))
            .IsEqualTo("primary");
        await Assert.That(wrongRole.ReceivedCommands).IsEquivalentTo(["ROLE", "ROLE"]);
        await Assert.That(primary.ReceivedCommands).IsEquivalentTo(["GET key"]);
    }

    [Test]
    public async Task OnlyCatalogVerifiedReadOnlyCommandsAreEligible()
    {
        await Assert.That(ReadOnlyCommandCatalog.Contains("GET")).IsTrue();
        await Assert.That(ReadOnlyCommandCatalog.Contains("MGET")).IsTrue();
        await Assert.That(ReadOnlyCommandCatalog.Contains("SET")).IsFalse();
        await Assert.That(ReadOnlyCommandCatalog.Contains("CUSTOM.READ")).IsFalse();
    }

    [Test]
    public async Task CancellationDuringReplicaValidationStopsBeforeApplicationCommand()
    {
        await using var primary = new FakeRespServer(FakeRespServer.OkReply);
        await using var replica = new FakeRespServer(ReplicaRole)
        {
            SuppressReply = command => command == "ROLE",
        };
        await using var client = RespireClient.Create(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            Connections = 1,
            ConnectTimeout = TimeSpan.FromSeconds(2),
            Endpoints = [new("127.0.0.1", primary.Port)],
            ReplicaEndpoints = [new("127.0.0.1", replica.Port)],
        });
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.That(async () => await client.WithReadFrom(RespireReadFrom.Replica)
            .GetStringAsync("key", cancellation.Token)).Throws<OperationCanceledException>();
        await Assert.That(replica.ReceivedCommands).IsEquivalentTo(["ROLE"]);
        await Assert.That(primary.ReceivedCommands).IsEmpty();
    }

    private static byte[] Bulk(string value)
        => Encoding.ASCII.GetBytes($"${Encoding.ASCII.GetByteCount(value)}\r\n{value}\r\n");
}
