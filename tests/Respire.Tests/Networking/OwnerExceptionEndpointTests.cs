using System.Runtime.CompilerServices;
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
    [Arguments(false)]
    [Arguments(true)]
    public async Task ReconnectExhaustionPreservesEndpointScopeThroughObservation(bool multipleEndpoints)
    {
        await using var server = new FakeRespServer(2, FakeRespServer.PongReply);
        var firstEndpoint = new RespireEndpoint("first.example", 6380);
        var secondEndpoint = multipleEndpoints ? new RespireEndpoint("second.example", 6381) : firstEndpoint;
        RespireConnectionException[] failures =
        [
            new("first replacement failed") { Endpoint = firstEndpoint },
            new("second replacement failed") { Endpoint = secondEndpoint },
        ];
        // A test-owned client isolates the endpoint and failure history being asserted.
#pragma warning disable RESP010
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespireProtocol.Resp2, Connections = 2, Endpoints = [new("127.0.0.1", server.Port)],
            ReconnectPolicy = new() { MaxAttempts = 1, InitialDelay = TimeSpan.Zero, JitterRatio = 0 },
        });
#pragma warning restore RESP010
        var multiplexer = client.Core.Multiplexer;
        var first = multiplexer.GetConnection(0);
        var second = multiplexer.GetConnection(1);
        // Seed completed recovery episodes from before/after an endpoint migration.
        // This isolates attribution from the handoff/reconnect scheduling machinery.
        Array.Fill(ReconnectAttempts(multiplexer)!, 1);
        failures.CopyTo(ReconnectErrors(multiplexer)!, 0);
        await first.DisposeAsync();
        await second.DisposeAsync();
        var error = await Assert.That(async () => await client.PingAsync())
            .ThrowsExactly<RespireReconnectLimitException>();
        await Assert.That(error!.Endpoint).IsEqualTo(multipleEndpoints ? null : (RespireEndpoint?)firstEndpoint);
        // A retry can retain the active endpoint from its preceding attempt.
        var owner = DispatchResponseSource<long>.Start();
        var observation = owner.Observation;
        observation.UseEndpoint(multiplexer.ActiveConnectionEndpoint);
        observation.Handled(error!);
        owner.Fail(error!);
        await Assert.That(error!.Endpoint).IsEqualTo(multipleEndpoints ? null : (RespireEndpoint?)firstEndpoint);
        var aggregate = (AggregateException)error.InnerException!;
        await Assert.That(aggregate.InnerExceptions.Count).IsEqualTo(2);
        await Assert.That(aggregate.InnerExceptions.Contains(failures[0])).IsTrue();
        await Assert.That(aggregate.InnerExceptions.Contains(failures[1])).IsTrue();
        await Assert.That(failures[0].Endpoint).IsEqualTo((RespireEndpoint?)firstEndpoint);
        await Assert.That(failures[1].Endpoint).IsEqualTo((RespireEndpoint?)secondEndpoint);
    }

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_reconnectAttempts")]
    private static extern ref int[]? ReconnectAttempts(RespireConnectionMultiplexer multiplexer);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_reconnectErrors")]
    private static extern ref Exception?[]? ReconnectErrors(RespireConnectionMultiplexer multiplexer);

    [Test]
    public async Task CombinedCandidateFailuresAreFlatAndKeepEncounterOrder()
    {
        var first = new IOException("first");
        var second = new IOException("second");
        var third = new IOException("third");
        var fourth = new IOException("fourth");
        var combined = (AggregateException)ConnectionFailures.Combine(
            ConnectionFailures.Combine(first, second), new AggregateException(third, new AggregateException(fourth)))!;
        await Assert.That(combined.InnerExceptions.SequenceEqual([first, second, third, fourth])).IsTrue();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task NearestPreservesExcludedFailureAndAddsLaterCandidatesOnce(bool additionalCandidates)
    {
        var primary = new RespireEndpoint("primary.example");
        RespireEndpoint[] replicas = additionalCandidates ? [new("first.example"), new("second.example")] : [];
        var original = new RespireTimeoutException("GET", TimeSpan.FromSeconds(1)) { Endpoint = primary };
        // A test-owned client isolates the endpoint and failure history being asserted.
#pragma warning disable RESP010
        await using var client = RespireClient.Create(new RespireOptions
        {
            Endpoints = [primary], ReplicaEndpoints = replicas.ToList(),
            TestingStreamFactory = (_, _, _) => ValueTask.FromException<Stream>(new IOException("unavailable")),
        });
#pragma warning restore RESP010
        var attempt = new ReadAttempt();
        attempt.TryAdd(primary, original);
        var router = client.Core.ReadRouter;
        router.FailedReplicaCooldown = TimeSpan.FromMinutes(1);
        var error = await Assert.That(async () => await router.SelectAsync(
            RespireReadFrom.Nearest, default, observation: default, attempt: attempt)).Throws<RespireException>();
        if (!additionalCandidates)
        {
            await Assert.That(error).IsSameReferenceAs(original);
            return;
        }
        var aggregate = (AggregateException)error!.InnerException!;
        await Assert.That(aggregate.InnerExceptions.Count).IsEqualTo(3);
        await Assert.That(aggregate.InnerExceptions[0]).IsSameReferenceAs(original);
        await Assert.That(aggregate.InnerExceptions.Skip(1).Cast<RespireException>()
            .Select(cause => cause.Endpoint!.Value)).IsEquivalentTo(replicas);
    }

    [Test]
    public async Task ClusterRecoveryPublishesOriginalTimeoutAfterSeedFailure()
    {
        var source = new RespireEndpoint("source.example");
        var timeouts = new List<RespireTimeoutException>();
        var options = new RespireOptions
        {
            UseCluster = true, Endpoints = [source, new("fallback.example"), new("unused.example")],
            ReconnectPolicy = new() { MaxAttempts = 1, InitialDelay = TimeSpan.Zero, JitterRatio = 0 },
            TestingStreamFactory = (_, _, _) =>
            {
                var timeout = new RespireTimeoutException("CONNECT", TimeSpan.FromSeconds(1));
                timeouts.Add(timeout);
                return ValueTask.FromException<Stream>(timeout);
            },
        };
        // A test-owned client isolates the endpoint and failure history being asserted.
#pragma warning disable RESP010
        await using var client = RespireClient.Create(options);
#pragma warning restore RESP010
        var router = client.Core.Cluster!;
        var disconnected = new TaskCompletionSource<RespireConnectionStateChange>(TaskCreationOptions.RunContinuationsAsynchronously);
        router.DiscoveryStateChanged += change =>
        {
            if (change.ReconnectExhausted) disconnected.TrySetResult(change);
        };
        var error = await Assert.That(async () => await router.EnsureConnectedAsync(
            default, discovery: null, observation: default)).ThrowsExactly<RespireReconnectLimitException>();
        var notification = await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(notification.Error).IsSameReferenceAs(timeouts[1]);
        await Assert.That(notification.Endpoint).IsEqualTo(options.Endpoints[1]);
        var retained = Causes(error!).OfType<RespireTimeoutException>().ToArray();
        await Assert.That(retained.Length).IsEqualTo(2);
        await Assert.That(retained[0]).IsSameReferenceAs(timeouts[0]);
        await Assert.That(retained[1]).IsSameReferenceAs(timeouts[1]);
        await Assert.That(retained.Select(cause => cause.Endpoint!.Value)).IsEquivalentTo(options.Endpoints.Take(2));
    }

    [Test]
    public async Task EmptySentinelReplicaDiscoveryHasNoInventedCauseOrEndpoint()
    {
        var error = await Assert.That(async () => await SentinelResolver.DiscoverReplicaEndpointsAsync(
            SentinelOptions(), [], default)).ThrowsExactly<RespireConnectionException>();
        await Assert.That(error!.InnerException).IsNull();
        await Assert.That(error.Endpoint).IsNull();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task MalformedSentinelPrimaryAddressNamesTheReportingEndpoint(bool nonStringField)
    {
        await using var sentinel = new FakeRespServer(nonStringField
            ? "*2\r\n:123\r\n$4\r\n6379\r\n"u8.ToArray() : FakeRespServer.OkReply);
        var options = SentinelOptions(sentinel.Port);
        var error = await Assert.That(async () => await SentinelResolver.ResolveAndConnectPrimaryAsync<object>(
            options, static (_, _, _) => throw new InvalidOperationException("No primary was reported."), default))
            .ThrowsExactly<RespireConnectionException>();
        var malformed = Causes(error!).OfType<RespireProtocolException>().Single();
        await Assert.That(malformed.Endpoint).IsEqualTo((RespireEndpoint?)options.Endpoints[0]);
    }

    [Test]
    public async Task CachedCorrectionRejectionCreatesSeparateExceptionsForLaterCallers()
    {
        await using var server = new FakeRespServer(4, FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => command == "CLIENT ID" ? ":42\r\n"u8.ToArray()
                : command.StartsWith("CLIENT KILL", StringComparison.Ordinal) ? "-NOPERM fence denied\r\n"u8.ToArray() : null,
        };
        // A test-owned client isolates the endpoint and failure history being asserted.
#pragma warning disable RESP010
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespireProtocol.Resp2, Connections = 1, Endpoints = [new("127.0.0.1", server.Port)],
        });
#pragma warning restore RESP010
        var first = await Assert.That(async () => await client.Core.Multiplexer.EnsureReliableCorrectionOrderingAsync())
            .ThrowsExactly<RespireServerException>();
        var second = await Assert.That(async () => await client.Core.Multiplexer.EnsureReliableCorrectionOrderingAsync())
            .ThrowsExactly<RespireServerException>();
        var third = await Assert.That(async () => await client.Core.Multiplexer.EnsureReliableCorrectionOrderingAsync())
            .ThrowsExactly<RespireServerException>();
        await Assert.That(ReferenceEquals(first, second) || ReferenceEquals(second, third)).IsFalse();
        await Assert.That(second!.Message).IsEqualTo(first!.Message);
        await Assert.That(third!.Endpoint).IsEqualTo(first.Endpoint);
        await Assert.That(third.CommandName).IsEqualTo(first.CommandName);
    }

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
        // A test-owned client isolates the endpoint and failure history being asserted.
#pragma warning disable RESP010
        await using var client = RespireClient.Create(new RespireOptions
        {
            Protocol = RespireProtocol.Resp2,
            Connections = 1,
            Endpoints = [new("127.0.0.1", first.Port)],
            ReplicaEndpoints = endpoints,
        });
#pragma warning restore RESP010
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
            Protocol = RespireProtocol.Resp2, UseCluster = true, Connections = 1,
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
        // A test-owned client isolates the endpoint and failure history being asserted.
#pragma warning disable RESP010
        await using var client = RespireClient.Create(new RespireOptions { UseCluster = true, Endpoints = [source] });
#pragma warning restore RESP010
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
        Protocol = RespireProtocol.Resp2,
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
