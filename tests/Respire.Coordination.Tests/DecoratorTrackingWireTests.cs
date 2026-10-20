using System.Reflection;
using Respire.Tests.Networking;
using TUnit.Assertions;
using TUnit.Assertions.Enums;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Coordination.Tests;

public class DecoratorTrackingWireTests
{
    [Test]
    [Arguments(false, false, RespireClientTrackingMode.OptIn)]
    [Arguments(true, false, RespireClientTrackingMode.OptIn)]
    [Arguments(false, true, RespireClientTrackingMode.OptIn)]
    [Arguments(true, true, RespireClientTrackingMode.OptIn)]
    [Arguments(false, false, RespireClientTrackingMode.OptOut)]
    [Arguments(true, false, RespireClientTrackingMode.OptOut)]
    [Arguments(false, true, RespireClientTrackingMode.OptOut)]
    [Arguments(true, true, RespireClientTrackingMode.OptOut)]
    [Arguments(false, false, RespireClientTrackingMode.Broadcast)]
    [Arguments(true, false, RespireClientTrackingMode.Broadcast)]
    [Arguments(false, true, RespireClientTrackingMode.Broadcast)]
    [Arguments(true, true, RespireClientTrackingMode.Broadcast)]
    public async Task RegistrationAlwaysSendsFreshRead(bool hashField, bool mutating, RespireClientTrackingMode mode)
    {
        await using var server = Server();
        var hook = new RecordingHook();
        await using var client = await ConnectAsync(server, hook, mode);
        var proxy = Decorate(client.WithKeyPrefix("tenant:"));
        var intercepted = 0;
        using var cancellation = new CancellationTokenSource();
        ((DecoratorTrackingTests.ForwardingClient)proxy).Before = (method, args) =>
        {
            if (method.Name != nameof(IRespireClient.RegisterKeyTrackingAsync)) return;
            // A decorator can authorize and transform this operation before forwarding it.
            if (!((RespireKey)args[0]!).Equals((RespireKey)"logical")
                || (CancellationToken)args[2]! != cancellation.Token)
                throw new InvalidOperationException("Registration lost the caller's key or cancellation token.");
            args[0] = (RespireKey)"routed";
            intercepted++;
        };
        RespireKey? field = hashField ? (RespireKey)"worker" : (RespireKey?)null;
        var command = hashField ? "HGET tenant:routed worker" : "GET tenant:routed";
        var cache = (ClientSideCacheCoordinator)client.ClientSideCache!;
        var mutation = mutating ? cache.BeginUnknownMutation() : default;
        try
        {
            // A resident result must not satisfy tracking registration. During a mutation
            // an ordinary read must still suppress publication.
            await ReadAsync(client.WithKeyPrefix("tenant:"), hashField);
            await Assert.That(cache.Count).IsEqualTo(mutating ? 0 : 1);
            hook.Completed.Clear();
            var before = server.CommandsSeen;
            await CoordinationCommandHooks.TrackAsync(proxy, "logical", field, cancellation.Token);
            await CoordinationCommandHooks.TrackAsync(proxy, "logical", field, cancellation.Token);
            var expected = mode == RespireClientTrackingMode.OptIn
                ? new[] { "CLIENT CACHING YES", command, "CLIENT CACHING YES", command }
                : new[] { command, command };
            await Assert.That(server.ReceivedCommands.Skip(before)).IsEquivalentTo(expected, CollectionOrdering.Matching);
            await Assert.That(intercepted).IsEqualTo(2);
            await Assert.That(hook.Completed).IsEquivalentTo(new[] { hashField ? "HGET" : "GET", hashField ? "HGET" : "GET" });
            await Assert.That(cache.Count).IsEqualTo(mutating ? 0 : 1);
            if (mutating)
            {
                before = server.CommandsSeen;
                await ReadAsync(client.WithKeyPrefix("tenant:"), hashField);
                var ordinary = mode == RespireClientTrackingMode.OptIn && !hashField
                    ? new[] { "CLIENT CACHING YES", command } : new[] { command };
                await Assert.That(server.ReceivedCommands.Skip(before)).IsEquivalentTo(ordinary, CollectionOrdering.Matching);
                await Assert.That(cache.Count).IsEqualTo(0);
            }
            cache.Clear();
            await CoordinationCommandHooks.TrackAsync(proxy, "logical", field, cancellation.Token);
            await Assert.That(cache.Count).IsEqualTo(0);
        }
        finally
        {
            if (mutating) cache.CompleteMutation(in mutation, succeeded: false);
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RegistrationDoesNotJoinPendingCacheProducerAndHonorsCancellation(bool hashField)
    {
        await using var server = Server();
        await using var client = await ConnectAsync(server);
        var proxy = Decorate(client.WithKeyPrefix("tenant:"));
        var firstRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        server.SuppressReply = command =>
        {
            if (command.StartsWith(hashField ? "HGET " : "GET ", StringComparison.Ordinal))
            {
                if (Interlocked.Increment(ref reads) == 1) firstRead.TrySetResult();
                else secondRead.TrySetResult();
            }
            return true;
        };
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var read = ReadAsync(proxy, hashField, cancellation.Token);
        Task? registration = null;
        try
        {
            await firstRead.Task.WaitAsync(cancellation.Token);
            registration = CoordinationCommandHooks.TrackAsync(
                proxy, "routed", hashField ? (RespireKey)"worker" : (RespireKey?)null, cancellation.Token).AsTask();
            await secondRead.Task.WaitAsync(cancellation.Token);
        }
        finally
        {
            await cancellation.CancelAsync();
            await Assert.That(async () => await read).Throws<OperationCanceledException>();
            if (registration is not null)
                await Assert.That(async () => await registration).Throws<OperationCanceledException>();
        }
        await Assert.That(reads).IsEqualTo(2);
        await Assert.That(client.ClientSideCache!.Count).IsEqualTo(0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task DecoratorAndHookCanRejectRegistrationBeforeWireDispatch(bool rejectInDecorator)
    {
        await using var server = Server();
        var hook = new RecordingHook { Reject = !rejectInDecorator };
        await using var client = await ConnectAsync(server, hook);
        var proxy = Decorate(client);
        ((DecoratorTrackingTests.ForwardingClient)proxy).Before = (method, _) =>
        {
            if (rejectInDecorator && method.Name == nameof(IRespireClient.RegisterKeyTrackingAsync))
                throw new UnauthorizedAccessException("Decorator policy.");
        };
        var before = server.CommandsSeen;
        await Assert.That(async () => await CoordinationCommandHooks.TrackAsync(proxy, "routed", null, default))
            .ThrowsExactly<UnauthorizedAccessException>();
        await Assert.That(server.CommandsSeen).IsEqualTo(before);
        await Assert.That(hook.Completed.Count).IsEqualTo(rejectInDecorator ? 0 : 1);
    }

    [Test]
    [Arguments("disabled")]
    [Arguments("bypass")]
    [Arguments("prefix")]
    [Arguments("canceled")]
    public async Task InvalidRegistrationDoesNotSendCommands(string kind)
    {
        await using var server = Server();
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespireProtocol.Resp3, Endpoints = [new("127.0.0.1", server.Port)], Connections = 1,
            ClientSideCache = kind == "disabled" ? null : new() { KeyPrefixes = ["tenant:"] },
        });
        var proxy = Decorate(kind == "bypass" ? client.WithoutClientCache() : client);
        var before = server.CommandsSeen;
        using var cancellation = new CancellationTokenSource();
        if (kind == "canceled") cancellation.Cancel();
        var failure = await Assert.That(async () =>
            await proxy.RegisterKeyTrackingAsync(kind == "prefix" ? "outside:key" : "tenant:key", cancellationToken: cancellation.Token))
            .Throws<Exception>();
        await Assert.That(failure is OperationCanceledException && kind == "canceled"
            || failure is ArgumentException && kind == "prefix"
            || failure is RespireConfigurationException && kind is "disabled" or "bypass").IsTrue();
        await Assert.That(server.CommandsSeen).IsEqualTo(before);
    }

    private static async Task ReadAsync(IRespireClient client, bool hashField, CancellationToken token = default)
    {
        if (hashField) _ = await client.Hashes.GetBytesAsync("routed", "worker", token);
        else _ = await client.Strings.GetStringAsync("routed", token);
    }

    private static IRespireClient Decorate(IRespireClient inner)
    {
        var proxy = DispatchProxy.Create<IRespireClient, DecoratorTrackingTests.ForwardingClient>();
        ((DecoratorTrackingTests.ForwardingClient)proxy).Inner = inner;
        return proxy;
    }

    private static FakeRespServer Server() => new(FakeRespServer.OkReply)
    {
        ReplyOverride = (_, command) => command switch
        {
            "HELLO 3" => "%1\r\n$5\r\nproto\r\n:3\r\n"u8.ToArray(),
            var read when read.StartsWith("GET ", StringComparison.Ordinal)
                || read.StartsWith("HGET ", StringComparison.Ordinal) => "$1\r\nx\r\n"u8.ToArray(),
            _ => FakeRespServer.OkReply,
        },
    };

    private static ValueTask<RespireClient> ConnectAsync(FakeRespServer server, RecordingHook? hook = null,
        RespireClientTrackingMode mode = RespireClientTrackingMode.OptIn)
        => RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespireProtocol.Resp3, Endpoints = [new("127.0.0.1", server.Port)], Connections = 1,
            ClientSideCache = new() { TrackingMode = mode, CoalesceConcurrentMisses = true },
            CommandHook = hook,
        });

    private sealed class RecordingHook : RespireCommandHook
    {
        internal bool Reject;
        internal List<string> Completed { get; } = [];
        public override object? OnExecuting(RespireCommandContext context)
            => Reject ? throw new UnauthorizedAccessException("Hook policy.") : null;
        public override void OnCompleted(RespireCommandContext context, object? state,
            RespireCommandOutcome outcome, Exception? error) => Completed.Add(context.Command);
    }
}
