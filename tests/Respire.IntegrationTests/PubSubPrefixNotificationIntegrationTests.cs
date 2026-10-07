using System.Text;
using FluentAssertions;
using TUnit.Core;

namespace Respire.IntegrationTests;

[Category(TestCategories.ProtocolIndependent)]
[ClassDataSource<KeyNotificationRedisContainer>(Shared = SharedType.PerTestSession)]
public class PubSubPrefixNotificationIntegrationTests(KeyNotificationRedisContainer fixture)
{
    [Test]
    [MatrixDataSource]
    public async Task NotificationDescriptorsUseTheCompleteKeyNamespace(
        [Matrix(2, 3)] int protocol, [Matrix("literal", "pattern", "event")] string family)
    {
        byte[] keyPrefix = [.. Encoding.UTF8.GetBytes($"keys:{Guid.NewGuid():N}:"), 255, 0, (byte)'*', (byte)':'];
        var options = fixture.Options(protocol) with { KeyPrefix = keyPrefix, PubSubPrefix = "events:" };
        await using var root = await RespireClient.ConnectAsync(options);
        var view = root.WithKeyPrefix("nested:").WithPubSubPrefix("nested:");
        RespireKey logical = new byte[] { 254, 0, (byte)'x' };
        var physical = view.ResolveKey(logical);
        var fullPrefix = view.ResolveKey(RespireKey.Empty).ToBytes();
        var descriptor = family switch
        {
            "literal" => RespireChannel.KeySpaceSingleKey(physical, options.Database),
            "pattern" => RespireChannel.KeySpacePrefix((RespireKey)fullPrefix, options.Database),
            _ => RespireChannel.KeyEvent(RespireKeyNotificationType.Set, options.Database),
        };
        await using var subscription = await view.SubscribeAsync(descriptor);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var reader = subscription.GetAsyncEnumerator(deadline.Token);
        await view.SetAsync(logical, "value", cancellationToken: deadline.Token);
        while (await reader.MoveNextAsync())
        {
            var message = reader.Current;
            if (!message.TryParseKeyNotification(fullPrefix, out var notification)
                || notification.Type != RespireKeyNotificationType.Set) continue;
            notification.KeyBytes.ToArray().Should().Equal(logical.ToBytes());
            notification.Database.Should().Be(options.Database);
            message.Channel.ToString().Should().StartWith(family == "event" ? "__keyevent@" : "__keyspace@");
            subscription.Targets.Single().Should().Be(descriptor);
            subscription.Targets.Single().RoutingSlot.Should().Be(descriptor.RoutingSlot);
            return;
        }
        throw new InvalidOperationException("The notification subscription ended before its SET event.");
    }
}
