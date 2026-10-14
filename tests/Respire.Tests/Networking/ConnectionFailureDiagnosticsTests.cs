using Respire.Infrastructure;
using Respire.Internal;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class ConnectionFailureDiagnosticsTests
{
    [Test]
    [Arguments("cache.example", 6380, 1, "cache.example:6380")]
    [Arguments("cache.example", 6380, 3, "cache.example:6380")]
    [Arguments("::1", 6380, 1, "[::1]:6380")]
    [Arguments("::1", 6380, 3, "[::1]:6380")]
    [Arguments("/tmp/redis.sock", 0, 1, "!/tmp/redis.sock")]
    [Arguments("/tmp/redis.sock", 0, 3, "!/tmp/redis.sock")]
    public async Task UnconnectedAcquisitionNamesEndpointAndPublicConnectMethod(
        string host, int port, int connections, string displayEndpoint)
    {
        await using var multiplexer = RespireConnectionMultiplexer.Create(host, port, connections);

        var error = await Assert.That(() => multiplexer.GetConnection())
            .ThrowsExactly<RespireConnectionException>();

        await Assert.That(error!.Message).Contains(displayEndpoint);
        await Assert.That(error.Message).Contains("RespireClient.ConnectAsync");
        await Assert.That(error.Message).DoesNotContain("EnsureConnectedAsync");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ExhaustedSeedsNameEveryEndpointAndPreserveEveryFailure(bool defaultSeed)
    {
        var options = new RespireOptions { UseCluster = true };
        if (!defaultSeed)
        {
            options.Endpoints.Add(new("cache.example", 6380));
            options.Endpoints.Add(new("2001:db8::1", 6381));
        }
        var first = defaultSeed ? new RespireEndpoint("localhost") : options.Endpoints[0];
        var attempts = new List<RespireEndpoint>();
        var failures = new List<Exception>();
        var connectionOptions = options.ToConnectionOptions() with
        {
            TestingStreamFactory = (host, port, _) =>
            {
                attempts.Add(new(host, port));
                var failure = new IOException($"Injected connection failure {attempts.Count}.");
                failures.Add(failure);
                return ValueTask.FromException<Stream>(failure);
            },
        };
        await using var primary = RespireConnectionMultiplexer.Create(first.Host, first.Port, options: connectionOptions);
        await using var router = new ClusterRouter(options, primary, connectionOptions);

        var error = await Assert.That(async () =>
            await router.EnsureConnectedAsync(CancellationToken.None, discovery: null, observation: default))
            .ThrowsExactly<RespireConnectionException>();

        RespireEndpoint[] expected = defaultSeed ? [first] : [.. options.Endpoints];
        await Assert.That(attempts.SequenceEqual(expected)).IsTrue();
        foreach (var endpoint in expected)
            await Assert.That(error!.Message).Contains(endpoint.ToString());
        await Assert.That(error!.Message).Contains("network reachability");
        await Assert.That(error.Endpoint).IsNull();
        var causes = error.InnerException is AggregateException aggregate
            ? aggregate.InnerExceptions.ToArray() : [error.InnerException!];
        await Assert.That(causes.Length).IsEqualTo(failures.Count);
        for (var index = 0; index < causes.Length; index++)
        {
            var cause = (RespireConnectionException)causes[index];
            await Assert.That(cause.Endpoint).IsEqualTo((RespireEndpoint?)expected[index]);
            await Assert.That(cause.InnerException).IsSameReferenceAs(failures[index]);
        }
    }

    [Test]
    public async Task CancellationDuringSeedConnectionKeepsCallerTokenAndSkipsRemainingSeeds()
    {
        using var cancellation = new CancellationTokenSource();
        var options = new RespireOptions
        {
            UseCluster = true,
            Endpoints = [new("cache-a.example"), new("cache-b.example")],
        };
        var attempts = 0;
        var connectionOptions = options.ToConnectionOptions() with
        {
            TestingStreamFactory = (_, _, token) =>
            {
                attempts++;
                cancellation.Cancel();
                return ValueTask.FromCanceled<Stream>(token);
            },
        };
        await using var primary = RespireConnectionMultiplexer.Create("cache-a.example", options: connectionOptions);
        await using var router = new ClusterRouter(options, primary, connectionOptions);

        var error = await Assert.That(async () =>
            await router.EnsureConnectedAsync(cancellation.Token, discovery: null, observation: default))
            .Throws<OperationCanceledException>();

        await Assert.That(error!.CancellationToken).IsEqualTo(cancellation.Token);
        await Assert.That(attempts).IsEqualTo(1);
    }
}
