using Respire.Tests.Networking;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Extensions.Coordination.Tests;

public class CoordinationAccessorTests
{
    [Test]
    public async Task ConcreteInterfaceAndConcurrentAccessShareWrapperWithoutConnecting()
    {
        await using var client = CreateClient();
        IRespireClient abstraction = client;
        var wrappers = new RespireCoordination[32];
        Parallel.For(0, wrappers.Length, i => wrappers[i] = abstraction.Coordination);

        foreach (var wrapper in wrappers)
            await Assert.That(wrapper).IsSameReferenceAs(client.Coordination);
        await Assert.That(client.Coordination.RateLimiters).IsSameReferenceAs(abstraction.Coordination.RateLimiters);
        await Assert.That(client.IsConnected).IsFalse();
    }

    [Test]
    public async Task ClientsAndPrefixedViewsHaveSeparateWrappers()
    {
        await using var client = CreateClient();
        await using var other = CreateClient();
        var prefixed = client.WithKeyPrefix("tenant:");

        await Assert.That(prefixed.Coordination).IsSameReferenceAs(prefixed.Coordination);
        await Assert.That(ReferenceEquals(client.Coordination, prefixed.Coordination)).IsFalse();
        await Assert.That(ReferenceEquals(client.Coordination, other.Coordination)).IsFalse();
    }

    [Test]
    public async Task NullReceiverThrows()
    {
        IRespireClient client = null!;
        await Assert.That(() => client.Coordination).Throws<ArgumentNullException>();
    }

    [Test]
    public async Task SemaphoreFactorySnapshotsKeyWithoutConnecting()
    {
        await using var client = CreateClient();
        var bytes = "permits"u8.ToArray();
        var semaphore = client.Coordination.CreateSemaphore(new RespireKey(bytes), 4);
        bytes[0] = (byte)'x';

        await Assert.That(semaphore.Key).IsEqualTo((RespireKey)"permits");
        await Assert.That(semaphore.Capacity).IsEqualTo(4);
        await Assert.That(client.IsConnected).IsFalse();
    }

    [Test]
    [Arguments(0)]
    [Arguments(-1)]
    public async Task SemaphoreFactoryRejectsInvalidCapacity(int capacity)
    {
        await using var client = CreateClient();
        await Assert.That(() => client.Coordination.CreateSemaphore("permits", capacity))
            .Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task SemaphoreUsesPrefixedClientForAcquisitionAndRelease()
    {
        await using var server = new FakeRespServer(
            SemaphoreWireTests.ClientIdReply, SemaphoreWireTests.ClientKillReply,
            ":1\r\n"u8.ToArray(), ":1\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var semaphore = client.WithKeyPrefix("tenant:").Coordination.CreateSemaphore("permits", 4);
        await using (var attempt = await semaphore.TryAcquireAsync(TimeSpan.FromSeconds(30)))
        {
            await Assert.That(attempt.Acquired).IsTrue();
        }

        var commands = SemaphoreWireTests.EvalCommands(server);
        await Assert.That(commands.Length).IsEqualTo(2);
        await Assert.That(commands.All(command => command.Contains(" 1 tenant:permits "))).IsTrue();
    }

    private static RespireClient CreateClient() => RespireClient.Create(new RespireOptions
    {
        Endpoints = { new RespireEndpoint("127.0.0.1", 1) },
    });
}
