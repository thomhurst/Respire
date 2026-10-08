using Respire.IntegrationTests;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using TUnit.Core.Interfaces;

namespace Respire.SignalR.Tests;

public sealed class SignalRClusterFixture : IAsyncInitializer, IAsyncDisposable
{
    private RedisClusterTestContainer? _cluster;
    public string ConnectionString => $"redis://{_cluster!.Host}:{_cluster.Port(0)}?protocol=3";
    public async Task InitializeAsync() => _cluster = await RedisClusterTestContainer.StartAsync();
    public async ValueTask DisposeAsync()
    {
        if (_cluster is not null) await _cluster.DisposeAsync();
    }
}

[ClassDataSource<SignalRClusterFixture>(Shared = SharedType.PerTestSession)]
[NotInParallel("signalr-integration")]
public class ClusterBackplaneTests(SignalRClusterFixture fixture)
{
    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task ClusterRoutesBroadcastMembershipAndClientResults(bool sharded, bool messagePack)
    {
        var prefix = Guid.NewGuid().ToString("N") + ":";
        await using var first = await BackplaneHost.StartAsync(fixture.ConnectionString, prefix, false, sharded, cluster: true);
        await using var second = await BackplaneHost.StartAsync(fixture.ConnectionString, prefix, false, sharded, cluster: true);
        await using var local = await first.ConnectAsync("local", messagePack);
        await using var remote = await second.ConnectAsync("remote", messagePack);
        await first.Manager.SendAllAsync("message", ["all"]);
        await local.ExpectAsync("all"); await remote.ExpectAsync("all");
        // Distinct hash tags route subscriptions and publishes to every primary.
        foreach (var tag in new[] { "a", "b", "c" })
        {
            var group = "{" + tag + "}";
            await first.Manager.AddToGroupAsync(remote.Id, group);
            await first.Manager.SendGroupAsync(group, "message", [group]);
            await remote.ExpectAsync(group);
            await first.Manager.RemoveFromGroupAsync(remote.Id, group);
        }
        await first.Manager.SendConnectionAsync(remote.Id, "message", ["connection"]);
        await remote.ExpectAsync("connection");
        using var handler = remote.Connection.On("answer", [typeof(string)],
            static (arguments, _) => Task.FromResult<object?>(((string)arguments[0]!).Length), null!);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await Assert.That(await first.Manager.InvokeConnectionAsync<int>(remote.Id, "answer", ["hello"], deadline.Token))
            .IsEqualTo(5);
    }
}
