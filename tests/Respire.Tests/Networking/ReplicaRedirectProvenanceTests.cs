using System.Text;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class ReplicaRedirectProvenanceTests
{
    [Test]
    [Arguments(false, RespireClientTrackingMode.OptIn)]
    [Arguments(true, RespireClientTrackingMode.OptIn)]
    [Arguments(false, RespireClientTrackingMode.OptOut)]
    [Arguments(true, RespireClientTrackingMode.OptOut)]
    [Arguments(false, RespireClientTrackingMode.Broadcast)]
    [Arguments(true, RespireClientTrackingMode.Broadcast)]
    public async Task TargetLossPreservesHealthyTargetAndSourceHits(bool retry, RespireClientTrackingMode mode)
    {
        var limit = TimeSpan.FromSeconds(10);
        await using var first = Server();
        await using var second = Server();
        await using var source = Server();
        source.ReplyOverride = (_, command) => command.StartsWith("GET first", StringComparison.Ordinal)
            ? Encoding.UTF8.GetBytes($"-REDIRECT 127.0.0.1:{first.Port}\r\n")
            : command.StartsWith("GET second", StringComparison.Ordinal)
                ? Encoding.UTF8.GetBytes($"-REDIRECT 127.0.0.1:{second.Port}\r\n") : Reply(command);
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = [new("127.0.0.1", source.Port)], Connections = 1,
            EnableReplicaRedirects = true, ClientSideCache = new() { TrackingMode = mode },
            RetryPolicy = retry ? new() : null, CommandTimeout = limit,
        });
        // Admit both destinations before warming: a new destination remains a failover fence.
        await client.GetStringAsync("first-admit");
        await client.GetStringAsync("second-admit");
        await client.GetStringAsync("first");
        await client.GetStringAsync("second");
        await client.GetStringAsync("source");
        var secondReads = second.ReceivedCommands.Count(command => command == "GET second");
        var sourceReads = source.ReceivedCommands.Count(command => command == "GET source");
        var lost = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = client.ClientSideCache!.SubscribeInvalidations("first", change =>
        {
            if ((change.Reasons & RespireClientCacheInvalidationReason.ContinuityLost) != 0) lost.TrySetResult();
        });
        first.CloseConnections();
        await lost.Task.WaitAsync(limit);

        await Assert.That(await client.GetStringAsync("second")).IsEqualTo("value");
        await Assert.That(await client.GetStringAsync("source")).IsEqualTo("value");
        await Assert.That(second.ReceivedCommands.Count(command => command == "GET second")).IsEqualTo(secondReads);
        await Assert.That(source.ReceivedCommands.Count(command => command == "GET source")).IsEqualTo(sourceReads);
        // A replacement socket acquires fresh provenance and can cache again.
        await Assert.That(await client.GetStringAsync("first")).IsEqualTo("value");
        await Assert.That(await client.GetStringAsync("first")).IsEqualTo("value");
        await Assert.That(first.ReceivedCommands.Count(command => command == "GET first")).IsEqualTo(2);
    }

    private static FakeRespServer Server() => new(8) { ReplyOverride = (_, command) => Reply(command) };
    private static byte[] Reply(string command) => Encoding.UTF8.GetBytes(command.StartsWith("HELLO 3", StringComparison.Ordinal)
        ? "%1\r\n$5\r\nproto\r\n:3\r\n" : command.StartsWith("GET ", StringComparison.Ordinal) ? "$5\r\nvalue\r\n" : "+OK\r\n");
}
