// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Respire.DependencyInjection;
using StackExchange.Redis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.SignalR.Tests;

// Scenarios adapted from Microsoft's MIT-licensed backplane functional tests.
// dotnet/aspnetcore v8.0.31: src/SignalR/server/Specification.Tests/src/ScaleoutHubLifetimeManagerTests.cs.
// Each case runs two real SignalR servers and multiple clients over an owned Redis server.
[ClassDataSource<RedisTestContainer>(Shared = SharedType.PerTestSession)]
[NotInParallel("signalr-integration")]
public class BackplaneIntegrationTests(RedisTestContainer fixture)
{
    [Test]
    public async Task LocalConnectionSendBypassesRedis()
    {
        var prefix = Guid.NewGuid().ToString("N");
        await using var host = await BackplaneHost.StartAsync(fixture.ConnectionString, prefix, false, false);
        await using var local = await host.ConnectAsync("local", false);
        await using var observer = await RespireClient.ConnectAsync(fixture.ConnectionString);
        var channel = prefix + typeof(BackplaneTestHub).FullName + ":connection:" + local.Id;
        await using var subscription = await observer.SubscribeAsync(channel);
        await host.Manager.SendConnectionAsync(local.Id, "message", ["local"]);
        await local.ExpectAsync("local");
        await observer.PublishAsync(channel, new byte[] { 42 });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var enumerator = subscription.GetAsyncEnumerator(deadline.Token);
        await Assert.That(await enumerator.MoveNextAsync()).IsTrue();
        await Assert.That(enumerator.Current.Payload.Span[0]).IsEqualTo((byte)42);
    }

    [Test]
    public async Task GroupNamesRemainCaseSensitiveAndRemovingMissingMembershipDoesNothing()
    {
        var prefix = Guid.NewGuid().ToString("N");
        await using var first = await BackplaneHost.StartAsync(fixture.ConnectionString, prefix, false, false);
        await using var second = await BackplaneHost.StartAsync(fixture.ConnectionString, prefix, false, false);
        await using var remote = await second.ConnectAsync("remote", false);
        await first.Manager.RemoveFromGroupAsync(remote.Id, "missing");
        await first.Manager.AddToGroupAsync(remote.Id, "Case");
        await first.Manager.AddToGroupAsync(remote.Id, "case");
        await first.Manager.SendGroupsAsync(["Case", "case"], "message", ["both"]);
        await remote.ExpectAsync("both"); await remote.ExpectAsync("both");
        await first.Manager.RemoveFromGroupAsync(remote.Id, "Case");
        await first.Manager.SendGroupAsync("case", "message", ["remaining"]);
        await remote.ExpectAsync("remaining");
    }

    [Test]
    public async Task ClientResultsFromDifferentServersHaveDistinctOwners()
    {
        var prefix = Guid.NewGuid().ToString("N");
        await using var first = await BackplaneHost.StartAsync(fixture.ConnectionString, prefix, false, false);
        await using var second = await BackplaneHost.StartAsync(fixture.ConnectionString, prefix, false, false);
        await using var remote = await second.ConnectAsync("remote", false);
        using var handler = remote.Connection.On("answer", [typeof(int)], static (args, _) => Task.FromResult(args[0]), null!);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var outside = first.Manager.InvokeConnectionAsync<int>(remote.Id, "answer", [1], deadline.Token);
        var inside = second.Manager.InvokeConnectionAsync<int>(remote.Id, "answer", [2], deadline.Token);
        await Assert.That(await outside).IsEqualTo(1);
        await Assert.That(await inside).IsEqualTo(2);
    }

    [Test]
    public async Task DisconnectForwardsPendingClientResultError()
    {
        var prefix = Guid.NewGuid().ToString("N");
        await using var first = await BackplaneHost.StartAsync(fixture.ConnectionString, prefix, false, false);
        await using var second = await BackplaneHost.StartAsync(fixture.ConnectionString, prefix, false, false);
        await using var remote = await second.ConnectAsync("remote", false);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = remote.Connection.On("pending", [], (_, _) => { entered.TrySetResult(); return release.Task; }, null!);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var pending = first.Manager.InvokeConnectionAsync<int>(remote.Id, "pending", [], deadline.Token);
        try
        {
            await entered.Task.WaitAsync(deadline.Token);
            await remote.Connection.SendAsync("Disconnect", deadline.Token);
            await Assert.That(async () => await pending).ThrowsExactly<HubException>();
        }
        finally { release.TrySetResult(1); }
    }

    [Test]
    [Arguments(false, false, false, false)]
    [Arguments(false, false, false, true)]
    [Arguments(false, true, false, false)]
    [Arguments(false, true, false, true)]
    [Arguments(true, false, false, false)]
    [Arguments(true, false, false, true)]
    [Arguments(false, false, true, false)]
    [Arguments(false, false, true, true)]
    public async Task BroadcastConnectionGroupAndUserRouting(bool firstMicrosoft, bool secondMicrosoft,
        bool sharded, bool messagePack)
    {
        var prefix = Guid.NewGuid().ToString("N") + ":";
        await using var first = await BackplaneHost.StartAsync(fixture.ConnectionString, prefix, firstMicrosoft, sharded);
        await using var second = await BackplaneHost.StartAsync(fixture.ConnectionString, prefix, secondMicrosoft, sharded);
        await using var local = await first.ConnectAsync("alice", messagePack);
        await using var remote = await second.ConnectAsync("alice", messagePack);
        await using var other = await second.ConnectAsync("bob", messagePack);
        var sender = first.Manager;

        await sender.SendAllAsync("message", ["all"]);
        await local.ExpectAsync("all"); await remote.ExpectAsync("all"); await other.ExpectAsync("all");
        await sender.SendAllExceptAsync("message", ["except"], [local.Id]);
        await remote.ExpectAsync("except"); await other.ExpectAsync("except");
        await sender.SendConnectionAsync(remote.Id, "message", ["connection"]);
        await remote.ExpectAsync("connection");
        await sender.SendUserAsync("alice", "message", ["user"]);
        await local.ExpectAsync("user"); await remote.ExpectAsync("user");
        await sender.SendConnectionsAsync([local.Id, other.Id], "message", ["connections"]);
        await local.ExpectAsync("connections"); await other.ExpectAsync("connections");

        // Remote membership completion acknowledges a live subscription before the next publish.
        await sender.AddToGroupAsync(remote.Id, "group");
        await sender.AddToGroupAsync(remote.Id, "group");
        await sender.SendGroupAsync("group", "message", ["group"]);
        await remote.ExpectAsync("group");
        await sender.AddToGroupAsync(other.Id, "group");
        await sender.SendGroupExceptAsync("group", "message", ["group-except"], [remote.Id]);
        await other.ExpectAsync("group-except");
        await sender.RemoveFromGroupAsync(other.Id, "group");
        await sender.SendGroupsAsync(["group", ""], "message", ["groups"]);
        await remote.ExpectAsync("groups");
        await sender.SendUsersAsync(["alice", "bob"], "message", ["users"]);
        await local.ExpectAsync("users"); await remote.ExpectAsync("users"); await other.ExpectAsync("users");
        await sender.SendAllAsync("message", ["end"]);
        await local.ExpectAsync("end"); await remote.ExpectAsync("end"); await other.ExpectAsync("end");
        await Assert.That(local.Messages.Reader.TryRead(out _)).IsFalse();
        await Assert.That(remote.Messages.Reader.TryRead(out _)).IsFalse();
        await Assert.That(other.Messages.Reader.TryRead(out _)).IsFalse();
    }

    [Test]
    [Arguments(false, false, false, false)]
    [Arguments(false, false, false, true)]
    [Arguments(false, true, false, false)]
    [Arguments(false, true, false, true)]
    [Arguments(true, false, false, false)]
    [Arguments(true, false, false, true)]
    [Arguments(false, false, true, false)]
    [Arguments(false, false, true, true)]
    public async Task RemoteClientResultsRoundTrip(bool firstMicrosoft, bool secondMicrosoft,
        bool sharded, bool messagePack)
    {
        var prefix = Guid.NewGuid().ToString("N") + ":";
        await using var first = await BackplaneHost.StartAsync(fixture.ConnectionString, prefix, firstMicrosoft, sharded);
        await using var second = await BackplaneHost.StartAsync(fixture.ConnectionString, prefix, secondMicrosoft, sharded);
        await using var local = await first.ConnectAsync("local", messagePack);
        await using var remote = await second.ConnectAsync("remote", messagePack);
        using var handler = remote.Connection.On("answer", [typeof(string)],
            static (arguments, _) => Task.FromResult<object?>(((string)arguments[0]!).Length), null!);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await Assert.That(await first.Manager.InvokeConnectionAsync<int>(remote.Id, "answer", ["hello"], deadline.Token)).IsEqualTo(5);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RemoteClientResultFailuresAndLateCancellationLeaveNextInvocationUsable(bool messagePack)
    {
        var prefix = Guid.NewGuid().ToString("N");
        await using var first = await BackplaneHost.StartAsync(fixture.ConnectionString, prefix, false, false);
        await using var second = await BackplaneHost.StartAsync(fixture.ConnectionString, prefix, false, false);
        await using var remote = await second.ConnectAsync("remote", messagePack);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await Assert.That(async () => await first.Manager.InvokeConnectionAsync<int>("missing", "answer", [], deadline.Token))
            .ThrowsExactly<IOException>();
        using var wrong = remote.Connection.On("wrong", [], static (_, _) => Task.FromResult<object?>("wrong"), null!);
        await Assert.That(async () => await first.Manager.InvokeConnectionAsync<int>(remote.Id, "wrong", [], deadline.Token))
            .ThrowsExactly<HubException>();
        using var error = remote.Connection.On("error", [], static (_, _) => Task.FromException<object?>(new InvalidOperationException("client error")), null!);
        await Assert.That(async () => await first.Manager.InvokeConnectionAsync<int>(remote.Id, "error", [], deadline.Token))
            .ThrowsExactly<HubException>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var late = remote.Connection.On("late", [], (_, _) => { entered.TrySetResult(); return release.Task; }, null!);
        using var cancellation = new CancellationTokenSource();
        var pending = first.Manager.InvokeConnectionAsync<int>(remote.Id, "late", [], cancellation.Token);
        try
        {
            await entered.Task.WaitAsync(deadline.Token);
            cancellation.Cancel();
            await Assert.That(async () => await pending).ThrowsExactly<HubException>();
        }
        finally { cancellation.Cancel(); release.TrySetResult(99); }
        using var answer = remote.Connection.On("answer", [], static (_, _) => Task.FromResult<object?>(7), null!);
        await Assert.That(await first.Manager.InvokeConnectionAsync<int>(remote.Id, "answer", [], deadline.Token)).IsEqualTo(7);
    }

    [Test]
    public async Task DisposingManagerSettlesPendingClientResult()
    {
        var prefix = Guid.NewGuid().ToString("N");
        await using var first = await BackplaneHost.StartAsync(fixture.ConnectionString, prefix, false, false);
        await using var second = await BackplaneHost.StartAsync(fixture.ConnectionString, prefix, false, false);
        await using var remote = await second.ConnectAsync("remote", false);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = remote.Connection.On("pending", [], (_, _) => { entered.TrySetResult(); return release.Task; }, null!);
        var pending = first.Manager.InvokeConnectionAsync<int>(remote.Id, "pending", [], CancellationToken.None);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await ((IAsyncDisposable)first.Manager).DisposeAsync();
            await Assert.That(async () => await pending).ThrowsExactly<HubException>();
            await Assert.That(async () => await first.Manager.InvokeConnectionAsync<int>(remote.Id, "pending", [], CancellationToken.None))
                .ThrowsExactly<ObjectDisposedException>();
        }
        finally { release.TrySetResult(1); }
    }

    [Test]
    public async Task DisposingOneHubManagerLeavesSharedClientUsable()
    {
        await using var host = await BackplaneHost.StartAsync(fixture.ConnectionString, Guid.NewGuid().ToString("N"), false, false);
        await using var peer = await host.ConnectAsync("user", false);
        var client = host.Application.Services.GetRequiredService<RespireClient>();
        await ((IAsyncDisposable)host.Manager).DisposeAsync();
        await Assert.That(await client.PingAsync()).IsGreaterThanOrEqualTo(TimeSpan.Zero);
        await Assert.That(async () => await host.Manager.SendAllAsync("message", ["disposed"]))
            .ThrowsExactly<ObjectDisposedException>();
    }

    [Test]
    public async Task MissingRemoteGroupWaitsHaveBoundedTimeoutAndCancellation()
    {
        await using var host = await BackplaneHost.StartAsync(fixture.ConnectionString, Guid.NewGuid().ToString("N"), false, false,
            TimeSpan.FromMilliseconds(100));
        await using var peer = await host.ConnectAsync("user", false);
        await Assert.That(async () => await host.Manager.AddToGroupAsync("missing", "group")).ThrowsExactly<TimeoutException>();
        using var cancellation = new CancellationTokenSource();
        var pending = host.Manager.AddToGroupAsync("missing", "group", cancellation.Token);
        cancellation.Cancel();
        await Assert.That(async () => await pending).Throws<OperationCanceledException>();
        await host.Manager.AddToGroupAsync(peer.Id, "group");
        await host.Manager.SendGroupAsync("group", "message", ["after"]);
        await peer.ExpectAsync("after");
    }
}

public sealed class BackplaneTestHub : Hub
{
    public string Identity() => Context.ConnectionId;
    public void Disconnect() => Context.Abort();
}

internal sealed class TestUserIdProvider : IUserIdProvider
{
    public string? GetUserId(HubConnectionContext connection)
        => connection.GetHttpContext()?.Request.Query["user"].ToString();
}

internal sealed class BackplaneHost(WebApplication application) : IAsyncDisposable
{
    internal WebApplication Application => application;
    internal HubLifetimeManager<BackplaneTestHub> Manager => application.Services.GetRequiredService<HubLifetimeManager<BackplaneTestHub>>();

    internal static async Task<BackplaneHost> StartAsync(string connectionString, string prefix, bool microsoft,
        bool sharded, TimeSpan? ackTimeout = null, bool cluster = false)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<IUserIdProvider, TestUserIdProvider>();
        var signalr = builder.Services.AddSignalR().AddMessagePackProtocol();
        if (microsoft)
        {
            var options = RespireOptions.Parse(connectionString);
            signalr.AddStackExchangeRedis(redis =>
            {
                redis.Configuration = new ConfigurationOptions { EndPoints = { { options.Endpoints[0].Host, options.Endpoints[0].Port } } };
                redis.Configuration.ChannelPrefix = RedisChannel.Literal(prefix);
            });
        }
        else
        {
            builder.Services.AddRespire(_ => RespireOptions.Parse(connectionString) with { UseCluster = cluster });
            signalr.AddRespire(options =>
            {
                options.ChannelPrefix = prefix; options.UseShardedPubSub = sharded;
                if (ackTimeout is { } timeout) options.GroupAckTimeout = timeout;
            });
        }
        var app = builder.Build();
        app.MapHub<BackplaneTestHub>("/hub");
        try { await app.StartAsync(); return new(app); }
        catch { await app.DisposeAsync(); throw; }
    }

    internal async Task<Peer> ConnectAsync(string user, bool messagePack)
    {
        var builder = new HubConnectionBuilder().WithUrl("http://localhost/hub?user=" + Uri.EscapeDataString(user), options =>
        {
            options.Transports = HttpTransportType.LongPolling;
            options.HttpMessageHandlerFactory = _ => application.GetTestServer().CreateHandler();
        });
        if (messagePack) builder.AddMessagePackProtocol();
        var connection = builder.Build();
        var messages = Channel.CreateUnbounded<string>();
        connection.On<string>("message", value => messages.Writer.TryWrite(value));
        try
        {
            await connection.StartAsync();
            // The server has finished OnConnectedAsync and subscribed before exposing its ID.
            var id = await connection.InvokeAsync<string>("Identity");
            return new(connection, id, messages);
        }
        catch { await connection.DisposeAsync(); throw; }
    }

    public async ValueTask DisposeAsync() { await application.StopAsync(); await application.DisposeAsync(); }
}

internal sealed class Peer(HubConnection connection, string id, Channel<string> messages) : IAsyncDisposable
{
    internal HubConnection Connection => connection;
    internal string Id => id;
    internal Channel<string> Messages => messages;
    internal async Task ExpectAsync(string expected)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await Assert.That(await messages.Reader.ReadAsync(deadline.Token)).IsEqualTo(expected);
    }
    public ValueTask DisposeAsync() => connection.DisposeAsync();
}
