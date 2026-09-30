using System.Text;
using FluentAssertions;
using Testcontainers.Redis;
using TUnit.Core;
using TUnit.Core.Interfaces;

namespace Respire.IntegrationTests;

// Notifications are configured by the fixture, never by the subscription API.
public sealed class KeyNotificationRedisContainer : IAsyncInitializer, IAsyncDisposable
{
    private readonly RedisContainer _container = new RedisBuilder("redis:8.8-alpine")
        .WithCommand("redis-server", "--databases", "4096", "--notify-keyspace-events", "KEAmoncSTIV")
        .Build();
    public RespireOptions Options(int protocol) => new()
    {
        Endpoints = [new(_container.Hostname, _container.GetMappedPublicPort(6379))],
        Database = TestContext.Current!.Isolation.UniqueId,
        Protocol = (RespProtocol)protocol, Connections = 1, AllowAdmin = true,
    };
    public Task InitializeAsync() => _container.StartAsync();
    public ValueTask DisposeAsync() => _container.DisposeAsync();
}

[ClassDataSource<KeyNotificationRedisContainer>(Shared = SharedType.PerTestSession)]
public class KeyNotificationIntegrationTests(KeyNotificationRedisContainer fixture)
{
    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task StandardFactoriesReceivePhysicalBinaryKeysAcrossDatabaseScopes(int protocol)
    {
        var options = fixture.Options(protocol);
        await using var client = await RespireClient.ConnectAsync(options);
        byte[] prefix = [.. Encoding.UTF8.GetBytes($"tenant:{Guid.NewGuid():N}:"), (byte)'*', (byte)'?', (byte)'[', (byte)']', (byte)'^', (byte)'-', 255, 0];
        byte[] key = [.. prefix, (byte)'x'];
        RespireChannel[] channels =
        [
            RespireChannel.KeySpaceSingleKey(key, options.Database),
            RespireChannel.KeySpacePrefix(prefix, options.Database),
            RespireChannel.KeySpacePrefix(prefix),
            RespireChannel.KeySpacePattern("*", options.Database),
            RespireChannel.KeyEvent(RespireKeyNotificationType.Set, options.Database),
            RespireChannel.KeyEvent(RespireKeyNotificationType.Set),
        ];
        foreach (var descriptor in channels)
        {
            // A prefixed view must not rewrite the descriptor's physical key.
            await using var subscription = await client.WithKeyPrefix("not-applied:").SubscribeAsync(descriptor);
            await client.SetAsync(key, "value");
            var notification = await ReadMatchingAsync(subscription, key, RespireKeyNotificationType.Set);
            notification.Database.Should().Be(options.Database);
            notification.KeyBytes.ToArray().Should().Equal(key);
            subscription.Kind.Should().Be(descriptor.Kind);
        }
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task EverySubKeyLayoutPreservesBinaryFieldsAndMultiFieldEvents(int protocol)
    {
        var options = fixture.Options(protocol);
        await using var client = await RespireClient.ConnectAsync(options);
        byte[] key = [.. Encoding.UTF8.GetBytes($"hash:{Guid.NewGuid():N}:"), 255, 0, (byte)':', (byte)',', (byte)'|'];
        byte[] field = [255, 0, (byte)':', (byte)',', (byte)'|', (byte)'\n'];
        RespireChannel[] descriptors =
        [
            RespireChannel.SubKeySpaceSingleKey(key, options.Database),
            RespireChannel.SubKeySpacePattern("*", options.Database),
            RespireChannel.SubKeySpacePrefix(key),
            RespireChannel.SubKeyEvent(RespireKeyNotificationType.HSet, options.Database),
            RespireChannel.SubKeyEvent(RespireKeyNotificationType.HSet),
            RespireChannel.SubKeySpaceItem(key, field, options.Database),
            RespireChannel.SubKeySpaceEvent(RespireKeyNotificationType.HSet, key, options.Database),
            RespireChannel.SubKeySpaceEvent(RespireKeyNotificationType.HSet, key),
        ];
        foreach (var descriptor in descriptors)
        {
            await using var subscription = await client.SubscribeAsync(descriptor);
            using var reply = await client.ExecuteAsync("HSET", key, field, "one", Array.Empty<byte>(), "two");
            var notification = await ReadMatchingAsync(subscription, key, RespireKeyNotificationType.HSet);
            notification.Database.Should().Be(options.Database);
            var fields = notification.GetSubKeys().ToArray();
            fields[0].ToArray().Should().Equal(field);
            if (notification.Kind == RespireKeyNotificationKind.SubKeySpaceItem) fields.Length.Should().Be(1);
            else
            {
                fields.Length.Should().Be(2);
                fields[1].IsEmpty.Should().BeTrue();
            }
        }
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task SubKeyDeleteTtlPersistAndExpiryDeliverAffectedFields(int protocol)
    {
        var options = fixture.Options(protocol);
        await using var client = await RespireClient.ConnectAsync(options);
        var key = Encoding.UTF8.GetBytes($"fields:{Guid.NewGuid():N}");
        await using var subscription = await client.SubscribeAsync(RespireChannel.SubKeySpaceSingleKey(key, options.Database));
        using (var reply = await client.ExecuteAsync("HSET", key, "a", "1", "b", "2")) { }
        await ReadMatchingAsync(subscription, key, RespireKeyNotificationType.HSet);
        using (var reply = await client.ExecuteAsync("HPEXPIRE", key, "60000", "FIELDS", "2", "a", "b")) { }
        var expires = await ReadMatchingAsync(subscription, key, RespireKeyNotificationType.HExpire);
        expires.GetSubKeys().Count.Should().Be(2);
        using (var reply = await client.ExecuteAsync("HPERSIST", key, "FIELDS", "2", "a", "b")) { }
        (await ReadMatchingAsync(subscription, key, RespireKeyNotificationType.HPersist)).GetSubKeys().Count.Should().Be(2);
        using (var reply = await client.ExecuteAsync("HDEL", key, "a", "b")) { }
        (await ReadMatchingAsync(subscription, key, RespireKeyNotificationType.HDel)).GetSubKeys().Count.Should().Be(2);
        using (var reply = await client.ExecuteAsync("HSET", key, "a", "1")) { }
        await ReadMatchingAsync(subscription, key, RespireKeyNotificationType.HSet);
        using (var reply = await client.ExecuteAsync("HPEXPIRE", key, "10", "FIELDS", "1", "a")) { }
        var expired = await ReadMatchingAsync(subscription, key, RespireKeyNotificationType.HExpired);
        expired.GetSubKeys().FirstOrDefault().Span.SequenceEqual("a"u8).Should().BeTrue();
    }

    [Test]
    [MatrixDataSource]
    public async Task EverySubKeyLayoutReceivesDeleteAndFieldLifetimeEvents(
        [Matrix(2, 3)] int protocol, [Matrix(0, 1, 2, 3)] int layout)
    {
        var options = fixture.Options(protocol);
        await using var client = await RespireClient.ConnectAsync(options);
        var key = Encoding.UTF8.GetBytes($"lifetime:{Guid.NewGuid():N}");
        foreach (var type in new[] { RespireKeyNotificationType.HDel, RespireKeyNotificationType.HExpire,
            RespireKeyNotificationType.HPersist, RespireKeyNotificationType.HExpired })
        {
            using (var reply = await client.ExecuteAsync("DEL", key)) { }
            using (var reply = await client.ExecuteAsync("HSET", key, "a", "1", "b", "2")) { }
            if (type == RespireKeyNotificationType.HPersist)
                using (var reply = await client.ExecuteAsync("HPEXPIRE", key, "60000", "FIELDS", "2", "a", "b")) { }
            var descriptor = layout switch
            {
                0 => RespireChannel.SubKeySpaceSingleKey(key, options.Database),
                1 => RespireChannel.SubKeyEvent(type, options.Database),
                2 => RespireChannel.SubKeySpaceItem(key, "a", options.Database),
                _ => RespireChannel.SubKeySpaceEvent(type, key, options.Database),
            };
            await using var subscription = await client.SubscribeAsync(descriptor);
            switch (type)
            {
                case RespireKeyNotificationType.HDel:
                    using (var reply = await client.ExecuteAsync("HDEL", key, "a", "b")) { }
                    break;
                case RespireKeyNotificationType.HPersist:
                    using (var reply = await client.ExecuteAsync("HPERSIST", key, "FIELDS", "2", "a", "b")) { }
                    break;
                case RespireKeyNotificationType.HExpired:
                    using (var reply = await client.ExecuteAsync("HPEXPIRE", key, "10", "FIELDS", "1", "a")) { }
                    break;
                default:
                    using (var reply = await client.ExecuteAsync("HPEXPIRE", key, "60000", "FIELDS", "2", "a", "b")) { }
                    break;
            }
            var notification = await ReadMatchingAsync(subscription, key, type);
            notification.GetSubKeys().Count.Should().Be(layout == 2 || type == RespireKeyNotificationType.HExpired ? 1 : 2);
            notification.GetSubKeys().FirstOrDefault().Span.SequenceEqual("a"u8).Should().BeTrue();
        }
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task StandardMutationFamiliesAndExpiryAreParsed(int protocol)
    {
        var options = fixture.Options(protocol);
        await using var client = await RespireClient.ConnectAsync(options);
        var key = Encoding.UTF8.GetBytes($"events:{Guid.NewGuid():N}");
        await using var subscription = await client.SubscribeAsync(RespireChannel.KeySpaceSingleKey(key, options.Database));
        foreach (var scenario in new (string Command, RespireValue[] Args, RespireKeyNotificationType Type)[]
        {
            ("SET", [key, "value"], RespireKeyNotificationType.Set),
            ("DEL", [key], RespireKeyNotificationType.Del),
            ("HSET", [key, "field", "value"], RespireKeyNotificationType.HSet),
            ("DEL", [key], RespireKeyNotificationType.Del),
            ("LPUSH", [key, "value"], RespireKeyNotificationType.LPush),
            ("DEL", [key], RespireKeyNotificationType.Del),
            ("SADD", [key, "value"], RespireKeyNotificationType.SAdd),
            ("DEL", [key], RespireKeyNotificationType.Del),
            ("ZADD", [key, "1", "value"], RespireKeyNotificationType.ZAdd),
            ("DEL", [key], RespireKeyNotificationType.Del),
            ("XADD", [key, "*", "field", "value"], RespireKeyNotificationType.XAdd),
            ("DEL", [key], RespireKeyNotificationType.Del),
        })
        {
            using var reply = await client.ExecuteAsync(scenario.Command, scenario.Args);
            await ReadMatchingAsync(subscription, key, scenario.Type);
        }
        using (var reply = await client.ExecuteAsync("SET", key, "expires", "PX", "10")) { }
        await ReadMatchingAsync(subscription, key, RespireKeyNotificationType.Expired);
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task ReservedLookingOrdinaryChannelsRemainPublishable(int protocol)
    {
        var options = fixture.Options(protocol);
        await using var client = await RespireClient.ConnectAsync(options);
        var key = Encoding.UTF8.GetBytes($"manual:{Guid.NewGuid():N}");
        var descriptor = RespireChannel.KeySpaceSingleKey(key, options.Database);
        var ordinary = new RespireChannel(descriptor.Bytes);
        await using var subscription = await client.SubscribeAsync(ordinary);
        (await client.PublishAsync(ordinary, "module.future")).Should().Be(1);
        var notification = await ReadMatchingAsync(subscription, key, RespireKeyNotificationType.Unknown);
        notification.RawType.Span.SequenceEqual("module.future"u8).Should().BeTrue();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var subscribe = async () => await client.SubscribeAsync(descriptor, cancellation.Token);
        await subscribe.Should().ThrowAsync<OperationCanceledException>();
        await subscription.DisposeAsync();
        (await client.PublishAsync(ordinary, "after-disposal")).Should().Be(0);
    }

    private static async Task<RespireKeyNotification> ReadMatchingAsync(RespireSubscription subscription,
        byte[] key, RespireKeyNotificationType type)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await foreach (var message in subscription.WithCancellation(timeout.Token))
        {
            if (message.TryParseKeyNotification(out var notification) && notification.Type == type
                && notification.KeyBytes.Span.SequenceEqual(key)) return notification;
        }
        throw new InvalidOperationException($"Subscription ended before {type} was delivered.");
    }
}

public sealed class KeyNotificationLegacyRedisContainer : IAsyncInitializer, IAsyncDisposable
{
    private readonly RedisContainer _container = new RedisBuilder("redis:7.0.15")
        .WithCommand("redis-server", "--notify-keyspace-events", "KEA").Build();
    public string ConnectionString => $"redis://{_container.Hostname}:{_container.GetMappedPublicPort(6379)}";
    public Task InitializeAsync() => _container.StartAsync();
    public ValueTask DisposeAsync() => _container.DisposeAsync();
}

[ClassDataSource<KeyNotificationLegacyRedisContainer>(Shared = SharedType.PerTestSession)]
public class KeyNotificationLegacyIntegrationTests(KeyNotificationLegacyRedisContainer fixture)
{
    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task OlderServerAcknowledgesSubKeyChannelsWithoutEmittingSubKeyEvents(int protocol)
    {
        await using var client = await RespireClient.ConnectAsync($"{fixture.ConnectionString}?protocol={protocol}");
        var prefix = $"legacy:[^-]\\*?:{Guid.NewGuid():N}:";
        var key = prefix + "key";
        var descriptor = RespireChannel.SubKeySpaceSingleKey(key, 0);
        await using var subkeys = await client.SubscribeAsync(descriptor);
        await using var keys = await client.SubscribeAsync(RespireChannel.KeySpacePrefix(prefix, 0));
        using (var reply = await client.ExecuteAsync("HSET", key, "field", "value")) { }
        // The subsequent ordinary publish is an ordered barrier on the same Pub/Sub
        // connection. No timeout-based absence assertion or retry is needed.
        (await client.PublishAsync(new RespireChannel(descriptor.Bytes), "barrier")).Should().Be(1);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var keyReader = keys.GetAsyncEnumerator(timeout.Token);
        (await keyReader.MoveNextAsync()).Should().BeTrue();
        keyReader.Current.TryParseKeyNotification(out var notification).Should().BeTrue();
        notification.Type.Should().Be(RespireKeyNotificationType.HSet);
        await using var subkeyReader = subkeys.GetAsyncEnumerator(timeout.Token);
        (await subkeyReader.MoveNextAsync()).Should().BeTrue();
        subkeyReader.Current.Text.Should().Be("barrier");
        subkeyReader.Current.TryParseKeyNotification(out _).Should().BeFalse();
    }
}
