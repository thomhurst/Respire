using System.Text;
using Respire.Commands;
using Respire.Internal;
using Respire.Protocol;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class ReplicaRedirectCacheTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RedirectCancellationBeforeTargetHandoffFencesContinuity(bool retry)
    {
        await using var target = Target();
        await using var source = Source(target.Port);
        var redirected = 0;
        var value = "old";
        source.ReplyOverride = (_, command) =>
        {
            if (command == "GET cached") return Bytes($"$3\r\n{value}\r\n");
            if (command != "GET redirected") return Handshake(command);
            Volatile.Write(ref redirected, 1);
            return Bytes($"-REDIRECT 127.0.0.1:{target.Port}\r\n");
        };
        await using var client = await RespireClient.ConnectAsync(Options(source.Port, retry));
        await Assert.That(await client.GetStringAsync("cached")).IsEqualTo("old");
        var flushes = client.ClientSideCache!.GetStatistics().ContinuityFlushes;
        value = "new";
        using var caller = new CancellationTokenSource();
        var command = new RedirectCancellationCommand(() => Volatile.Read(ref redirected) != 0, caller);

        // Keep the accepted source reply independent of caller cancellation. The admission
        // callback cancels only after REDIRECT was received, before the handoff's deadline check.
        var error = await Assert.That(async () =>
        {
            using var reply = await client.SendTrackedAsync("GET", command, caller.Token);
        }).Throws<OperationCanceledException>();

        await Assert.That(error!.CancellationToken).IsEqualTo(caller.Token);
        await Assert.That(client.ClientSideCache.Count).IsEqualTo(0);
        await Assert.That(client.ClientSideCache.GetStatistics().ContinuityFlushes - flushes).IsEqualTo(1L);
        await Assert.That(await client.GetStringAsync("cached")).IsEqualTo("new");
        await Assert.That(target.ReceivedCommands.Count).IsEqualTo(0);
    }

    private readonly struct RedirectCancellationCommand(Func<bool> redirected, CancellationTokenSource caller) : IRespCommand
    {
        public ReadCommandKind ReadKind => ReadCommandKind.Read;
        public RespireCommandRetryCategory RetryCategory => RespireCommandRetryCategory.ReadOnly;
        public ClientCacheCommandMetadata GetClientCacheMetadata(string operation) => Verbs.Get.CacheMetadata;
        public RespireCacheMutation GetCacheMutation(string operation) => Verbs.Get.CacheMetadata.Policy;
        public CancellationToken GetResponseCancellationToken(CancellationToken admissionToken) => default;
        public void ValidateAdmission() { if (redirected()) caller.Cancel(); }
        public void Write(ref RespWriter writer) => new Cmd1(Verbs.Get, "redirected").Write(ref writer);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task TargetSelectionFailureFencesContinuityOnce(bool disposed)
    {
        await using var source = Target();
        await using var target = Target();
        await using var client = await RespireClient.ConnectAsync(Options(source.Port, retry: false));
        var targets = client.Core.RedirectTargets!;
        if (disposed) await targets.DisposeAsync();
        await Assert.That(await client.GetStringAsync("cached")).IsEqualTo("value");
        await Assert.That(client.ClientSideCache!.Count).IsEqualTo(1);
        var flushes = client.ClientSideCache.GetStatistics().ContinuityFlushes;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var endpoint = new RespireEndpoint("127.0.0.1", target.Port);

        if (disposed)
            await Assert.That(async () => { using var lease = await targets.AcquireAsync(endpoint, default); })
                .Throws<ObjectDisposedException>();
        else
        {
            var error = await Assert.That(async () => { using var lease = await targets.AcquireAsync(endpoint, cancellation.Token); })
                .Throws<OperationCanceledException>();
            await Assert.That(error!.CancellationToken).IsEqualTo(cancellation.Token);
        }

        await Assert.That(client.ClientSideCache.Count).IsEqualTo(0);
        await Assert.That(client.ClientSideCache.GetStatistics().ContinuityFlushes - flushes).IsEqualTo(1L);
        await Assert.That(target.ReceivedCommands.Count).IsEqualTo(0);
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(false, true)]
    [Arguments(true, true)]
    public async Task TrackedTargetAcquisitionFailureFencesPreviouslyCachedReads(bool retry, bool circuitRejection)
    {
        await using var target = Target();
        target.CloseConnectionAfterCommand = 1;
        await using var source = Source(target.Port);
        var value = "old";
        source.ReplyOverride = (_, command) => command == "GET cached"
            ? Bytes($"$3\r\n{value}\r\n")
            : command.StartsWith("GET ", StringComparison.Ordinal)
                ? Bytes($"-REDIRECT 127.0.0.1:{target.Port}\r\n") : Handshake(command);
        await using var client = await RespireClient.ConnectAsync(Options(source.Port, retry) with
        {
            CircuitBreaker = new() { MinimumFailureCount = 1, OpenDuration = TimeSpan.FromMinutes(1) },
            RetryPolicy = retry ? new() { RetryableErrors = RespireRetryableErrors.Loading } : null,
        });
        await Assert.That(await client.GetStringAsync("cached")).IsEqualTo("old");
        await Assert.That(client.ClientSideCache!.Count).IsEqualTo(1);
        var flushes = client.ClientSideCache.GetStatistics().ContinuityFlushes;
        if (circuitRejection)
        {
            // Reject before target selection can run its new-destination continuity fence.
            var admission = client.Core.Circuits!.Acquire(new("127.0.0.1", target.Port), default);
            try { admission.ConnectionFailed(new IOException("Target unavailable."), default); }
            finally { admission.Dispose(); }
        }
        value = "new";

        if (circuitRejection)
            await Assert.That(async () => await client.GetStringAsync("redirected")).Throws<RespireCircuitOpenException>();
        else
            await Assert.That(async () => await client.GetStringAsync("redirected")).Throws<RespireConnectionException>();

        await Assert.That(client.ClientSideCache.Count).IsEqualTo(0);
        await Assert.That(client.ClientSideCache.GetStatistics().ContinuityFlushes - flushes).IsEqualTo(1L);
        await Assert.That(await client.GetStringAsync("cached")).IsEqualTo("new");
        await Assert.That(source.ReceivedCommands.Count(command => command == "GET cached")).IsEqualTo(2);
        // A setup failure must still count against the target circuit and preserve the source.
        await Assert.That(async () => await client.GetStringAsync("redirected")).Throws<RespireCircuitOpenException>();
        if (circuitRejection) await Assert.That(target.ReceivedCommands.Count).IsEqualTo(0);
    }

    [Test]
    [Arguments(false, RespireClientTrackingMode.OptIn)]
    [Arguments(true, RespireClientTrackingMode.OptIn)]
    [Arguments(false, RespireClientTrackingMode.OptOut)]
    [Arguments(true, RespireClientTrackingMode.OptOut)]
    [Arguments(false, RespireClientTrackingMode.Broadcast)]
    [Arguments(true, RespireClientTrackingMode.Broadcast)]
    public async Task ConcurrentMissesReuseOneTargetAndKeepOtherKeysCached(bool retry, RespireClientTrackingMode mode)
    {
        await using var target = Target();
        await using var source = Source(target.Port);
        await using var client = await RespireClient.ConnectAsync(Options(source.Port, retry) with
        { ClientSideCache = new() { TrackingMode = mode } });

        var values = await Task.WhenAll(Enumerable.Range(0, 16)
            .Select(index => client.GetStringAsync($"key{index}").AsTask()));
        await Assert.That(values.All(value => value == "value")).IsTrue();
        await Assert.That(client.ClientSideCache!.Count).IsEqualTo(16);
        var reads = target.ReceivedCommands.Count(command => command.StartsWith("GET ", StringComparison.Ordinal));
        foreach (var index in Enumerable.Range(0, 16))
            await Assert.That(await client.GetStringAsync($"key{index}")).IsEqualTo("value");
        await Assert.That(target.ReceivedCommands.Count(command => command.StartsWith("GET ", StringComparison.Ordinal))).IsEqualTo(reads);
        await Assert.That(target.ReceivedCommands.Count(command => command.StartsWith("CLIENT TRACKING ", StringComparison.Ordinal))).IsEqualTo(1);
        await Assert.That(client.Core.RedirectTargets!.Count).IsEqualTo(1);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task InvalidationBeforeReplyPreventsStalePublication(bool retry)
    {
        var reads = 0;
        await using var target = Target();
        target.ReplyOverride = (_, command) => command == "GET key"
            ? Bytes($"$1\r\n{Interlocked.Increment(ref reads)}\r\n") : Handshake(command);
        await using var source = Source(target.Port);
        await using var client = await RespireClient.ConnectAsync(Options(source.Port, retry));
        var received = Signal();
        var release = Signal();
        target.GateCommand("GET key", release.Task, () => received.TrySetResult());
        var pending = client.GetStringAsync("key").AsTask();
        try
        {
            await received.Task.WaitAsync(Limit);
            var invalidated = Signal();
            using var subscription = client.ClientSideCache!.SubscribeInvalidations("key", change =>
            {
                if ((change.Reasons & RespireClientCacheInvalidationReason.ServerInvalidation) != 0) invalidated.TrySetResult();
            });
            // The CACHING reply is still held by the fake server; pushes must not consume it.
            await target.SendRawAsync(Bytes(">2\r\n+invalidate\r\n*1\r\n$3\r\nkey\r\n"));
            await invalidated.Task.WaitAsync(Limit);
        }
        finally { release.TrySetResult(); }
        await Assert.That(await pending.WaitAsync(Limit)).IsEqualTo("1");
        await Assert.That(client.ClientSideCache!.Count).IsEqualTo(0);
        await Assert.That(await client.GetStringAsync("key")).IsEqualTo("2");
        await Assert.That(await client.GetStringAsync("key")).IsEqualTo("2");
        await Assert.That(reads).IsEqualTo(2);
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(false, true)]
    [Arguments(true, true)]
    public async Task ActiveMutationCannotBeRebasedIntoACacheableRead(bool retry, bool duringHandshake)
    {
        await using var target = Target();
        await using var source = Source(target.Port);
        await using var client = await RespireClient.ConnectAsync(Options(source.Port, retry));
        var readReceived = Signal();
        var readRelease = Signal();
        var writeReceived = Signal();
        var writeRelease = Signal();
        // Hold acquisition or the tracked read itself while a local mutation is active.
        target.GateCommand(duringHandshake ? "CLIENT TRACKING " : "GET key", readRelease.Task,
            () => readReceived.TrySetResult());
        source.GateCommand("SET key ", writeRelease.Task, () => writeReceived.TrySetResult());
        var read = client.GetStringAsync("key").AsTask();
        Task? write = null;
        try
        {
            await readReceived.Task.WaitAsync(Limit);
            write = client.Strings.SetAsync("key", "new").AsTask();
            await writeReceived.Task.WaitAsync(Limit);
            readRelease.TrySetResult();
            await Assert.That(await read.WaitAsync(Limit)).IsEqualTo("value");
            await Assert.That(client.ClientSideCache!.Count).IsEqualTo(0);
        }
        finally
        {
            readRelease.TrySetResult();
            writeRelease.TrySetResult();
        }
        if (write is not null) await write.WaitAsync(Limit);
        await Assert.That(await client.GetStringAsync("key")).IsEqualTo("value");
        await Assert.That(target.ReceivedCommands.Count(command => command == "GET key")).IsEqualTo(2);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task CompletedMutationDuringTargetHandshakeCannotPublishStaleRead(bool retry)
    {
        await using var target = Target();
        await using var source = Source(target.Port);
        await using var client = await RespireClient.ConnectAsync(Options(source.Port, retry));
        var received = Signal();
        var release = Signal();
        target.GateCommand("CLIENT TRACKING ", release.Task, () => received.TrySetResult());
        var read = client.GetStringAsync("key").AsTask();
        try
        {
            await received.Task.WaitAsync(Limit);
            // Finish the mutation before tracking is ready. The target never saw it
            // and deliberately keeps returning an old replica value.
            await client.Strings.SetAsync("key", "new").AsTask().WaitAsync(Limit);
        }
        finally { release.TrySetResult(); }
        await Assert.That(await read.WaitAsync(Limit)).IsEqualTo("value");
        await Assert.That(client.ClientSideCache!.Count).IsEqualTo(0);
        await Assert.That(await client.GetStringAsync("key")).IsEqualTo("value");
        await Assert.That(await client.GetStringAsync("key")).IsEqualTo("value");
        await Assert.That(target.ReceivedCommands.Count(command => command == "GET key")).IsEqualTo(2);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task DisconnectFlushesEntriesAndReplacementRestoresTracking(bool retry)
    {
        await using var target = Target();
        await using var source = Source(target.Port);
        await using var client = await RespireClient.ConnectAsync(Options(source.Port, retry) with
        { ReconnectPolicy = new() { InitialDelay = TimeSpan.Zero, JitterRatio = 0 } });
        await Assert.That(await client.GetStringAsync("key")).IsEqualTo("value");
        var invalidated = Signal();
        using var subscription = client.ClientSideCache!.SubscribeInvalidations("key", change =>
        {
            if ((change.Reasons & RespireClientCacheInvalidationReason.ContinuityLost) != 0) invalidated.TrySetResult();
        });
        target.CloseConnections();
        await invalidated.Task.WaitAsync(Limit);
        await Assert.That(client.ClientSideCache.Count).IsEqualTo(0);
        await Assert.That(await client.GetStringAsync("key")).IsEqualTo("value");
        await Assert.That(await client.GetStringAsync("key")).IsEqualTo("value");
        await Assert.That(target.ReceivedCommands.Count(command => command == "GET key")).IsEqualTo(2);
        await Assert.That(target.ReceivedCommands.Count(command => command.StartsWith("CLIENT TRACKING ", StringComparison.Ordinal))).IsEqualTo(2);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task InitialTargetHandshakeRecoversBeforeTrackedRead(bool retry)
    {
        await using var target = Target();
        target.CloseConnectionAfterCommand = 1;
        await using var source = Source(target.Port);
        await using var client = await RespireClient.ConnectAsync(Options(source.Port, retry) with
        {
            ReconnectPolicy = new() { MaxAttempts = 1, InitialDelay = TimeSpan.Zero, JitterRatio = 0 },
        });
        await Assert.That(await client.GetStringAsync("key")).IsEqualTo("value");
        await Assert.That(await client.GetStringAsync("key")).IsEqualTo("value");
        await Assert.That(target.ReceivedCommands.Count(command => command == "HELLO 3")).IsEqualTo(2);
        await Assert.That(target.ReceivedCommands.Count(command => command == "GET key")).IsEqualTo(1);
        await Assert.That(client.ClientSideCache!.Count).IsEqualTo(1);
    }

    [Test]
    public async Task DisposalCancelsTargetAcquisitionBackoff()
    {
        await using var target = Target();
        target.CloseConnectionAfterCommand = 1;
        await using var source = Source(target.Port);
        await using var client = await RespireClient.ConnectAsync(Options(source.Port, retry: false) with
        {
            ReconnectPolicy = new() { InitialDelay = TimeSpan.FromMinutes(1), MaxDelay = TimeSpan.FromMinutes(1), JitterRatio = 0 },
        });
        var backoff = Signal();
        client.ConnectionStateChanged += change =>
        {
            if (change.Endpoint.Port == target.Port && change.NextReconnectDelay is not null) backoff.TrySetResult();
        };
        var pending = client.GetStringAsync("key").AsTask();
        await backoff.Task.WaitAsync(Limit);
        await client.DisposeAsync().AsTask().WaitAsync(Limit);
        await Assert.That(async () => await pending.WaitAsync(Limit)).Throws<Exception>();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task TargetOwnershipIsBoundedAndEvictionFencesEntries(bool retry)
    {
        var targets = Enumerable.Range(0, ReplicaRedirectTargets.MaximumTargets + 1).Select(_ => Target()).ToArray();
        try
        {
            var current = targets[0];
            await using var source = Source(current.Port);
            source.ReplyOverride = (_, command) => command.StartsWith("GET ", StringComparison.Ordinal)
                ? Bytes($"-REDIRECT 127.0.0.1:{current.Port}\r\n") : Handshake(command);
            await using var client = await RespireClient.ConnectAsync(Options(source.Port, retry));
            for (var index = 0; index < targets.Length; index++)
            {
                current = targets[index];
                await Assert.That(await client.GetStringAsync($"key{index}")).IsEqualTo("value");
                await Assert.That(client.Core.RedirectTargets!.Count).IsEqualTo(Math.Min(index + 1, ReplicaRedirectTargets.MaximumTargets));
            }
            await targets[0].PeerClosed.WaitAsync(Limit);
            await Assert.That(client.ClientSideCache!.Count).IsEqualTo(1);
            current = targets[0];
            await Assert.That(await client.GetStringAsync("key0")).IsEqualTo("value");
            await Assert.That(targets[0].ReceivedCommands.Count(command => command == "GET key0")).IsEqualTo(2);
        }
        finally { foreach (var target in targets) await target.DisposeAsync(); }
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(false, true)]
    [Arguments(true, true)]
    public async Task DisposalAbortsTargetReadOrHandshake(bool retry, bool handshake)
    {
        await using var target = Target();
        await using var source = Source(target.Port);
        await using var client = await RespireClient.ConnectAsync(Options(source.Port, retry));
        var received = Signal();
        var release = Signal();
        target.GateCommand(handshake ? "CLIENT TRACKING " : "GET key", release.Task,
            () => received.TrySetResult());
        var pending = client.GetStringAsync("key").AsTask();
        try
        {
            await received.Task.WaitAsync(Limit);
            await client.DisposeAsync().AsTask().WaitAsync(Limit);
            await Assert.That(async () => await pending.WaitAsync(Limit)).Throws<Exception>();
            release.TrySetResult();
            await target.PeerClosed.WaitAsync(Limit);
            await Assert.That(client.Core.RedirectTargets!.Count).IsEqualTo(0);
            await Assert.That(client.ClientSideCache!.Count).IsEqualTo(0);
        }
        finally { release.TrySetResult(); }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ExplicitTrackingRegistrationRetainsTheInvalidationSocket(bool retry)
    {
        await using var target = Target();
        await using var source = Source(target.Port);
        await using var client = await RespireClient.ConnectAsync(Options(source.Port, retry));
        await client.RegisterKeyTrackingAsync("key");
        await Assert.That(client.ClientSideCache!.Count).IsEqualTo(0);
        var invalidated = Signal();
        using var subscription = client.ClientSideCache.SubscribeInvalidations("key", _ => invalidated.TrySetResult());
        await target.SendRawAsync(Bytes(">2\r\n+invalidate\r\n*1\r\n$3\r\nkey\r\n"));
        await invalidated.Task.WaitAsync(Limit);
        await Assert.That(target.ReceivedCommands).Contains("CLIENT CACHING YES");
        await Assert.That(client.Core.RedirectTargets!.Count).IsEqualTo(1);
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(false, true)]
    [Arguments(true, true)]
    public async Task HashQueryAndPartialFieldReadsCacheRedirectedReplies(bool retry, bool reuseFields)
    {
        await using var target = Target();
        target.ReplyOverride = (_, command) => command switch
        {
            "HMGET hash a b" => Bytes("*2\r\n$1\r\nA\r\n$1\r\nB\r\n"),
            "HGET hash a" => Bytes("$1\r\nA\r\n"),
            _ => Handshake(command),
        };
        await using var source = Source(target.Port);
        source.ReplyOverride = (_, command) => command.StartsWith("HGET ", StringComparison.Ordinal)
            || command.StartsWith("HMGET ", StringComparison.Ordinal)
                ? Bytes($"-REDIRECT 127.0.0.1:{target.Port}\r\n") : Handshake(command);
        await using var client = await RespireClient.ConnectAsync(Options(source.Port, retry) with
        { ClientSideCache = new() { ReuseHashFields = reuseFields } });
        for (var attempt = 0; attempt < 2; attempt++)
            await Assert.That(await client.Hashes.GetManyAsync("hash", "a", "b"))
                .IsEquivalentTo(new string?[] { "A", "B" });
        await Assert.That(target.ReceivedCommands.Count(command => command == "HMGET hash a b")).IsEqualTo(1);
        await Assert.That(await client.Hashes.GetStringAsync("hash", "a")).IsEqualTo("A");
        await Assert.That(target.ReceivedCommands.Count(command => command == "HGET hash a")).IsEqualTo(reuseFields ? 0 : 1);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task TargetCredentialRenewalFencesPublishedReads(bool retry)
    {
        var clock = new Respire.Testing.CredentialTestClock();
        var provider = new TargetCredentials(clock);
        await using var target = Target();
        await using var source = Source(target.Port);
        await using var client = await RespireClient.ConnectAsync(Options(source.Port, retry) with
        {
            CredentialProvider = provider, CredentialTimeProvider = clock,
            CredentialRefreshBeforeExpiry = TimeSpan.FromSeconds(20),
        });
        await Assert.That(await client.GetStringAsync("key")).IsEqualTo("value");
        using var timeout = new CancellationTokenSource(Limit);
        while (!clock.HasDelay(TimeSpan.FromSeconds(20))) await Task.Delay(5, timeout.Token);
        var authReceived = Signal();
        var authRelease = Signal();
        target.GateCommand("AUTH user second", authRelease.Task, () => authReceived.TrySetResult());
        var invalidated = Signal();
        using var subscription = client.ClientSideCache!.SubscribeInvalidations("key", _ => invalidated.TrySetResult());
        clock.Advance(TimeSpan.FromSeconds(20));
        try
        {
            await authReceived.Task.WaitAsync(Limit);
            await invalidated.Task.WaitAsync(Limit);
            await Assert.That(client.ClientSideCache.Count).IsEqualTo(0);
        }
        finally { authRelease.TrySetResult(); }
        // The next renewal timer is armed only after the post-AUTH continuity fence.
        while (!clock.HasDelay(TimeSpan.FromSeconds(20))) await Task.Delay(5, timeout.Token);
        await Assert.That(await client.GetStringAsync("key")).IsEqualTo("value");
        await Assert.That(await client.GetStringAsync("key")).IsEqualTo("value");
        await Assert.That(source.ReceivedCommands.Contains("AUTH user second")).IsFalse();
        await Assert.That(target.ReceivedCommands.Contains("AUTH user second")).IsTrue();
        await Assert.That(target.ReceivedCommands.Count(command => command == "GET key")).IsEqualTo(2);
    }

    private sealed class TargetCredentials(TimeProvider clock) : IRespireCredentialProvider
    {
        private int _requests;
        public ValueTask<RespireCredentials> GetCredentialsAsync(CancellationToken cancellationToken = default)
        {
            var request = Interlocked.Increment(ref _requests);
            // Only the target has expiring credentials, isolating its cache fence from the source.
            return ValueTask.FromResult(request == 1 ? new RespireCredentials("user", "first")
                : new RespireCredentials("user", request == 2 ? "first" : "second", clock.GetUtcNow().AddSeconds(40)));
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task BusyOwnersUseUntrackedOverflowWithoutEvictingActiveReads(bool retry)
    {
        var targets = Enumerable.Range(0, ReplicaRedirectTargets.MaximumTargets + 1).Select(_ => Target()).ToArray();
        var release = Signal();
        var reads = new List<Task<string?>>();
        try
        {
            await using var source = Source(targets[0].Port);
            source.ReplyOverride = (_, command) => command.StartsWith("GET key", StringComparison.Ordinal)
                ? Bytes($"-REDIRECT 127.0.0.1:{targets[int.Parse(command[7..])].Port}\r\n") : Handshake(command);
            await using var client = await RespireClient.ConnectAsync(Options(source.Port, retry));
            try
            {
                for (var index = 0; index < ReplicaRedirectTargets.MaximumTargets; index++)
                {
                    var received = Signal();
                    targets[index].GateCommand("GET ", release.Task, () => received.TrySetResult());
                    reads.Add(client.GetStringAsync($"key{index}").AsTask());
                    await received.Task.WaitAsync(Limit);
                }
                for (var attempt = 0; attempt < 2; attempt++)
                    await Assert.That(await client.GetStringAsync($"key{ReplicaRedirectTargets.MaximumTargets}")).IsEqualTo("value");
                await Assert.That(client.Core.RedirectTargets!.Count).IsEqualTo(ReplicaRedirectTargets.MaximumTargets);
                await Assert.That(targets[^1].ReceivedCommands.Count(command => command.StartsWith("GET ", StringComparison.Ordinal))).IsEqualTo(2);
                await Assert.That(targets[^1].ReceivedCommands.Any(command => command.StartsWith("CLIENT TRACKING ", StringComparison.Ordinal))).IsFalse();
            }
            finally { release.TrySetResult(); }
            await Task.WhenAll(reads).WaitAsync(Limit);
        }
        finally
        {
            release.TrySetResult();
            foreach (var target in targets) await target.DisposeAsync();
        }
    }

    private static RespireOptions Options(int port, bool retry) => new()
    {
        Endpoints = [new("127.0.0.1", port)], Connections = 1, EnableReplicaRedirects = true,
        ClientSideCache = new(), RetryPolicy = retry ? new() : null, CommandTimeout = Limit,
    };

    private static FakeRespServer Target() => new(32)
    {
        ReplyOverride = (_, command) => command.StartsWith("GET ", StringComparison.Ordinal)
            ? Bytes("$5\r\nvalue\r\n") : Handshake(command),
    };

    private static FakeRespServer Source(int targetPort) => new(8)
    {
        ReplyOverride = (_, command) => command.StartsWith("GET ", StringComparison.Ordinal)
            ? Bytes($"-REDIRECT 127.0.0.1:{targetPort}\r\n") : Handshake(command),
    };

    private static byte[] Handshake(string command) => command.StartsWith("HELLO 3", StringComparison.Ordinal)
        ? Bytes("%1\r\n$5\r\nproto\r\n:3\r\n") : FakeRespServer.OkReply;
    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
