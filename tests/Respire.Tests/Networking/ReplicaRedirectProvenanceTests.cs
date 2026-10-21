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

    [Test]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(false, true)]
    [Arguments(true, true)]
    public async Task QueryTargetLossPreservesIndependentHitsAndInvalidatesEveryDependency(bool retry, bool multipleKeys)
    {
        var limit = TimeSpan.FromSeconds(10);
        await using var target = Server();
        await using var source = Server();
        source.ReplyOverride = (_, command) => command.StartsWith("HGET first ", StringComparison.Ordinal)
            || command == "LCS first secondary" || command == "GET first-admit"
            ? Encoding.UTF8.GetBytes($"-REDIRECT 127.0.0.1:{target.Port}\r\n") : Reply(command);
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = [new("127.0.0.1", source.Port)], Connections = 1,
            EnableReplicaRedirects = true, ClientSideCache = new(),
            RetryPolicy = retry ? new() : null, CommandTimeout = limit,
        });
        await client.GetStringAsync("first-admit");
        async ValueTask<string?> ReadQuery() => multipleKeys
            ? await client.Strings.LcsAsync("first", "secondary")
            : await client.Hashes.GetStringAsync("first", "field");
        await Assert.That(await ReadQuery()).IsEqualTo("value");
        await Assert.That(await ReadQuery()).IsEqualTo("value");
        await client.GetStringAsync("secondary");
        await client.GetStringAsync("independent");
        var queryCommand = multipleKeys ? "LCS first secondary" : "HGET first field";
        await Assert.That(target.ReceivedCommands.Count(command => command == queryCommand)).IsEqualTo(1);
        var lost = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // Wait for the last dependency so its invalidation is complete before refetching it.
        using var subscription = client.ClientSideCache!.SubscribeInvalidations(multipleKeys ? "secondary" : "first", change =>
        {
            if ((change.Reasons & RespireClientCacheInvalidationReason.ContinuityLost) != 0) lost.TrySetResult();
        });
        target.CloseConnections();
        await lost.Task.WaitAsync(limit);

        await Assert.That(await client.GetStringAsync("independent")).IsEqualTo("value");
        await Assert.That(source.ReceivedCommands.Count(command => command == "GET independent")).IsEqualTo(1);
        await client.GetStringAsync("secondary");
        await Assert.That(source.ReceivedCommands.Count(command => command == "GET secondary"))
            .IsEqualTo(multipleKeys ? 2 : 1);
        await Assert.That(await ReadQuery()).IsEqualTo("value");
        await Assert.That(target.ReceivedCommands.Count(command => command == queryCommand)).IsEqualTo(2);
    }

    [Test]
    [Arguments(false, RespireClientTrackingMode.OptIn)]
    [Arguments(true, RespireClientTrackingMode.OptIn)]
    [Arguments(false, RespireClientTrackingMode.OptOut)]
    [Arguments(true, RespireClientTrackingMode.OptOut)]
    [Arguments(false, RespireClientTrackingMode.Broadcast)]
    [Arguments(true, RespireClientTrackingMode.Broadcast)]
    public async Task UncacheableReadDoesNotExhaustTargetProvenance(bool retry, RespireClientTrackingMode mode)
    {
        var limit = TimeSpan.FromSeconds(10);
        await using var target = Server();
        await using var source = Server();
        source.ReplyOverride = (_, command) => command.StartsWith("GET cache:target", StringComparison.Ordinal)
            || command.StartsWith("GET excluded", StringComparison.Ordinal)
            ? Encoding.UTF8.GetBytes($"-REDIRECT 127.0.0.1:{target.Port}\r\n") : Reply(command);
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = [new("127.0.0.1", source.Port)], Connections = 1,
            EnableReplicaRedirects = true,
            ClientSideCache = new() { TrackingMode = mode, KeyPrefixes = ["cache:"] },
            RetryPolicy = retry ? new() : null, CommandTimeout = limit,
        });
        await client.GetStringAsync("cache:target");
        await client.GetStringAsync("cache:independent");
        var excludedKey = "excluded" + new string('x', ClientSideCacheCoordinator.TrackingProvenance.MaximumKeyBytes);
        await Assert.That(await client.GetStringAsync(excludedKey)).IsEqualTo("value");
        var lost = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = client.ClientSideCache!.SubscribeInvalidations("cache:target", change =>
        {
            if ((change.Reasons & RespireClientCacheInvalidationReason.ContinuityLost) != 0) lost.TrySetResult();
        });
        target.CloseConnections();
        await lost.Task.WaitAsync(limit);

        await Assert.That(await client.GetStringAsync("cache:independent")).IsEqualTo("value");
        await Assert.That(source.ReceivedCommands.Count(command => command == "GET cache:independent")).IsEqualTo(1);
    }

    [Test]
    [Arguments(false, RespireClientTrackingMode.OptIn)]
    [Arguments(true, RespireClientTrackingMode.OptIn)]
    [Arguments(false, RespireClientTrackingMode.OptOut)]
    [Arguments(true, RespireClientTrackingMode.OptOut)]
    [Arguments(false, RespireClientTrackingMode.Broadcast)]
    [Arguments(true, RespireClientTrackingMode.Broadcast)]
    public async Task MixedMgetDoesNotChargeExcludedKeysToProvenance(bool retry, RespireClientTrackingMode mode)
    {
        var limit = TimeSpan.FromSeconds(10);
        await using var target = Server();
        await using var source = Server();
        source.ReplyOverride = (_, command) => command.StartsWith("MGET ", StringComparison.Ordinal)
            || command.StartsWith("GET cache:target", StringComparison.Ordinal)
            ? Encoding.UTF8.GetBytes($"-REDIRECT 127.0.0.1:{target.Port}\r\n") : Reply(command);
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = [new("127.0.0.1", source.Port)], Connections = 1,
            EnableReplicaRedirects = true,
            ClientSideCache = new() { TrackingMode = mode, KeyPrefixes = ["cache:"] },
            RetryPolicy = retry ? new() : null, CommandTimeout = limit,
        });
        await client.GetStringAsync("cache:target-admit");
        await client.GetStringAsync("cache:independent");
        var excluded = "excluded" + new string('x', ClientSideCacheCoordinator.TrackingProvenance.MaximumKeyBytes);
        var values = await client.Strings.GetManyAsync("cache:target-first", excluded, "cache:target-second");
        await Assert.That(values.Length).IsEqualTo(3);
        await Assert.That(values.All(value => value == "value")).IsTrue();
        await Assert.That(target.ReceivedCommands.Count(command => command.StartsWith("MGET ", StringComparison.Ordinal))).IsEqualTo(1);
        await Assert.That(await client.GetStringAsync("cache:target-first")).IsEqualTo("value");
        await Assert.That(await client.GetStringAsync("cache:target-second")).IsEqualTo("value");
        await Assert.That(target.ReceivedCommands.Count(command => command == "GET cache:target-first"
            || command == "GET cache:target-second")).IsEqualTo(0);
        var lost = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = client.ClientSideCache!.SubscribeInvalidations("cache:target-second", change =>
        {
            if ((change.Reasons & RespireClientCacheInvalidationReason.ContinuityLost) != 0) lost.TrySetResult();
        });
        target.CloseConnections();
        await lost.Task.WaitAsync(limit);

        await Assert.That(await client.GetStringAsync("cache:independent")).IsEqualTo("value");
        await Assert.That(source.ReceivedCommands.Count(command => command == "GET cache:independent")).IsEqualTo(1);
        await Assert.That(await client.GetStringAsync("cache:target-first")).IsEqualTo("value");
        await Assert.That(await client.GetStringAsync("cache:target-second")).IsEqualTo("value");
        await Assert.That(target.ReceivedCommands.Count(command => command == "GET cache:target-first")).IsEqualTo(1);
        await Assert.That(target.ReceivedCommands.Count(command => command == "GET cache:target-second")).IsEqualTo(1);
    }

    private static FakeRespServer Server() => new(8) { ReplyOverride = (_, command) => Reply(command) };
    private static byte[] Reply(string command) => Encoding.UTF8.GetBytes(command.StartsWith("HELLO 3", StringComparison.Ordinal)
        ? "%1\r\n$5\r\nproto\r\n:3\r\n" : command.StartsWith("MGET ", StringComparison.Ordinal)
            ? $"*{command.Count(character => character == ' ')}\r\n" + string.Concat(Enumerable.Repeat("$5\r\nvalue\r\n", command.Count(character => character == ' ')))
        : command.StartsWith("GET ", StringComparison.Ordinal)
            || command.StartsWith("HGET ", StringComparison.Ordinal) || command.StartsWith("LCS ", StringComparison.Ordinal)
                ? "$5\r\nvalue\r\n" : "+OK\r\n");
}
