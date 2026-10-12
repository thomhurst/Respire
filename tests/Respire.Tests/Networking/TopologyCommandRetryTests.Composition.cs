using System.Diagnostics;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public partial class TopologyCommandRetryTests
{
    [Test]
    [Arguments("Sentinel")]
    [Arguments("Cluster")]
    public async Task FailedDiscoveryKeepsRoutingAndCommandRetryOwnershipSeparate(string topology)
    {
        using var configuration = new MetricConfigurationScope();
        using var evidence = new TrackedCacheEvidence();
        var discoveries = 0;
        var fallbackReads = 0;
        await using var primary = Primary(command => command == "GET key" ? Value : null);
        await using var discovery = new FakeRespServer(32, FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) =>
            {
                if (command == "GET key") return Count(ref fallbackReads, Value);
                if (command == "SENTINEL SENTINELS mymaster") return "*0\r\n"u8.ToArray();
                if (command != (topology == "Sentinel" ? "SENTINEL GET-MASTER-ADDR-BY-NAME mymaster" : "CLUSTER SLOTS")) return null;
                if (Interlocked.Increment(ref discoveries) == 1) return "-ERR injected discovery failure\r\n"u8.ToArray();
                return topology == "Sentinel" ? PrimaryReply(primary.Port) : Slots(primary.Port);
            },
        };
        var options = topology == "Sentinel" ? SentinelOptions(discovery) : ClusterOptions(discovery);
        await using var client = RespireClient.Create(options);
        await Assert.That(await client.GetStringAsync("key")).IsEqualTo("value");
        await Assert.That(discoveries).IsGreaterThanOrEqualTo(topology == "Sentinel" ? 2 : 1);
        await Assert.That(primary.ReceivedCommands.Count(command => command == "GET key") + fallbackReads).IsEqualTo(1);
        // Cluster's native discovery fallback can use its seed without retrying GET.
        // Sentinel must complete another discovery before admitting its command retry.
        await Assert.That(evidence.Retries.GetValueOrDefault("attempted")).IsEqualTo(topology == "Cluster" ? 0 : 1);
        await Assert.That(evidence.Retries.GetValueOrDefault("succeeded")).IsEqualTo(topology == "Cluster" ? 0 : 1);
        await Assert.That(evidence.Retries.GetValueOrDefault("exhausted")).IsEqualTo(0);
        await Assert.That(evidence.FinalErrors).IsEqualTo(0);
    }

    public static IEnumerable<(string, RespireCommandRetryCategory)> PossibleWriteCases()
    {
        foreach (var shape in CompositionShapes())
        foreach (var category in Enum.GetValues<RespireCommandRetryCategory>())
            yield return (shape, category);
    }

    public static IEnumerable<string> CompositionShapes()
        => ["Sentinel", "Cluster", "raw", "script", "function", "group",
            "Sentinel/raw", "Sentinel/script", "Sentinel/function", "Cluster/raw", "Cluster/script", "Cluster/function"];

    [Test]
    [MethodDataSource(nameof(PossibleWriteCases))]
    public async Task UnwrittenCommandsRespectTransportEvidenceAndLogicalOwnerRisk(string shape, RespireCommandRetryCategory category)
    {
        using var configuration = new MetricConfigurationScope();
        using var evidence = new TrackedCacheEvidence();
        var calls = 0;
        var occupied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var fixture = await CompositionFixture.ConnectAsync(shape,
            command => IsCompositionRead(command) ? Count(ref calls, Value) : null,
            new() { InitialBackoff = TimeSpan.Zero }, maxInflight: 1);
        fixture.Server.SuppressReply = command =>
        {
            if (command != "ECHO occupy") return false;
            occupied.TrySetResult();
            return true;
        };
        var occupying = fixture.OccupyAsync().AsTask();
        await occupied.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var pending = fixture.ReadAsync(default, category).AsTask();
        await Assert.That(pending.IsCompleted).IsFalse();
        await Assert.That(calls).IsEqualTo(0);
        fixture.Server.CloseConnections();
        // Cluster, declared scripts/functions and group delegates intentionally expose
        // unknown write evidence. Only standalone/Sentinel dispatch can prove unwritten.
        var ownsTransportEvidence = !shape.StartsWith("Cluster", StringComparison.Ordinal)
            && shape != "group" && RequestShape(shape) is not ("script" or "function");
        var retries = category != RespireCommandRetryCategory.Never && (ownsTransportEvidence
            || category is RespireCommandRetryCategory.Always or RespireCommandRetryCategory.Connection or RespireCommandRetryCategory.ReadOnly);
        if (retries) await Assert.That(await pending.WaitAsync(TimeSpan.FromSeconds(5))).IsEqualTo("value");
        else await Assert.That(async () => await pending).Throws<RespireConnectionException>();
        await Assert.That(async () => { using var ignored = await occupying; }).Throws<RespireConnectionException>();
        await Assert.That(calls).IsEqualTo(retries ? 1 : 0);
        await Assert.That(evidence.Retries.GetValueOrDefault("attempted")).IsEqualTo(retries ? 1 : 0);
        await Assert.That(evidence.Retries.GetValueOrDefault("succeeded")).IsEqualTo(retries ? 1 : 0);
        await Assert.That(evidence.Retries.GetValueOrDefault("exhausted")).IsEqualTo(0);
    }

    [Test]
    [MethodDataSource(nameof(PossibleWriteCases))]
    public async Task PossibleWriteAdmissionPreservesDeclaredCategoryAcrossOwners(string shape, RespireCommandRetryCategory category)
    {
        using var configuration = new MetricConfigurationScope();
        using var evidence = new TrackedCacheEvidence();
        var calls = 0;
        CompositionFixture? owner = null;
        await using var fixture = await CompositionFixture.ConnectAsync(shape, command =>
        {
            if (!IsCompositionRead(command)) return null;
            if (Interlocked.Increment(ref calls) == 1)
            {
                if (shape == "group") return TryAgain;
                owner!.Server.CloseConnections();
                return null;
            }
            return Value;
        }, new() { InitialBackoff = TimeSpan.Zero });
        owner = fixture;
        var retries = category is RespireCommandRetryCategory.Always or RespireCommandRetryCategory.Connection or RespireCommandRetryCategory.ReadOnly;
        if (retries) await Assert.That(await fixture.ReadAsync(default, category)).IsEqualTo("value");
        else if (shape == "group") await Assert.That(async () => await fixture.ReadAsync(default, category)).ThrowsExactly<RespireServerException>();
        else await Assert.That(async () => await fixture.ReadAsync(default, category)).Throws<RespireConnectionException>();
        await Assert.That(calls).IsEqualTo(retries ? 2 : 1);
        await Assert.That(evidence.Retries.GetValueOrDefault("attempted")).IsEqualTo(retries ? 1 : 0);
        await Assert.That(evidence.Retries.GetValueOrDefault("succeeded")).IsEqualTo(retries ? 1 : 0);
        await Assert.That(evidence.Retries.GetValueOrDefault("exhausted")).IsEqualTo(0);
    }

    [Test]
    [MethodDataSource(nameof(CompositionShapes))]
    public async Task CrossPathCancellationDuringBackoffPreservesTokenAndDoesNotResend(string shape)
    {
        using var configuration = new MetricConfigurationScope();
        using var evidence = new TrackedCacheEvidence();
        var calls = 0;
        await using var fixture = await CompositionFixture.ConnectAsync(shape, command =>
            IsCompositionRead(command) ? Count(ref calls, TryAgain) : null,
            new() { InitialBackoff = TimeSpan.FromSeconds(1), Jitter = 0 });
        using var cancellation = new CancellationTokenSource();
        var pending = fixture.ReadAsync(cancellation.Token).AsTask();
        // Error observation happens after eligibility, before entering backoff. No timing race
        // with peer receipt, and cancellation cannot accidentally target the next logical call.
        await evidence.ErrorObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (shape == "group")
        {
            // Member final-error telemetry precedes the group's retry admission. Allow
            // that continuation to enter its one-second backoff before cancelling.
            await Task.Delay(100);
            await Assert.That(pending.IsCompleted).IsFalse();
        }
        cancellation.Cancel();
        var error = await Failure(async () => await pending);
        await Assert.That(error).IsTypeOf<OperationCanceledException>();
        await Assert.That(((OperationCanceledException)error).CancellationToken).IsEqualTo(cancellation.Token);
        await Assert.That(calls).IsEqualTo(1);
        await Assert.That(evidence.Retries.Count).IsEqualTo(0);
    }

    [Test]
    [MethodDataSource(nameof(DeadlineShapes))]
    public async Task CrossPathOriginalDeadlineRejectsLateReplyAndRetiresItsFifoSlot(string shape)
    {
        using var configuration = new MetricConfigurationScope();
        using var evidence = new TrackedCacheEvidence();
        var calls = 0;
        var lateReply = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var fixture = await CompositionFixture.ConnectAsync(shape, command =>
            IsCompositionRead(command) ? Interlocked.Increment(ref calls) switch
            { 1 => TryAgain, 2 => "$4\r\nlate\r\n"u8.ToArray(), _ => Value } : null,
            new() { InitialBackoff = TimeSpan.FromSeconds(2), MaxBackoff = TimeSpan.FromSeconds(2), Jitter = 0 },
            timeout: TimeSpan.FromMilliseconds(3500));
        // Install the gate only after the first error was processed. The second attempt
        // keeps its reply slot through timeout; the next call must never receive that reply.
        var elapsed = Stopwatch.StartNew();
        var pending = fixture.ReadAsync(default).AsTask();
        await evidence.ErrorObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        fixture.Server.GateCommand(RequestShape(shape) switch { "script" => "EVALSHA", "function" => "FCALL_RO", _ => "GET key" }, lateReply.Task);
        try
        {
            await Assert.That(async () => await pending).ThrowsExactly<RespireTimeoutException>();
            // A fresh per-attempt timeout would finish after 5.5 seconds. Leave a full
            // second of scheduler tolerance while excluding that deadline reset.
            await Assert.That(elapsed.Elapsed).IsLessThan(TimeSpan.FromMilliseconds(4500));
            await Assert.That(evidence.Retries.GetValueOrDefault("succeeded")).IsEqualTo(0);
        }
        finally { lateReply.TrySetResult(); }
        await Assert.That(await fixture.ReadAsync(default)).IsEqualTo("value");
        await Assert.That(calls).IsEqualTo(3);
        await Assert.That(evidence.Retries.GetValueOrDefault("attempted")).IsEqualTo(1);
        await Assert.That(evidence.Retries.GetValueOrDefault("exhausted")).IsEqualTo(1);
    }

    [Test]
    [MethodDataSource(nameof(CompositionShapes))]
    public async Task CrossPathSelectionAndReusedSourcesKeepAttemptAndFinalErrorOwnership(string shape)
    {
        using var configuration = new MetricConfigurationScope();
        using var evidence = new TrackedCacheEvidence(throwAfterRecording: true);
        var calls = 0;
        var exhaust = false;
        var unselected = false;
        await using var fixture = await CompositionFixture.ConnectAsync(shape, command =>
        {
            if (!IsCompositionRead(command)) return null;
            Interlocked.Increment(ref calls);
            if (unselected) return "-LOADING unselected\r\n"u8.ToArray();
            return exhaust || calls % 2 == 1 ? TryAgain : Value;
        }, new() { MaxAttempts = 2, InitialBackoff = TimeSpan.Zero, RetryableErrors = RespireRetryableErrors.TryAgain });
        for (var i = 0; i < 32; i++)
            await Assert.That(await fixture.ReadAsync(default)).IsEqualTo("value");
        exhaust = true;
        for (var i = 0; i < 32; i++)
            await Assert.That(async () => await fixture.ReadAsync(default)).ThrowsExactly<RespireServerException>();
        unselected = true;
        await Assert.That(async () => await fixture.ReadAsync(default)).ThrowsExactly<RespireServerException>();
        await Assert.That(calls).IsEqualTo(129);
        await Assert.That(evidence.Retries.GetValueOrDefault("attempted")).IsEqualTo(64);
        await Assert.That(evidence.Retries.GetValueOrDefault("succeeded")).IsEqualTo(32);
        await Assert.That(evidence.Retries.GetValueOrDefault("exhausted")).IsEqualTo(32);
        // A group owns the delegate, while its members publish their own final command
        // failures. Other paths borrow one error observation for the complete command.
        await Assert.That(evidence.InternalErrors).IsEqualTo(shape == "group" ? 0 : 64);
        await Assert.That(evidence.FinalErrors).IsEqualTo(shape == "group" ? 97 : 33);
    }

    public static IEnumerable<string> DeadlineShapes() => CompositionShapes().Where(shape => shape != "group");

    private static string RequestShape(string shape) => shape.Split('/')[^1];

    private static bool IsCompositionRead(string command) => command == "GET key"
        || command.StartsWith("EVALSHA ", StringComparison.Ordinal) || command == "FCALL_RO reader 1 key";

    private sealed class CompositionFixture(FakeRespServer server, RespireClient? client,
        FakeRespServer? sentinel, RespireFailoverGroup? group, string shape) : IAsyncDisposable
    {
        internal FakeRespServer Server => server;

        internal static async Task<CompositionFixture> ConnectAsync(string shape, Func<string, byte[]?> reply,
            RespireRetryPolicy policy, TimeSpan? timeout = null, int maxInflight = 16 * 1024)
        {
            var cluster = shape.StartsWith("Cluster", StringComparison.Ordinal);
            var usesSentinel = shape.StartsWith("Sentinel", StringComparison.Ordinal);
            var server = cluster ? ClusterNode((command, _) => reply(command)) : Standalone((command, _) =>
                reply(command) ?? (command == "ROLE" ? RoleReply("master") : command.StartsWith("HELLO", StringComparison.Ordinal)
                    ? "%1\r\n$5\r\nproto\r\n:3\r\n"u8.ToArray() : null));
            FakeRespServer? sentinel = null;
            try
            {
                if (shape == "group")
                {
                    var candidate = Candidate(server, 0);
                    var group = await RespireFailoverGroup.ConnectAsync(
                        [candidate with { Options = candidate.Options with { MaxInflightCommands = maxInflight } }],
                        GroupOptions() with { RetryPolicy = policy });
                    return new(server, null, null, group, shape);
                }
                if (usesSentinel) sentinel = Sentinel(() => server.Port);
                var options = usesSentinel ? SentinelOptions(sentinel!) : cluster ? ClusterOptions(server) : StandaloneOptions(server);
                var client = await RespireClient.ConnectAsync(options with
                {
                    RetryPolicy = policy, CommandTimeout = timeout ?? TimeSpan.FromSeconds(5), Connections = 1,
                    MaxInflightCommands = maxInflight,
                });
                return new(server, client, sentinel, null, shape);
            }
            catch
            {
                if (sentinel is not null) await sentinel.DisposeAsync();
                await server.DisposeAsync();
                throw;
            }
        }

        internal async ValueTask<string?> ReadAsync(CancellationToken token, RespireCommandRetryCategory category = RespireCommandRetryCategory.ReadOnly)
        {
            if (group is not null) return await group.ExecuteAsync((member, callerToken) => member.GetStringAsync("key", callerToken),
                category, token);
            var request = RequestShape(shape);
            if (request == "script") return await client!.Scripts.ExecuteStringAsync(
                RespireScript.Create("return redis.call('GET', KEYS[1])").WithRetryCategory(category),
                ["key"], cancellationToken: token);
            if (request == "function") return await client!.Functions.ExecuteStringAsync(
                RespireFunction.Create("reader", readOnly: true).WithRetryCategory(category),
                ["key"], cancellationToken: token);
            if (request == "raw" || category != RespireCommandRetryCategory.ReadOnly)
            {
                using var result = await client!.ExecuteAsync(RespireCommand.Create("GET").WithRetryCategory(category),
                    ["key"], cancellationToken: token);
                return result.AsString();
            }
            return await client!.GetStringAsync("key", token);
        }

        internal ValueTask<RespireResult> OccupyAsync()
            => (client ?? group!.ActiveClient).ExecuteAsync("ECHO", ["occupy"]);

        public async ValueTask DisposeAsync()
        {
            if (group is not null) await group.DisposeAsync();
            if (client is not null) await client.DisposeAsync();
            if (sentinel is not null) await sentinel.DisposeAsync();
            await server.DisposeAsync();
        }
    }

}
