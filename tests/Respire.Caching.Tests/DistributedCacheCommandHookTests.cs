using System.Buffers;
using System.Collections.Concurrent;
using Respire.Compression;
using Respire.Tests.Networking;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Caching.Tests;

public class DistributedCacheCommandHookTests
{
    [Test]
    [Arguments("get", "key")]
    [Arguments("try-get", "key")]
    [Arguments("try-get", "destination")]
    [Arguments("set", "key")]
    [Arguments("set", "value")]
    [Arguments("set", "options")]
    [Arguments("set-buffer", "key")]
    [Arguments("set-buffer", "options")]
    [Arguments("refresh", "key")]
    [Arguments("remove", "key")]
    public async Task InvalidInputPrecedesAdmission(string route, string parameter)
    {
        await using var server = Server();
        var admissions = 0;
        var hook = new Hook
        {
            Before = _ =>
            {
                Interlocked.Increment(ref admissions);
                throw new InvalidOperationException("Must validate before admission");
            },
        };
        await using var client = await RespireClient.ConnectAsync(Options(server, hook));
        await using var cache = new RespireDistributedCache(client);
        var key = parameter == "key" ? null! : "key";
        var options = parameter == "options" ? null! : new Microsoft.Extensions.Caching.Distributed.DistributedCacheEntryOptions();

        var failure = await Assert.That(async () =>
        {
            switch (route)
            {
                case "get": await cache.GetAsync(key); break;
                case "try-get": await cache.TryGetAsync(key, parameter == "destination" ? null! : new ArrayBufferWriter<byte>()); break;
                case "set": await cache.SetAsync(key, parameter == "value" ? null! : new byte[] { 1 }, options); break;
                case "set-buffer": await cache.SetAsync(key, ReadOnlySequence<byte>.Empty, options); break;
                case "refresh": await cache.RefreshAsync(key); break;
                default: await cache.RemoveAsync(key); break;
            }
        }).Throws<ArgumentNullException>();

        await Assert.That(failure!.ParamName).IsEqualTo(parameter);
        await Assert.That(admissions).IsEqualTo(0);
        await Assert.That(hook.Completed).IsEmpty();
        await Assert.That(server.ReceivedCommands).IsEmpty();
    }

    [Test]
    public async Task DestinationWriteFailureIsThePublicOutcome()
    {
        await using var server = Server();
        var hook = new Hook();
        await using var client = await RespireClient.ConnectAsync(Options(server, hook));
        await using var cache = new RespireDistributedCache(client);
        var expected = new InvalidOperationException("Destination rejected payload");

        var failure = await Assert.That(async () => await cache.TryGetAsync("key", new ThrowingWriter(expected)))
            .Throws<InvalidOperationException>();

        await Assert.That(failure).IsSameReferenceAs(expected);
        await Assert.That(hook.Completed.Count).IsEqualTo(1);
        var completion = hook.Completed.Single();
        await Assert.That(completion.Context.Command).IsEqualTo("GET");
        await Assert.That(completion.Outcome).IsEqualTo(RespireCommandOutcome.Failed);
        await Assert.That(completion.Error).IsSameReferenceAs(expected);
    }

    [Test]
    [Arguments("get", false)]
    [Arguments("get", true)]
    [Arguments("try-get", false)]
    [Arguments("try-get", true)]
    [Arguments("set", false)]
    [Arguments("set", true)]
    [Arguments("set-buffer", false)]
    [Arguments("set-buffer", true)]
    [Arguments("refresh", false)]
    [Arguments("refresh", true)]
    [Arguments("remove", false)]
    [Arguments("remove", true)]
    public async Task RejectionPrecedesTrackingEncodingAndMutation(string route, bool synchronous)
    {
        await using var server = Server();
        var rejection = new InvalidOperationException("Admission denied");
        var hook = new Hook { Before = _ => throw rejection };
        await using var client = await RespireClient.ConnectAsync(Options(server, hook));
        var codec = new Codec();
        await using var cache = new RespireDistributedCache((IRespireClient)client, new() { ValueCodec = codec });

        var failure = await Assert.That(async () => await Execute(cache, route, synchronous))
            .Throws<InvalidOperationException>();

        await Assert.That(failure).IsSameReferenceAs(rejection);
        await Assert.That(server.ReceivedCommands).IsEmpty();
        await Assert.That(codec.Calls).IsEqualTo(0);
        await Assert.That(hook.Completed.Count).IsEqualTo(1);
        var completion = hook.Completed.Single();
        await Assert.That(completion.Context.Command).IsEqualTo(Command(route));
        await Assert.That(completion.Outcome).IsEqualTo(RespireCommandOutcome.Rejected);
        await Assert.That(completion.Error).IsSameReferenceAs(rejection);
        await Assert.That(completion.State).IsNull();
    }

    [Test]
    [Arguments("get")]
    [Arguments("try-get")]
    [Arguments("set")]
    [Arguments("set-buffer")]
    [Arguments("refresh")]
    [Arguments("remove")]
    public async Task PreCanceledCallHasOneCanceledCompletionAndNoWire(string route)
    {
        await using var server = Server();
        var hook = new Hook();
        await using var client = await RespireClient.ConnectAsync(Options(server, hook));
        await using var cache = new RespireDistributedCache(client);
        using var source = new CancellationTokenSource();
        source.Cancel();

        var failure = await Assert.That(async () => await Execute(cache, route, token: source.Token))
            .Throws<OperationCanceledException>();

        await Assert.That(failure!.CancellationToken).IsEqualTo(source.Token);
        await Assert.That(server.ReceivedCommands).IsEmpty();
        await Assert.That(hook.Completed.Count).IsEqualTo(1);
        var completion = hook.Completed.Single();
        await Assert.That(completion.Context.CancellationToken).IsEqualTo(source.Token);
        await Assert.That(completion.Outcome).IsEqualTo(RespireCommandOutcome.Canceled);
        await Assert.That(completion.Error).IsSameReferenceAs(failure);
    }

    [Test]
    [Arguments("get")]
    [Arguments("try-get")]
    [Arguments("set")]
    [Arguments("set-buffer")]
    [Arguments("refresh")]
    [Arguments("remove")]
    public async Task InternalScriptFallbackAndLeasesCompleteOnlyThePublicOwner(string route)
    {
        await using var server = Server(fallback: true);
        var hook = new Hook { ThrowOnCompleted = true };
        await using var client = await RespireClient.ConnectAsync(Options(server, hook));
        var codec = new Codec();
        await using var cache = new RespireDistributedCache((IRespireClient)client,
            new() { InstanceName = "cache:", ValueCodec = codec });
        hook.Before = _ =>
        {
            if (server.CommandsSeen != 0 || codec.Calls != 0)
                throw new InvalidOperationException("Admission was late");
        };

        await Execute(cache, route);

        await Assert.That(hook.Completed.Count).IsEqualTo(1);
        var completion = hook.Completed.Single();
        await Assert.That(completion.Context.Command).IsEqualTo(Command(route));
        await Assert.That(completion.Outcome).IsEqualTo(RespireCommandOutcome.Succeeded);
        await Assert.That(completion.State).IsSameReferenceAs(completion.Context);
        await Assert.That(completion.Error).IsNull();
        await Assert.That(server.ReceivedCommands.Any(command => command.Contains("cache:key", StringComparison.Ordinal))).IsTrue();
        if (route != "remove")
        {
            await Assert.That(server.ReceivedCommands.Any(command => command.StartsWith("EVALSHA ", StringComparison.Ordinal))).IsTrue();
            await Assert.That(server.ReceivedCommands.Any(command => command.StartsWith("EVAL ", StringComparison.Ordinal))).IsTrue();
        }
        await Assert.That(codec.Calls).IsEqualTo(route is "refresh" or "remove" ? 0 : 1);
    }

    [Test]
    [Arguments("get")]
    [Arguments("try-get")]
    [Arguments("set")]
    [Arguments("set-buffer")]
    public async Task CodecFailureIsThePublicOutcome(string route)
    {
        await using var server = Server();
        var hook = new Hook();
        await using var client = await RespireClient.ConnectAsync(Options(server, hook));
        var expected = new InvalidOperationException("Codec failed");
        var codec = new Codec { Failure = expected };
        await using var cache = new RespireDistributedCache((IRespireClient)client, new() { ValueCodec = codec });

        var failure = await Assert.That(async () => await Execute(cache, route)).Throws<InvalidOperationException>();

        await Assert.That(failure).IsSameReferenceAs(expected);
        await Assert.That(hook.Completed.Count).IsEqualTo(1);
        var completion = hook.Completed.Single();
        await Assert.That(completion.Outcome).IsEqualTo(RespireCommandOutcome.Failed);
        await Assert.That(completion.Error).IsSameReferenceAs(expected);
        if (route.StartsWith("set", StringComparison.Ordinal))
            await Assert.That(server.ReceivedCommands).IsEmpty();
    }

    [Test]
    public async Task ForegroundCorrectionFinishesBeforeCompletion()
    {
        await using var server = Server();
        var hook = new Hook();
        await using var client = await RespireClient.ConnectAsync(Options(server, hook));
        await using var cache = new RespireDistributedCache(client);
        var correcting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var getPrefix = $"EVALSHA {RespireDistributedCache.GetAndRefreshScript.Sha1} ";
        var correctionPrefix = $"EVALSHA {RespireDistributedCache.CapRefreshedTtlScript.Sha1} ";
        server.DelayCommand(getPrefix, 1100);
        server.GateCommand(correctionPrefix, release.Task, () => correcting.TrySetResult());
        server.ReplyOverride = (_, command) => command == "CLIENT ID" ? ":123\r\n"u8.ToArray()
            : command.StartsWith(getPrefix, StringComparison.Ordinal) ? "*2\r\n:1\r\n$5\r\nvalue\r\n"u8.ToArray()
            : ":1\r\n"u8.ToArray();

        var pending = cache.GetAsync("key");
        try
        {
            await correcting.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.That(hook.Completed).IsEmpty();
            await Assert.That(pending.IsCompleted).IsFalse();
        }
        finally { release.TrySetResult(); }
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(result!.AsSpan().SequenceEqual("value"u8)).IsTrue();
        await Assert.That(hook.Completed.Count).IsEqualTo(1);
        await Assert.That(hook.Completed.Single().Outcome).IsEqualTo(RespireCommandOutcome.Succeeded);
    }

    [Test]
    public async Task ApplicationCommandFromHookRetainsItsOwnScope()
    {
        await using var server = Server();
        var hook = new Hook();
        await using var client = await RespireClient.ConnectAsync(Options(server, hook));
        await using var cache = new RespireDistributedCache(client);
        Task? nested = null;
        hook.Before = context =>
        {
            if (context.Command == "GET") nested = client.SetAsync("nested", "value").AsTask();
        };

        await cache.GetAsync("key");
        await nested!;

        await Assert.That(hook.Completed.Count).IsEqualTo(2);
        await Assert.That(hook.Completed.Count(item => item.Context.Command == "GET")).IsEqualTo(1);
        await Assert.That(hook.Completed.Count(item => item.Context.Command == "SET")).IsEqualTo(1);
    }

    private static FakeRespServer Server(bool fallback = false)
        => new(8, FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => command switch
            {
                "CLIENT ID" => ":123\r\n"u8.ToArray(),
                _ when command.StartsWith("CLIENT KILL ", StringComparison.Ordinal) => ":0\r\n"u8.ToArray(),
                _ when fallback && command.StartsWith("EVALSHA ", StringComparison.Ordinal) => "-NOSCRIPT missing\r\n"u8.ToArray(),
                _ when command.StartsWith($"EVALSHA {RespireDistributedCache.GetAndRefreshScript.Sha1} ", StringComparison.Ordinal)
                    || command.StartsWith("EVAL " + RespireDistributedCache.GetAndRefreshScript.Source, StringComparison.Ordinal)
                    => "*2\r\n:0\r\n$5\r\nvalue\r\n"u8.ToArray(),
                _ when command.StartsWith("EVAL", StringComparison.Ordinal) => ":1\r\n"u8.ToArray(),
                _ => null,
            },
        };

    private static RespireOptions Options(FakeRespServer server, Hook hook) => new()
    {
        Protocol = RespireProtocol.Resp2, Endpoints = [new("127.0.0.1", server.Port)],
        Connections = 1, CommandTimeout = TimeSpan.FromSeconds(5), ThreadPoolMonitoring = false, CommandHook = hook,
    };

    private static string Command(string route) => route switch
    {
        "get" or "try-get" => "GET", "set" or "set-buffer" => "SET", "refresh" => "REFRESH", _ => "UNLINK",
    };

    private static async Task Execute(RespireDistributedCache cache, string route, bool synchronous = false,
        CancellationToken token = default)
    {
        if (synchronous)
        {
            switch (route)
            {
                case "get": cache.Get("key"); break;
                case "try-get": cache.TryGet("key", new ArrayBufferWriter<byte>()); break;
                case "set": cache.Set("key", [1], new()); break;
                case "set-buffer": cache.Set("key", new ReadOnlySequence<byte>(new byte[] { 1 }), new()); break;
                case "refresh": cache.Refresh("key"); break;
                default: cache.Remove("key"); break;
            }
            return;
        }
        switch (route)
        {
            case "get": await cache.GetAsync("key", token); break;
            case "try-get": await cache.TryGetAsync("key", new ArrayBufferWriter<byte>(), token); break;
            case "set": await cache.SetAsync("key", [1], new(), token); break;
            case "set-buffer": await cache.SetAsync("key", new ReadOnlySequence<byte>(new byte[] { 1 }), new(), token); break;
            case "refresh": await cache.RefreshAsync("key", token); break;
            default: await cache.RemoveAsync("key", token); break;
        }
    }

    private sealed class Codec : IRespireValueCodec
    {
        public int Calls;
        public Exception? Failure;
        public byte[] Encode(ReadOnlySpan<byte> payload) => Decode(payload);
        public byte[] Decode(ReadOnlySpan<byte> payload)
        {
            Interlocked.Increment(ref Calls);
            if (Failure is { } failure) throw failure;
            return payload.ToArray();
        }
    }

    private sealed class ThrowingWriter(Exception failure) : IBufferWriter<byte>
    {
        public void Advance(int count) => throw failure;
        public Memory<byte> GetMemory(int sizeHint = 0) => throw failure;
        public Span<byte> GetSpan(int sizeHint = 0) => throw failure;
    }

    private sealed class Hook : RespireCommandHook
    {
        public Action<RespireCommandContext>? Before;
        public bool ThrowOnCompleted;
        public ConcurrentQueue<(RespireCommandContext Context, object? State, RespireCommandOutcome Outcome, Exception? Error)> Completed { get; } = new();
        public override object? OnExecuting(RespireCommandContext context) { Before?.Invoke(context); return context; }
        public override void OnCompleted(RespireCommandContext context, object? state, RespireCommandOutcome outcome, Exception? exception)
        {
            Completed.Enqueue((context, state, outcome, exception));
            if (ThrowOnCompleted) throw new InvalidOperationException("Observer failure");
        }
    }
}
