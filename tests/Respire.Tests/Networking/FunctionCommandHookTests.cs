using System.Collections.Concurrent;
using System.Text;
using Respire.Protocol;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public sealed class FunctionCommandHookTests
{
    [Test]
    [Arguments("owned")]
    [Arguments("lease")]
    [Arguments("integer")]
    [Arguments("string")]
    [Arguments("typed")]
    [Arguments("load")]
    [Arguments("library")]
    [Arguments("delete")]
    [Arguments("flush")]
    [Arguments("restore")]
    public async Task RejectionPrecedesWireAndCancellation(string route)
    {
        await using var server = new FakeRespServer(":42\r\n"u8.ToArray());
        var denied = new InvalidOperationException("Denied");
        var hook = new Hook { Before = _ => throw denied, ThrowOnCompleted = true };
        await using var client = await RespireClient.ConnectAsync(Options(server.Port, hook));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var baseline = server.CommandsSeen;
        foreach (var token in new[] { CancellationToken.None, cancellation.Token })
        {
            var pending = Execute(client, route, token);
            await Assert.That(hook.Entered.Last().CancellationToken).IsEqualTo(token);
            var error = await Failure(() => pending);
            await Assert.That(error).IsSameReferenceAs(denied);
        }
        await Assert.That(server.CommandsSeen).IsEqualTo(baseline);
        await Assert.That(hook.Entered.Count).IsEqualTo(2);
        await Assert.That(hook.Completed.Count).IsEqualTo(2);
        await Assert.That(hook.Completed.All(c => c.Outcome == RespireCommandOutcome.Rejected
            && c.State is null && ReferenceEquals(c.Error, denied))).IsTrue();
    }

    [Test]
    [Arguments("owned")]
    [Arguments("lease")]
    [Arguments("integer")]
    [Arguments("typed")]
    [Arguments("flush")]
    public async Task AcceptedCancellationCompletesOnceWithoutWire(string route)
    {
        await using var server = new FakeRespServer(":42\r\n"u8.ToArray());
        var hook = new Hook();
        await using var client = await RespireClient.ConnectAsync(Options(server.Port, hook));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var baseline = server.CommandsSeen;
        var error = await Failure(() => Execute(client, route, cancellation.Token));
        await Assert.That(error is OperationCanceledException).IsTrue();
        await Assert.That(server.CommandsSeen).IsEqualTo(baseline);
        await Check(hook, RespireCommandOutcome.Canceled, error);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task CancellationAfterSendCompletesOnce(bool mutation)
    {
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new FakeRespServer { SuppressReply = _ => { arrived.TrySetResult(); return true; } };
        var hook = new Hook();
        await using var client = await RespireClient.ConnectAsync(Options(server.Port, hook));
        using var cancellation = new CancellationTokenSource();
        var pending = Execute(client, mutation ? "flush" : "integer", cancellation.Token);
        await arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(hook.Completed.Count).IsEqualTo(0);
        cancellation.Cancel();
        var error = await Failure(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        await Assert.That(error is OperationCanceledException).IsTrue();
        await Check(hook, RespireCommandOutcome.Canceled, error);
    }

    [Test]
    [Arguments("owned", false)]
    [Arguments("owned", true)]
    [Arguments("lease", false)]
    [Arguments("lease", true)]
    [Arguments("integer", false)]
    [Arguments("integer", true)]
    [Arguments("string", true)]
    [Arguments("typed", true)]
    public async Task ResultOwnersCompleteAfterConversionAndTransfer(string route, bool readOnly)
    {
        await using var server = new FakeRespServer(route is "typed" or "string"
            ? "$2\r\n42\r\n"u8.ToArray() : ":42\r\n"u8.ToArray());
        var hook = new Hook { ThrowOnCompleted = true };
        await using var client = await RespireClient.ConnectAsync(Options(server.Port, hook));
        await Execute(client, route, readOnly: readOnly);
        await Check(hook, RespireCommandOutcome.Succeeded);
        await Assert.That(hook.Entered.Single().Command).IsEqualTo(readOnly ? "FCALL_RO" : "FCALL");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ThrowingObserverPreservesAggregateResultOwnership(bool leased)
    {
        await using var server = new FakeRespServer("*2\r\n$3\r\none\r\n*1\r\n$3\r\ntwo\r\n"u8.ToArray());
        var hook = new Hook { ThrowOnCompleted = true };
        await using var client = await RespireClient.ConnectAsync(Options(server.Port, hook));
        var function = RespireFunction.Create("function", readOnly: true);
        using var result = leased ? await client.Functions.ExecuteLeaseAsync(function, "key")
            : await client.Functions.ExecuteAsync(function, "key");
        await Check(hook, RespireCommandOutcome.Succeeded);
        await Assert.That(result[0].AsString()).IsEqualTo("one");
        await Assert.That(result[1][0].AsString()).IsEqualTo("two");
    }

    [Test]
    public async Task DeclaredRetriesRemainInsideOneScope()
    {
        await using var server = new FakeRespServer("-TRYAGAIN busy\r\n"u8.ToArray(), ":42\r\n"u8.ToArray());
        var hook = new Hook();
        await using var client = await RespireClient.ConnectAsync(Options(server.Port, hook) with
        {
            RetryPolicy = new() { InitialBackoff = TimeSpan.Zero },
        });
        var function = RespireFunction.Create("reader", readOnly: true)
            .WithRetryCategory(RespireCommandRetryCategory.ReadOnly);
        await Assert.That(await client.Functions.ExecuteIntegerAsync(function, "key")).IsEqualTo(42);
        await Assert.That(server.CommandsSeen).IsEqualTo(2);
        await Check(hook, RespireCommandOutcome.Succeeded);
    }

    [Test]
    public async Task TypedConversionFailureIsTheHookOutcome()
    {
        await using var server = new FakeRespServer("$8\r\nnot-json\r\n"u8.ToArray());
        var hook = new Hook { ThrowOnCompleted = true };
        await using var client = await RespireClient.ConnectAsync(Options(server.Port, hook));
        var error = await Failure(() => Execute(client, "typed"));
        await Check(hook, RespireCommandOutcome.Failed, error);
        await Assert.That(server.CommandsSeen).IsEqualTo(1);
    }

    [Test]
    public async Task MissingLibraryRecoveryAndReentrantCallHaveIndependentScopes()
    {
        const string source = "#!lua name=sample\nreturn 1";
        var calls = 0;
        var lists = 0;
        await using var server = new FakeRespServer(FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => command.StartsWith("FCALL ", StringComparison.Ordinal)
                ? Interlocked.Increment(ref calls) == 1 ? "-ERR Function not found\r\n"u8.ToArray() : ":42\r\n"u8.ToArray()
                : command.StartsWith("FUNCTION LIST", StringComparison.Ordinal)
                    ? Interlocked.Increment(ref lists) == 1 ? "*0\r\n"u8.ToArray() : LibraryList(source)
                : command.StartsWith("FUNCTION LOAD", StringComparison.Ordinal) ? "$6\r\nsample\r\n"u8.ToArray()
                : command == "STRLEN nested" ? ":2\r\n"u8.ToArray() : null,
        };
        var hook = new Hook();
        await using var client = await RespireClient.ConnectAsync(Options(server.Port, hook));
        Task<long>? nested = null;
        hook.Before = context =>
        {
            if (context.Command == "FCALL") nested = client.Strings.LengthAsync("nested").AsTask();
        };
        var function = RespireFunctionLibrary.Create(source).Function("function");
        await Assert.That(await client.Functions.ExecuteIntegerAsync(function, "key")).IsEqualTo(42);
        await Assert.That(await nested!).IsEqualTo(2);
        await Assert.That(calls).IsEqualTo(2);
        await Assert.That(lists).IsEqualTo(2);
        await Assert.That(server.ReceivedCommands.Count(c => c.StartsWith("FUNCTION LOAD", StringComparison.Ordinal))).IsEqualTo(1);
        await Assert.That(hook.Entered.Select(c => c.Command)).IsEquivalentTo(["FCALL", "STRLEN"]);
        await Assert.That(hook.Completed.Count).IsEqualTo(2);
        await Assert.That(hook.Completed.All(c => c.Outcome == RespireCommandOutcome.Succeeded)).IsTrue();
    }

    [Test]
    [Arguments("load")]
    [Arguments("delete")]
    [Arguments("flush")]
    [Arguments("restore")]
    public async Task ClusterMutationRejectionPreventsEveryLeg(string route)
    {
        await using var first = new FakeRespServer(FakeRespServer.OkReply);
        await using var second = new FakeRespServer(FakeRespServer.OkReply);
        await using var seed = new FakeRespServer(Topology(first.Port, second.Port));
        var denied = new InvalidOperationException("Denied");
        var hook = new Hook { Before = _ => throw denied };
        await using var client = await RespireClient.ConnectAsync(Options(seed.Port, hook) with { UseCluster = true });
        var error = await Failure(() => Execute(client, route));
        await Assert.That(error).IsSameReferenceAs(denied);
        await Assert.That(first.CommandsSeen + second.CommandsSeen).IsEqualTo(0);
        await Check(hook, RespireCommandOutcome.Rejected, denied);
    }

    [Test]
    [Arguments("load", false)]
    [Arguments("delete", false)]
    [Arguments("flush", false)]
    [Arguments("restore", false)]
    [Arguments("load", true)]
    [Arguments("delete", true)]
    [Arguments("flush", true)]
    [Arguments("restore", true)]
    public async Task ClusterMutationCompletesOnceAfterEveryLeg(string route, bool fail)
    {
        var firstArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var first = new FakeRespServer { SuppressReply = _ => { firstArrived.TrySetResult(); return true; } };
        await using var second = new FakeRespServer { SuppressReply = _ => { secondArrived.TrySetResult(); return true; } };
        await using var seed = new FakeRespServer(Topology(first.Port, second.Port));
        var hook = new Hook();
        await using var client = await RespireClient.ConnectAsync(Options(seed.Port, hook) with { UseCluster = true });
        var pending = Execute(client, route);
        await Task.WhenAll(firstArrived.Task, secondArrived.Task).WaitAsync(TimeSpan.FromSeconds(5));
        await first.SendRawAsync(fail ? "-ERR rejected\r\n"u8.ToArray() : FakeRespServer.OkReply);
        await Assert.That(pending.IsCompleted).IsFalse();
        await Assert.That(hook.Completed.Count).IsEqualTo(0);
        await second.SendRawAsync(FakeRespServer.OkReply);
        if (fail)
        {
            var error = await Failure(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
            await Assert.That(error is RespireServerException).IsTrue();
            await Check(hook, RespireCommandOutcome.Failed, error);
        }
        else
        {
            await pending.WaitAsync(TimeSpan.FromSeconds(5));
            await Check(hook, RespireCommandOutcome.Succeeded);
        }
    }

    [Test]
    [Arguments("load", "FUNCTION LOAD")]
    [Arguments("library", "FUNCTION LOAD")]
    [Arguments("delete", "FUNCTION DELETE")]
    [Arguments("flush", "FUNCTION FLUSH")]
    [Arguments("restore", "FUNCTION RESTORE")]
    public async Task StandaloneMutationHasOneConvertedCompletion(string route, string command)
    {
        await using var server = new FakeRespServer(FakeRespServer.OkReply);
        var hook = new Hook { ThrowOnCompleted = true };
        await using var client = await RespireClient.ConnectAsync(Options(server.Port, hook));
        await Execute(client, route);
        await Check(hook, RespireCommandOutcome.Succeeded);
        await Assert.That(hook.Entered.Single().Command).IsEqualTo(command);
        await Assert.That(server.CommandsSeen).IsEqualTo(1);
    }

    [Test]
    public async Task InvalidFunctionPreflightDoesNotEnterHooks()
    {
        await using var server = new FakeRespServer();
        var hook = new Hook();
        await using var client = await RespireClient.ConnectAsync(Options(server.Port, hook));
        await Failure(async () => await client.Functions.ExecuteIntegerAsync(null!));
        await Failure(async () => { using var result = await client.Functions.ExecuteSpanAsync(null!, [], []); });
        await Failure(async () => await client.Functions.LoadAsync(" "));
        await Assert.That(hook.Entered.Count).IsEqualTo(0);
        await Assert.That(server.CommandsSeen).IsEqualTo(0);
    }

    private static async Task Execute(RespireClient client, string route, CancellationToken token = default, bool readOnly = false)
    {
        var function = RespireFunction.Create("function", readOnly);
        switch (route)
        {
            case "owned":
                using (var result = await client.Functions.ExecuteSpanAsync(function, ["key"], [], token))
                    await Assert.That(result.AsInteger()).IsEqualTo(42);
                break;
            case "lease":
                using (var result = await client.Functions.ExecuteLeaseSpanAsync(function, ["key"], [], token))
                    await Assert.That(result.AsInteger()).IsEqualTo(42);
                break;
            case "integer": await client.Functions.ExecuteIntegerAsync(function, "key", cancellationToken: token); break;
            case "string": await client.Functions.ExecuteStringAsync(function, "key", cancellationToken: token); break;
            case "typed": await client.Functions.ExecuteAsync<int>(function, "key", cancellationToken: token); break;
            case "load": await client.Functions.LoadAsync("source", cancellationToken: token); break;
            case "library": await client.Functions.LoadAsync(RespireFunctionLibrary.Create("#!lua name=sample\nreturn 1"), token); break;
            case "delete": await client.Functions.DeleteAsync("sample", token); break;
            case "flush": await client.Functions.FlushAsync(cancellationToken: token); break;
            case "restore": await client.Functions.RestoreAsync("binary"u8.ToArray(), cancellationToken: token); break;
            default: throw new ArgumentOutOfRangeException(nameof(route));
        }
    }

    private static async Task Check(Hook hook, RespireCommandOutcome outcome, Exception? error = null)
    {
        await Assert.That(hook.Entered.Count).IsEqualTo(1);
        await Assert.That(hook.Completed.Count).IsEqualTo(1);
        var completed = hook.Completed.Single();
        await Assert.That(completed.Context).IsSameReferenceAs(hook.Entered.Single());
        if (outcome != RespireCommandOutcome.Rejected)
            await Assert.That(completed.State).IsSameReferenceAs(completed.Context);
        await Assert.That(completed.Outcome).IsEqualTo(outcome);
        await Assert.That(completed.Error).IsSameReferenceAs(error);
    }

    private static RespireOptions Options(int port, Hook hook) => new()
    {
        Endpoints = [new("127.0.0.1", port)], Protocol = RespireProtocol.Resp2, Connections = 1,
        ThreadPoolMonitoring = false, ClusterTopologyRefreshInterval = null,
        CommandTimeout = TimeSpan.FromSeconds(5), CommandHook = hook,
    };

    private static byte[] Topology(int first, int second) => Encoding.ASCII.GetBytes(
        $"*2\r\n*3\r\n:0\r\n:8191\r\n*2\r\n$9\r\n127.0.0.1\r\n:{first}\r\n" +
        $"*3\r\n:8192\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{second}\r\n");

    private static byte[] LibraryList(string source) => Encoding.UTF8.GetBytes(
        $"*1\r\n*8\r\n+library_name\r\n+sample\r\n+engine\r\n+LUA\r\n+functions\r\n*1\r\n*6\r\n" +
        $"+name\r\n+function\r\n+description\r\n$-1\r\n+flags\r\n*0\r\n+library_code\r\n${Encoding.UTF8.GetByteCount(source)}\r\n{source}\r\n");

    private static async Task<Exception> Failure(Func<Task> action)
    {
        try { await action(); }
        catch (Exception error) { return error; }
        throw new InvalidOperationException("Expected failure.");
    }

    private sealed class Hook : RespireCommandHook
    {
        internal Action<RespireCommandContext>? Before;
        internal bool ThrowOnCompleted;
        internal readonly ConcurrentQueue<RespireCommandContext> Entered = new();
        internal readonly ConcurrentQueue<(RespireCommandContext Context, object? State, RespireCommandOutcome Outcome, Exception? Error)> Completed = new();
        public override object? OnExecuting(RespireCommandContext context)
        {
            Entered.Enqueue(context);
            Before?.Invoke(context);
            return context;
        }
        public override void OnCompleted(RespireCommandContext context, object? state, RespireCommandOutcome outcome, Exception? error)
        {
            Completed.Enqueue((context, state, outcome, error));
            if (ThrowOnCompleted) throw new InvalidOperationException("Observer failure");
        }
    }
}
