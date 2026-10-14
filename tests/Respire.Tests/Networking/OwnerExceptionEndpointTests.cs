using System.Text;
using Respire.Commands;
using Respire.Infrastructure;
using Respire.Internal;
using Respire.Networking;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class OwnerExceptionEndpointTests
{
    [Test]
    public async Task CandidateContextPreservesKnownOriginsAggregatesAndUnsentMarkers()
    {
        var source = new RespireEndpoint("source.example", 6379);
        var target = new RespireEndpoint("target.example", 6380);
        var rejection = new RespireServerException("MOVED 1 target.example:6380") { Endpoint = source };
        await Assert.That(ConnectionFailures.AtEndpoint(rejection, target)).IsSameReferenceAs(rejection);
        var aggregate = new RespireConnectionException("Several failed", new AggregateException(rejection));
        await Assert.That(ConnectionFailures.AtEndpoint(aggregate, target)).IsSameReferenceAs(aggregate);
        await Assert.That(aggregate.Endpoint).IsNull();
        var unsent = new RespireConnectionException("not submitted") { IsCommandNotSubmitted = true };
        var contextual = (RespireConnectionException)ConnectionFailures.AtEndpoint(unsent, source);
        await Assert.That(contextual.Endpoint).IsEqualTo((RespireEndpoint?)source);
        await Assert.That(contextual.IsCommandNotSubmitted).IsTrue();
        await Assert.That(contextual.InnerException).IsSameReferenceAs(unsent);
    }

    [Test]
    [Arguments(1)]
    [Arguments(2)]
    public async Task UninitializedOwnerNamesItsEndpointAndPublicStartupApi(int connections)
    {
        var endpoint = new RespireEndpoint("redis.example", 6380);
        await using var owner = RespireConnectionMultiplexer.Create(endpoint.Host, endpoint.Port, connections);
        var error = await Assert.That(() => owner.GetConnection()).ThrowsExactly<RespireConnectionException>();
        await Assert.That(error!.Endpoint).IsEqualTo((RespireEndpoint?)endpoint);
        await Assert.That(error.Message).Contains("RespireClient.ConnectAsync");
    }

    [Test]
    [Arguments(1)]
    [Arguments(2)]
    public async Task ReplicaSelectionRetainsEachRoleRejectionWithoutAssigningAnAggregateEndpoint(int candidates)
    {
        await using var first = new FakeRespServer("-NOPERM first ROLE denied\r\n"u8.ToArray());
        await using var second = new FakeRespServer("-NOPERM second ROLE denied\r\n"u8.ToArray());
        RespireEndpoint[] endpoints = candidates == 1
            ? [new("127.0.0.1", first.Port)] : [new("127.0.0.1", first.Port), new("127.0.0.1", second.Port)];
        await using var client = RespireClient.Create(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            Connections = 1,
            Endpoints = [new("127.0.0.1", first.Port)],
            ReplicaEndpoints = endpoints,
        });
        var error = await Assert.That(async () => await client.WithReadFrom(RespireReadFrom.Replica).GetStringAsync("key"))
            .ThrowsExactly<RespireConnectionException>();
        await Assert.That(error!.Endpoint).IsNull();
        var rejections = Causes(error).OfType<RespireServerException>().ToArray();
        await Assert.That(rejections.Length).IsEqualTo(candidates);
        await Assert.That(rejections.Select(cause => cause.Endpoint!.Value)).IsEquivalentTo(endpoints);
        foreach (var rejection in rejections)
        {
            await Assert.That(rejection.CommandName).IsEqualTo("ROLE");
            var wrapper = Causes(error).OfType<RespireConnectionException>()
                .Single(cause => ReferenceEquals(cause.InnerException, rejection));
            await Assert.That(wrapper.Endpoint).IsEqualTo(rejection.Endpoint);
        }
    }

    [Test]
    public async Task SentinelDiscoveryRetainsBothReportingEndpoints()
    {
        await using var first = new FakeRespServer("-ERR first sentinel\r\n"u8.ToArray());
        await using var second = new FakeRespServer("-ERR second sentinel\r\n"u8.ToArray());
        var options = SentinelOptions(first.Port, second.Port);
        var error = await Assert.That(async () => await SentinelResolver.ResolveAndConnectPrimaryAsync<object>(
            options, static (_, _, _) => throw new InvalidOperationException("No primary was reported."), default))
            .ThrowsExactly<RespireConnectionException>();
        await Assert.That(error!.Endpoint).IsNull();
        var errors = Causes(error).OfType<RespireServerException>().ToArray();
        await Assert.That(errors.Length).IsEqualTo(2);
        await Assert.That(errors.Select(cause => cause.Endpoint!.Value)).IsEquivalentTo(options.Endpoints);
    }

    [Test]
    public async Task SentinelReplicaDiscoveryRetainsMalformedRepliesAndServerErrors()
    {
        await using var first = new FakeRespServer("-NOPERM replicas denied\r\n"u8.ToArray());
        await using var second = new FakeRespServer(FakeRespServer.OkReply);
        var options = SentinelOptions(first.Port, second.Port);
        var error = await Assert.That(async () => await SentinelResolver.DiscoverReplicaEndpointsAsync(options, options.Endpoints, default))
            .ThrowsExactly<RespireConnectionException>();
        await Assert.That(error!.Endpoint).IsNull();
        await Assert.That(Causes(error).OfType<RespireServerException>().Single().Endpoint)
            .IsEqualTo((RespireEndpoint?)options.Endpoints[0]);
        await Assert.That(Causes(error).OfType<RespireProtocolException>().Single().Endpoint)
            .IsEqualTo((RespireEndpoint?)options.Endpoints[1]);
    }

    [Test]
    public async Task SentinelRoleFailureNamesDataCandidateRatherThanReporter()
    {
        await using var primary = new FakeRespServer("-NOPERM ROLE denied\r\n"u8.ToArray());
        var port = primary.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        await using var sentinel = new FakeRespServer(Encoding.ASCII.GetBytes($"*2\r\n$9\r\n127.0.0.1\r\n${port.Length}\r\n{port}\r\n"));
        var error = await Assert.That(async () => await RespireClient.ConnectAsync(SentinelOptions(sentinel.Port)))
            .ThrowsExactly<RespireConnectionException>();
        await Assert.That(error!.Endpoint).IsNull();
        var rejection = Causes(error).OfType<RespireServerException>().Single();
        await Assert.That(rejection.Endpoint).IsEqualTo((RespireEndpoint?)new RespireEndpoint("127.0.0.1", primary.Port));
        await Assert.That(rejection.CommandName).IsEqualTo("ROLE");
    }

    [Test]
    public async Task PinnedAskingFinalErrorNamesRespondingServerRatherThanRedirectTarget()
    {
        await using var server = new FakeRespServer(FakeRespServer.OkReply, "-MOVED 1 other.example:6380\r\n"u8.ToArray());
        await using var connection = await RespireConnection.ConnectAsync("127.0.0.1", server.Port);
        var error = await Assert.That(async () =>
        {
            using var reply = await ClusterRouter.SendAskingAsync(connection, new Cmd1(Verbs.Get, "key"), default, "GET", pinToConnection: true);
        }).ThrowsExactly<RespireServerException>();
        await Assert.That(error!.Endpoint).IsEqualTo((RespireEndpoint?)new RespireEndpoint("127.0.0.1", server.Port));
        await Assert.That(error.Code).IsEqualTo("MOVED");
    }

    [Test]
    public async Task ClusterSeedFailureRetainsEachCandidateAndAuthenticationCause()
    {
        await using var first = new FakeRespServer("-ERR first authentication failed\r\n"u8.ToArray());
        await using var second = new FakeRespServer("-ERR second authentication failed\r\n"u8.ToArray());
        RespireEndpoint[] endpoints = [new("127.0.0.1", first.Port), new("127.0.0.1", second.Port)];
        var error = await Assert.That(async () => await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, UseCluster = true, Connections = 1,
            Endpoints = endpoints, Password = "test-only",
        })).ThrowsExactly<RespireConnectionException>();
        await Assert.That(error!.Endpoint).IsNull();
        await Assert.That(Causes(error).OfType<RespireException>()
            .Where(cause => cause.Endpoint is not null).Select(cause => cause.Endpoint!.Value).Distinct())
            .IsEquivalentTo(endpoints);
        await Assert.That(Causes(error).OfType<RespireServerException>().Count()).IsEqualTo(2);
    }

    [Test]
    [Arguments(1)]
    [Arguments(2)]
    public async Task SentinelTimeoutIdentifiesOneCandidateButNeverAttributesSeveralToTheLast(int candidates)
    {
        await using var first = new FakeRespServer { SuppressReply = _ => true };
        await using var second = new FakeRespServer { SuppressReply = _ => true };
        var options = (candidates == 1 ? SentinelOptions(first.Port) : SentinelOptions(first.Port, second.Port))
            with { CommandTimeout = TimeSpan.FromMilliseconds(100) };
        var error = await Assert.That(async () => await SentinelResolver.ResolveAndConnectPrimaryAsync<object>(
            options, static (_, _, _) => throw new InvalidOperationException("No primary was reported."), default))
            .ThrowsExactly<RespireTimeoutException>();
        await Assert.That(error!.Endpoint).IsEqualTo(candidates == 1 ? options.Endpoints[0] : (RespireEndpoint?)null);
        await Assert.That(error.Diagnostics.Stage).IsEqualTo(RespireCommandStage.Connecting);
        await Assert.That(Causes(error).OfType<RespireTimeoutException>()
            .Where(cause => cause.Endpoint is not null).Select(cause => cause.Endpoint!.Value).Distinct())
            .IsEquivalentTo(options.Endpoints);
    }

    [Test]
    public async Task ClusterRecoveryLimitRetainsRejectingServerRatherThanRedirectDestination()
    {
        var source = new RespireEndpoint("source.example", 6379);
        var target = new RespireEndpoint("target.example", 6380);
        await using var client = RespireClient.Create(new RespireOptions { UseCluster = true, Endpoints = [source] });
        var round = new ClusterRouter.DiscoveryRound(client.Core.Cluster!, new() { MaxAttempts = 0 });
        var rejection = new RespireServerException("MOVED 1 target.example:6380", "GET") { Endpoint = source };
        try
        {
            round.Rejected(source, rejection);
            var error = await Assert.That(async () => await round.BeforeCandidateAsync(target, default))
                .ThrowsExactly<RespireReconnectLimitException>();
            await Assert.That(error!.Endpoint).IsNull();
            await Assert.That(error.InnerException).IsSameReferenceAs(rejection);
            await Assert.That(rejection.Endpoint).IsEqualTo((RespireEndpoint?)source);
        }
        finally { round.Finish(); }
    }

    private static RespireOptions SentinelOptions(params int[] ports) => new()
    {
        Protocol = RespProtocol.Resp2,
        Connections = 1,
        SentinelPrimaryName = "mymaster",
        Endpoints = ports.Select(port => new RespireEndpoint("127.0.0.1", port)).ToList(),
        ConnectTimeout = TimeSpan.FromSeconds(2),
    };

    private static IEnumerable<Exception> Causes(Exception error)
    {
        yield return error;
        var causes = error is AggregateException aggregate ? aggregate.InnerExceptions
            : error.InnerException is { } inner ? (IEnumerable<Exception>)[inner] : [];
        foreach (var cause in causes)
            foreach (var nested in Causes(cause)) yield return nested;
    }
}
