using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Reflection;
using System.Text.Json.Serialization.Metadata;
using Respire.Json;
using Respire.Protocol;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class JsonCommandHookTests
{
    private static readonly JsonTypeInfo<int> IntInfo = (JsonTypeInfo<int>)new JsonSerializerOptions
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    }.GetTypeInfo(typeof(int));

    [Test]
    [Arguments("get", "JSON.GET")]
    [Arguments("many", "JSON.GET")]
    [Arguments("text", "JSON.GET")]
    [Arguments("multi", "JSON.MGET")]
    [Arguments("set", "JSON.SET")]
    [Arguments("set-text", "JSON.SET")]
    [Arguments("set-bytes", "JSON.SET")]
    [Arguments("set-fpha", "JSON.SET")]
    [Arguments("set-fpha-text", "JSON.SET")]
    [Arguments("set-fpha-bytes", "JSON.SET")]
    [Arguments("merge", "JSON.MERGE")]
    [Arguments("multi-set", "JSON.MSET")]
    [Arguments("delete", "JSON.DEL")]
    [Arguments("forget", "JSON.FORGET")]
    [Arguments("memory", "JSON.DEBUG MEMORY")]
    public async Task TypedOwnersRejectAndCompleteOnce(string route, string operation)
    {
        await using var server = new FakeRespServer(FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => command.StartsWith("JSON.GET ", StringComparison.Ordinal) ? Bulk("1")
                : command.StartsWith("JSON.MGET ", StringComparison.Ordinal) ? Bytes("*1\r\n$1\r\n1\r\n")
                : command.StartsWith("JSON.DEL ", StringComparison.Ordinal)
                    || command.StartsWith("JSON.FORGET ", StringComparison.Ordinal)
                    || command.StartsWith("JSON.DEBUG ", StringComparison.Ordinal) ? Bytes(":1\r\n") : null,
        };
        var hook = new RecordingHook();
        await using var client = await Connect(server, hook);
        var json = client.WithKeyPrefix("tenant:").Json;
        var rejected = new InvalidOperationException("denied");
        hook.Before = _ => throw rejected;
        var baseline = server.CommandsSeen;
        var error = await Failure(() => Execute(default));
        await Assert.That(error).IsSameReferenceAs(rejected);
        await Assert.That(server.CommandsSeen).IsEqualTo(baseline);
        await Check(hook, operation, RespireCommandOutcome.Rejected, error);

        hook.Reset();
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        error = await Failure(() => Execute(canceled.Token));
        await Assert.That(error is OperationCanceledException).IsTrue();
        await Assert.That(server.CommandsSeen).IsEqualTo(baseline);
        await Check(hook, operation, RespireCommandOutcome.Canceled, error);

        hook.Reset();
        await Execute(default);
        await Check(hook, operation, RespireCommandOutcome.Succeeded, null);
        await Assert.That(server.ReceivedCommands.Any(c => c.StartsWith(operation + " tenant:key", StringComparison.Ordinal))).IsTrue();

        async Task Execute(CancellationToken token)
        {
            switch (route)
            {
                case "get": await json.GetAsync("key", IntInfo, cancellationToken: token); break;
                case "many": await json.GetManyAsync("key", IntInfo, default, token); break;
                case "text": await json.GetJsonAsync("key", cancellationToken: token); break;
                case "multi": await json.MultiGetAsync("key", IntInfo, cancellationToken: token); break;
                case "set": await json.SetAsync("key", 1, IntInfo, cancellationToken: token); break;
                case "set-text": await json.SetJsonAsync("key", "1", cancellationToken: token); break;
                case "set-bytes": await json.SetJsonAsync("key", "1"u8.ToArray(), cancellationToken: token); break;
                case "set-fpha": await json.SetAsync(RespireJsonFloatingPointType.Fp32, "key", 1, IntInfo, cancellationToken: token); break;
                case "set-fpha-text": await json.SetJsonAsync(RespireJsonFloatingPointType.Fp32, "key", "1", cancellationToken: token); break;
                case "set-fpha-bytes": await json.SetJsonAsync(RespireJsonFloatingPointType.Fp32, "key", "1"u8.ToArray(), cancellationToken: token); break;
                case "merge": await json.MergeAsync("key", 1, IntInfo, cancellationToken: token); break;
                case "multi-set": await json.MultiSetAsync([new("key", 1)], IntInfo, token); break;
                case "delete": await json.DeleteAsync("key", cancellationToken: token); break;
                case "forget": await json.ForgetAsync("key", cancellationToken: token); break;
                case "memory": await json.GetMemoryUsageAsync("key", cancellationToken: token); break;
            }
        }
    }

    [Test]
    [Arguments("invalid-json")]
    [Arguments("cardinality")]
    [Arguments("multi")]
    public async Task ConversionFailureIsTheOwnerOutcome(string route)
    {
        await using var server = new FakeRespServer(FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => command.StartsWith("JSON.MGET ", StringComparison.Ordinal)
                ? Bytes("*1\r\n$1\r\nx\r\n")
                : command.StartsWith("JSON.GET ", StringComparison.Ordinal) ? Bulk(route == "cardinality" ? "[1,2]" : "x") : null,
        };
        var hook = new RecordingHook();
        await using var client = await Connect(server, hook);
        var error = await Failure(async () =>
        {
            if (route == "multi") await client.Json.MultiGetAsync("key", IntInfo);
            else await client.Json.GetAsync("key", IntInfo, RespireJsonPath.JsonPath("$"));
        });
        await Assert.That(error is JsonException or InvalidOperationException).IsTrue();
        await Check(hook, route == "multi" ? "JSON.MGET" : "JSON.GET", RespireCommandOutcome.Failed, error);
    }

    [Test]
    public async Task ReentrantCommandsAndRawTransfersKeepIndependentScopes()
    {
        await using var server = new FakeRespServer(FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => command.StartsWith("JSON.GET ", StringComparison.Ordinal) ? Bulk("x") : null,
        };
        var hook = new RecordingHook();
        await using var client = await Connect(server, hook);
        Task? nested = null;
        hook.Before = context =>
        {
            if (context.Command == "JSON.GET")
                nested = client.Strings.SetAsync("nested", "value").AsTask();
        };
        using var raw = await client.Json.Commands.GetPathAsync("key", ".");
        await nested!;
        await Assert.That(hook.Completed.Count).IsEqualTo(2);
        await Assert.That(hook.Completed.All(c => c.Outcome == RespireCommandOutcome.Succeeded)).IsTrue();
        hook.Reset();
        hook.Before = context =>
        {
            if (context.Command == "JSON.GET")
                nested = client.Strings.SetAsync("nested", "value").AsTask();
        };
        await Failure(async () => await client.Json.GetAsync("key", IntInfo));
        await nested!;
        await Assert.That(hook.Completed.Count).IsEqualTo(2);
        await Assert.That(hook.Completed.Single(c => c.Context.Command == "JSON.GET").Outcome).IsEqualTo(RespireCommandOutcome.Failed);
        await Assert.That(hook.Completed.Single(c => c.Context.Command == "SET").Outcome).IsEqualTo(RespireCommandOutcome.Succeeded);
    }

    [Test]
    [Arguments("set")]
    [Arguments("merge")]
    [Arguments("multi-set")]
    public async Task SerializationAndCleanupBelongToOwner(string route)
    {
        await using var server = new FakeRespServer(FakeRespServer.OkReply);
        var hook = new RecordingHook();
        await using var client = await Connect(server, hook);
        var failure = new InvalidOperationException("converter failed");
        var converter = new CallbackConverter();
        var options = new JsonSerializerOptions { TypeInfoResolver = new DefaultJsonTypeInfoResolver(), Converters = { converter } };
        var info = (JsonTypeInfo<int>)options.GetTypeInfo(typeof(int));
        var calls = 0;
        converter.WriteValue = (writer, value) => { calls++; writer.WriteStartArray(); throw failure; };
        hook.Before = _ => throw new InvalidOperationException("denied");
        var baseline = server.CommandsSeen;
        await Failure(Write);
        await Assert.That(calls).IsEqualTo(0);
        await Assert.That(server.CommandsSeen).IsEqualTo(baseline);

        hook.Reset();
        var error = await Failure(Write);
        await Assert.That(error).IsSameReferenceAs(failure);
        await Assert.That(calls).IsEqualTo(1);
        await Assert.That(server.CommandsSeen).IsEqualTo(baseline);
        await Check(hook, route switch { "merge" => "JSON.MERGE", "multi-set" => "JSON.MSET", _ => "JSON.SET" }, RespireCommandOutcome.Failed, failure);

        // The next call can reuse this client's serialization buffer after failure.
        // A fresh valid payload must not retain the failed converter's uncommitted array token.
        hook.Reset();
        Task? nested = null;
        converter.WriteValue = (writer, value) =>
        {
            nested = client.Strings.SetAsync("converter-command", "value").AsTask();
            writer.WriteNumberValue(value);
        };
        await Write();
        await nested!;
        await Assert.That(hook.Completed.Count).IsEqualTo(2);
        await Assert.That(hook.Completed.All(c => c.Outcome == RespireCommandOutcome.Succeeded)).IsTrue();
        await Assert.That(server.ReceivedCommands.Any(c => c.EndsWith(" key . 7", StringComparison.Ordinal))).IsTrue();

        Task Write() => route switch
        {
            "merge" => client.Json.MergeAsync("key", 7, info).AsTask(),
            "multi-set" => client.Json.MultiSetAsync([new("key", 7)], info).AsTask(),
            _ => client.Json.SetAsync("key", 7, info).AsTask(),
        };
    }

    [Test]
    public async Task ExternalDecoratorKeepsPublicDispatchAndRawBoundary()
    {
        await using var server = new FakeRespServer(FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => command.StartsWith("JSON.GET ", StringComparison.Ordinal) ? Bulk("x") : null,
        };
        var hook = new RecordingHook();
        await using var client = await Connect(server, hook);
        var proxy = DispatchProxy.Create<IRespireClient, ClientProxy>();
        var decorator = (ClientProxy)proxy;
        decorator.Inner = client;
        var json = new RespireJsonClient(proxy);
        var error = await Failure(async () => await json.GetAsync("key", IntInfo));
        await Assert.That(error is JsonException).IsTrue();
        await Assert.That(decorator.Methods.Single()).IsEqualTo(nameof(IRespireClient.ExecuteAsync));
        // An external decorator supplies only the raw execution hook; Respire must not unwrap it.
        await Check(hook, "JSON.GET", RespireCommandOutcome.Succeeded, null);
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

    private sealed class CallbackConverter : JsonConverter<int>
    {
        internal Action<Utf8JsonWriter, int> WriteValue = null!;
        public override int Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.GetInt32();
        public override void Write(Utf8JsonWriter writer, int value, JsonSerializerOptions options) => WriteValue(writer, value);
    }

    [Test]
    [Arguments("set")]
    [Arguments("multi-set")]
    [Arguments("merge")]
    [Arguments("delete")]
    public async Task WritesRetainMutationFencesAndRejectionLeavesCacheIntact(string route)
    {
        await using var server = new FakeRespServer(Bytes("%1\r\n$5\r\nproto\r\n:3\r\n"), FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => command.StartsWith("JSON.DEL ", StringComparison.Ordinal) ? Bytes(":1\r\n") : null,
        };
        var hook = new RecordingHook();
#pragma warning disable RESP010 // This JSON mutation scenario owns the client and cache entries inspected around admission.
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = [new("127.0.0.1", server.Port)], Protocol = RespireProtocol.Resp3,
            Connections = 1, ThreadPoolMonitoring = false, CommandHook = hook, ClientSideCache = new(),
        });
#pragma warning restore RESP010
        var cache = client.Core.ClientCache!;
        var key = new RespireKey("tenant:key");
        var other = new RespireKey("tenant:other");
        Seed(key);
        Seed(other);
        hook.Before = _ => throw new InvalidOperationException("denied");
        await Failure(Write);
        await Assert.That(cache.TryGet(in key, out var retained)).IsTrue();
        retained.Dispose();
        hook.Reset();
        await Write();
        await Assert.That(cache.TryGet(in key, out var removed)).IsFalse();
        removed.Dispose();
        await Assert.That(cache.TryGet(in other, out retained)).IsTrue();
        retained.Dispose();
        await Assert.That(cache.InspectForTests().ActiveMutationCount).IsEqualTo(0);
        await Assert.That(hook.Completed.Count).IsEqualTo(1);
        await Assert.That(hook.Completed.Single().Outcome).IsEqualTo(RespireCommandOutcome.Succeeded);

        void Seed(RespireKey cachedKey)
        {
            using var value = RespValue.BulkString("old"u8.ToArray());
            var read = cache.BeginRead(in cachedKey);
            cache.CompleteRead(in read, in value, allowInsert: true);
        }
        Task Write() => route switch
        {
            "multi-set" => client.WithKeyPrefix("tenant:").Json.MultiSetAsync([new("key", 7)], IntInfo).AsTask(),
            "merge" => client.WithKeyPrefix("tenant:").Json.MergeAsync("key", 7, IntInfo).AsTask(),
            "delete" => client.WithKeyPrefix("tenant:").Json.DeleteAsync("key").AsTask(),
            _ => client.WithKeyPrefix("tenant:").Json.SetAsync("key", 7, IntInfo).AsTask(),
        };
    }

    private static async Task Check(RecordingHook hook, string command, RespireCommandOutcome outcome, Exception? error)
    {
        await Assert.That(hook.Entered.Count).IsEqualTo(1);
        var completed = hook.Completed.Single();
        await Assert.That(completed.Context).IsSameReferenceAs(hook.Entered.Single());
        await Assert.That(completed.Context.Command).IsEqualTo(command);
        await Assert.That(completed.Outcome).IsEqualTo(outcome);
        await Assert.That(completed.Error).IsSameReferenceAs(error);
    }

    private static ValueTask<RespireClient> Connect(FakeRespServer server, RespireCommandHook hook)
        => RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = [new("127.0.0.1", server.Port)], Protocol = RespireProtocol.Resp2,
            Connections = 1, ThreadPoolMonitoring = false, CommandHook = hook,
        });

    private static async Task<Exception> Failure(Func<Task> call)
    {
        try { await call(); }
        catch (Exception error) { return error; }
        throw new InvalidOperationException("Expected failure.");
    }

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);
    private static byte[] Bulk(string text) => Bytes($"${Encoding.UTF8.GetByteCount(text)}\r\n{text}\r\n");

    private sealed class RecordingHook : RespireCommandHook
    {
        internal readonly ConcurrentQueue<RespireCommandContext> Entered = new();
        internal readonly ConcurrentQueue<(RespireCommandContext Context, RespireCommandOutcome Outcome, Exception? Error)> Completed = new();
        internal Action<RespireCommandContext>? Before;
        public override object? OnExecuting(RespireCommandContext context)
        {
            Entered.Enqueue(context);
            Before?.Invoke(context);
            return null;
        }
        public override void OnCompleted(RespireCommandContext context, object? state, RespireCommandOutcome outcome, Exception? error)
            => Completed.Enqueue((context, outcome, error));
        internal void Reset() { Entered.Clear(); Completed.Clear(); Before = null; }
    }
}
