using Testcontainers.Redis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using TUnit.Core.Enums;
using TUnit.Core.Interfaces;

namespace Respire.IntegrationTests;

[Category(TestCategories.ProtocolIndependent)]
[RunOn(OS.Linux)]
[ClassDataSource<UnixSocketRedisFixture>(Shared = SharedType.PerTestSession)]
public class UnixSocketIntegrationTests(UnixSocketRedisFixture fixture)
{
    [Test]
    [Arguments(RespProtocol.Resp2)]
    [Arguments(RespProtocol.Resp3)]
    public async Task CommandsAndBlockingPoolUseSocket(RespProtocol protocol)
    {
        await using var client = await RespireClient.ConnectAsync(fixture.Options(protocol));
        await using var control = await RespireClient.ConnectAsync(fixture.TcpOptions);
        var key = $"uds:list:{Guid.NewGuid():N}";
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var pending = client.Lists.LeftPopAsync(key, TimeSpan.FromSeconds(5), timeout.Token).AsTask();
        using var pong = await client.ExecuteAsync("PING");
        await Assert.That(pong.AsString()).IsEqualTo("PONG");
        await control.Lists.RightPushAsync(key, "payload");
        await Assert.That(await pending.WaitAsync(timeout.Token)).IsEqualTo("payload");
        await client.SetAsync(key + ":value", "over-socket");
        await Assert.That(await control.GetStringAsync(key + ":value")).IsEqualTo("over-socket");
    }

    [Test]
    [Arguments(RespProtocol.Resp2)]
    [Arguments(RespProtocol.Resp3)]
    public async Task SubscriptionUsesSocket(RespProtocol protocol)
    {
        await using var client = await RespireClient.ConnectAsync(fixture.Options(protocol));
        var channel = $"uds:channel:{Guid.NewGuid():N}";
        await using var subscription = await client.SubscribeAsync(channel);
        await using var messages = subscription.GetAsyncEnumerator();
        var received = messages.MoveNextAsync().AsTask();
        await Assert.That(await client.PublishAsync(channel, "message")).IsEqualTo(1);
        await Assert.That(await received.WaitAsync(TimeSpan.FromSeconds(5))).IsTrue();
        await Assert.That(messages.Current.Text).IsEqualTo("message");
    }

    [Test]
    public async Task TrackingInvalidatesCachedReadsOverSocket()
    {
        await using var client = await RespireClient.ConnectAsync(fixture.Options(RespProtocol.Resp3) with
        {
            ClientSideCache = new(),
        });
        await using var control = await RespireClient.ConnectAsync(fixture.TcpOptions);
        var key = $"uds:cache:{Guid.NewGuid():N}";
        await control.SetAsync(key, "before");
        await Assert.That(await client.GetStringAsync(key)).IsEqualTo("before");
        await Assert.That(await client.GetStringAsync(key)).IsEqualTo("before");
        await Assert.That(client.ClientSideCache!.GetStatistics().Hits).IsGreaterThan(0);
        await control.SetAsync(key, "after");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (client.ClientSideCache.Count != 0) await Task.Delay(10, timeout.Token);
        await Assert.That(await client.GetStringAsync(key)).IsEqualTo("after");
    }

    [Test]
    public async Task ReconnectRetainsSocketPath()
    {
        await using var client = await RespireClient.ConnectAsync(fixture.Options(RespProtocol.Resp3));
        await using var control = await RespireClient.ConnectAsync(fixture.TcpOptions);
        using var initial = await client.ExecuteAsync("CLIENT", "ID");
        var oldId = initial.AsInteger();
        using var killed = await control.ExecuteAsync("CLIENT", "KILL", "ID", oldId);
        await Assert.That(killed.AsInteger()).IsEqualTo(1);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            timeout.Token.ThrowIfCancellationRequested();
            try
            {
                using var current = await client.ExecuteAsync("CLIENT", ["ID"], cancellationToken: timeout.Token);
                if (current.AsInteger() != oldId) break;
            }
            catch (RespireConnectionException) { }
            await Task.Delay(10, timeout.Token);
        }
        using var info = await client.ExecuteAsync("CLIENT", "INFO");
        await Assert.That(info.AsString()).Contains("/sockets/redis.sock");
    }
}

public sealed class UnixSocketRedisFixture : IAsyncInitializer, IAsyncDisposable
{
    private RedisContainer? _container;
    private string? _directory;

    public RespireOptions Options(RespProtocol protocol) => new()
    {
        Endpoints = [RespireEndpoint.UnixSocket(Path.Combine(_directory!, "redis.sock"))],
        Protocol = protocol,
        AllowAdmin = true,
        // These TCP settings must never be applied to an AF_UNIX socket.
        TcpKeepAliveTime = TimeSpan.FromSeconds(5),
        TcpKeepAliveInterval = TimeSpan.FromSeconds(1),
        TcpKeepAliveRetryCount = 3,
    };

    public RespireOptions TcpOptions => new()
    {
        Endpoints = [new(_container!.Hostname, _container.GetMappedPublicPort(6379))],
        AllowAdmin = true,
    };

    public async Task InitializeAsync()
    {
        if (!OperatingSystem.IsLinux()) return;
        _directory = Path.Combine(Path.GetTempPath(), "respire-uds-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        // The image runs Redis as its own UID; both it and the host test need this disposable directory.
        File.SetUnixFileMode(_directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute);
        _container = new RedisBuilder("redis:7.4.11-alpine")
            .WithBindMount(_directory, "/sockets")
            .WithCommand("redis-server", "--unixsocket", "/sockets/redis.sock", "--unixsocketperm", "777")
            .Build();
        try { await _container.StartAsync(); }
        catch { await DisposeAsync(); throw; }
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
            _container = null;
        }
        if (_directory is not null)
        {
            File.Delete(Path.Combine(_directory, "redis.sock"));
            Directory.Delete(_directory);
            _directory = null;
        }
        GC.SuppressFinalize(this);
    }
}
