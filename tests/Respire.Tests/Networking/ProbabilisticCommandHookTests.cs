using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using Respire.Probabilistic;
using Respire.Protocol;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class ProbabilisticCommandHookTests
{
    [Test]
    [Arguments("BF.RESERVE")]
    [Arguments("BF.ADD")]
    [Arguments("BF.EXISTS")]
    [Arguments("BF.MADD")]
    [Arguments("BF.MEXISTS")]
    [Arguments("BF.INSERT")]
    [Arguments("BF.CARD")]
    [Arguments("BF.SCANDUMP")]
    [Arguments("BF.LOADCHUNK")]
    [Arguments("CF.RESERVE")]
    [Arguments("CF.ADD")]
    [Arguments("CF.ADDNX")]
    [Arguments("CF.INSERT")]
    [Arguments("CF.INSERTNX")]
    [Arguments("CF.DEL")]
    [Arguments("CF.EXISTS")]
    [Arguments("CF.MEXISTS")]
    [Arguments("CF.COUNT")]
    [Arguments("CF.SCANDUMP")]
    [Arguments("CF.LOADCHUNK")]
    [Arguments("CMS.INITBYDIM")]
    [Arguments("CMS.INITBYPROB")]
    [Arguments("CMS.INCRBY")]
    [Arguments("CMS.QUERY")]
    [Arguments("CMS.MERGE")]
    [Arguments("TOPK.RESERVE")]
    [Arguments("TOPK.ADD")]
    [Arguments("TOPK.INCRBY")]
    [Arguments("TOPK.QUERY")]
    [Arguments("TOPK.COUNT")]
    [Arguments("TDIGEST.CREATE")]
    [Arguments("TDIGEST.RESET")]
    [Arguments("TDIGEST.MERGE")]
    [Arguments("TDIGEST.ADD")]
    [Arguments("TDIGEST.MIN")]
    [Arguments("TDIGEST.MAX")]
    [Arguments("TDIGEST.QUANTILE")]
    [Arguments("TDIGEST.CDF")]
    [Arguments("TDIGEST.RANK")]
    [Arguments("TDIGEST.REVRANK")]
    [Arguments("TDIGEST.BYRANK")]
    [Arguments("TDIGEST.BYREVRANK")]
    [Arguments("TDIGEST.TRIMMED_MEAN")]
    public async Task TypedOwnersRejectCancelAndCompleteOnce(string command)
    {
        await using var server = Server(command, ValidReply(command));
        var hook = new RecordingHook();
        await using var client = await Connect(server, hook);
        var module = client.WithKeyPrefix("tenant:").Probabilistic;
        var rejected = new InvalidOperationException("denied");
        hook.Before = _ => throw rejected;
        var baseline = server.CommandsSeen;
        var error = await Failure(() => Execute(module, command));
        await Assert.That(error).IsSameReferenceAs(rejected);
        await Assert.That(server.CommandsSeen).IsEqualTo(baseline);
        await Check(hook, command, RespireCommandOutcome.Rejected, error);

        hook.Reset();
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        error = await Failure(() => Execute(module, command, canceled.Token));
        await Assert.That(error is OperationCanceledException).IsTrue();
        await Assert.That(server.CommandsSeen).IsEqualTo(baseline);
        await Check(hook, command, RespireCommandOutcome.Canceled, error);

        hook.Reset();
        await Execute(module, command);
        await Check(hook, command, RespireCommandOutcome.Succeeded, null);
        await Assert.That(server.ReceivedCommands.Count(c => c.StartsWith(command + " tenant:key", StringComparison.Ordinal))).IsEqualTo(1);
        if (command is "CMS.MERGE" or "TDIGEST.MERGE")
            await Assert.That(server.ReceivedCommands.Contains(command + " tenant:key 1 tenant:source")).IsTrue();
    }

    [Test]
    [Arguments("CF.INSERT", "*2\r\n:1\r\n:2\r\n")]
    [Arguments("TDIGEST.MIN", "+invalid\r\n")]
    [Arguments("TDIGEST.QUANTILE", "*2\r\n$1\r\n1\r\n+invalid\r\n")]
    [Arguments("BF.SCANDUMP", "*1\r\n:0\r\n")]
    [Arguments("CF.SCANDUMP", "+invalid\r\n")]
    [Arguments("TOPK.ADD", "-ERR unavailable\r\n")]
    public async Task ConversionAndServerFailuresAreReportedAfterConversion(string command, string reply)
    {
        await using var server = Server(command, reply);
        var hook = new RecordingHook();
        await using var client = await Connect(server, hook);
        var error = await Failure(() => Execute(client.Probabilistic, command));
        await Check(hook, command, RespireCommandOutcome.Failed, error);
    }

    [Test]
    public async Task CancellationAfterWireAdmissionCompletesTheTypedOwnerOnce()
    {
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = Server("BF.SCANDUMP", ValidReply("BF.SCANDUMP"));
        server.SuppressReply = command =>
        {
            if (!command.StartsWith("BF.SCANDUMP ", StringComparison.Ordinal)) return false;
            received.TrySetResult();
            return true;
        };
        var hook = new RecordingHook();
        await using var client = await Connect(server, hook);
        using var cancellation = new CancellationTokenSource();
        var pending = client.Probabilistic.BloomScanDumpAsync("key", cancellationToken: cancellation.Token).AsTask();
        await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        var error = await Failure(async () => await pending.WaitAsync(TimeSpan.FromSeconds(10)));
        await Assert.That(error is OperationCanceledException).IsTrue();
        await Check(hook, "BF.SCANDUMP", RespireCommandOutcome.Canceled, error);
    }

    [Test]
    public async Task InvalidArgumentsCompleteTheOwnerWithoutWireWork()
    {
        await using var server = Server("BF.RESERVE", "+OK\r\n");
        var hook = new RecordingHook();
        await using var client = await Connect(server, hook);
        var baseline = server.CommandsSeen;
        var error = await Failure(async () => await client.Probabilistic.BloomReserveAsync("key", 0.01, 0));
        await Assert.That(error is ArgumentOutOfRangeException).IsTrue();
        await Assert.That(server.CommandsSeen).IsEqualTo(baseline);
        await Check(hook, "BF.RESERVE", RespireCommandOutcome.Failed, error);
    }

    [Test]
    public async Task AdmissionPrecedesApplicationEnumerationAndNestedCommandsKeepTheirScopes()
    {
        await using var server = Server("BF.MADD", "*1\r\n:1\r\n");
        var hook = new RecordingHook();
        await using var client = await Connect(server, hook);
        Task? nested = null;
        var items = new CallbackItems(() => nested = client.Strings.SetAsync("nested", "value").AsTask());
        hook.Before = _ => throw new InvalidOperationException("denied");
        await Failure(async () => await client.Probabilistic.BloomMultiAddAsync("key", items));
        await Assert.That(items.Reads).IsEqualTo(0);

        hook.Reset();
        await client.Probabilistic.BloomMultiAddAsync("key", items);
        await nested!;
        await Assert.That(hook.Completed.Count).IsEqualTo(2);
        await Assert.That(hook.Completed.Single(c => c.Context.Command == "BF.MADD").Outcome).IsEqualTo(RespireCommandOutcome.Succeeded);
        await Assert.That(hook.Completed.Single(c => c.Context.Command == "SET").Outcome).IsEqualTo(RespireCommandOutcome.Succeeded);

        hook.Reset();
        var failure = new InvalidOperationException("enumeration failed");
        items = new CallbackItems(() => throw failure);
        var baseline = server.CommandsSeen;
        var error = await Failure(async () => await client.Probabilistic.BloomMultiAddAsync("key", items));
        await Assert.That(error).IsSameReferenceAs(failure);
        await Assert.That(server.CommandsSeen).IsEqualTo(baseline);
        await Check(hook, "BF.MADD", RespireCommandOutcome.Failed, error);
    }

    [Test]
    public async Task HookReentrancyAndCompletionExceptionsDoNotChangeResults()
    {
        await using var server = Server("BF.ADD", ":1\r\n");
        var hook = new RecordingHook();
        await using var client = await Connect(server, hook);
        Task? nested = null;
        hook.Before = context =>
        {
            if (context.Command == "BF.ADD") nested = client.Strings.SetAsync("nested", "value").AsTask();
        };
        hook.After = () => throw new InvalidOperationException("observer failed");
        await Assert.That(await client.Probabilistic.BloomAddAsync("key", "item")).IsTrue();
        await nested!;
        await Assert.That(hook.Completed.Count).IsEqualTo(2);
        await Assert.That(hook.Completed.All(c => c.Outcome == RespireCommandOutcome.Succeeded)).IsTrue();
    }

    [Test]
    [Arguments("BF.INFO")]
    [Arguments("CF.INFO")]
    [Arguments("CMS.INFO")]
    [Arguments("TOPK.INFO")]
    [Arguments("TOPK.LIST")]
    [Arguments("TDIGEST.INFO")]
    public async Task RawTransfersCompleteBeforeCallerConversion(string command)
    {
        await using var server = Server(command, "*2\r\n+size\r\n:7\r\n");
        var hook = new RecordingHook();
        await using var client = await Connect(server, hook);
        var module = client.Probabilistic;
        using var raw = await (command switch
        {
            "BF.INFO" => module.BloomInfoAsync("key"),
            "CF.INFO" => module.CuckooInfoAsync("key"),
            "CMS.INFO" => module.CountMinInfoAsync("key"),
            "TOPK.INFO" => module.TopKInfoAsync("key"),
            "TOPK.LIST" => module.TopKListAsync("key", withCount: true),
            _ => module.TDigestInfoAsync("key"),
        });
        await Check(hook, command, RespireCommandOutcome.Succeeded, null);
        await Assert.That(raw[0].AsString()).IsEqualTo("size");
        await Assert.That(raw[1].AsInteger()).IsEqualTo(7);
    }

    [Test]
    public async Task ExternalDecoratorKeepsPublicDispatchAndRawBoundary()
    {
        await using var server = Server("BF.SCANDUMP", "*1\r\n:0\r\n");
        var hook = new RecordingHook();
        await using var client = await Connect(server, hook);
        var proxy = DispatchProxy.Create<IRespireClient, ClientProxy>();
        var decorator = (ClientProxy)proxy;
        decorator.Inner = client;
        var module = new RespireProbabilisticClient(proxy);
        var error = await Failure(async () => await module.BloomScanDumpAsync("key"));
        await Assert.That(error is InvalidOperationException).IsTrue();
        await Assert.That(decorator.Methods.Single()).IsEqualTo(nameof(IRespireClient.ExecuteAsync));
        await Check(hook, "BF.SCANDUMP", RespireCommandOutcome.Succeeded, null);
    }

    [Test]
    [Arguments("BF.ADD")]
    [Arguments("CMS.MERGE")]
    [Arguments("TDIGEST.MERGE")]
    public async Task WritesRetainMutationFencesAndRejectionLeavesCacheIntact(string command)
    {
        await using var server = Server(command, ValidReply(command));
        var hook = new RecordingHook();
#pragma warning disable RESP010 // This probabilistic mutation scenario owns the client and cache entries inspected around admission.
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = [new("127.0.0.1", server.Port)], Protocol = RespireProtocol.Resp3,
            Connections = 1, ThreadPoolMonitoring = false, CommandHook = hook, ClientSideCache = new(),
        });
#pragma warning restore RESP010
        var cache = client.Core.ClientCache!;
        var key = new RespireKey("tenant:key");
        var source = new RespireKey("tenant:source");
        Seed(key);
        Seed(source);
        hook.Before = _ => throw new InvalidOperationException("denied");
        await Failure(() => Execute(client.WithKeyPrefix("tenant:").Probabilistic, command));
        await Assert.That(cache.TryGet(in key, out var retained)).IsTrue();
        retained.Dispose();
        hook.Reset();
        await Execute(client.WithKeyPrefix("tenant:").Probabilistic, command);
        await Assert.That(cache.TryGet(in key, out var removed)).IsFalse();
        removed.Dispose();
        await Assert.That(cache.TryGet(in source, out retained)).IsTrue();
        retained.Dispose();
        await Assert.That(cache.InspectForTests().ActiveMutationCount).IsEqualTo(0);
        await Check(hook, command, RespireCommandOutcome.Succeeded, null);

        void Seed(RespireKey cachedKey)
        {
            using var value = RespValue.BulkString("old"u8.ToArray());
            var read = cache.BeginRead(in cachedKey);
            cache.CompleteRead(in read, in value, allowInsert: true);
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task OwnedByteResultsSurviveLaterCommands(bool hooked)
    {
        await using var server = new FakeRespServer(FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => command.StartsWith("BF.SCANDUMP ", StringComparison.Ordinal)
                ? Bytes("*2\r\n:3\r\n$4\r\ndata\r\n")
                : command.StartsWith("TOPK.ADD ", StringComparison.Ordinal) ? Bytes("*2\r\n$4\r\nitem\r\n$-1\r\n") : null,
        };
        var hook = hooked ? new RecordingHook() : null;
        await using var client = await Connect(server, hook);
        var chunk = await client.Probabilistic.BloomScanDumpAsync("key");
        var evicted = await client.Probabilistic.TopKAddAsync("key", ["a", "b"]);
        await client.Strings.SetAsync("other", new string('x', 4096));
        await Assert.That(chunk.Iterator).IsEqualTo(3);
        await Assert.That(Encoding.UTF8.GetString(chunk.Data!)).IsEqualTo("data");
        await Assert.That(Encoding.UTF8.GetString(evicted[0]!)).IsEqualTo("item");
        await Assert.That(evicted[1]).IsNull();
        if (hook is not null) await Assert.That(hook.Completed.Count).IsEqualTo(3);
    }

    private static async Task Execute(RespireProbabilisticClient module, string command, CancellationToken token = default)
    {
        switch (command)
        {
            case "BF.RESERVE": await module.BloomReserveAsync("key", 0.01, 100, cancellationToken: token); break;
            case "BF.ADD": await module.BloomAddAsync("key", "item", token); break;
            case "BF.EXISTS": await module.BloomExistsAsync("key", "item", token); break;
            case "BF.MADD": await module.BloomMultiAddAsync("key", ["item"], token); break;
            case "BF.MEXISTS": await module.BloomMultiExistsAsync("key", ["item"], token); break;
            case "BF.INSERT": await module.BloomInsertAsync("key", ["item"], cancellationToken: token); break;
            case "BF.CARD": await module.BloomCardinalityAsync("key", token); break;
            case "BF.SCANDUMP": await module.BloomScanDumpAsync("key", cancellationToken: token); break;
            case "BF.LOADCHUNK": await module.BloomLoadChunkAsync("key", new(1, "data"u8.ToArray()), token); break;
            case "CF.RESERVE": await module.CuckooReserveAsync("key", 100, cancellationToken: token); break;
            case "CF.ADD": await module.CuckooAddAsync("key", "item", token); break;
            case "CF.ADDNX": await module.CuckooAddIfAbsentAsync("key", "item", token); break;
            case "CF.INSERT": await module.CuckooInsertAsync("key", ["item"], cancellationToken: token); break;
            case "CF.INSERTNX": await module.CuckooInsertIfAbsentAsync("key", ["item"], cancellationToken: token); break;
            case "CF.DEL": await module.CuckooDeleteAsync("key", "item", token); break;
            case "CF.EXISTS": await module.CuckooExistsAsync("key", "item", token); break;
            case "CF.MEXISTS": await module.CuckooMultiExistsAsync("key", ["item"], token); break;
            case "CF.COUNT": await module.CuckooCountAsync("key", "item", token); break;
            case "CF.SCANDUMP": await module.CuckooScanDumpAsync("key", cancellationToken: token); break;
            case "CF.LOADCHUNK": await module.CuckooLoadChunkAsync("key", new(1, "data"u8.ToArray()), token); break;
            case "CMS.INITBYDIM": await module.CountMinInitializeByDimensionsAsync("key", 100, 5, token); break;
            case "CMS.INITBYPROB": await module.CountMinInitializeByProbabilityAsync("key", 0.01, 0.01, token); break;
            case "CMS.INCRBY": await module.CountMinIncrementAsync("key", new Dictionary<RespireValue, long> { ["item"] = 1 }, token); break;
            case "CMS.QUERY": await module.CountMinQueryAsync("key", ["item"], token); break;
            case "CMS.MERGE": await module.CountMinMergeAsync("key", "source", cancellationToken: token); break;
            case "TOPK.RESERVE": await module.TopKReserveAsync("key", 5, cancellationToken: token); break;
            case "TOPK.ADD": await module.TopKAddAsync("key", ["item"], token); break;
            case "TOPK.INCRBY": await module.TopKIncrementAsync("key", new Dictionary<RespireValue, long> { ["item"] = 1 }, token); break;
            case "TOPK.QUERY": await module.TopKQueryAsync("key", ["item"], token); break;
            case "TOPK.COUNT": await module.TopKCountAsync("key", ["item"], token); break;
            case "TDIGEST.CREATE": await module.TDigestCreateAsync("key", cancellationToken: token); break;
            case "TDIGEST.RESET": await module.TDigestResetAsync("key", token); break;
            case "TDIGEST.MERGE": await module.TDigestMergeAsync("key", "source", cancellationToken: token); break;
            case "TDIGEST.ADD": await module.TDigestAddAsync("key", [1.0], token); break;
            case "TDIGEST.MIN": await module.TDigestMinimumAsync("key", token); break;
            case "TDIGEST.MAX": await module.TDigestMaximumAsync("key", token); break;
            case "TDIGEST.QUANTILE": await module.TDigestQuantileAsync("key", [0.5], token); break;
            case "TDIGEST.CDF": await module.TDigestCdfAsync("key", [1.0], token); break;
            case "TDIGEST.RANK": await module.TDigestRankAsync("key", [1.0], token); break;
            case "TDIGEST.REVRANK": await module.TDigestReverseRankAsync("key", [1.0], token); break;
            case "TDIGEST.BYRANK": await module.TDigestByRankAsync("key", [1L], token); break;
            case "TDIGEST.BYREVRANK": await module.TDigestByReverseRankAsync("key", [1L], token); break;
            case "TDIGEST.TRIMMED_MEAN": await module.TDigestTrimmedMeanAsync("key", 0.1, 0.9, token); break;
            default: throw new ArgumentException(command);
        }
    }

    private static string ValidReply(string command) => command switch
    {
        "BF.SCANDUMP" or "CF.SCANDUMP" => "*2\r\n:0\r\n$-1\r\n",
        "BF.MADD" or "BF.MEXISTS" or "BF.INSERT" or "CF.INSERT" or "CF.INSERTNX" or "CF.MEXISTS"
            or "CMS.INCRBY" or "CMS.QUERY" or "TOPK.QUERY" or "TOPK.COUNT"
            or "TDIGEST.RANK" or "TDIGEST.REVRANK" => "*1\r\n:1\r\n",
        "TDIGEST.QUANTILE" or "TDIGEST.CDF" or "TDIGEST.BYRANK" or "TDIGEST.BYREVRANK" => "*1\r\n$3\r\n1.5\r\n",
        "TDIGEST.MIN" or "TDIGEST.MAX" or "TDIGEST.TRIMMED_MEAN" => "$3\r\n1.5\r\n",
        "TOPK.ADD" or "TOPK.INCRBY" => "*1\r\n$-1\r\n",
        "BF.ADD" or "BF.EXISTS" or "BF.CARD" or "CF.ADD" or "CF.ADDNX" or "CF.DEL" or "CF.EXISTS" or "CF.COUNT" => ":1\r\n",
        _ => "+OK\r\n",
    };

    private static FakeRespServer Server(string operation, string reply) => new(FakeRespServer.OkReply)
    {
        ReplyOverride = (_, command) => command == "HELLO 3" ? Bytes("%1\r\n+proto\r\n:3\r\n")
            : command.StartsWith(operation + " ", StringComparison.Ordinal) ? Bytes(reply) : null,
    };

    private static ValueTask<RespireClient> Connect(FakeRespServer server, RespireCommandHook? hook)
        => RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = [new("127.0.0.1", server.Port)], Protocol = RespireProtocol.Resp2,
            Connections = 1, ThreadPoolMonitoring = false, CommandHook = hook,
        });

    private static async Task Check(RecordingHook hook, string command, RespireCommandOutcome outcome, Exception? error)
    {
        await Assert.That(hook.Entered.Count).IsEqualTo(1);
        var completed = hook.Completed.Single();
        await Assert.That(completed.Context).IsSameReferenceAs(hook.Entered.Single());
        await Assert.That(completed.Context.Command).IsEqualTo(command);
        await Assert.That(completed.Outcome).IsEqualTo(outcome);
        await Assert.That(completed.Error).IsSameReferenceAs(error);
    }

    private static async Task<Exception> Failure(Func<Task> call)
    {
        try { await call(); }
        catch (Exception error) { return error; }
        throw new InvalidOperationException("Expected failure.");
    }

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    private sealed class CallbackItems(Action firstRead) : IReadOnlyList<RespireValue>
    {
        internal int Reads;
        public int Count => 1;
        public RespireValue this[int index] { get { if (++Reads == 1) firstRead(); return "item"; } }
        public IEnumerator<RespireValue> GetEnumerator() { yield return this[0]; }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
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

    private sealed class RecordingHook : RespireCommandHook
    {
        internal readonly ConcurrentQueue<RespireCommandContext> Entered = new();
        internal readonly ConcurrentQueue<(RespireCommandContext Context, RespireCommandOutcome Outcome, Exception? Error)> Completed = new();
        internal Action<RespireCommandContext>? Before;
        internal Action? After;
        public override object? OnExecuting(RespireCommandContext context)
        {
            Entered.Enqueue(context);
            Before?.Invoke(context);
            return null;
        }
        public override void OnCompleted(RespireCommandContext context, object? state, RespireCommandOutcome outcome, Exception? error)
        {
            Completed.Enqueue((context, outcome, error));
            After?.Invoke();
        }
        internal void Reset() { Entered.Clear(); Completed.Clear(); Before = null; After = null; }
    }
}
