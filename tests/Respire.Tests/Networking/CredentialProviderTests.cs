using Clock = Respire.Testing.CredentialTestClock;
using Respire.Commands;
using Respire.Internal;
using Respire.Networking;
using Respire.Protocol;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class CredentialProviderTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(5);
    private static readonly byte[] Hello = "%1\r\n$5\r\nproto\r\n:3\r\n"u8.ToArray();

    [Test]
    [Arguments(RespProtocol.Auto)]
    [Arguments(RespProtocol.Resp2)]
    [Arguments(RespProtocol.Resp3)]
    public async Task EveryNewConnectionObtainsCurrentCredentials(RespProtocol protocol)
    {
        await using var server = Server();
        var provider = new Provider();
        var options = new RespireOptions
        {
            Endpoints = { new("127.0.0.1", server.Port) }, Connections = 1,
            CredentialProvider = provider, Username = "ignored", Password = "ignored", Protocol = protocol,
        };
        await using (var first = await RespireClient.ConnectAsync(options)) { }
        provider.Current = new("user", "second");
        await using (var second = await RespireClient.ConnectAsync(options)) { }
        await Assert.That(provider.Calls).IsEqualTo(2);
        await Assert.That(server.ReceivedCommands)
            .IsEquivalentTo(protocol == RespProtocol.Resp2
                ? new[] { "AUTH user first", "AUTH user second" }
                : new[] { "HELLO 3 AUTH user first", "HELLO 3 AUTH user second" });
    }

    [Test]
    [Arguments(RespProtocol.Auto, "WRONGPASS")]
    [Arguments(RespProtocol.Auto, "ERR")]
    [Arguments(RespProtocol.Auto, "NOAUTH")]
    [Arguments(RespProtocol.Resp2, "WRONGPASS")]
    [Arguments(RespProtocol.Resp2, "ERR")]
    [Arguments(RespProtocol.Resp2, "NOAUTH")]
    [Arguments(RespProtocol.Resp3, "WRONGPASS")]
    [Arguments(RespProtocol.Resp3, "ERR")]
    [Arguments(RespProtocol.Resp3, "NOAUTH")]
    public async Task RejectedInitialAuthenticationHasTypedRedactedFailure(RespProtocol protocol, string errorCode)
    {
        await using var server = new FakeRespServer(System.Text.Encoding.UTF8.GetBytes(
            $"-{errorCode} invalid password private-token for private-user\r\n"));
        try
        {
            await using var connection = await RespireConnection.ConnectAsync("127.0.0.1", server.Port,
                new RespireConnectionOptions
                {
                    CredentialProvider = new Provider { Current = new("private-user", "private-token") },
                    Protocol = protocol,
                });
            throw new InvalidOperationException("Expected authentication failure.");
        }
        catch (RespireAuthenticationException error)
        {
            await Assert.That(error.ToString().Contains("private-", StringComparison.Ordinal)).IsFalse();
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task InitialProviderFailuresDoNotExposeProviderExceptionText(bool typed)
    {
        await using var server = Server();
        var provider = new Provider
        {
            Failure = typed ? new RespireAuthenticationException("private-token")
                : new InvalidOperationException("private-token"),
        };
        try
        {
            await using var connection = await RespireConnection.ConnectAsync("127.0.0.1", server.Port,
                new RespireConnectionOptions { CredentialProvider = provider });
            throw new InvalidOperationException("Expected acquisition failure.");
        }
        catch (RespireAuthenticationException error)
        {
            await Assert.That(error.ToString().Contains("private-token", StringComparison.Ordinal)).IsFalse();
            await Assert.That(error.InnerException).IsNull();
        }
        await Assert.That(server.ReceivedCommands).IsEmpty();
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task InvalidationObserversCanSynchronouslyReadAfterCredentialRenewal(bool broadcast, bool coalesce)
    {
        var clock = new Clock();
        var provider = ExpiringProvider(clock);
        var renewalReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var readStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var freshRead = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var renewed = 0;
        await using var server = Server();
        server.ReplyOverride = (_, command) =>
        {
            if (command.StartsWith("HELLO ", StringComparison.Ordinal)) return Hello;
            if (command == "GET key")
                return Volatile.Read(ref renewed) == 0 ? "$3\r\nold\r\n"u8.ToArray() : "$3\r\nnew\r\n"u8.ToArray();
            return FakeRespServer.OkReply;
        };
        server.SuppressReply = command => command == "AUTH user second";
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = [new("127.0.0.1", server.Port)], Connections = 1,
            Protocol = RespProtocol.Resp3, CredentialProvider = provider, CredentialTimeProvider = clock,
            CredentialRefreshBeforeExpiry = TimeSpan.FromSeconds(10), ConnectTimeout = Limit,
            ClientSideCache = new()
            {
                CoalesceConcurrentMisses = coalesce,
                TrackingMode = broadcast ? RespireClientTrackingMode.Broadcast : RespireClientTrackingMode.OptIn,
            },
        });
        await Assert.That(await client.GetStringAsync("key")).IsEqualTo("old");
        using var subscription = client.ClientSideCache!.SubscribeInvalidations("key", invalidation =>
        {
            try
            {
                if (!invalidation.Reasons.HasFlag(RespireClientCacheInvalidationReason.ContinuityLost))
                    throw new InvalidOperationException("Renewal must invalidate tracking continuity.");
                // The first flush may dispatch before AUTH. Keep that callback alive across
                // the fence and synchronously read once Redis has received the replacement.
                renewalReceived.Task.WaitAsync(Limit).GetAwaiter().GetResult();
                var read = client.GetStringAsync("key");
                readStarted.TrySetResult();
                freshRead.TrySetResult(read.AsTask().WaitAsync(Limit).GetAwaiter().GetResult());
            }
            catch (Exception error) { freshRead.TrySetException(error); }
        });
        await UntilAsync(() => clock.HasDelay(TimeSpan.FromSeconds(20)));
        provider.Current = new("user", "second", clock.GetUtcNow().AddSeconds(60));
        clock.Advance(TimeSpan.FromSeconds(20));
        await UntilAsync(() => server.ReceivedCommands.Contains("AUTH user second"));
        Volatile.Write(ref renewed, 1);
        renewalReceived.TrySetResult();
        await readStarted.Task.WaitAsync(Limit);
        await Assert.That(freshRead.Task.IsCompleted).IsFalse();
        await Assert.That(server.ReceivedCommands.Count(command => command == "GET key")).IsEqualTo(1);
        await server.SendRawAsync(FakeRespServer.OkReply);
        await Assert.That(await freshRead.Task.WaitAsync(Limit)).IsEqualTo("new");
        await UntilAsync(() => clock.HasDelay(TimeSpan.FromSeconds(30)));
        await Assert.That(await client.GetStringAsync("key")).IsEqualTo("new");
        await Assert.That(client.IsConnected).IsTrue();
    }

    [Test]
    public async Task CredentialStringificationDoesNotExposeSecrets()
    {
        var credentials = new RespireCredentials("private-user", "private-token");
        await Assert.That(credentials.ToString().Contains("private-", StringComparison.Ordinal)).IsFalse();
    }

    [Test]
    [Arguments("user", "first", 0, true)]
    [Arguments("other", "first", 0, false)]
    [Arguments("user", "firsX", 0, false)]
    [Arguments("user", "first-longer", 0, false)]
    [Arguments("user", "first", 1, false)]
    public async Task CredentialEqualityIncludesIdentitySecretAndExpiry(string user, string password, int seconds, bool same)
    {
        var expiry = DateTimeOffset.UnixEpoch.AddMinutes(1);
        var first = new RespireCredentials("user", "first", expiry);
        var other = new RespireCredentials(user, new string(password.AsSpan()), expiry.AddSeconds(seconds));
        await Assert.That(first.IsSameAs(other)).IsEqualTo(same);
    }

    [Test]
    public async Task ReplacementWithoutExpiryReportsStoppedRenewal()
    {
        var clock = new Clock();
        var provider = ExpiringProvider(clock);
        await using var server = Server();
        var events = new System.Collections.Concurrent.ConcurrentQueue<(string? Stage, string? Outcome)>();
        using var listener = new System.Diagnostics.Metrics.MeterListener();
        listener.InstrumentPublished = (instrument, observer) =>
        {
            if (instrument.Meter.Name == "Respire" && instrument.Name == "respire.authentication.refresh")
                observer.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            int? port = null;
            string? stage = null, outcome = null;
            foreach (var tag in tags)
            {
                if (tag.Key == "server.port") port = (int)tag.Value!;
                if (tag.Key == "respire.authentication.stage") stage = (string)tag.Value!;
                if (tag.Key == "respire.authentication.outcome") outcome = (string)tag.Value!;
            }
            if (port == server.Port) events.Enqueue((stage, outcome));
        });
        listener.Start();
        await using var connection = await RespireConnection.ConnectAsync("127.0.0.1", server.Port,
            Options(server, provider, clock));
        await UntilAsync(() => clock.HasDelay(TimeSpan.FromSeconds(20)));
        provider.Current = new("user", "second");
        clock.Advance(TimeSpan.FromSeconds(20));
        await connection.CredentialRefreshCompletion!.WaitAsync(Limit);
        await Assert.That(events.ToArray()).IsEquivalentTo(new (string?, string?)[]
            { ("reauthenticate", "success"), ("no-expiry", "success") });
        clock.Advance(TimeSpan.FromDays(1));
        await Assert.That(provider.Calls).IsEqualTo(2);
        await Assert.That(connection.IsConnected).IsTrue();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RefreshPreservesInflightReplyOrder(bool resp3)
    {
        var clock = new Clock();
        var provider = ExpiringProvider(clock);
        await using var server = Server();
        await using var connection = await RespireConnection.ConnectAsync("127.0.0.1", server.Port,
            Options(server, provider, clock) with { Protocol = resp3 ? RespProtocol.Resp3 : RespProtocol.Resp2 });
        await UntilAsync(() => clock.HasDelay(TimeSpan.FromSeconds(20)));
        server.SuppressReply = command => command == "PING" || command == "AUTH user second";
        var ping = connection.SendAsync(new RawCommand(FakeRespServer.PingFrame)).AsTask();
        await UntilAsync(() => server.ReceivedCommands.Contains("PING"));
        provider.Current = new("user", "second", clock.GetUtcNow().AddSeconds(60));
        clock.Advance(TimeSpan.FromSeconds(20));
        await UntilAsync(() => server.ReceivedCommands.Contains("AUTH user second"));
        await server.SendRawAsync("+PONG\r\n+OK\r\n"u8.ToArray());
        using var reply = await ping.WaitAsync(Limit);
        await Assert.That(reply.AsString()).IsEqualTo("PONG");
        await UntilAsync(() => clock.HasDelay(TimeSpan.FromSeconds(30)));
        await Assert.That(connection.IsConnected).IsTrue();
        await Assert.That(provider.Calls).IsEqualTo(2);
        await Assert.That(server.ReceivedCommands).IsEquivalentTo(new[]
        {
            resp3 ? "HELLO 3 AUTH user first" : "AUTH user first", "PING", "AUTH user second",
        });
    }

    [Test]
    [Arguments("raw", false, false)]
    [Arguments("raw", false, true)]
    [Arguments("raw", true, false)]
    [Arguments("raw", true, true)]
    [Arguments("string", false, false)]
    [Arguments("string", false, true)]
    [Arguments("string", true, false)]
    [Arguments("string", true, true)]
    [Arguments("converted", false, false)]
    [Arguments("converted", false, true)]
    [Arguments("converted", true, false)]
    [Arguments("converted", true, true)]
    [Arguments("prefix", false, false)]
    [Arguments("prefix", false, true)]
    [Arguments("prefix", true, false)]
    [Arguments("prefix", true, true)]
    [Arguments("transaction", false, false)]
    [Arguments("transaction", false, true)]
    [Arguments("transaction", true, false)]
    [Arguments("transaction", true, true)]
    [Arguments("fire-and-forget", false, false)]
    [Arguments("fire-and-forget", false, true)]
    [Arguments("fire-and-forget", true, false)]
    [Arguments("fire-and-forget", true, true)]
    public async Task PendingRenewalFencesEverySendPath(string kind, bool direct, bool accept)
    {
        var clock = new Clock();
        var provider = ExpiringProvider(clock);
        await using var server = Server();
        await using var connection = await RespireConnection.ConnectAsync("127.0.0.1", server.Port, Options(server, provider, clock));
        await UntilAsync(() => clock.HasDelay(TimeSpan.FromSeconds(20)));
        server.SuppressReply = command => command == "AUTH user second";
        provider.Current = new("user", "second", clock.GetUtcNow().AddSeconds(60));
        clock.Advance(TimeSpan.FromSeconds(20));
        await UntilAsync(() => server.ReceivedCommands.Contains("AUTH user second"));
        var pending = StartSend(connection, kind, direct);
        // Send APIs synchronously attempt admission before returning their incomplete task.
        // A single AUTH slot proves the application frame was not queued, independent of flushing.
        await Assert.That(connection.CaptureTimeoutDiagnostics().InflightCount).IsEqualTo(1);
        await Assert.That(pending.IsCompleted).IsFalse();
        await server.SendRawAsync(accept ? FakeRespServer.OkReply : "-WRONGPASS rejected\r\n"u8.ToArray());
        if (accept)
        {
            await pending.WaitAsync(Limit);
            await UntilAsync(() => server.ReceivedCommands.Count > 2);
            await Assert.That(connection.IsConnected).IsTrue();
        }
        else
        {
            await Assert.That(async () => await pending.WaitAsync(Limit)).Throws<RespireAuthenticationException>();
            await connection.Closed.WaitAsync(Limit);
            await Assert.That(server.ReceivedCommands).IsEquivalentTo(new[] { "AUTH user first", "AUTH user second" });
            await Assert.That(connection.CloseError is RespireAuthenticationException).IsTrue();
        }
    }

    [Test]
    [Arguments("cancel")]
    [Arguments("dispose")]
    [Arguments("expire")]
    public async Task PendingRenewalReleasesWaitingCallerWithoutSendingIt(string outcome)
    {
        var clock = new Clock();
        var provider = ExpiringProvider(clock);
        await using var server = Server();
        await using var connection = await RespireConnection.ConnectAsync("127.0.0.1", server.Port, Options(server, provider, clock));
        await UntilAsync(() => clock.HasDelay(TimeSpan.FromSeconds(20)));
        server.SuppressReply = command => command == "AUTH user second";
        provider.Current = new("user", "second", clock.GetUtcNow().AddSeconds(60));
        clock.Advance(TimeSpan.FromSeconds(20));
        await UntilAsync(() => server.ReceivedCommands.Contains("AUTH user second"));
        using var cancellation = new CancellationTokenSource();
        var pending = StartSend(connection, "raw", false, cancellation.Token);
        await Assert.That(connection.CaptureTimeoutDiagnostics().InflightCount).IsEqualTo(1);
        if (outcome == "cancel")
        {
            cancellation.Cancel();
            await Assert.That(async () => await pending.WaitAsync(Limit)).Throws<OperationCanceledException>();
            await server.SendRawAsync(FakeRespServer.OkReply);
            await UntilAsync(() => clock.HasDelay(TimeSpan.FromSeconds(30)));
        }
        else
        {
            if (outcome == "dispose") await connection.DisposeAsync().AsTask().WaitAsync(Limit);
            else clock.Advance(TimeSpan.FromSeconds(10));
            await Assert.That(async () => await pending.WaitAsync(Limit)).Throws<RespireConnectionException>();
        }
        await Assert.That(server.ReceivedCommands).IsEquivalentTo(new[] { "AUTH user first", "AUTH user second" });
    }

    [Test]
    [Arguments("delay")]
    [Arguments("provider")]
    [Arguments("enqueue")]
    public async Task RetirementDuringRenewalPreservesAcceptedReply(string phase)
    {
        var clock = new Clock();
        var provider = ExpiringProvider(clock);
        await using var server = Server();
        Task? retirement = null;
        RespireConnection? retiringConnection = null;
        await using var connection = await RespireConnection.ConnectAsync("127.0.0.1", server.Port,
            Options(server, provider, clock) with
            {
                CredentialCacheInvalidation = () =>
                {
                    if (phase == "enqueue") retirement ??= retiringConnection!.RetireAsync();
                    return 0;
                },
            });
        retiringConnection = connection;
        await UntilAsync(() => clock.HasDelay(TimeSpan.FromSeconds(20)));
        server.SuppressReply = command => command == "PING";
        var accepted = connection.SendAsync(new RawCommand(FakeRespServer.PingFrame)).AsTask();
        await UntilAsync(() => server.ReceivedCommands.Contains("PING"));
        provider.Current = new("user", "second", clock.GetUtcNow().AddSeconds(60));
        if (phase == "provider") provider.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        if (phase == "delay") retirement = connection.RetireAsync();
        clock.Advance(TimeSpan.FromSeconds(20));
        if (phase == "provider")
        {
            await UntilAsync(() => Volatile.Read(ref provider.Calls) == 2);
            retirement = connection.RetireAsync();
            provider.Pending!.TrySetResult(provider.Current);
        }
        await connection.CredentialRefreshCompletion!.WaitAsync(Limit);
        await Assert.That(retirement).IsNotNull();
        await Assert.That(retirement!.IsCompleted).IsFalse();
        await Assert.That(connection.IsConnected).IsTrue();
        await Assert.That(accepted.IsCompleted).IsFalse();
        await server.SendRawAsync(FakeRespServer.PongReply);
        using var reply = await accepted.WaitAsync(Limit);
        await Assert.That(reply.AsString()).IsEqualTo("PONG");
        await retirement.WaitAsync(Limit);
        await Assert.That(connection.DrainedSuccessfully).IsTrue();
        await Assert.That(server.ReceivedCommands).IsEquivalentTo(new[] { "AUTH user first", "PING" });
        await Assert.That(provider.Calls).IsEqualTo(phase == "delay" ? 1 : 2);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task FencedFireAndForgetHonorsCommandTimeoutWithoutWriting(bool direct)
    {
        var clock = new Clock();
        var provider = ExpiringProvider(clock);
        await using var server = Server();
        await using var connection = await RespireConnection.ConnectAsync("127.0.0.1", server.Port,
            Options(server, provider, clock) with { CommandTimeout = TimeSpan.FromMilliseconds(200) });
        await UntilAsync(() => clock.HasDelay(TimeSpan.FromSeconds(20)));
        server.SuppressReply = command => command == "AUTH user second";
        provider.Current = new("user", "second", clock.GetUtcNow().AddSeconds(60));
        clock.Advance(TimeSpan.FromSeconds(20));
        await UntilAsync(() => server.ReceivedCommands.Contains("AUTH user second"));
        var pending = StartSend(connection, "fire-and-forget", direct);
        var error = await Assert.That(async () => await pending.WaitAsync(Limit)).ThrowsExactly<RespireTimeoutException>();
        await Assert.That(error!.Diagnostics.Stage).IsEqualTo(RespireCommandStage.WaitingForCapacity);
        await Assert.That(connection.IsConnected).IsTrue();
        await Assert.That(server.ReceivedCommands).IsEquivalentTo(new[] { "AUTH user first", "AUTH user second" });
        await server.SendRawAsync(FakeRespServer.OkReply);
        await UntilAsync(() => clock.HasDelay(TimeSpan.FromSeconds(30)));
        using var reply = await connection.SendAsync(new RawCommand(FakeRespServer.PingFrame));
        await Assert.That(reply.AsString()).IsEqualTo("PONG");
    }

    private static Task StartSend(RespireConnection connection, string kind, bool direct, CancellationToken cancellationToken = default)
    {
        // The large-frame fallback is thread-static. Set and restore it synchronously
        // around the initial enqueue attempt, never across an await or another thread.
        var budget = typeof(RespireConnection).GetField("_directPathBudget",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var previous = budget.GetValue(null);
        try
        {
            budget.SetValue(null, direct ? 1 : 0);
            return SendAsync();
        }
        finally { budget.SetValue(null, previous); }

        async Task SendAsync()
        {
            var ping = new RawCommand(FakeRespServer.PingFrame);
            switch (kind)
            {
                case "string":
                    await connection.SendStringAsync(ping, cancellationToken);
                    break;
                case "converted":
                    await connection.SendConvertedAsync(ping, 0,
                        static (int _, in RespValue value) => value.AsString(), false, cancellationToken);
                    break;
                case "fire-and-forget":
                    await connection.SendFireAndForgetAsync(ping, cancellationToken);
                    break;
                case "prefix":
                    using (await connection.SendPrefixedCheckedAsync(ping, ping, cancellationToken)) { }
                    break;
                case "transaction":
                    using (await connection.SendTransactionAsync(FakeRespServer.PingFrame, 1, cancellationToken)) { }
                    break;
                default:
                    using (await connection.SendAsync(ping, cancellationToken)) { }
                    break;
            }
        }
    }

    [Test]
    public async Task ProviderFailureRetriesWithoutClosingAValidConnection()
    {
        var clock = new Clock();
        var provider = ExpiringProvider(clock);
        await using var server = Server();
        await using var connection = await RespireConnection.ConnectAsync("127.0.0.1", server.Port, Options(server, provider, clock));
        await UntilAsync(() => clock.HasDelay(TimeSpan.FromSeconds(20)));
        provider.Failure = new InvalidOperationException("provider failed");
        clock.Advance(TimeSpan.FromSeconds(20));
        await UntilAsync(() => provider.Calls == 2 && clock.HasDelay(TimeSpan.FromSeconds(1)));
        await Assert.That(connection.IsConnected).IsTrue();
        provider.Failure = null;
        provider.Current = new("user", "second", clock.GetUtcNow().AddSeconds(60));
        clock.Advance(TimeSpan.FromSeconds(1));
        await UntilAsync(() => server.ReceivedCommands.Contains("AUTH user second"));
        await Assert.That(connection.IsConnected).IsTrue();
    }

    [Test]
    public async Task UnchangedCredentialsAreRetriedAndCannotOutliveExpiry()
    {
        var clock = new Clock();
        var provider = ExpiringProvider(clock);
        await using var server = Server();
        await using var connection = await RespireConnection.ConnectAsync("127.0.0.1", server.Port, Options(server, provider, clock));
        await UntilAsync(() => clock.HasDelay(TimeSpan.FromSeconds(20)));
        clock.Advance(TimeSpan.FromSeconds(20));
        await UntilAsync(() => provider.Calls == 2 && clock.HasDelay(TimeSpan.FromSeconds(1)));
        clock.Advance(TimeSpan.FromSeconds(10));
        await connection.Closed.WaitAsync(Limit);
        await Assert.That(connection.CloseError is RespireAuthenticationException).IsTrue();
        await Assert.That(server.ReceivedCommands).IsEquivalentTo(new[] { "AUTH user first" });
    }

    [Test]
    [Arguments("WRONGPASS")]
    [Arguments("ERR")]
    public async Task RejectedRenewalClosesWithAuthenticationFailure(string errorCode)
    {
        var clock = new Clock();
        var provider = ExpiringProvider(clock);
        await using var server = Server();
        server.ReplyOverride = (_, command) => command == "AUTH user second"
            ? System.Text.Encoding.UTF8.GetBytes($"-{errorCode} invalid password second\r\n") : FakeRespServer.OkReply;
        await using var connection = await RespireConnection.ConnectAsync("127.0.0.1", server.Port, Options(server, provider, clock));
        await UntilAsync(() => clock.HasDelay(TimeSpan.FromSeconds(20)));
        provider.Current = new("user", "second", clock.GetUtcNow().AddSeconds(60));
        clock.Advance(TimeSpan.FromSeconds(20));
        await connection.Closed.WaitAsync(Limit);
        await Assert.That(connection.CloseError is RespireAuthenticationException).IsTrue();
        await Assert.That(connection.CloseError!.ToString().Contains("second", StringComparison.Ordinal)).IsFalse();
    }

    [Test]
    public async Task DisposalCancelsAnUncooperativeRefreshProvider()
    {
        var clock = new Clock();
        var provider = ExpiringProvider(clock);
        await using var server = Server();
        await using var connection = await RespireConnection.ConnectAsync("127.0.0.1", server.Port, Options(server, provider, clock));
        await UntilAsync(() => clock.HasDelay(TimeSpan.FromSeconds(20)));
        provider.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        clock.Advance(TimeSpan.FromSeconds(20));
        await UntilAsync(() => provider.Calls == 2);
        await connection.DisposeAsync().AsTask().WaitAsync(Limit);
        await Assert.That(connection.CredentialRefreshCompletion!.IsCompletedSuccessfully).IsTrue();
        provider.Pending.SetResult(provider.Current);
    }

    [Test]
    public async Task InitialProviderAcquisitionPreservesCallerCancellation()
    {
        var provider = new Provider { Pending = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        using var cancellation = new CancellationTokenSource();
        var pending = RespireConnection.ConnectAsync("127.0.0.1", 1,
            new RespireConnectionOptions { CredentialProvider = provider }, cancellationToken: cancellation.Token);
        await UntilAsync(() => provider.Calls == 1);
        cancellation.Cancel();
        try
        {
            await pending.WaitAsync(Limit);
            throw new InvalidOperationException("Expected cancellation.");
        }
        catch (OperationCanceledException error)
        {
            await Assert.That(error.CancellationToken).IsEqualTo(cancellation.Token);
        }
        provider.Pending.SetResult(provider.Current);
    }

    [Test]
    public async Task SentinelProviderAndStaticOverridesStaySeparate()
    {
        var data = new Provider();
        var sentinel = new Provider();
        var options = new RespireOptions { CredentialProvider = data, SentinelCredentialProvider = sentinel };
        await Assert.That(ReferenceEquals(SentinelResolver.CreateSentinelConnectionOptions(options).CredentialProvider, sentinel)).IsTrue();
        await Assert.That(ReferenceEquals(SentinelResolver.CreateSentinelConnectionOptions(options with { SentinelCredentialProvider = null }).CredentialProvider, data)).IsTrue();
        await Assert.That(SentinelResolver.CreateSentinelConnectionOptions(options with { SentinelPassword = "" }).CredentialProvider).IsNull();
        await Assert.That(SentinelResolver.CreateSentinelConnectionOptions(options with { SentinelCredentialProvider = null, SentinelPassword = "static" }).CredentialProvider).IsNull();
    }

    [Test]
    [Arguments("null")]
    [Arguments("expired")]
    [Arguments("failure")]
    public async Task InvalidInitialCredentialsFailBeforeOpeningASocket(string kind)
    {
        var clock = new Clock();
        var provider = ExpiringProvider(clock);
        if (kind == "null") provider.Current = null!;
        if (kind == "expired") provider.Current = new("user", "secret", clock.GetUtcNow());
        if (kind == "failure") provider.Failure = new InvalidOperationException("acquisition failed");
        await using var server = Server();
        await Assert.That(async () => await RespireConnection.ConnectAsync("127.0.0.1", server.Port,
            Options(server, provider, clock))).ThrowsExactly<RespireAuthenticationException>();
        await Assert.That(server.CommandsSeen).IsEqualTo(0);
    }

    [Test]
    public async Task CredentialsExpiringDuringHandshakeCannotPublishAConnection()
    {
        var clock = new Clock();
        var provider = ExpiringProvider(clock);
        await using var server = Server();
        server.SuppressReply = _ => true;
        var pending = RespireConnection.ConnectAsync("127.0.0.1", server.Port, Options(server, provider, clock));
        await UntilAsync(() => server.CommandsSeen == 1);
        clock.Advance(TimeSpan.FromSeconds(30));
        await server.SendRawAsync(FakeRespServer.OkReply);
        await Assert.That(async () => await pending.WaitAsync(Limit)).ThrowsExactly<RespireAuthenticationException>();
    }

    [Test]
    public async Task InitialProviderTimeoutIsBounded()
    {
        var clock = new Clock();
        var provider = new Provider { Pending = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var server = Server();
        var pending = RespireConnection.ConnectAsync("127.0.0.1", server.Port, Options(server, provider, clock));
        await UntilAsync(() => provider.Calls == 1 && clock.HasDelay(Limit));
        clock.Advance(Limit);
        await Assert.That(async () => await pending.WaitAsync(Limit)).ThrowsExactly<RespireAuthenticationException>();
        provider.Pending.SetResult(provider.Current);
    }

    [Test]
    public async Task RenewalCannotWaitPastReplacementExpiry()
    {
        var clock = new Clock();
        var provider = ExpiringProvider(clock);
        await using var server = Server();
        await using var connection = await RespireConnection.ConnectAsync("127.0.0.1", server.Port, Options(server, provider, clock));
        await UntilAsync(() => clock.HasDelay(TimeSpan.FromSeconds(20)));
        provider.Current = new("user", "second", clock.GetUtcNow().AddSeconds(21));
        server.SuppressReply = command => command == "AUTH user second";
        clock.Advance(TimeSpan.FromSeconds(20));
        await UntilAsync(() => server.ReceivedCommands.Contains("AUTH user second"));
        clock.Advance(TimeSpan.FromSeconds(1));
        await connection.Closed.WaitAsync(Limit);
        await Assert.That(connection.CloseError is RespireAuthenticationException).IsTrue();
    }

    [Test]
    public async Task RenewalRejectsIdentityChanges()
    {
        var clock = new Clock();
        var provider = ExpiringProvider(clock);
        await using var server = Server();
        await using var connection = await RespireConnection.ConnectAsync("127.0.0.1", server.Port, Options(server, provider, clock));
        await UntilAsync(() => clock.HasDelay(TimeSpan.FromSeconds(20)));
        provider.Current = new("other-user", "second", clock.GetUtcNow().AddSeconds(60));
        clock.Advance(TimeSpan.FromSeconds(20));
        await connection.Closed.WaitAsync(Limit);
        await Assert.That(connection.CloseError is RespireAuthenticationException).IsTrue();
        await Assert.That(server.ReceivedCommands).IsEquivalentTo(new[] { "AUTH user first" });
    }

    [Test]
    public async Task ExpiryDuringFinalCacheFlushNeverReopensAdmission()
    {
        var clock = new Clock();
        var provider = ExpiringProvider(clock);
        var flushes = 0;
        await using var server = Server();
        await using var connection = await RespireConnection.ConnectAsync("127.0.0.1", server.Port,
            Options(server, provider, clock) with
            {
                CredentialCacheInvalidation = () =>
                {
                    if (Interlocked.Increment(ref flushes) == 2) clock.Advance(TimeSpan.FromSeconds(40));
                    return 0;
                },
            });
        await UntilAsync(() => clock.HasDelay(TimeSpan.FromSeconds(20)));
        server.SuppressReply = command => command == "AUTH user second";
        provider.Current = new("user", "second", clock.GetUtcNow().AddSeconds(60));
        clock.Advance(TimeSpan.FromSeconds(20));
        await UntilAsync(() => server.ReceivedCommands.Contains("AUTH user second"));
        var pending = connection.SendAsync(new RawCommand(FakeRespServer.PingFrame)).AsTask();
        await server.SendRawAsync(FakeRespServer.OkReply);
        await connection.CredentialRefreshCompletion!.WaitAsync(Limit);
        await Assert.That(async () => await pending.WaitAsync(Limit)).Throws<RespireException>();
        await Assert.That(connection.CloseError is RespireAuthenticationException).IsTrue();
        await Assert.That(server.ReceivedCommands.Contains("PING")).IsFalse();
    }

    [Test]
    [Arguments(1)]
    [Arguments(2)]
    public async Task CacheMutationFailureAbortsWithoutReopeningAdmission(int failingFlush)
    {
        var clock = new Clock();
        var provider = ExpiringProvider(clock);
        var flushes = 0;
        await using var server = Server();
        await using var connection = await RespireConnection.ConnectAsync("127.0.0.1", server.Port,
            Options(server, provider, clock) with
            {
                CredentialCacheInvalidation = () => Interlocked.Increment(ref flushes) == failingFlush
                    ? throw new InvalidOperationException("cache mutation failed") : 0,
            });
        await UntilAsync(() => clock.HasDelay(TimeSpan.FromSeconds(20)));
        provider.Current = new("user", "second", clock.GetUtcNow().AddSeconds(60));
        clock.Advance(TimeSpan.FromSeconds(20));
        await connection.CredentialRefreshCompletion!.WaitAsync(Limit);
        await Assert.That(connection.IsConnected).IsFalse();
        await Assert.That(connection.CloseError is RespireAuthenticationException).IsTrue();
        await Assert.That(server.ReceivedCommands.Contains("AUTH user second")).IsEqualTo(failingFlush == 2);
        await Assert.That(async () => await connection.SendAsync(new RawCommand(FakeRespServer.PingFrame)))
            .Throws<RespireException>();
        await Assert.That(server.ReceivedCommands.Contains("PING")).IsFalse();
    }

    [Test]
    public async Task RenewalFlushesCacheBeforeAndAfterAuthDespiteObserverFailure()
    {
        var clock = new Clock();
        var provider = ExpiringProvider(clock);
        var flushes = 0;
        var observe = new AsyncLocal<bool>();
        var failures = 0;
        using var listener = new System.Diagnostics.Metrics.MeterListener();
        listener.InstrumentPublished = (instrument, observer) =>
        {
            if (instrument.Meter.Name == "Respire" && instrument.Name == "respire.client_cache.continuity_flushes")
                observer.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, _, _, _) =>
        {
            if (!observe.Value) return;
            Interlocked.Increment(ref failures);
            throw new InvalidOperationException("observer failed");
        });
        listener.Start();
        await using var server = Server();
        await using var connection = await RespireConnection.ConnectAsync("127.0.0.1", server.Port,
            Options(server, provider, clock) with
            {
                CredentialCacheInvalidation = () => { observe.Value = true; Interlocked.Increment(ref flushes); return 0; },
            });
        await UntilAsync(() => clock.HasDelay(TimeSpan.FromSeconds(20)));
        server.SuppressReply = command => command == "AUTH user second";
        provider.Current = new("user", "second", clock.GetUtcNow().AddSeconds(60));
        clock.Advance(TimeSpan.FromSeconds(20));
        await UntilAsync(() => server.ReceivedCommands.Contains("AUTH user second"));
        await Assert.That(Volatile.Read(ref flushes)).IsEqualTo(1);
        await server.SendRawAsync(FakeRespServer.OkReply);
        await UntilAsync(() => clock.HasDelay(TimeSpan.FromSeconds(30)));
        await Assert.That(Volatile.Read(ref flushes)).IsEqualTo(2);
        await Assert.That(Volatile.Read(ref failures)).IsEqualTo(2);
        await Assert.That(connection.IsConnected).IsTrue();
    }

    [Test]
    public async Task RenewalStaysOutsideAtomicTransactionFrames()
    {
        var clock = new Clock();
        var provider = ExpiringProvider(clock);
        await using var server = Server();
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = { new("127.0.0.1", server.Port) }, CredentialProvider = provider, Protocol = RespProtocol.Resp2,
            CredentialTimeProvider = clock, CredentialRefreshBeforeExpiry = TimeSpan.FromSeconds(10),
        });
        await UntilAsync(() => clock.HasDelay(TimeSpan.FromSeconds(20)));
        provider.Current = new("user", "second", clock.GetUtcNow().AddSeconds(60));
        server.ReplyOverride = (_, command) =>
        {
            if (command == "MULTI") clock.Advance(TimeSpan.FromSeconds(20));
            return command.StartsWith("SET ", StringComparison.Ordinal) ? "+QUEUED\r\n"u8.ToArray()
                : command == "EXEC" ? "*1\r\n+OK\r\n"u8.ToArray() : FakeRespServer.OkReply;
        };
        await using var transaction = client.CreateTransaction();
        var stored = transaction.Set("key", "value");
        await transaction.CommitAsync();
        await UntilAsync(() => server.ReceivedCommands.Contains("AUTH user second"));
        await Assert.That(await stored).IsTrue();
        await Assert.That(server.ReceivedCommands).IsEquivalentTo(new[]
        {
            "AUTH user first", "MULTI", "SET key value", "EXEC", "AUTH user second",
        }, TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task UnchangedCredentialsExposeRetryWithoutAuthenticationOrWarning()
    {
        var clock = new Clock();
        var provider = ExpiringProvider(clock);
        await using var server = Server();
        var observed = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var listener = new System.Diagnostics.Metrics.MeterListener();
        listener.InstrumentPublished = (instrument, observer) =>
        {
            if (instrument.Meter.Name == "Respire" && instrument.Name == "respire.authentication.refresh")
                observer.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            int? port = null;
            string? stage = null, outcome = null;
            foreach (var tag in tags)
            {
                if (tag.Key == "server.port") port = (int)tag.Value!;
                if (tag.Key == "respire.authentication.stage") stage = (string)tag.Value!;
                if (tag.Key == "respire.authentication.outcome") outcome = (string)tag.Value!;
            }
            if (port == server.Port && stage == "unchanged") observed.TrySetResult(outcome);
        });
        listener.Start();
        var logger = new FailingLogger();
        await using var connection = await RespireConnection.ConnectAsync("127.0.0.1", server.Port,
            Options(server, provider, clock), logger);
        await UntilAsync(() => clock.HasDelay(TimeSpan.FromSeconds(20)));
        clock.Advance(TimeSpan.FromSeconds(20));
        await Assert.That(await observed.Task.WaitAsync(Limit)).IsEqualTo("retry");
        await Assert.That(logger.Messages.Count).IsEqualTo(0);
        await Assert.That(server.ReceivedCommands).IsEquivalentTo(new[] { "AUTH user first" });
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RenewalMetricsObserversCanSendAfterAdmissionResumes(bool advanceFromReply)
    {
        var clock = new Clock();
        var provider = ExpiringProvider(clock);
        await using var server = Server();
        var observe = new AsyncLocal<bool>();
        var completed = 0;
        RespireConnection? observedConnection = null;
        using var listener = new System.Diagnostics.Metrics.MeterListener();
        listener.InstrumentPublished = (instrument, observer) =>
        {
            if (instrument.Meter.Name == "Respire"
                && instrument.Name is "respire.authentication.refresh" or "respire.client_cache.continuity_flushes")
                observer.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, _, _, _) =>
        {
            if (!observe.Value || observedConnection is null) return;
            using var reply = observedConnection.SendAsync(new RawCommand(FakeRespServer.PingFrame)).AsTask()
                .WaitAsync(Limit).GetAwaiter().GetResult();
            Interlocked.Increment(ref completed);
        });
        listener.Start();
        await using var connection = await RespireConnection.ConnectAsync("127.0.0.1", server.Port,
            Options(server, provider, clock) with
            {
                CredentialCacheInvalidation = () => { observe.Value = true; return 0; },
            });
        observedConnection = connection;
        await UntilAsync(() => clock.HasDelay(TimeSpan.FromSeconds(20)));
        provider.Current = new("user", "second", clock.GetUtcNow().AddSeconds(60));
        if (advanceFromReply)
        {
            // Force the clock callback onto this connection's serial completion worker.
            // The first cache metric must leave that worker before synchronously awaiting PING.
            server.SuppressReply = command => command == "PING";
            var advance = AdvanceFromReplyAsync();
            await UntilAsync(() => server.ReceivedCommands.Contains("PING"));
            server.SuppressReply = null;
            await server.SendRawAsync("+PONG\r\n"u8.ToArray());
            await advance.WaitAsync(Limit);
        }
        else clock.Advance(TimeSpan.FromSeconds(20));

        async Task AdvanceFromReplyAsync()
        {
            using var reply = await connection.SendAsync(new RawCommand(FakeRespServer.PingFrame)).ConfigureAwait(false);
            clock.Advance(TimeSpan.FromSeconds(20));
        }
        await UntilAsync(() => clock.HasDelay(TimeSpan.FromSeconds(30)));
        await Assert.That(Volatile.Read(ref completed)).IsEqualTo(3);
        await Assert.That(connection.IsConnected).IsTrue();
    }

    [Test]
    public async Task FailingTelemetryCannotStopRenewalOrExposeProviderSecrets()
    {
        var clock = new Clock();
        var provider = ExpiringProvider(clock);
        await using var server = Server();
        var stages = new System.Collections.Concurrent.ConcurrentQueue<string>();
        using var listener = new System.Diagnostics.Metrics.MeterListener();
        listener.InstrumentPublished = (instrument, observer) =>
        {
            if (instrument.Meter.Name == "Respire" && instrument.Name == "respire.authentication.refresh")
                observer.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            var port = 0;
            var stage = "";
            foreach (var tag in tags)
            {
                if (tag.Key == "server.port") port = (int)tag.Value!;
                if (tag.Key == "respire.authentication.stage") stage = (string)tag.Value!;
            }
            if (port != server.Port) return;
            stages.Enqueue(stage);
            throw new InvalidOperationException("listener failed");
        });
        listener.Start();
        var logger = new FailingLogger();
        await using var connection = await RespireConnection.ConnectAsync("127.0.0.1", server.Port,
            Options(server, provider, clock), logger);
        await UntilAsync(() => clock.HasDelay(TimeSpan.FromSeconds(20)));
        provider.Failure = new InvalidOperationException("private-token");
        clock.Advance(TimeSpan.FromSeconds(20));
        await UntilAsync(() => clock.HasDelay(TimeSpan.FromSeconds(1)));
        provider.Failure = null;
        provider.Current = new("user", "second", clock.GetUtcNow().AddSeconds(60));
        clock.Advance(TimeSpan.FromSeconds(1));
        await UntilAsync(() => stages.Contains("reauthenticate"));
        await Assert.That(stages.Contains("provider")).IsTrue();
        await Assert.That(logger.Messages.Any(message => message.Contains("private-token", StringComparison.Ordinal))).IsFalse();
        await Assert.That(logger.Messages.Count).IsEqualTo(1);
        await Assert.That(connection.IsConnected).IsTrue();
    }

    [Test]
    public async Task RetryDelayLongerThanOneDayIsPreserved()
    {
        var clock = new Clock();
        var provider = new Provider
        {
            Current = new("user", "first", clock.GetUtcNow().AddDays(10)),
        };
        await using var server = Server();
        await using var connection = await RespireConnection.ConnectAsync("127.0.0.1", server.Port,
            Options(server, provider, clock) with
            {
                CredentialRefreshBeforeExpiry = TimeSpan.FromDays(5),
                CredentialRefreshRetryDelay = TimeSpan.FromDays(3),
            });

        provider.Failure = new InvalidOperationException("provider unavailable");
        for (var day = 0; day < 5; day++)
        {
            await UntilAsync(() => clock.HasDelay(TimeSpan.FromDays(1)));
            clock.Advance(TimeSpan.FromDays(1));
        }
        await UntilAsync(() => provider.Calls == 2 && clock.HasDelay(TimeSpan.FromDays(1)));

        clock.Advance(TimeSpan.FromDays(1));
        await UntilAsync(() => clock.HasDelay(TimeSpan.FromDays(1)));
        await Assert.That(provider.Calls).IsEqualTo(2);
        await Assert.That(connection.IsConnected).IsTrue();
    }

    [Test]
    [Arguments(RespProtocol.Resp2)]
    [Arguments(RespProtocol.Auto)]
    public async Task SubscriptionsRequireStrictResp3ForRenewableCredentials(RespProtocol protocol)
    {
        await using var server = Server();
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = { new("127.0.0.1", server.Port) }, CredentialProvider = new Provider(),
            Protocol = protocol,
        });
        await Assert.That(async () => await client.SubscribeAsync("channel")).ThrowsExactly<RespireConfigurationException>();
    }

    [Test]
    public async Task StaticCredentialsCreateNoRefreshWorker()
    {
        await using var server = Server();
        await using var connection = await RespireConnection.ConnectAsync("127.0.0.1", server.Port,
            new RespireConnectionOptions { Password = "static" });
        await Assert.That((object?)connection.CredentialRefreshCompletion).IsNull();
    }

    private static FakeRespServer Server() => new(3, FakeRespServer.OkReply)
    {
        ReplyOverride = (_, command) => command.StartsWith("HELLO ", StringComparison.Ordinal) ? Hello
            : command == "PING" ? FakeRespServer.PongReply : FakeRespServer.OkReply,
    };

    private static RespireConnectionOptions Options(FakeRespServer server, Provider provider, Clock clock)
        => new RespireOptions
        {
            Endpoints = { new("127.0.0.1", server.Port) }, Connections = 1, CredentialProvider = provider, Protocol = RespProtocol.Resp2,
            CredentialTimeProvider = clock, CredentialRefreshBeforeExpiry = TimeSpan.FromSeconds(10),
            CredentialRefreshRetryDelay = TimeSpan.FromSeconds(1), ConnectTimeout = Limit,
        }.ToConnectionOptions();

    private static Provider ExpiringProvider(Clock clock)
        => new() { Current = new("user", "first", clock.GetUtcNow().AddSeconds(30)) };

    private static async Task UntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(Limit);
        while (!condition()) await Task.Delay(5, timeout.Token);
    }

    private sealed class Provider : IRespireCredentialProvider
    {
        public RespireCredentials Current = new("user", "first");
        public int Calls;
        public Exception? Failure;
        public TaskCompletionSource<RespireCredentials>? Pending;

        public ValueTask<RespireCredentials> GetCredentialsAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls);
            if (Failure is { } error) throw error;
            if (Pending is { } pending) return new(pending.Task);
            return ValueTask.FromResult(Current);
        }
    }

    private sealed class FailingLogger : Microsoft.Extensions.Logging.ILogger
    {
        public System.Collections.Concurrent.ConcurrentQueue<string> Messages { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId,
            TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (eventId.Id != 4001) return;
            Messages.Enqueue(formatter(state, exception));
            throw new InvalidOperationException("logger failed");
        }
    }

}
