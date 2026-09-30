using Clock = Respire.Testing.CredentialTestClock;
using Respire.Commands;
using Respire.Internal;
using Respire.Networking;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class CredentialProviderTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(5);
    private static readonly byte[] Hello = "%1\r\n$5\r\nproto\r\n:3\r\n"u8.ToArray();

    [Test]
    public async Task EveryNewConnectionObtainsCurrentCredentials()
    {
        await using var server = new FakeRespServer(2, FakeRespServer.OkReply);
        var provider = new Provider();
        var options = new RespireOptions
        {
            Endpoints = { new("127.0.0.1", server.Port) }, Connections = 1,
            CredentialProvider = provider, Username = "ignored", Password = "ignored",
        };
        await using (var first = await RespireClient.ConnectAsync(options)) { }
        provider.Current = new("user", "second");
        await using (var second = await RespireClient.ConnectAsync(options)) { }
        await Assert.That(provider.Calls).IsEqualTo(2);
        await Assert.That(server.ReceivedCommands)
            .IsEquivalentTo(new[] { "AUTH user first", "AUTH user second" });
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RejectedInitialAuthenticationHasTypedRedactedFailure(bool resp3)
    {
        await using var server = new FakeRespServer("-WRONGPASS private-token\r\n"u8.ToArray());
        try
        {
            await using var connection = await RespireConnection.ConnectAsync("127.0.0.1", server.Port,
                new RespireConnectionOptions { CredentialProvider = new Provider(), UseResp3 = resp3 });
            throw new InvalidOperationException("Expected authentication failure.");
        }
        catch (RespireAuthenticationException error)
        {
            await Assert.That(error.ToString().Contains("private-token", StringComparison.Ordinal)).IsFalse();
        }
    }

    [Test]
    public async Task CredentialStringificationDoesNotExposeSecrets()
    {
        var credentials = new RespireCredentials("private-user", "private-token");
        await Assert.That(credentials.ToString().Contains("private-", StringComparison.Ordinal)).IsFalse();
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
            Options(server, provider, clock) with { UseResp3 = resp3 });
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
    public async Task RejectedRenewalClosesWithAuthenticationFailure()
    {
        var clock = new Clock();
        var provider = ExpiringProvider(clock);
        await using var server = Server();
        server.ReplyOverride = (_, command) => command == "AUTH user second"
            ? "-WRONGPASS rejected\r\n"u8.ToArray() : FakeRespServer.OkReply;
        await using var connection = await RespireConnection.ConnectAsync("127.0.0.1", server.Port, Options(server, provider, clock));
        await UntilAsync(() => clock.HasDelay(TimeSpan.FromSeconds(20)));
        provider.Current = new("user", "second", clock.GetUtcNow().AddSeconds(60));
        clock.Advance(TimeSpan.FromSeconds(20));
        await connection.Closed.WaitAsync(Limit);
        await Assert.That(connection.CloseError is RespireAuthenticationException).IsTrue();
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
    public async Task RenewalFlushesCacheBeforeAndAfterAuthDespiteObserverFailure()
    {
        var clock = new Clock();
        var provider = ExpiringProvider(clock);
        var flushes = 0;
        await using var server = Server();
        await using var connection = await RespireConnection.ConnectAsync("127.0.0.1", server.Port,
            Options(server, provider, clock) with
            {
                CredentialCacheInvalidation = () => { Interlocked.Increment(ref flushes); throw new InvalidOperationException(); },
            });
        await UntilAsync(() => clock.HasDelay(TimeSpan.FromSeconds(20)));
        server.SuppressReply = command => command == "AUTH user second";
        provider.Current = new("user", "second", clock.GetUtcNow().AddSeconds(60));
        clock.Advance(TimeSpan.FromSeconds(20));
        await UntilAsync(() => server.ReceivedCommands.Contains("AUTH user second"));
        await Assert.That(Volatile.Read(ref flushes)).IsEqualTo(1);
        await server.SendRawAsync(FakeRespServer.OkReply);
        await UntilAsync(() => Volatile.Read(ref flushes) == 2);
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
            Endpoints = { new("127.0.0.1", server.Port) }, CredentialProvider = provider,
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
    public async Task Resp2SubscriptionsRejectRenewableCredentials()
    {
        await using var server = Server();
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = { new("127.0.0.1", server.Port) }, CredentialProvider = new Provider(),
            Protocol = RespProtocol.Resp2,
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
            Endpoints = { new("127.0.0.1", server.Port) }, Connections = 1, CredentialProvider = provider,
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
