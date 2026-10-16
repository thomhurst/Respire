using FluentAssertions;
using Respire.Samples.Recipes;
using TUnit.Core;

namespace Respire.IntegrationTests;

[Category(TestCategories.ProtocolIndependent)]
[ClassDataSource<VersionedServerFixture>(Shared = SharedType.PerTestSession)]
public class RecipeIntegrationTests(VersionedServerFixture fixture)
{
    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task LeaderboardRanksUpdatesAndLoadsMetadata(int protocol)
    {
        var server = await fixture.LeaseAsync("redis:8.4-alpine");
        await using var client = await RespireClient.ConnectAsync(server.ConnectionString(protocol));
        var board = new Leaderboard(client);
        (await board.TopAsync(10)).Should().BeEmpty();
        await board.SaveAsync("ada", "Ada", 10);
        await board.SaveAsync("grace", "Grace", 20);
        (await board.TopAsync(1)).Should().Equal(new Leaderboard.Row("grace", "Grace", 20));
        await board.SaveAsync("ada", "Ada Lovelace", 30);
        (await board.TopAsync(10)).Should().Equal(
            new Leaderboard.Row("ada", "Ada Lovelace", 30), new Leaderboard.Row("grace", "Grace", 20));
        await client.Hashes.RemoveAsync(Leaderboard.NamesKey, "ada");
        (await board.TopAsync(1))[0].DisplayName.Should().Be("ada");
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task SessionsRefreshOnlyTheReadFieldAndDoNotResurrectExpiredFields(int protocol)
    {
        var server = await fixture.LeaseAsync("redis:8.4-alpine");
        await using var client = await RespireClient.ConnectAsync(server.ConnectionString(protocol));
        var sessions = new SessionStore(client);
        await sessions.SaveAsync("42", "desktop", "desktop-data", TimeSpan.FromMinutes(1));
        await sessions.SaveAsync("42", "phone", "phone-data", TimeSpan.FromMinutes(1));
        (await sessions.ReadAndRefreshAsync("42", "desktop", TimeSpan.FromMinutes(10)))
            .Should().Be("desktop-data");
        (await client.Hashes.ExpiryAsync(SessionStore.Key("42"), "desktop", CancellationToken.None))
            .TimeToLive.Should().BeGreaterThan(TimeSpan.FromMinutes(9));
        (await client.Hashes.ExpiryAsync(SessionStore.Key("42"), "phone", CancellationToken.None))
            .TimeToLive.Should().BeLessThanOrEqualTo(TimeSpan.FromMinutes(1));

        // Expire deterministically in the past instead of relying on a wall-clock sleep.
        await client.Hashes.ExpireAsync(SessionStore.Key("42"),
            RespireExpiry.At(DateTimeOffset.UnixEpoch.AddSeconds(1)), "phone", CancellationToken.None);
        (await sessions.ReadAndRefreshAsync("42", "phone", TimeSpan.FromMinutes(10))).Should().BeNull();
        (await sessions.ReadAndRefreshAsync("42", "absent", TimeSpan.FromMinutes(10))).Should().BeNull();
        (await sessions.RevokeAsync("42", "desktop")).Should().Be(1);
        (await sessions.ReadAndRefreshAsync("42", "desktop", TimeSpan.FromMinutes(10))).Should().BeNull();
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task OutboxRecoversFailedPublicationBeforeAcknowledging(int protocol)
    {
        var server = await fixture.LeaseAsync("redis:8.4-alpine");
        await using var client = await RespireClient.ConnectAsync(server.ConnectionString(protocol));
        var outbox = new Outbox(client);
        var id = await outbox.SaveOrderAsync("order-42", "paid", "event-42");
        (await outbox.SaveOrderAsync("order-42", "paid", "event-42")).Should().Be(id);
        (await client.Hashes.GetStringAsync(Outbox.StateKey, "order-42")).Should().Be("paid");
        (await outbox.InitializeRelayAsync()).Should().BeTrue();
        (await outbox.InitializeRelayAsync()).Should().BeFalse();
        var failing = () => outbox.RelayPageAsync("crashed", TimeSpan.FromSeconds(30),
            (_, _) => throw new InvalidOperationException("Publisher unavailable"));
        await failing.Should().ThrowAsync<InvalidOperationException>();
        (await client.Streams.PendingSummaryAsync(Outbox.StreamKey, Outbox.Group)).Count.Should().Be(1);

        // Model a dead consumer's idle time without delaying the test.
        await client.Streams.ClaimAsync(new RespireStreamClaimOptions { IdleTime = TimeSpan.FromMinutes(1) },
            Outbox.StreamKey, Outbox.Group, "crashed", TimeSpan.Zero, id);
        List<RespireStreamId> published = [];
        (await outbox.RelayPageAsync("replacement", TimeSpan.FromSeconds(30), (entry, _) =>
        {
            published.Add(entry.Id);
            entry.GetString("_respire.job.id").Should().Be("event-42");
            using var payload = System.Text.Json.JsonDocument.Parse(entry["_respire.job.payload"]!);
            payload.RootElement.GetProperty("OrderId").GetString().Should().Be("order-42");
            payload.RootElement.GetProperty("State").GetString().Should().Be("paid");
            entry.PreviousIdleTime.Should().BeGreaterThanOrEqualTo(TimeSpan.FromMinutes(1));
            return Task.CompletedTask;
        })).Should().Be(1);
        published.Should().Equal(id);
        (await client.Streams.PendingSummaryAsync(Outbox.StreamKey, Outbox.Group)).Count.Should().Be(0);
        (await outbox.RelayPageAsync("replacement", TimeSpan.FromSeconds(30),
            (_, _) => throw new InvalidOperationException("No duplicate expected"))).Should().Be(0);
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task LeaderElectionExcludesContendersAndReleasesAfterFailure(int protocol)
    {
        var server = await fixture.LeaseAsync("redis:8.4-alpine");
        await using var first = await RespireClient.ConnectAsync(server.ConnectionString(protocol));
        await using var second = await RespireClient.ConnectAsync(server.ConnectionString(protocol));
        long previousToken = 0;
        var failing = () => LeaderElection.TryRunAsync(first, async (token, lifetime) =>
        {
            previousToken = token;
            lifetime.IsCancellationRequested.Should().BeFalse();
            (await LeaderElection.TryRunAsync(second,
                (_, _) => throw new InvalidOperationException("Contender must not run"))).Should().BeFalse();
            throw new InvalidOperationException("Leader work failed");
        });
        await failing.Should().ThrowAsync<InvalidOperationException>().WithMessage("Leader work failed");
        (await LeaderElection.TryRunAsync(second, (token, _) =>
        {
            token.Should().BeGreaterThan(previousToken);
            return Task.CompletedTask;
        })).Should().BeTrue();
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task LeaderCancellationReachesWorkAndReleasesTheLease(int protocol)
    {
        var server = await fixture.LeaseAsync("redis:8.4-alpine");
        await using var client = await RespireClient.ConnectAsync(server.ConnectionString(protocol));
        using var stopping = new CancellationTokenSource();
        var cancelled = () => LeaderElection.TryRunAsync(client, (_, lifetime) =>
        {
            stopping.Cancel();
            lifetime.IsCancellationRequested.Should().BeTrue();
            return Task.CompletedTask;
        }, stopping.Token);
        await cancelled.Should().ThrowAsync<OperationCanceledException>();
        (await LeaderElection.TryRunAsync(client, (_, _) => Task.CompletedTask)).Should().BeTrue();
    }

    [Test]
    [Arguments("REDIS_URL", 2)]
    [Arguments("REDIS_URL", 3)]
    [Arguments("VALKEY_URL", 2)]
    [Arguments("VALKEY_URL", 3)]
    public async Task EnvironmentUrlConnectsAndRoundTrips(string variable, int protocol)
    {
        var server = await fixture.LeaseAsync("redis:8.4-alpine");
        var connection = EnvironmentConnection.Resolve(name =>
            name == variable ? server.ConnectionString(protocol) : " ");
        await using var client = await EnvironmentConnection.ConnectAsync(name =>
            name == variable ? server.ConnectionString(protocol) : " ");
        await client.SetAsync("recipe:environment", "connected");
        (await client.GetStringAsync("recipe:environment")).Should().Be("connected");
        EnvironmentConnection.Resolve(name => name == "REDIS_URL" ? connection : "invalid")
            .Should().Be(connection);
        var absent = () => EnvironmentConnection.Resolve(_ => null);
        absent.Should().Throw<InvalidOperationException>();
    }
}
