using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Respire.Streaming;
using Respire.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Streaming.Tests;

public class StreamWorkerTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    [Test]
    [Arguments(2, false)]
    [Arguments(2, true)]
    [Arguments(3, false)]
    [Arguments(3, true)]
    public async Task AckUsesRegisteredViewAndFreshAsyncScopes(int protocol, bool prefix)
    {
        await using var fixture = await Fixture.CreateAsync(protocol, prefix);
        for (var i = 0; i < 5; i++) await fixture.AddAsync(i);
        await fixture.StartAsync();
        for (var i = 0; i < 5; i++) await fixture.State.Deliveries.Reader.ReadAsync().AsTask().WaitAsync(Deadline);
        await UntilAsync(async () => (await fixture.View.Streams.PendingSummaryAsync("events", "workers")).Count == 0);
        await fixture.StopAsync();
        await Assert.That(fixture.State.ScopeIds.Distinct().Count()).IsEqualTo(5);
        await Assert.That(fixture.State.DisposedScopes).IsEqualTo(5);
        await Assert.That(await fixture.Client.PingAsync()).IsGreaterThanOrEqualTo(TimeSpan.Zero);
        if (prefix)
            await Assert.That(await fixture.Client.Streams.CountAsync("events")).IsEqualTo(0);
    }

    [Test]
    [Arguments("nack")]
    [Arguments("throw")]
    [Arguments("cancel")]
    [Arguments("invalid")]
    [Arguments("deserialize")]
    public async Task UnsuccessfulDeliveryStaysPendingWhileNextMessageSucceeds(string failure)
    {
        var state = new State();
        state.Handle = (entry, _) => entry.GetString("payload") == "0"
            ? failure switch
            {
                "throw" => throw new InvalidOperationException("private payload"),
                "cancel" => throw new OperationCanceledException(),
                "invalid" => ValueTask.FromResult((RespireStreamWorkerResult)42),
                _ => ValueTask.FromResult(RespireStreamWorkerResult.Nack),
            }
            : ValueTask.FromResult(RespireStreamWorkerResult.Ack);
        await using var fixture = await Fixture.CreateAsync(state: state,
            deserializeFailure: failure == "deserialize");
        await fixture.AddAsync(0);
        await fixture.AddAsync(1);
        await fixture.StartAsync();
        if (failure != "deserialize") await state.Deliveries.Reader.ReadAsync().AsTask().WaitAsync(Deadline);
        var success = await state.Deliveries.Reader.ReadAsync().AsTask().WaitAsync(Deadline);
        await Assert.That(success.GetString("payload")).IsEqualTo("1");
        await UntilAsync(async () => (await fixture.View.Streams.PendingSummaryAsync("events", "workers")).Count == 1);
        await fixture.StopAsync();
        var pending = await fixture.View.Streams.PendingAsync("events", "workers");
        await Assert.That(state.WarningCount).IsEqualTo(failure == "nack" ? 0 : 1);
        foreach (var warning in state.Warnings)
        {
            var exceptionType = failure switch
            {
                "cancel" => typeof(OperationCanceledException).FullName,
                "deserialize" => typeof(FormatException).FullName,
                _ => typeof(InvalidOperationException).FullName,
            };
            await Assert.That(warning.Fields.SingleOrDefault(pair => pair.Key == "ExceptionType").Value)
                .IsEqualTo(exceptionType);
            await Assert.That(warning.Message.Contains("private payload", StringComparison.Ordinal)).IsFalse();
            await Assert.That(warning.Exception).IsNull();
        }
        await Assert.That(pending.Length).IsEqualTo(1);
        await Assert.That(pending[0].DeliveryCount).IsEqualTo(1);
        await Assert.That(state.ScopeIds.Count).IsEqualTo(failure == "deserialize" ? 1 : 2);
        await Assert.That(state.DisposedScopes).IsEqualTo(2);
    }

    [Test]
    [Arguments(1, 1)]
    [Arguments(3, 4)]
    public async Task ConcurrencyAndPrefetchAreBounded(int consumers, int batch)
    {
        var release = NewSignal();
        var state = new State { Handle = async (_, token) =>
        {
            await release.Task.WaitAsync(token);
            return RespireStreamWorkerResult.Ack;
        } };
        await using var fixture = await Fixture.CreateAsync(state: state, options: new()
        {
            ConsumerCount = consumers, BatchSize = batch,
        });
        for (var i = 0; i < consumers * batch + 7; i++) await fixture.AddAsync(i);
        try
        {
            await fixture.StartAsync();
            for (var i = 0; i < consumers; i++) await state.Deliveries.Reader.ReadAsync().AsTask().WaitAsync(Deadline);
            await Assert.That(state.Active).IsEqualTo(consumers);
            await Assert.That(state.Peak).IsEqualTo(consumers);
            var pending = await fixture.View.Streams.PendingSummaryAsync("events", "workers");
            await Assert.That(pending.Count).IsEqualTo(consumers * batch);
            using var stop = new CancellationTokenSource(Deadline);
            var stopping = fixture.Service.StopAsync(stop.Token);
            release.TrySetResult();
            await stopping.WaitAsync(Deadline);
            await Assert.That(state.ScopeIds.Count).IsEqualTo(consumers);
            await Assert.That(state.Active).IsEqualTo(0);
            await Assert.That(state.DisposedScopes).IsEqualTo(consumers);
            await Assert.That((await fixture.View.Streams.PendingSummaryAsync("events", "workers")).Count)
                .IsEqualTo(consumers * (batch - 1));
        }
        finally { release.TrySetResult(); }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ShutdownStopsReadersBeforeHandlerCancellationCallbacks(bool dispose)
    {
        // The cancellation callback waits for the worker drain before returning.
        // Readers must already be cancelled or this callback blocks shutdown.
        var completion = new TaskCompletionSource<RespireStreamWorkerResult>();
        var waiting = NewSignal();
        Fixture? fixture = null;
        // Dispose this registration after shutdown. Disposing inside the handler
        // would join the callback that is waiting for that same handler to finish.
        CancellationTokenRegistration registration = default;
        var state = new State { Handle = (entry, token) =>
        {
            if (entry.GetString("payload") != "0") return ValueTask.FromResult(RespireStreamWorkerResult.Nack);
            registration = token.Register(() =>
            {
                completion.TrySetResult(RespireStreamWorkerResult.Nack);
                fixture!.Service.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            });
            waiting.TrySetResult();
            return new(completion.Task);
        } };
        fixture = await Fixture.CreateAsync(state: state, options: new() { BatchSize = 2 });
        await using var owned = fixture;
        await fixture.AddAsync(0);
        await fixture.AddAsync(1);
        await fixture.StartAsync();
        await waiting.Task.WaitAsync(Deadline);
        try
        {
            if (dispose) fixture.Service.Dispose();
            else await fixture.Service.StopAsync(new CancellationToken(canceled: true)).WaitAsync(Deadline);
            await fixture.Service.ExecuteTask!.WaitAsync(Deadline);
            await Assert.That(state.ScopeIds.Count).IsEqualTo(1);
            await Assert.That(state.DisposedScopes).IsEqualTo(1);
            await Assert.That(state.WarningCount).IsEqualTo(0);
            await Assert.That((await fixture.View.Streams.PendingSummaryAsync("events", "workers")).Count).IsEqualTo(2);
        }
        finally
        {
            completion.TrySetResult(RespireStreamWorkerResult.Nack);
            registration.Dispose();
        }
    }

    [Test]
    public async Task GracefulStopKeepsHandlerTokenLiveUntilCompletion()
    {
        var release = NewSignal();
        CancellationToken handlerToken = default;
        var state = new State { Handle = async (_, token) =>
        {
            handlerToken = token;
            await release.Task;
            return RespireStreamWorkerResult.Ack;
        } };
        await using var fixture = await Fixture.CreateAsync(state: state);
        await fixture.AddAsync(0);
        await fixture.StartAsync();
        await state.Deliveries.Reader.ReadAsync().AsTask().WaitAsync(Deadline);
        try
        {
            using var stop = new CancellationTokenSource(Deadline);
            var stopping = fixture.Service.StopAsync(stop.Token);
            await Assert.That(stopping.IsCompleted).IsFalse();
            await Assert.That(handlerToken.IsCancellationRequested).IsFalse();
            await Assert.That(state.DisposedScopes).IsEqualTo(0);
            await fixture.AddAsync(1);
            release.TrySetResult();
            await stopping.WaitAsync(Deadline);
            await Assert.That(state.ScopeIds.Count).IsEqualTo(1);
            await Assert.That(state.DisposedScopes).IsEqualTo(1);
            await Assert.That((await fixture.View.Streams.PendingSummaryAsync("events", "workers")).Count).IsEqualTo(0);
            var next = await fixture.View.Streams.ReadGroupOnceAsync("events", "workers", "control");
            await Assert.That(next.Length).IsEqualTo(1);
            await Assert.That(next[0].GetString("payload")).IsEqualTo("1");
        }
        finally { release.TrySetResult(); }
    }

    [Test]
    public async Task ExpiredStopCancelsHandlerButKeepsScopeUntilIgnoredCancellationCompletes()
    {
        var release = NewSignal();
        var canceled = NewSignal();
        var state = new State { Handle = async (_, token) =>
        {
            using var registration = token.Register(() => canceled.TrySetResult());
            await release.Task; // Deliberately ignores cancellation to prove scope lifetime.
            return RespireStreamWorkerResult.Ack;
        } };
        await using var fixture = await Fixture.CreateAsync(state: state);
        await fixture.AddAsync(0);
        await fixture.StartAsync();
        await state.Deliveries.Reader.ReadAsync().AsTask().WaitAsync(Deadline);
        try
        {
            using var stop = new CancellationTokenSource();
            var stopping = fixture.Service.StopAsync(stop.Token);
            stop.Cancel();
            await stopping.WaitAsync(Deadline);
            await canceled.Task.WaitAsync(Deadline);
            fixture.Service.Dispose();
            await Assert.That(state.DisposedScopes).IsEqualTo(0);
            release.TrySetResult();
            await fixture.Service.ExecuteTask!.WaitAsync(Deadline);
            await Assert.That(state.DisposedScopes).IsEqualTo(1);
            await Assert.That((await fixture.View.Streams.PendingSummaryAsync("events", "workers")).Count).IsEqualTo(1);
        }
        finally { release.TrySetResult(); }
    }

    [Test]
    public async Task TwoRegistrationsHaveDistinctConsumerNamesAndExistingGroupIsRetained()
    {
        var release = NewSignal();
        var state = new State { Handle = async (_, token) =>
        {
            await release.Task.WaitAsync(token);
            return RespireStreamWorkerResult.Ack;
        } };
        await using var fixture = await Fixture.CreateAsync(state: state, registrations: 2);
        await fixture.View.Streams.CreateGroupAsync("events", "workers", RespireStreamId.Beginning);
        await fixture.AddAsync(0);
        await fixture.AddAsync(1);
        try
        {
            await fixture.StartAsync();
            await state.Deliveries.Reader.ReadAsync().AsTask().WaitAsync(Deadline);
            await state.Deliveries.Reader.ReadAsync().AsTask().WaitAsync(Deadline);
            var pending = await fixture.View.Streams.PendingAsync("events", "workers");
            await Assert.That(pending.Select(entry => entry.Consumer).Distinct().Count()).IsEqualTo(2);
            await Assert.That(fixture.Services.Length).IsEqualTo(2);
        }
        finally { release.TrySetResult(); }
    }

    [Test]
    public async Task TypedSerializerFeedsScopedHandler()
    {
        await using var fixture = await Fixture.CreateAsync(typed: true);
        await fixture.AddAsync(7);
        await fixture.StartAsync();
        await Assert.That(await fixture.State.Typed.Task.WaitAsync(Deadline)).IsEqualTo(7);
        await UntilAsync(async () => (await fixture.View.Streams.PendingSummaryAsync("events", "workers")).Count == 0);
        await fixture.StopAsync();
        await Assert.That(fixture.State.DisposedScopes).IsEqualTo(1);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task StartupOrReadFailureFaultsBackgroundTask(bool skipCreate)
    {
        await using var fixture = await Fixture.CreateAsync(options: new() { CreateGroup = !skipCreate });
        if (!skipCreate) await fixture.View.SetAsync("events", "wrong type");
        await fixture.StartAsync();
        await Assert.That(async () => await fixture.Service.ExecuteTask!.WaitAsync(Deadline))
            .Throws<RespireServerException>();
        await Assert.That(fixture.State.ScopeIds.Count).IsEqualTo(0);
    }

    [Test]
    public async Task AckFailureFaultsWorkerAndRetainsDelivery()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var fault = fixture.Server.InjectFault("XACK", RespireFakeFault.Loading());
        await fixture.AddAsync(0);
        await fixture.StartAsync();
        await Assert.That(async () => await fixture.Service.ExecuteTask!.WaitAsync(Deadline))
            .Throws<RespireServerException>();
        await Assert.That((await fixture.View.Streams.PendingSummaryAsync("events", "workers")).Count).IsEqualTo(1);
        await Assert.That(fixture.State.DisposedScopes).IsEqualTo(1);
    }

    [Test]
    public async Task FirstConsumerFailureReachesTheHostWhileAnotherHandlerRemainsParked()
    {
        var release = NewSignal();
        var canceled = NewSignal();
        var state = new State { Handle = async (entry, token) =>
        {
            if (entry.GetString("payload") == "0")
            {
                using var registration = token.Register(() => canceled.TrySetResult());
                await release.Task; // Ignore cancellation so the host and scope lifetimes differ.
            }
            return RespireStreamWorkerResult.Ack;
        } };
        await using var fixture = await Fixture.CreateAsync(state: state, options: new() { ConsumerCount = 2 });
        using var fault = fixture.Server.InjectFault("XACK", RespireFakeFault.Loading());
        await fixture.AddAsync(0);
        await fixture.AddAsync(1);
        try
        {
            await fixture.StartAsync();
            await Assert.That(async () => await fixture.Service.ExecuteTask!.WaitAsync(Deadline))
                .Throws<RespireServerException>();
            await canceled.Task.WaitAsync(Deadline);
            await Assert.That(state.Active).IsEqualTo(1);
            await Assert.That(state.DisposedScopes).IsEqualTo(1);
            fixture.Service.Dispose();
            await Assert.That(state.DisposedScopes).IsEqualTo(1);
            release.TrySetResult();
            await UntilAsync(() => Task.FromResult(state.DisposedScopes == 2));
            await Assert.That((await fixture.View.Streams.PendingSummaryAsync("events", "workers")).Count).IsEqualTo(2);
        }
        finally { release.TrySetResult(); }
    }

    [Test]
    public async Task ExpectedShutdownCancellationDoesNotLogAHandlerFailure()
    {
        var canceled = NewSignal();
        var state = new State { Handle = async (_, token) =>
        {
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            finally { canceled.TrySetResult(); }
            return RespireStreamWorkerResult.Ack;
        } };
        await using var fixture = await Fixture.CreateAsync(state: state);
        await fixture.AddAsync(0);
        await fixture.StartAsync();
        await state.Deliveries.Reader.ReadAsync().AsTask().WaitAsync(Deadline);
        using var stop = new CancellationTokenSource();
        var stopping = fixture.Service.StopAsync(stop.Token);
        stop.Cancel();
        await stopping.WaitAsync(Deadline);
        await canceled.Task.WaitAsync(Deadline);
        await fixture.Service.ExecuteTask!.WaitAsync(Deadline);
        await Assert.That(state.WarningCount).IsEqualTo(0);
        await Assert.That(state.DisposedScopes).IsEqualTo(1);
        await Assert.That((await fixture.View.Streams.PendingSummaryAsync("events", "workers")).Count).IsEqualTo(1);
    }

    [Test]
    public async Task NewGroupPositionCanSkipExistingEntries()
    {
        await using var fixture = await Fixture.CreateAsync(options: new() { GroupStart = RespireStreamId.New });
        await fixture.AddAsync(0);
        await fixture.StartAsync();
        await UntilAsync(async () => (await fixture.View.Streams.GroupInfoAsync("events")).Length == 1);
        await fixture.AddAsync(1);
        var entry = await fixture.State.Deliveries.Reader.ReadAsync().AsTask().WaitAsync(Deadline);
        await Assert.That(entry.GetString("payload")).IsEqualTo("1");
    }

    [Test]
    [Arguments(2, false)]
    [Arguments(2, true)]
    [Arguments(3, false)]
    [Arguments(3, true)]
    public async Task StableConsumerReplaysItsOwnPendingOnceBeforeNewEntries(int protocol, bool nack)
    {
        var state = new State { Handle = (entry, _) => ValueTask.FromResult(
            nack && entry.GetString("payload") == "0" ? RespireStreamWorkerResult.Nack : RespireStreamWorkerResult.Ack) };
        await using var fixture = await Fixture.CreateAsync(protocol, state: state, options: new()
        {
            ConsumerName = "stable", BatchSize = 1, ReadWait = TimeSpan.FromMilliseconds(50),
        });
        for (var i = 0; i < 3; i++) await fixture.AddAsync(i);
        await fixture.View.Streams.CreateGroupAsync("events", "workers", RespireStreamId.Beginning);
        await fixture.View.Streams.ReadGroupOnceAsync("events", "workers", "stable-0", new() { Count = 1 });
        await fixture.View.Streams.ReadGroupOnceAsync("events", "workers", "other-0", new() { Count = 1 });
        await fixture.StartAsync();
        var replay = await state.Deliveries.Reader.ReadAsync().AsTask().WaitAsync(Deadline);
        var fresh = await state.Deliveries.Reader.ReadAsync().AsTask().WaitAsync(Deadline);
        await Assert.That(replay.GetString("payload")).IsEqualTo("0");
        await Assert.That(fresh.GetString("payload")).IsEqualTo("2");
        await UntilAsync(async () => (await fixture.View.Streams.PendingSummaryAsync("events", "workers")).Count == (nack ? 2 : 1));
        await Task.Delay(100); // Two new-entry blocking intervals must not redeliver the Nack.
        await Assert.That(state.Deliveries.Reader.TryRead(out _)).IsFalse();
        var pending = await fixture.View.Streams.PendingAsync("events", "workers");
        await Assert.That(pending.Single(entry => entry.Consumer == "other-0").DeliveryCount).IsEqualTo(1);
        if (nack) await Assert.That(pending.Single(entry => entry.Consumer == "stable-0").DeliveryCount).IsEqualTo(2);
    }

    [Test]
    [Arguments("")]
    [Arguments(" ")]
    public async Task EmptyStableConsumerNamesFailDuringRegistration(string name)
        => await Assert.That(() => new ServiceCollection().AddRespireStreamWorker<EntryHandler>("events", "workers",
            new() { ConsumerName = name })).Throws<ArgumentException>();

    [Test]
    [Arguments(0, 1, 1)]
    [Arguments(-1, 1, 1)]
    [Arguments(1, 0, 1)]
    [Arguments(1, -1, 1)]
    [Arguments(1, 1, 0)]
    [Arguments(1, 1, -1)]
    [Arguments(1, 1, long.MaxValue)]
    public async Task InvalidOptionsFailDuringRegistration(int consumers, int batch, long waitTicks)
    {
        await Assert.That(() => new ServiceCollection().AddRespireStreamWorker<EntryHandler>("events", "workers",
            new() { ConsumerCount = consumers, BatchSize = batch, ReadWait = TimeSpan.FromTicks(waitTicks) }))
            .Throws<ArgumentOutOfRangeException>();
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task UntilAsync(Func<Task<bool>> condition)
    {
        using var deadline = new CancellationTokenSource(Deadline);
        while (!await condition()) await Task.Delay(10, deadline.Token);
    }

    public sealed class State
    {
        public Func<RespireStreamEntry, CancellationToken, ValueTask<RespireStreamWorkerResult>> Handle { get; set; }
            = static (_, _) => ValueTask.FromResult(RespireStreamWorkerResult.Ack);
        public Channel<RespireStreamEntry> Deliveries { get; } = Channel.CreateUnbounded<RespireStreamEntry>();
        public ConcurrentBag<Guid> ScopeIds { get; } = [];
        public TaskCompletionSource<int> Typed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Active;
        public int Peak;
        public int DisposedScopes;
        public int WarningCount;
        public ConcurrentBag<(string Message, Exception? Exception, KeyValuePair<string, object?>[] Fields)> Warnings { get; } = [];
    }

    public sealed class ScopeProbe(State state) : IAsyncDisposable
    {
        public Guid Id { get; } = Guid.NewGuid();
        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref state.DisposedScopes);
            return ValueTask.CompletedTask;
        }
    }

    public sealed class EntryHandler(State state, ScopeProbe scope) : IRespireStreamHandler<RespireStreamEntry>
    {
        public async ValueTask<RespireStreamWorkerResult> HandleAsync(RespireStreamEntry message, CancellationToken token)
        {
            state.ScopeIds.Add(scope.Id);
            var active = Interlocked.Increment(ref state.Active);
            int peak;
            do { peak = Volatile.Read(ref state.Peak); }
            while (peak < active && Interlocked.CompareExchange(ref state.Peak, active, peak) != peak);
            state.Deliveries.Writer.TryWrite(message);
            try { return await state.Handle(message, token); }
            finally { Interlocked.Decrement(ref state.Active); }
        }
    }

    public sealed class TypedHandler(State state, ScopeProbe scope) : IRespireStreamHandler<int>
    {
        public ValueTask<RespireStreamWorkerResult> HandleAsync(int message, CancellationToken token)
        {
            state.ScopeIds.Add(scope.Id);
            state.Typed.TrySetResult(message);
            return ValueTask.FromResult(RespireStreamWorkerResult.Ack);
        }
    }

    private sealed class RecordingLoggerProvider(State state) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new RecordingLogger(state);
        public void Dispose() { }
        private sealed class RecordingLogger(State state) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState value) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState value, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (logLevel == LogLevel.Warning)
                {
                    state.Warnings.Add((formatter(value, exception), exception,
                        ((IEnumerable<KeyValuePair<string, object?>>)value!).ToArray()));
                    Interlocked.Increment(ref state.WarningCount);
                }
            }
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public required RespireFakeServer Server { get; init; }
        public required RespireClient Client { get; init; }
        public required IRespireClient View { get; init; }
        public required ServiceProvider Provider { get; init; }
        public required State State { get; init; }
        public required BackgroundService[] Services { get; init; }
        public BackgroundService Service => Services[0];

        public static async Task<Fixture> CreateAsync(int protocol = 3, bool prefix = false, State? state = null,
            RespireStreamWorkerOptions? options = null, int registrations = 1, bool typed = false,
            bool deserializeFailure = false)
        {
            var server = new RespireFakeServer();
            var client = await RespireClient.ConnectAsync(server.CreateOptions() with { Protocol = (RespProtocol)protocol });
            var view = prefix ? client.WithKeyPrefix("tenant:") : client;
            state ??= new State();
            var collection = new ServiceCollection().AddSingleton(state).AddScoped<ScopeProbe>()
                .AddSingleton<IRespireClient>(view);
            collection.AddLogging(logging => logging.AddProvider(new RecordingLoggerProvider(state)));
            for (var i = 0; i < registrations; i++)
            {
                if (typed)
                    collection.AddRespireStreamWorker<TypedHandler, int>("events", "workers",
                        entry => int.Parse(entry.GetString("payload")!), options);
                else if (deserializeFailure)
                    collection.AddRespireStreamWorker<EntryHandler, RespireStreamEntry>("events", "workers",
                        entry => entry.GetString("payload") == "0" ? throw new FormatException("private payload") : entry, options);
                else collection.AddRespireStreamWorker<EntryHandler>("events", "workers", options);
            }
            var provider = collection.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
            return new Fixture
            {
                Server = server, Client = client, View = view, Provider = provider, State = state,
                Services = provider.GetServices<IHostedService>().Cast<BackgroundService>().ToArray(),
            };
        }

        public async Task AddAsync(int value) => await View.Streams.AddAsync("events", ("payload", value.ToString()));
        public async Task StartAsync()
        {
            foreach (var service in Services) await service.StartAsync(CancellationToken.None);
        }
        public async Task StopAsync()
        {
            using var deadline = new CancellationTokenSource(Deadline);
            foreach (var service in Services) await service.StopAsync(deadline.Token);
        }
        public async ValueTask DisposeAsync()
        {
            await StopAsync();
            await Provider.DisposeAsync();
            await Client.DisposeAsync();
            await Server.DisposeAsync();
        }
    }
}
