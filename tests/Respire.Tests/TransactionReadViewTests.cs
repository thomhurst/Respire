using System.Reflection;
using System.Text;
using Respire.Serialization;
using Respire.Testing;
using Respire.Tests.Networking;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

public class TransactionReadViewTests
{
    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task UnsupportedCustomViewDisposesWatchWithoutRetry(bool rejectCacheView, bool generic)
    {
        await using var server = new RespireFakeServer();
        await using var root = await RespireClient.ConnectAsync(server.CreateOptions());
        var client = DispatchProxy.Create<IRespireClient, UnsupportedViewClient>();
        var proxy = (UnsupportedViewClient)client;
        proxy.Root = root;
        proxy.RejectCacheView = rejectCacheView;
        var callbacks = 0;
        Exception? observed = null;
        try
        {
            if (generic)
                await client.RunTransactionWithReadsAsync(["watched"], (_, _, _) => { callbacks++; return ValueTask.FromResult(1); });
            else
                await client.RunTransactionWithReadsAsync(["watched"], (_, _, _) => { callbacks++; return ValueTask.CompletedTask; });
        }
        catch (Exception error) { observed = error; }

        await Assert.That(observed).IsSameReferenceAs(proxy.Failure);
        await Assert.That(proxy.Attempts).IsEqualTo(1);
        await Assert.That(callbacks).IsEqualTo(0);
        await Assert.That(() => { _ = proxy.Transaction!.Set("watched", "never"); }).Throws<InvalidOperationException>();
        await root.SetAsync("still-owned", "value");
        await Assert.That(await root.GetStringAsync("still-owned")).IsEqualTo("value");
    }

    public class UnsupportedViewClient : DispatchProxy
    {
        public IRespireClient Root { get; set; } = null!;
        public bool RejectCacheView { get; set; }
        public int Attempts { get; private set; }
        public RespireWatchedTransaction? Transaction { get; private set; }
        public NotSupportedException Failure { get; } = new("custom view unavailable");

        protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name switch
        {
            nameof(IRespireClient.CreateTransactionAsync) => CreateTransactionAsync((RespireKey[])args![0]!, (CancellationToken)args[1]!),
            nameof(IRespireClient.WithReadFrom) when RejectCacheView => this,
            nameof(IRespireClient.WithReadFrom) or nameof(IRespireClient.WithoutClientCache) => throw Failure,
            _ => throw new InvalidOperationException($"Unexpected custom-client call: {method.Name}"),
        };

        private async ValueTask<RespireWatchedTransaction> CreateTransactionAsync(RespireKey[] keys, CancellationToken token)
        {
            Attempts++;
            Transaction = await Root.CreateTransactionAsync(keys, token);
            return Transaction;
        }
    }

    [Test]
    public async Task ReadsBypassCacheAndReplicaAndPreserveClientSettings()
    {
        var value = "old";
        await using var primary = new FakeRespServer(64, FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => Reply(command, value, false),
        };
        await using var replica = new FakeRespServer(64, FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => Reply(command, "replica", true),
        };
        var serializer = new SystemTextJsonSerializer();
        await using var root = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = [new("127.0.0.1", primary.Port)],
            ReplicaEndpoints = [new("127.0.0.1", replica.Port)],
            ReadFrom = RespireReadFrom.Replica, Database = 3, Serializer = serializer,
            ClientSideCache = new(),
        });
        var client = root.WithKeyPrefix("tenant:");
        await Assert.That(await client.GetStringAsync("balance")).IsEqualTo("replica");
        var cached = client.WithReadFrom(RespireReadFrom.Primary);
        await Assert.That(await cached.GetStringAsync("balance")).IsEqualTo("old");
        value = "fresh";
        await Assert.That(await cached.GetStringAsync("balance")).IsEqualTo("old");
        var replicaReads = replica.ReceivedCommands.Count(command => command.StartsWith("GET "));

        var result = await client.RunTransactionWithReadsAsync(["guard"], async (reads, _, token) =>
        {
            var concrete = (RespireClient)reads;
            await Assert.That(concrete.Core.Options.Serializer).IsSameReferenceAs(serializer);
            await Assert.That(concrete.Core.Options.Database).IsEqualTo(3);
            await Assert.That(concrete.ReadCache).IsNull();
            await Assert.That(concrete.GetBatchReadFromPolicy()).IsEqualTo(RespireReadFrom.Primary);
            var current = await reads.GetStringAsync("balance", token);
            await reads.DisposeAsync(); // A shared view must not dispose the caller's connections.
            return current;
        });

        await Assert.That(result).IsEqualTo("fresh");
        await Assert.That(replica.ReceivedCommands.Count(command => command.StartsWith("GET "))).IsEqualTo(replicaReads);
        await Assert.That(primary.ReceivedCommands).Contains("WATCH tenant:guard");
        await Assert.That(primary.ReceivedCommands).Contains("GET tenant:balance");
        await Assert.That(primary.ReceivedCommands).Contains("SELECT 3");
        await Assert.That(await cached.GetStringAsync("balance")).IsEqualTo("old");
        await Assert.That(root.IsConnected).IsTrue();
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task RetriesReadFreshInputsAndKeepPrefixes(int protocol)
    {
        await using var server = new RespireFakeServer();
        await using var root = await RespireClient.ConnectAsync(server.CreateOptions() with { Protocol = (RespProtocol)protocol });
        var client = root.WithKeyPrefix("tenant:");
        await client.SetAsync("balance", "1");
        var attempts = 0;
        var views = new List<IRespireClient>();
        var result = await client.RunTransactionWithReadsAsync(["balance"], async (reads, transaction, token) =>
        {
            views.Add(reads);
            var current = int.Parse((await reads.GetStringAsync("balance", token))!);
            if (++attempts == 1) await client.SetAsync("balance", "2", cancellationToken: token);
            _ = transaction.Set("balance", current + 10);
            return current + 10;
        });
        await Assert.That(result).IsEqualTo(12);
        await Assert.That(attempts).IsEqualTo(2);
        await Assert.That(ReferenceEquals(views[0], views[1])).IsFalse();
        await Assert.That(await root.GetStringAsync("tenant:balance")).IsEqualTo("12");
        await Assert.That(await root.ExistsAsync("balance")).IsFalse();
        foreach (var view in views) await view.DisposeAsync();
        await Assert.That(root.IsConnected).IsTrue();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task CallbackFailureOrCancellationDoesNotCommit(bool cancel)
    {
        await using var server = new RespireFakeServer();
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions());
        using var cancellation = new CancellationTokenSource();
        var attempts = 0;
        var expected = new InvalidOperationException("callback");
        Exception? observed = null;
        try
        {
            await client.RunTransactionWithReadsAsync(["result"], (reads, transaction, token) =>
            {
                attempts++;
                _ = transaction.Set("result", "never");
                if (!cancel) throw expected;
                cancellation.Cancel();
                return ValueTask.CompletedTask;
            }, cancellationToken: cancellation.Token);
        }
        catch (Exception error) { observed = error; }
        await Assert.That(cancel ? observed is OperationCanceledException : ReferenceEquals(observed, expected)).IsTrue();
        await Assert.That(attempts).IsEqualTo(1);
        await Assert.That(await client.ExistsAsync("result")).IsFalse();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task LostCommitReplyIsNotReplayed(bool afterExecution)
    {
        await using var server = new RespireFakeServer();
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions());
        using var fault = server.InjectFault("EXEC", RespireFakeFault.Disconnect(afterExecution));
        var attempts = 0;
        await Assert.That(async () => await client.RunTransactionWithReadsAsync(["result"], (_, transaction, _) =>
        {
            attempts++;
            _ = transaction.Increment("result");
            return ValueTask.CompletedTask;
        })).Throws<RespireConnectionException>();
        await Assert.That(attempts).IsEqualTo(1);
        await using var observer = await RespireClient.ConnectAsync(server.CreateOptions());
        await Assert.That(await observer.GetStringAsync("result")).IsEqualTo(afterExecution ? "1" : null);
    }

    [Test]
    public async Task CrossSlotWatchFailsBeforeCallback()
    {
        await using var client = RespireClient.Create(new RespireOptions { Endpoints = [new("127.0.0.1", 1)], UseCluster = true });
        var calls = 0;
        await Assert.That(async () => await client.RunTransactionWithReadsAsync(["slot-a", "slot-b"], (_, _, _) =>
        {
            calls++;
            return ValueTask.CompletedTask;
        })).Throws<InvalidOperationException>();
        await Assert.That(calls).IsEqualTo(0);
    }

    [Test]
    public async Task ValidationRemainsSynchronous()
    {
        await using var client = RespireClient.Create("127.0.0.1:1");
        // Void callbacks deliberately discard ValueTask: only synchronous validation can satisfy these assertions.
        await Assert.That(() => { _ = client.RunTransactionWithReadsAsync([], null!); }).Throws<ArgumentNullException>();
        await Assert.That(() => { _ = client.RunTransactionAsync([], null!); }).Throws<ArgumentNullException>();
        await Assert.That(() => { _ = client.RunTransactionWithReadsAsync([], (_, _, _) => ValueTask.CompletedTask, new() { MaxAttempts = 0 }); }).Throws<ArgumentOutOfRangeException>();
    }

    private static byte[] Reply(string command, string value, bool replica) => command switch
    {
        "HELLO 3" => "%1\r\n+proto\r\n:3\r\n"u8.ToArray(),
        "ROLE" when replica => "*5\r\n$5\r\nslave\r\n$9\r\n127.0.0.1\r\n:6379\r\n$9\r\nconnected\r\n:0\r\n"u8.ToArray(),
        "EXEC" => "*0\r\n"u8.ToArray(),
        "PING" => "+PONG\r\n"u8.ToArray(),
        _ when command.StartsWith("GET ") => Encoding.UTF8.GetBytes($"${value.Length}\r\n{value}\r\n"),
        _ => FakeRespServer.OkReply,
    };
}
