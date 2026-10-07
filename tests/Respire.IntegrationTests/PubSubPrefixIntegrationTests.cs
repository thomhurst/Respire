using System.Text;
using FluentAssertions;
using TUnit.Core;

namespace Respire.IntegrationTests;

[Category(TestCategories.ProtocolIndependent)]
[ClassDataSource<RedisTestContainer>(Shared = SharedType.PerTestSession)]
public class PubSubPrefixIntegrationTests(RedisTestContainer fixture)
{
    [Test]
    [MatrixDataSource]
    public async Task LiteralPatternAndShardedViewsShareOnlyPhysicalRoutes(
        [Matrix(2, 3)] int protocol,
        [Matrix(SubscriptionKind.Channel, SubscriptionKind.Pattern, SubscriptionKind.Sharded)] SubscriptionKind kind)
    {
        byte[] prefix = [.. Encoding.UTF8.GetBytes($"events:{Guid.NewGuid():N}:"), 255, 0,
            (byte)'*', (byte)'?', (byte)'[', (byte)']', (byte)'\\', (byte)':'];
        var options = RespireOptions.Parse(fixture.ConnectionString) with { Protocol = (RespProtocol)protocol };
        await using var root = await RespireClient.ConnectAsync(options with { KeyPrefix = "configured-keys:", PubSubPrefix = prefix });
        await using var physicalClient = await RespireClient.ConnectAsync(options with { Database = RedisTestContainer.ScratchDatabase });
        var view = root.WithKeyPrefix("nested:").WithPubSubPrefix("nested:");
        var logical = (RespireChannel)"item";
        var target = kind switch
        {
            SubscriptionKind.Pattern => RespireChannel.Pattern("i*"),
            SubscriptionKind.Sharded => RespireChannel.Sharded(logical),
            _ => logical,
        };
        var physical = view.ResolveChannel(logical);
        var physicalTarget = view.ResolveChannel(target);
        prefix[0] = 1;
        await using var first = await view.SubscribeAsync(target);
        await using var duplicate = await root.WithPubSubPrefix("nested:").SubscribeAsync(target);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var firstReader = first.GetAsyncEnumerator(deadline.Token);
        await using var duplicateReader = duplicate.GetAsyncEnumerator(deadline.Token);
        (await Publish(physicalClient, logical, "outside")).Should().Be(0);
        (await Publish(view, logical, "inside")).Should().Be(1);
        (await firstReader.MoveNextAsync()).Should().BeTrue();
        (await duplicateReader.MoveNextAsync()).Should().BeTrue();
        firstReader.Current.Channel.Should().Be(physical);
        duplicateReader.Current.Channel.Should().Be(physical);
        firstReader.Current.Text.Should().Be("inside");
        firstReader.Current.Pattern.Should().Be(kind == SubscriptionKind.Pattern ? physicalTarget : (RespireChannel?)null);
        first.Targets.Should().Equal(physicalTarget);
        view.ResolveKey("item").Should().Be((RespireKey)"configured-keys:nested:item");
        var physicalCounts = await view.Server.PubSubSubscriberCountsAsync([physical], sharded: kind == SubscriptionKind.Sharded);
        if (kind != SubscriptionKind.Pattern) physicalCounts.Single().Subscribers.Should().Be(1);
        await first.DisposeAsync();
        await view.DisposeAsync();
        (await Publish(physicalClient, physical, "retained")).Should().Be(1);
        (await duplicateReader.MoveNextAsync()).Should().BeTrue();
        duplicateReader.Current.Channel.Should().Be(physical);
        duplicateReader.Current.Text.Should().Be("retained");
        await duplicate.DisposeAsync();
        (await Publish(physicalClient, physical, "removed")).Should().Be(0);
        (await root.PingAsync()).Should().BeGreaterThanOrEqualTo(TimeSpan.Zero);

        ValueTask<long> Publish(IRespireClient client, RespireChannel channel, string value)
            => kind == SubscriptionKind.Sharded ? client.PublishShardedAsync(channel, value, deadline.Token)
                : client.PublishAsync(channel, value, deadline.Token);
    }

    [Test]
    [MatrixDataSource]
    public async Task ReconnectPreservesPrefixedRoutesAndPhysicalGapIdentity(
        [Matrix(2, 3)] int protocol,
        [Matrix(SubscriptionKind.Channel, SubscriptionKind.Pattern, SubscriptionKind.Sharded)] SubscriptionKind kind)
    {
        var clientName = $"prefix-reconnect-{Guid.NewGuid():N}";
        var options = RespireOptions.Parse(fixture.ConnectionString) with { Protocol = (RespProtocol)protocol, ClientName = clientName };
        await using var root = await RespireClient.ConnectAsync(options);
        await using var publisher = await RespireClient.ConnectAsync(options with { ClientName = null });
        byte[] prefix = [.. Encoding.UTF8.GetBytes($"{clientName}:"), 255, 0, (byte)'*', (byte)':'];
        var view = root.WithPubSubPrefix((RespireKey)prefix);
        var logical = (RespireChannel)"item";
        var target = kind switch
        {
            SubscriptionKind.Pattern => RespireChannel.Pattern("i*"),
            SubscriptionKind.Sharded => RespireChannel.Sharded(logical),
            _ => logical,
        };
        var physical = view.ResolveChannel(logical);
        await using var subscription = await view.SubscribeAsync(target);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var reader = subscription.GetAsyncEnumerator(deadline.Token);
        (await Publish("before")).Should().Be(1);
        (await reader.MoveNextAsync()).Should().BeTrue();
        reader.Current.Channel.Should().Be(physical);
        using var clients = await publisher.ExecuteAsync("CLIENT LIST");
        var verb = kind switch { SubscriptionKind.Pattern => "psubscribe", SubscriptionKind.Sharded => "ssubscribe", _ => "subscribe" };
        var line = clients.AsString().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Single(value => value.Contains($"name={clientName}", StringComparison.Ordinal) && value.Contains($"cmd={verb}", StringComparison.Ordinal));
        var id = line.Split(' ').Single(value => value.StartsWith("id=", StringComparison.Ordinal))[3..];
        using (var killed = await publisher.ExecuteAsync("CLIENT KILL", ["ID", id], cancellationToken: deadline.Token))
            killed.AsInteger().Should().Be(1);
        while (await Publish("after") == 0) await Task.Delay(10, deadline.Token);
        (await reader.MoveNextAsync()).Should().BeTrue();
        reader.Current.Kind.Should().Be(RespireMessageKind.Gap);
        reader.Current.Channel.Bytes.IsEmpty.Should().BeTrue();
        reader.Current.Pattern.Should().BeNull();
        reader.Current.Gap!.Reason.Should().Be(RespireSubscriptionGapReason.Reconnect);
        (await reader.MoveNextAsync()).Should().BeTrue();
        reader.Current.Channel.Should().Be(physical);
        reader.Current.Pattern.Should().Be(kind == SubscriptionKind.Pattern ? view.ResolveChannel(target) : (RespireChannel?)null);
        reader.Current.Text.Should().Be("after");
        subscription.Targets.Should().Equal(view.ResolveChannel(target));
        await subscription.DisposeAsync();
        (await Publish("removed")).Should().Be(0);

        ValueTask<long> Publish(string value)
            => kind == SubscriptionKind.Sharded ? publisher.PublishShardedAsync(physical, value, deadline.Token)
                : publisher.PublishAsync(physical, value, deadline.Token);
    }
}
