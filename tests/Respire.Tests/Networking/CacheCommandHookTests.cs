using Respire.Internal;
using Respire.Protocol;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public partial class CommandHookTests
{
    [Test]
    public async Task ProtectedCacheAsideValidatesOptionsBeforeHookAndAdmitsOneLogicalOperation()
    {
        using var endpoint = new ReservedUnavailablePort();
        var hook = new RecordingHook { Before = _ => throw new InvalidOperationException("rejected") };
        await using var client = RespireClient.Create(new RespireOptions
        {
            Endpoints = [new("127.0.0.1", endpoint.Port)], CommandHook = hook, ThreadPoolMonitoring = false,
        });
        await Assert.That(await Failure(async () => await client.GetOrSetAsync<string>("key",
            new RespireCacheAsideOptions { EarlyRefreshBeta = -1 }, _ => throw new Exception("factory"), TimeSpan.FromMinutes(1)))
            is ArgumentOutOfRangeException).IsTrue();
        await Assert.That(hook.Entered).IsEmpty();
        await Assert.That(await Failure(async () => await client.GetOrSetAsync<string>("key",
            new RespireCacheAsideOptions(), _ => throw new Exception("factory"), TimeSpan.FromMinutes(1)))
            is InvalidOperationException).IsTrue();
        await Assert.That(hook.Entered.Single().Command).IsEqualTo("GETORSET");
        await Assert.That(hook.Completed.Single().Outcome).IsEqualTo(RespireCommandOutcome.Rejected);
        await Assert.That(client.IsConnected).IsFalse();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task CacheOwnersSnapshotBinaryKeysBeforeAdmission(bool aside)
    {
        using var endpoint = new ReservedUnavailablePort();
        byte[] key = "key"u8.ToArray();
        var hook = new RecordingHook { Before = _ => key[0] = (byte)'x' };
        await using var client = RespireClient.Create(new RespireOptions
        {
            Endpoints = [new("127.0.0.1", endpoint.Port)], ClientSideCache = new(),
            CommandHook = hook, ThreadPoolMonitoring = false,
        });
        SeedCacheOwner(client, "many", "42");
        if (aside)
            await Assert.That(await client.GetOrSetAsync<int>(key, _ => throw new Exception("factory"), TimeSpan.FromMinutes(1))).IsEqualTo(42);
        else
            await Assert.That((await client.Strings.GetManyAsync<int>([key]))[0]).IsEqualTo(42);
        await Assert.That(hook.Completed.Single().Outcome).IsEqualTo(RespireCommandOutcome.Succeeded);
        await Assert.That(client.IsConnected).IsFalse();
    }

    [Test]
    public async Task CacheOwnerPreflightFailuresDoNotReachRejectingHook()
    {
        using var endpoint = new ReservedUnavailablePort();
        var hook = new RecordingHook { Before = _ => throw new Exception("hook") };
        await using var client = RespireClient.Create(new RespireOptions
        {
            Endpoints = [new("127.0.0.1", endpoint.Port)], ClientSideCache = new(), UseCluster = true,
            CommandHook = hook, ThreadPoolMonitoring = false,
        });
        await Assert.That(await Failure(async () => await client.Strings.GetManyAsync(["{a}", "{b}"])) is RespireCrossSlotException).IsTrue();
        await Assert.That(await Failure(async () => await client.GetOrSetAsync<string>("key", null!, TimeSpan.FromMinutes(1))) is ArgumentNullException).IsTrue();
        await Assert.That(await Failure(async () => await client.GetOrSetAsync<string>("key", _ => ValueTask.FromResult<string?>("42"), TimeSpan.Zero)) is ArgumentOutOfRangeException).IsTrue();
        await Assert.That(hook.Entered).IsEmpty();
        await Assert.That(hook.Completed).IsEmpty();
        await Assert.That(client.IsConnected).IsFalse();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task PartialCacheReadsKeepRetriesAndConversionWithinOneScope(bool hash)
    {
        var attempts = 0;
        var hook = new RecordingHook();
        await using var server = CacheHookServer();
        var command = hash ? "HMGET key missing" : "MGET missing";
        server.ReplyOverride = (_, text) => text == command
            ? Interlocked.Increment(ref attempts) == 1 ? "-TRYAGAIN busy\r\n"u8.ToArray() : "*1\r\n$2\r\n43\r\n"u8.ToArray()
            : null;
        await using var client = await RespireClient.ConnectAsync(Options(server, hook) with
        {
            Protocol = RespireProtocol.Resp3, ClientSideCache = new() { ReuseHashFields = true },
            RetryPolicy = new() { InitialBackoff = TimeSpan.Zero },
        });
        SeedCacheOwner(client, "many", "42");
        var values = hash
            ? await client.Hashes.GetManyAsync("key", ["field", "missing", "field"])
            : await client.Strings.GetManyAsync(["key", "missing", "key"]);
        await Assert.That(string.Join(",", values)).IsEqualTo("42,43,42");
        await Assert.That(attempts).IsEqualTo(2);
        await Assert.That(hook.Entered.Single().Command).IsEqualTo(hash ? "HMGET" : "MGET");
        await Assert.That(hook.Completed.Single().Outcome).IsEqualTo(RespireCommandOutcome.Succeeded);
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task CacheAsideFactoryCommandsHaveIndependentScopesAndInternalLegsDoNot(bool failFactory, bool bypassCache)
    {
        var hook = new RecordingHook();
        await using var server = CacheHookServer();
        server.ReplyOverride = (_, command) => command == "GET missing" || command.StartsWith("SET missing ")
            ? "$-1\r\n"u8.ToArray() : command == "GET nested" ? "$2\r\n42\r\n"u8.ToArray() : null;
        await using var client = await RespireClient.ConnectAsync(Options(server, hook) with
        { Protocol = RespireProtocol.Resp3, ClientSideCache = new() });
        IRespireClient view = bypassCache ? client.WithoutClientCache() : client;
        var failure = new InvalidOperationException("factory failed");
        async ValueTask<string?> Factory(CancellationToken token)
        {
            await Assert.That(hook.Completed).IsEmpty();
            var result = await client.GetStringAsync("nested", token);
            if (failFactory) throw failure;
            return result;
        }
        if (failFactory)
            await Assert.That(await Failure(async () => await view.GetOrSetAsync("missing", Factory, TimeSpan.FromMinutes(1))))
                .IsSameReferenceAs(failure);
        else
            await Assert.That(await view.GetOrSetAsync("missing", Factory, TimeSpan.FromMinutes(1))).IsEqualTo("42");
        await Assert.That(string.Join(",", hook.Entered.Select(c => c.Command))).IsEqualTo("GETORSET,GET");
        await Assert.That(string.Join(",", hook.Completed.Select(c => c.Context.Command))).IsEqualTo("GET,GETORSET");
        await Assert.That(hook.Completed.Last().Outcome).IsEqualTo(failFactory ? RespireCommandOutcome.Failed : RespireCommandOutcome.Succeeded);
        await Assert.That(server.ReceivedCommands.Count(c => c.StartsWith("SET missing "))).IsEqualTo(failFactory ? 0 : 1);
    }

    [Test]
    public async Task CacheAsideCoalescedCallersKeepIndependentCancellationAndCompletion()
    {
        var hook = new RecordingHook();
        await using var server = CacheHookServer();
        server.ReplyOverride = (_, command) => command.StartsWith("GET ") || command.StartsWith("SET ")
            ? "$-1\r\n"u8.ToArray() : null;
        await using var client = await RespireClient.ConnectAsync(Options(server, hook) with
        { Protocol = RespireProtocol.Resp3, ClientSideCache = new() { CoalesceConcurrentMisses = true } });
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        async ValueTask<string?> Factory(CancellationToken token)
        {
            Interlocked.Increment(ref calls);
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            return "42";
        }
        using var canceled = new CancellationTokenSource();
        var first = client.GetOrSetAsync("missing", Factory, TimeSpan.FromMinutes(1), canceled.Token).AsTask();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var second = client.GetOrSetAsync("missing", Factory, TimeSpan.FromMinutes(1)).AsTask();
            canceled.Cancel();
            await Assert.That(await Failure(async () => await first) is OperationCanceledException).IsTrue();
            await Assert.That(hook.Completed.Single().Outcome).IsEqualTo(RespireCommandOutcome.Canceled);
            await Assert.That(second.IsCompleted).IsFalse();
            release.TrySetResult();
            await Assert.That(await second.WaitAsync(TimeSpan.FromSeconds(5))).IsEqualTo("42");
        }
        finally { release.TrySetResult(); }
        await Assert.That(calls).IsEqualTo(1);
        await Assert.That(hook.Entered.Count).IsEqualTo(2);
        await Assert.That(hook.Completed.Count).IsEqualTo(2);
        await Assert.That(hook.Completed.Last().Outcome).IsEqualTo(RespireCommandOutcome.Succeeded);
    }

    [Test]
    public async Task CacheAsideRejectsBeforeFactoryTrackingOrMutation()
    {
        var rejection = new InvalidOperationException("denied");
        var hook = new RecordingHook { Before = _ => throw rejection };
        await using var server = CacheHookServer();
        await using var client = await RespireClient.ConnectAsync(Options(server, hook) with
        { Protocol = RespireProtocol.Resp3, ClientSideCache = new() });
        var before = server.CommandsSeen;
        var error = await Failure(async () => await client.GetOrSetAsync<string>("missing",
            _ => throw new Exception("factory ran"), TimeSpan.FromMinutes(1)));
        await Assert.That(error).IsSameReferenceAs(rejection);
        await Assert.That(server.CommandsSeen).IsEqualTo(before);
        await Assert.That(hook.Completed.Single().Outcome).IsEqualTo(RespireCommandOutcome.Rejected);
    }

    private static FakeRespServer CacheHookServer()
        => new("%1\r\n$5\r\nproto\r\n:3\r\n"u8.ToArray(), FakeRespServer.OkReply);

    [Test]
    [Arguments("many")]
    [Arguments("typed-many")]
    [Arguments("raw-many")]
    [Arguments("lease-many")]
    [Arguments("hash-many")]
    [Arguments("hash-map")]
    [Arguments("aside")]
    public async Task CacheOwnersAdmitResidentHitsAndRejectionPrecedesLookup(string route)
    {
        using var endpoint = new ReservedUnavailablePort();
        var hook = new RecordingHook();
        await using var client = RespireClient.Create(new RespireOptions
        {
            Endpoints = [new("127.0.0.1", endpoint.Port)],
            ClientSideCache = new() { CoalesceConcurrentMisses = true, ReuseHashFields = true },
            CommandHook = hook, ThreadPoolMonitoring = false,
        });
        SeedCacheOwner(client, route, "42");
        await ReadCacheOwner(client, route);
        var name = route switch { "hash-many" => "HMGET", "hash-map" => "HGETALL", "aside" => "GETORSET", _ => "MGET" };
        await Assert.That(hook.Entered.Single().Command).IsEqualTo(name);
        await Assert.That(hook.Completed.Single().Outcome).IsEqualTo(RespireCommandOutcome.Succeeded);
        var beforeRejection = client.ClientSideCache!.GetStatistics();
        var rejection = new InvalidOperationException("denied");
        hook.Before = _ => throw rejection;
        var error = await Failure(() => ReadCacheOwner(client, route));
        await Assert.That(error).IsSameReferenceAs(rejection);
        await Assert.That(hook.Completed.Last().Outcome).IsEqualTo(RespireCommandOutcome.Rejected);
        await Assert.That(hook.Completed.Count).IsEqualTo(2);
        await Assert.That(client.ClientSideCache.GetStatistics().Hits).IsEqualTo(beforeRejection.Hits);
        await Assert.That(client.ClientSideCache.GetStatistics().Misses).IsEqualTo(beforeRejection.Misses);
        await Assert.That(client.IsConnected).IsFalse();
    }

    [Test]
    [Arguments("typed-many")]
    [Arguments("hash-map")]
    [Arguments("aside")]
    public async Task CacheOwnersIncludeResidentTypedConversionFailure(string route)
    {
        using var endpoint = new ReservedUnavailablePort();
        var hook = new RecordingHook { ThrowOnCompleted = true };
        await using var client = RespireClient.Create(new RespireOptions
        {
            Endpoints = [new("127.0.0.1", endpoint.Port)], ClientSideCache = new() { ReuseHashFields = true },
            CommandHook = hook, ThreadPoolMonitoring = false,
        });
        SeedCacheOwner(client, route, "not-an-integer");
        var error = await Failure(() => ReadCacheOwner(client, route));
        var completion = hook.Completed.Single();
        await Assert.That(completion.Outcome).IsEqualTo(RespireCommandOutcome.Failed);
        await Assert.That(completion.Error).IsSameReferenceAs(error);
        await Assert.That(client.IsConnected).IsFalse();
    }

    [Test]
    [Arguments("many")]
    [Arguments("hash-many")]
    [Arguments("aside")]
    [Arguments("protected-aside")]
    public async Task CacheOwnersAdmitCancellationBeforeResidentLookup(string route)
    {
        using var endpoint = new ReservedUnavailablePort();
        var hook = new RecordingHook();
        await using var client = RespireClient.Create(new RespireOptions
        {
            Endpoints = [new("127.0.0.1", endpoint.Port)], ClientSideCache = new() { ReuseHashFields = true },
            CommandHook = hook, ThreadPoolMonitoring = false,
        });
        SeedCacheOwner(client, route, "42");
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        var error = await Failure(() => ReadCacheOwner(client, route, canceled.Token));
        await Assert.That(error is OperationCanceledException).IsTrue();
        await Assert.That(hook.Completed.Single().Outcome).IsEqualTo(RespireCommandOutcome.Canceled);
        await Assert.That(hook.Completed.Single().Context.CancellationToken).IsEqualTo(canceled.Token);
        await Assert.That(client.IsConnected).IsFalse();
    }

    private static void SeedCacheOwner(RespireClient client, string route, string text)
    {
        var cache = client.Core.ClientCache!;
        var key = new RespireKey("key");
        using var value = RespValue.BulkString(System.Text.Encoding.UTF8.GetBytes(text));
        var read = cache.BeginRead(in key);
        cache.CompleteRead(in read, in value, allowInsert: true);
        var query = new ClientSideCacheCoordinator.QueryRequest(new ClientCacheCommandKey("HGET", key.AsValue(), "field"), key);
        var fieldRead = cache.BeginRead("HGET", in query);
        cache.CompleteRead(in fieldRead, in value, allowInsert: true);
        query = new(new ClientCacheCommandKey("HGETALL", key.AsValue()), key);
        var mapRead = cache.BeginRead("HGETALL", in query);
        using var map = RespValue.Array([RespValue.BulkString("field"u8.ToArray()), value.ToOwned()]);
        cache.CompleteRead(in mapRead, in map, allowInsert: true);
    }

    private static async Task ReadCacheOwner(RespireClient client, string route, CancellationToken token = default)
    {
        switch (route)
        {
            case "many": await client.Strings.GetManyAsync(["key", "key"], token); break;
            case "typed-many": await client.Strings.GetManyAsync<int>(["key", "key"], token); break;
            case "raw-many": using (await client.ExecuteAsync(RespireCommands.String.MGET, ["key", "key"], cancellationToken: token)) { } break;
            case "lease-many": using (await client.ExecuteLeaseAsync(RespireCommands.String.MGET, ["key", "key"], cancellationToken: token)) { } break;
            case "hash-many": await client.Hashes.GetManyAsync("key", ["field", "field"], token); break;
            case "hash-map": await client.Hashes.GetAllAsync<int>("key", token); break;
            case "aside": await client.GetOrSetAsync<int>("key", _ => throw new Exception("Factory must not run on a hit."), TimeSpan.FromMinutes(1), token); break;
            case "protected-aside": await client.GetOrSetAsync<int>("key", new RespireCacheAsideOptions(), _ => throw new Exception("Factory must not run."), TimeSpan.FromMinutes(1), token); break;
            default: throw new ArgumentOutOfRangeException(nameof(route));
        }
    }
}
