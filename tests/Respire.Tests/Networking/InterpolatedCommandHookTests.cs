using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Text;
using Respire.Internal;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public partial class CommandHookTests
{
    [Test]
    [NotInParallel]
    [Arguments("owned", "preflight")]
    [Arguments("lease", "preflight")]
    [Arguments("forget", "preflight")]
    [Arguments("owned", "rejection")]
    [Arguments("lease", "rejection")]
    [Arguments("forget", "rejection")]
    [Arguments("owned", "cancellation")]
    [Arguments("lease", "cancellation")]
    [Arguments("forget", "cancellation")]
    [Arguments("owned", "server")]
    [Arguments("lease", "server")]
    public async Task InterpolatedFailuresPublishOneCallerError(string route, string phase)
    {
        await using var server = Server();
        server.ReplyOverride = (_, command) => command == "GET key" ? "-ERR failed\r\n"u8.ToArray() : null;
        using var cancellation = new CancellationTokenSource();
        var rejection = new InvalidOperationException("denied");
        var hook = new RecordingHook
        {
            Before = _ =>
            {
                if (phase == "rejection") throw rejection;
                if (phase == "cancellation") cancellation.Cancel();
            },
        };
#pragma warning disable RESP010 // This interpolation hook scenario owns its client, observer state and scripted replies.
        await using var client = await RespireClient.ConnectAsync(Options(server, hook));
#pragma warning restore RESP010
        using var errors = new InterpolatedErrorCapture();
        var baseline = server.CommandsSeen;
        RespireCommandInterpolatedStringHandler command = $"GET {"key"}";
        if (phase == "preflight") command = new RespireCommandInterpolatedStringHandler(0, 0);
        var error = await Failure(() => SendInterpolated(client, route, command, token: cancellation.Token));
        if (phase == "rejection") await Assert.That(error).IsSameReferenceAs(rejection);
        if (phase == "cancellation") await Assert.That(error is OperationCanceledException).IsTrue();
        if (phase == "server") await Assert.That(error is RespireServerException).IsTrue();
        else await Assert.That(server.CommandsSeen).IsEqualTo(baseline);
        await Assert.That(hook.Completed.Count).IsEqualTo(phase == "preflight" ? 0 : 1);
        await Assert.That(errors.Items.Count).IsEqualTo(1);
        await Assert.That((bool)errors.Items.Single()["redis.client.errors.internal"]!).IsFalse();
    }

    private sealed class InterpolatedErrorCapture : IDisposable
    {
        private readonly MeterListener _listener = new();
        internal ConcurrentQueue<Dictionary<string, object?>> Items { get; } = new();

        internal InterpolatedErrorCapture()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (ReferenceEquals(instrument.Meter, RespireTelemetry.Meter) && instrument.Name == "redis.client.errors")
                    listener.EnableMeasurementEvents(instrument);
            };
            _listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
                Items.Enqueue(tags.ToArray().ToDictionary(tag => tag.Key, tag => tag.Value)));
            _listener.Start();
        }

        public void Dispose() => _listener.Dispose();
    }

    [Test]
    [Arguments("owned")]
    [Arguments("lease")]
    [Arguments("forget")]
    public async Task InterpolatedAdmissionFreezesBinaryTokensAndHandlerContents(string route)
    {
        await using var server = Server();
        var subcommand = "EXISTS"u8.ToArray();
        var digest = "digest"u8.ToArray();
        var command = new RespireCommandInterpolatedStringHandler(6, 2);
        command.AppendLiteral("SCRIPT");
        command.AppendFormatted(subcommand);
        command.AppendFormatted((ReadOnlyMemory<byte>)digest);
        var hook = new RecordingHook
        {
            Before = _ =>
            {
                "FLUSH "u8.CopyTo(subcommand);
                digest.AsSpan().Fill((byte)'x');
                command.AppendLiteral("extra");
            },
        };
#pragma warning disable RESP010 // This interpolation hook scenario owns its client, observer state and scripted replies.
        await using var client = await RespireClient.ConnectAsync(Options(server, hook));
#pragma warning restore RESP010
        await SendInterpolated(client, route, command);
        // A reply-bearing command drains the connection after fire-and-forget writes.
        await client.Strings.LengthAsync("barrier");
        await Assert.That(server.ReceivedCommands.Where(c => c.StartsWith("SCRIPT", StringComparison.Ordinal)))
            .IsEquivalentTo(["SCRIPT EXISTS digest"]);
        await Assert.That(hook.Entered.First().Command).IsEqualTo("SCRIPT EXISTS");
        await Assert.That(hook.Completed.First().Context.Command).IsEqualTo("SCRIPT EXISTS");
        await Assert.That(hook.Completed.First().Outcome).IsEqualTo(RespireCommandOutcome.Succeeded);
    }

    [Test]
    [Arguments("owned")]
    [Arguments("lease")]
    [Arguments("forget")]
    public async Task InterpolatedNamesOmitArgumentsAndNormalizeSubcommands(string route)
    {
        await using var server = Server();
        var rejected = new InvalidOperationException("denied");
        var hook = new RecordingHook { Before = _ => throw rejected };
#pragma warning disable RESP010 // This interpolation hook scenario owns its client, observer state and scripted replies.
        await using var client = await RespireClient.ConnectAsync(Options(server, hook));
#pragma warning restore RESP010
        var baseline = server.CommandsSeen;
        foreach (var formattedSubcommand in new[] { false, true })
        {
            var command = new RespireCommandInterpolatedStringHandler(20, 2);
            command.AppendLiteral(formattedSubcommand ? "script" : "script exists");
            if (formattedSubcommand) command.AppendFormatted("exists");
            command.AppendFormatted("secret digest with spaces");
            var error = await Failure(() => SendInterpolated(client, route, command));
            await Assert.That(error).IsSameReferenceAs(rejected);
        }
        await Assert.That(server.CommandsSeen).IsEqualTo(baseline);
        await Assert.That(hook.Entered.Count).IsEqualTo(2);
        await Assert.That(hook.Entered.All(c => c.Command == "SCRIPT EXISTS")).IsTrue();
        await Assert.That(hook.Completed.All(c => c.Outcome == RespireCommandOutcome.Rejected)).IsTrue();
    }

    [Test]
    [Arguments("owned")]
    [Arguments("lease")]
    [Arguments("forget")]
    public async Task InterpolatedPreflightMatchesUnconfiguredErrors(string route)
    {
        using var endpoint = new ReservedUnavailablePort();
        var hook = new RecordingHook { Before = _ => throw new InvalidOperationException("denied") };
        var options = OptionsForUnavailableEndpoint(endpoint.Port);
#pragma warning disable RESP010 // This preflight scenario owns isolated client options and hooks to verify rejection before I/O.
        await using var plain = RespireClient.Create(options);
#pragma warning restore RESP010
#pragma warning disable RESP010 // This preflight scenario owns isolated client options and hooks to verify rejection before I/O.
        await using var hooked = RespireClient.Create(options with { CommandHook = hook });
#pragma warning restore RESP010
        var empty = new RespireCommandInterpolatedStringHandler(0, 0);
        await Check(empty);
        await Check($"MGET {"{a}:one"} {"{b}:two"}");
        if (route == "forget") await Check($"BLPOP {"key"} {0}");
        else
        {
            await Check($"GET {"key"}", (RespireCommandFlags)128);
            await Check($"FUNCTION FLUSH", RespireCommandFlags.NoRedirect);
        }
        await Assert.That(hook.Entered).IsEmpty();
        await Assert.That(hook.Completed).IsEmpty();
        await Assert.That(hooked.IsConnected).IsFalse();

        async Task Check(RespireCommandInterpolatedStringHandler command, RespireCommandFlags flags = default)
        {
            var expected = await Failure(() => SendInterpolated(plain, route, command, flags));
            var actual = await Failure(() => SendInterpolated(hooked, route, command, flags));
            await Assert.That(actual.GetType()).IsEqualTo(expected.GetType());
            await Assert.That(actual.Message).IsEqualTo(expected.Message);
        }
    }

    private static RespireOptions OptionsForUnavailableEndpoint(int port) => new()
    {
        Endpoints = [new("127.0.0.1", port)], UseCluster = true,
        Protocol = RespireProtocol.Resp2, ThreadPoolMonitoring = false,
    };

    [Test]
    [Arguments("owned", false)]
    [Arguments("lease", false)]
    [Arguments("forget", false)]
    [Arguments("owned", true)]
    [Arguments("lease", true)]
    public async Task InterpolatedCancellationCompletesOnce(string route, bool pending)
    {
        await using var server = Server();
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.SuppressReply = command =>
        {
            if (command != "GET key") return false;
            received.TrySetResult();
            return true;
        };
        var hook = new RecordingHook();
#pragma warning disable RESP010 // This cancellation scenario owns its client, hook state and scripted in-flight reply boundaries.
        await using var client = await RespireClient.ConnectAsync(Options(server, hook));
#pragma warning restore RESP010
        var baseline = server.CommandsSeen;
        using var cancellation = new CancellationTokenSource();
        if (!pending) cancellation.Cancel();
        var response = SendInterpolated(client, route, $"GET {"key"}", token: cancellation.Token);
        if (pending)
        {
            await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
        }
        var error = await Failure(() => response);
        await Assert.That(error is OperationCanceledException).IsTrue();
        var completion = hook.Completed.Single();
        await Assert.That(completion.Outcome).IsEqualTo(RespireCommandOutcome.Canceled);
        await Assert.That(completion.Error).IsSameReferenceAs(error);
        await Assert.That(completion.Context.CancellationToken).IsEqualTo(cancellation.Token);
        await Assert.That(hook.Entered.Count).IsEqualTo(1);
        if (!pending) await Assert.That(server.CommandsSeen).IsEqualTo(baseline);
    }

    [Test]
    [Arguments("owned")]
    [Arguments("lease")]
    public async Task InterpolatedServerErrorsCompleteAsFailed(string route)
    {
        await using var server = Server();
        server.ReplyOverride = (_, command) => command == "GET key" ? "-ERR failed\r\n"u8.ToArray() : null;
        var hook = new RecordingHook { ThrowOnCompleted = true };
#pragma warning disable RESP010 // This interpolation hook scenario owns its client, observer state and scripted replies.
        await using var client = await RespireClient.ConnectAsync(Options(server, hook));
#pragma warning restore RESP010
        var error = await Failure(() => SendInterpolated(client, route, $"GET {"key"}"));
        var completion = hook.Completed.Single();
        await Assert.That(completion.Outcome).IsEqualTo(RespireCommandOutcome.Failed);
        await Assert.That(completion.Error).IsSameReferenceAs(error);
    }

    [Test]
    [Arguments("owned", false)]
    [Arguments("lease", false)]
    [Arguments("owned", true)]
    [Arguments("lease", true)]
    public async Task InterpolatedHooksPreserveRawReplayRestrictions(string route, bool configured)
    {
        await using var server = Server();
        server.ReplyOverride = (_, command) => command == "GET key" ? "-TRYAGAIN busy\r\n"u8.ToArray() : null;
        var hook = new RecordingHook();
#pragma warning disable RESP010 // This interpolation hook scenario owns its client, observer state and scripted replies.
        await using var client = await RespireClient.ConnectAsync(Options(server, hook) with
        {
            CommandHook = configured ? hook : null,
            RetryPolicy = new() { InitialBackoff = TimeSpan.Zero },
        });
#pragma warning restore RESP010
        var error = await Failure(() => SendInterpolated(client, route, $"GET {"key"}"));
        await Assert.That(error is RespireServerException).IsTrue();
        // DynamicCommand deliberately defaults to Never; a name must not grant replay permission.
        await Assert.That(server.ReceivedCommands.Count(c => c == "GET key")).IsEqualTo(1);
        await Assert.That(hook.Entered.Count).IsEqualTo(configured ? 1 : 0);
        await Assert.That(hook.Completed.Count).IsEqualTo(configured ? 1 : 0);
        if (configured)
        {
            await Assert.That(hook.Completed.Single().Outcome).IsEqualTo(RespireCommandOutcome.Failed);
            await Assert.That(hook.Completed.Single().Error).IsSameReferenceAs(error);
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task InterpolatedResultTransferPrecedesCallerConversion(bool lease)
    {
        await using var server = Server();
        server.ReplyOverride = (_, command) => command == "GET key" ? "$3\r\nbad\r\n"u8.ToArray() : null;
        var hook = new RecordingHook { ThrowOnCompleted = true };
#pragma warning disable RESP010 // This interpolation hook scenario owns its client, observer state and scripted replies.
        await using var client = await RespireClient.ConnectAsync(Options(server, hook));
#pragma warning restore RESP010
        using var result = lease ? await client.ExecuteLeaseAsync($"GET {"key"}")
            : await client.ExecuteAsync($"GET {"key"}");
        await Assert.That(result.AsString()).IsEqualTo("bad");
        await Assert.That(hook.Completed.Single().Outcome).IsEqualTo(RespireCommandOutcome.Succeeded);
        // Raw APIs do not choose a typed conversion. A caller's later conversion failure must
        // neither change the published result outcome nor produce a second completion.
        var error = await Failure(() => { _ = result.AsDouble(); return Task.CompletedTask; });
        await Assert.That(error is FormatException).IsTrue();
        await Assert.That(hook.Completed.Count).IsEqualTo(1);
        await Assert.That(result.IsDisposed).IsFalse();
    }

    [Test]
    [Arguments("interpolated-owned")]
    [Arguments("interpolated-lease")]
    [Arguments("interpolated-forget")]
    public async Task InterpolatedRedirectsAndReentrantCallsAreIndependent(string route)
    {
        await using var server = Server();
        await using var target = Server();
        var topology = Encoding.ASCII.GetBytes($"*1\r\n*3\r\n:0\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{server.Port}\r\n");
        server.ReplyOverride = (_, command) => command == "CLUSTER SLOTS" ? topology
            : command == "GET key" ? Encoding.ASCII.GetBytes($"-MOVED {ClusterHash.GetSlot("key")} 127.0.0.1:{target.Port}\r\n")
            : command == "STRLEN nested" ? ":2\r\n"u8.ToArray() : null;
        target.ReplyOverride = (_, command) => command == "CLUSTER SLOTS" ? topology
            : command == "GET key" ? "$2\r\n42\r\n"u8.ToArray() : null;
        var hook = new RecordingHook();
#pragma warning disable RESP010 // This reentrancy/concurrency scenario owns its client and shared callback state until all calls finish.
        await using var client = await RespireClient.ConnectAsync(Options(server, hook) with
        {
            UseCluster = true, ClusterTopologyRefreshInterval = null,
        });
#pragma warning restore RESP010
        Task<long>? nested = null;
        hook.Before = context =>
        {
            if (context.Command == "GET") nested = client.Strings.LengthAsync("nested").AsTask();
        };
        await Execute(client, route);
        await nested!;
        await Assert.That(hook.Entered.Select(c => c.Command)).IsEquivalentTo(["GET", "STRLEN"]);
        await Assert.That(hook.Completed.Count).IsEqualTo(2);
        await Assert.That(hook.Completed.All(c => c.Outcome == RespireCommandOutcome.Succeeded)).IsTrue();
        await Assert.That(server.ReceivedCommands.Count(c => c == "GET key")).IsEqualTo(1);
        await Assert.That(target.ReceivedCommands.Count(c => c == "GET key")).IsEqualTo(1);
    }

    [Test]
    [Arguments("owned")]
    [Arguments("lease")]
    [Arguments("forget")]
    public async Task InterpolatedMutationCompletesAfterFenceCleanup(string route)
    {
        await using var server = new FakeRespServer("%1\r\n$5\r\nproto\r\n:3\r\n"u8.ToArray(), FakeRespServer.OkReply);
        var hook = new RecordingHook();
#pragma warning disable RESP010 // This interpolation hook scenario owns its client, observer state and scripted replies.
        await using var client = await RespireClient.ConnectAsync(Options(server, hook) with
        {
            Protocol = RespireProtocol.Resp3, ClientSideCache = new(),
        });
#pragma warning restore RESP010
        var activeAtAdmission = -1;
        var activeAtCompletion = -1;
        hook.Before = _ => activeAtAdmission = client.Core.ClientCache!.InspectForTests().ActiveMutationCount;
        hook.After = () => activeAtCompletion = client.Core.ClientCache!.InspectForTests().ActiveMutationCount;
        await SendInterpolated(client, route, $"SET {"key"} {"value with spaces"}");
        await Assert.That(hook.Completed.Single().Outcome).IsEqualTo(RespireCommandOutcome.Succeeded);
        await Assert.That(activeAtAdmission).IsEqualTo(0);
        await Assert.That(activeAtCompletion).IsEqualTo(0);
        await Assert.That(server.ReceivedCommands).Contains("SET key value with spaces");
    }

    private static async Task SendInterpolated(RespireClient client, string route,
        RespireCommandInterpolatedStringHandler command, RespireCommandFlags flags = default,
        CancellationToken token = default)
    {
        if (route == "forget") await client.ExecuteFireAndForgetAsync(command, token);
        else if (route == "lease") { using var result = await client.ExecuteLeaseAsync(command, flags, token); }
        else { using var result = await client.ExecuteAsync(command, flags, token); }
    }
}
