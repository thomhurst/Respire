using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using Respire.Protocol;
using Respire.TimeSeries;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class TimeSeriesCommandHookTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task EveryOwnerRejectsOrCancelsWithoutWireWork(bool cancel)
    {
        await using var server = new FakeRespServer(FakeRespServer.OkReply);
        var hook = new RecordingHook();
        await using var client = await Connect(Options(server, hook));
        var series = new RespireTimeSeriesClient(client);
        using var caller = new CancellationTokenSource();
        if (cancel) caller.Cancel();
        else hook.Before = _ => throw new InvalidOperationException("denied");
        foreach (var operation in Operations)
        {
            hook.Completed.Clear();
            var error = await Failure(() => Invoke(series, operation, caller.Token));
            var completion = hook.Completed.Single();
            await Assert.That(completion.Command).IsEqualTo(operation);
            await Assert.That(completion.Outcome).IsEqualTo(cancel ? RespireCommandOutcome.Canceled : RespireCommandOutcome.Rejected);
            await Assert.That(ReferenceEquals(error, completion.Error)).IsTrue();
        }
        await Assert.That(server.ReceivedCommands).IsEmpty();
    }

    [Test]
    [Arguments("TS.GET")]
    [Arguments("TS.RANGE")]
    [Arguments("TS.MGET")]
    [Arguments("TS.INFO")]
    [Arguments("TS.NRANGE")]
    [Arguments("TS.QUERYLABELS")]
    [Arguments("TS.READ")]
    public async Task ConversionFailureIsTheSingleCompletion(string operation)
    {
        var reply = operation == "TS.QUERYLABELS" ? ":1\r\n" : "*1\r\n+malformed\r\n";
        await using var server = new FakeRespServer(Encoding.UTF8.GetBytes(reply));
        var hook = new RecordingHook();
        await using var client = await Connect(Options(server, hook));
        var error = await Failure(() => Invoke(new(client), operation, default));
        var completion = hook.Completed.Single();
        await Assert.That(completion.Command).IsEqualTo(operation);
        await Assert.That(completion.Outcome).IsEqualTo(RespireCommandOutcome.Failed);
        await Assert.That(ReferenceEquals(error, completion.Error)).IsTrue();
    }

    [Test]
    [Arguments("success")]
    [Arguments("sample-error")]
    [Arguments("malformed")]
    [Arguments("server-error")]
    public async Task MultiAddOwnsEveryChunkAndFinalException(string mode)
    {
        var second = mode switch
        {
            "sample-error" => "*1\r\n-ERR sample rejected\r\n",
            "malformed" => "*0\r\n",
            "server-error" => "-ERR chunk rejected\r\n",
            _ => "*1\r\n:2\r\n",
        };
        await using var server = new FakeRespServer("*1\r\n:1\r\n"u8.ToArray(), Encoding.UTF8.GetBytes(second));
        var hook = new RecordingHook();
        await using var client = await Connect(Options(server, hook));
        Exception? error = null;
        try { await new RespireTimeSeriesClient(client).MultiAddAsync([new("a", 1, 1), new("b", 2, 2)], 1); }
        catch (Exception caught) { error = caught; }
        var completion = hook.Completed.Single();
        await Assert.That(completion.Command).IsEqualTo("TS.MADD");
        await Assert.That(completion.Outcome).IsEqualTo(mode == "success" ? RespireCommandOutcome.Succeeded : RespireCommandOutcome.Failed);
        await Assert.That(ReferenceEquals(error, completion.Error)).IsTrue();
        await Assert.That(server.ReceivedCommands.Count).IsEqualTo(2);
        if (mode is "malformed" or "server-error")
            await Assert.That(error is RespireTimeSeriesMultiAddInterruptedException).IsTrue();
        if (mode == "sample-error")
            await Assert.That(error is RespireTimeSeriesMultiAddException).IsTrue();
    }

    [Test]
    public async Task ProjectionAndHookReentrancyHaveIndependentOwners()
    {
        await using var server = new FakeRespServer("*0\r\n"u8.ToArray());
        var hook = new RecordingHook();
        await using var client = await Connect(Options(server, hook));
        var series = new RespireTimeSeriesClient(client);
        Task<RespireTimeSeriesSample?>? admitted = null;
        Task<RespireTimeSeriesSample?>? completed = null;
        Task<RespireTimeSeriesSample?>? projected = null;
        hook.Before = command => { if (command == "TS.INFO") admitted = series.GetAsync("admitted").AsTask(); };
        hook.After = command => { if (command == "TS.INFO") completed = series.GetAsync("completed").AsTask(); };
        var failure = new InvalidOperationException("projection failed");
        var error = await Failure(async () => await series.GetRawInfoAsync<int>("info", result =>
        {
            projected = series.GetAsync("projected").AsTask();
            throw failure;
        }));
        await Task.WhenAll(admitted!, completed!, projected!);
        await Assert.That(ReferenceEquals(error, failure)).IsTrue();
        await Assert.That(hook.Completed.Count).IsEqualTo(4);
        var info = hook.Completed.Single(c => c.Command == "TS.INFO");
        await Assert.That(info.Outcome).IsEqualTo(RespireCommandOutcome.Failed);
        await Assert.That(ReferenceEquals(info.Error, failure)).IsTrue();
        await Assert.That(hook.Completed.Where(c => c.Command == "TS.GET").All(c => c.Outcome == RespireCommandOutcome.Succeeded)).IsTrue();
    }

    [Test]
    public async Task CancellationAfterConfirmedChunkCompletesWithTranslatedException()
    {
        var attempted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new FakeRespServer("*1\r\n:1\r\n"u8.ToArray())
        {
            SuppressReply = command =>
            {
                if (!command.StartsWith("TS.MADD second ", StringComparison.Ordinal)) return false;
                attempted.TrySetResult();
                return true;
            },
        };
        var hook = new RecordingHook();
        await using var client = await Connect(Options(server, hook));
        using var caller = new CancellationTokenSource();
        var write = new RespireTimeSeriesClient(client).MultiAddAsync(
            [new("first", 1, 1), new("second", 2, 2), new("unsent", 3, 3)], 1, caller.Token).AsTask();
        await attempted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await caller.CancelAsync();
        var error = await Failure(async () => await write.WaitAsync(TimeSpan.FromSeconds(5)));
        await Assert.That(error is RespireTimeSeriesMultiAddCanceledException).IsTrue();
        await Assert.That(((OperationCanceledException)error).CancellationToken).IsEqualTo(caller.Token);
        var progress = RespireTimeSeriesMultiAddProgress.FromException(error)!;
        await Assert.That(progress.CompletedSampleCount).IsEqualTo(1);
        await Assert.That(progress.UncertainSampleCount).IsEqualTo(1);
        await Assert.That(progress.UnattemptedSampleCount).IsEqualTo(1);
        var completion = hook.Completed.Single();
        await Assert.That(completion.Outcome).IsEqualTo(RespireCommandOutcome.Canceled);
        await Assert.That(ReferenceEquals(completion.Error, error)).IsTrue();
        await Assert.That(server.ReceivedCommands.Count).IsEqualTo(2);
    }

    [Test]
    public async Task ExternalDecoratorRetainsPublicDispatchAndRawBoundary()
    {
        await using var server = new FakeRespServer("*1\r\n+malformed\r\n"u8.ToArray());
        var hook = new RecordingHook();
        await using var client = await Connect(Options(server, hook));
        var proxy = DispatchProxy.Create<IRespireClient, ClientProxy>();
        var decorator = (ClientProxy)proxy;
        decorator.Inner = client;
        var error = await Failure(async () => await new RespireTimeSeriesClient(proxy).GetAsync("key"));
        await Assert.That(error is InvalidOperationException).IsTrue();
        await Assert.That(decorator.Methods.Single()).IsEqualTo(nameof(IRespireClient.ExecuteAsync));
        await Assert.That(hook.Completed.Single().Outcome).IsEqualTo(RespireCommandOutcome.Succeeded);
    }

    [Test]
    [Arguments("TS.CREATE")]
    [Arguments("TS.CREATERULE")]
    [Arguments("TS.MADD")]
    public async Task PrefixedWritesRetainMutationFencesAndRejectedWritesRetainCache(string operation)
    {
        await using var server = new FakeRespServer("%1\r\n$5\r\nproto\r\n:3\r\n"u8.ToArray(), FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => command.StartsWith("TS.MADD ", StringComparison.Ordinal)
                ? "*1\r\n:1\r\n"u8.ToArray() : null,
        };
        var hook = new RecordingHook();
        await using var client = await Connect(Options(server, hook) with
        {
            Protocol = RespireProtocol.Resp3, ClientSideCache = new(),
        });
        var cache = client.Core.ClientCache!;
        var key = new RespireKey("tenant:a");
        var other = new RespireKey("tenant:other");
        Seed(key);
        Seed(other);
        var series = new RespireTimeSeriesClient(client.WithKeyPrefix("tenant:"));
        hook.Before = _ => throw new InvalidOperationException("denied");
        await Failure(() => Invoke(series, operation, default));
        await Assert.That(cache.TryGet(in key, out var retained)).IsTrue();
        retained.Dispose();
        hook.Before = null;
        hook.Completed.Clear();
        var fencesAtCompletion = -1;
        hook.After = _ => fencesAtCompletion = cache.InspectForTests().ActiveMutationCount;
        await Invoke(series, operation, default);
        await Assert.That(cache.TryGet(in key, out var removed)).IsFalse();
        removed.Dispose();
        await Assert.That(cache.TryGet(in other, out retained)).IsEqualTo(operation != "TS.MADD");
        retained.Dispose();
        await Assert.That(fencesAtCompletion).IsEqualTo(0);
        await Assert.That(hook.Completed.Single().Outcome).IsEqualTo(RespireCommandOutcome.Succeeded);
        await Assert.That(server.ReceivedCommands.Any(c => c.StartsWith(operation + " tenant:a", StringComparison.Ordinal))).IsTrue();

        void Seed(RespireKey cachedKey)
        {
            using var value = RespValue.BulkString("old"u8.ToArray());
            var read = cache.BeginRead(in cachedKey);
            cache.CompleteRead(in read, in value, allowInsert: true);
        }
    }

    public class ClientProxy : DispatchProxy
    {
        internal IRespireClient Inner = null!;
        internal readonly List<string> Methods = [];
        protected override object? Invoke(MethodInfo? method, object?[]? arguments)
        {
            Methods.Add(method!.Name);
            return method.Invoke(Inner, arguments);
        }
    }

    private static readonly string[] Operations =
    [
        "TS.CREATE", "TS.ALTER", "TS.ADD", "TS.MADD", "TS.INCRBY", "TS.DECRBY", "TS.GET", "TS.MGET",
        "TS.RANGE", "TS.REVRANGE", "TS.MRANGE", "TS.MREVRANGE", "TS.DEL", "TS.CREATERULE", "TS.DELETERULE",
        "TS.INFO", "TS.QUERYINDEX", "TS.NRANGE", "TS.NREVRANGE", "TS.QUERYLABELS", "TS.READ",
    ];

    private static async Task Invoke(RespireTimeSeriesClient series, string operation, CancellationToken token)
    {
        switch (operation)
        {
            case "TS.CREATE": await series.CreateAsync("a", cancellationToken: token); break;
            case "TS.ALTER": await series.AlterAsync("a", new(), token); break;
            case "TS.ADD": await series.AddAsync("a", 1, 1, cancellationToken: token); break;
            case "TS.MADD": await series.MultiAddAsync([new("a", 1, 1), new("b", 2, 2)], 1, token); break;
            case "TS.INCRBY": await series.IncrementByAsync("a", 1, cancellationToken: token); break;
            case "TS.DECRBY": await series.DecrementByAsync("a", 1, cancellationToken: token); break;
            case "TS.GET": await series.GetAsync("a", cancellationToken: token); break;
            case "TS.MGET": await series.MultiGetAsync(["room=1"], cancellationToken: token); break;
            case "TS.RANGE": await series.RangeAsync("a", new(0, 10), cancellationToken: token); break;
            case "TS.REVRANGE": await series.ReverseRangeAsync("a", new(0, 10), cancellationToken: token); break;
            case "TS.MRANGE": await series.MultiRangeAsync(new(0, 10), new() { Filters = ["room=1"] }, token); break;
            case "TS.MREVRANGE": await series.MultiReverseRangeAsync(new(0, 10), new() { Filters = ["room=1"] }, token); break;
            case "TS.DEL": await series.DeleteRangeAsync("a", new(0, 10), token); break;
            case "TS.CREATERULE": await series.CreateRuleAsync("a", "b", RespireTimeSeriesAggregation.Avg, 1L, cancellationToken: token); break;
            case "TS.DELETERULE": await series.DeleteRuleAsync("a", "b", token); break;
            case "TS.INFO": await series.GetInfoAsync("a", token); break;
            case "TS.QUERYINDEX": await series.QueryIndexAsync(["room=1"], token); break;
            case "TS.NRANGE": await series.RangeKeysAsync("a", new(0, 10), cancellationToken: token); break;
            case "TS.NREVRANGE": await series.ReverseRangeKeysAsync("a", new(0, 10), cancellationToken: token); break;
            case "TS.QUERYLABELS": await series.QueryLabelsAsync(cancellationToken: token); break;
            case "TS.READ": await series.ReadAsync("a", 0, cancellationToken: token); break;
            default: throw new ArgumentOutOfRangeException(nameof(operation));
        }
    }

    private static ValueTask<RespireClient> Connect(RespireOptions options) => RespireClient.ConnectAsync(options);

    private static RespireOptions Options(FakeRespServer server, RespireCommandHook hook) => new()
    {
        Endpoints = [new("127.0.0.1", server.Port)],
        Protocol = RespireProtocol.Resp2,
        Connections = 1,
        ClientName = null,
        CommandHook = hook,
    };

    private static async Task<Exception> Failure(Func<Task> action)
    {
        try { await action(); }
        catch (Exception error) { return error; }
        throw new InvalidOperationException("Expected failure.");
    }

    private sealed class RecordingHook : RespireCommandHook
    {
        internal Action<string>? Before;
        internal Action<string>? After;
        internal ConcurrentQueue<(string Command, RespireCommandOutcome Outcome, Exception? Error)> Completed { get; } = new();
        public override object? OnExecuting(RespireCommandContext context) { Before?.Invoke(context.Command); return null; }
        public override void OnCompleted(RespireCommandContext context, object? state, RespireCommandOutcome outcome, Exception? error)
        {
            Completed.Enqueue((context.Command, outcome, error));
            After?.Invoke(context.Command);
        }
    }
}
