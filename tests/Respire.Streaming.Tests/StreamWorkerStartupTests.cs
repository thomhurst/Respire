using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
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

[ClassDataSource<RedisTestContainer>(Shared = SharedType.PerTestSession)]
public class StreamWorkerStartupTests(RedisTestContainer redis)
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task HostStaysRunningUntilLazyConnectionRecovers(bool socketError)
    {
        await using var server = new RespireFakeServer();
        var options = server.CreateOptions();
        await using var producer = await RespireClient.ConnectAsync(options);
        await producer.Streams.AddAsync("events", ("payload", "value"));
        var online = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var client = RespireClient.Create(options with
        {
            ConnectionFactory = (endpoint, token) => online.Task.IsCompleted
                ? options.ConnectionFactory!(endpoint, token)
                : ValueTask.FromException<Stream>(socketError
                    ? new SocketException((int)SocketError.ConnectionRefused) : new IOException("Redis offline")),
        });
        var state = new HandlerState();
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton(state).AddSingleton<IRespireClient>(client);
        builder.Services.AddLogging(logging => logging.ClearProviders().AddProvider(new RecordingLogger(state)));
        builder.Services.AddRespireStreamWorker<Handler>("events", "workers");
        using var host = builder.Build();
        using var deadline = new CancellationTokenSource(Deadline);
        await host.StartAsync(deadline.Token);
        try
        {
            await state.Warning.Reader.ReadAsync(deadline.Token);
            await Assert.That(host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.IsCancellationRequested).IsFalse();
            await Assert.That(state.Delivered.Task.IsCompleted).IsFalse();
            online.TrySetResult();
            await state.Delivered.Task.WaitAsync(deadline.Token);
        }
        finally { await host.StopAsync(deadline.Token); }
    }

    [Test]
    [Arguments(false, "connection")]
    [Arguments(false, "timeout")]
    [Arguments(false, "system-timeout")]
    [Arguments(true, "connection")]
    [Arguments(true, "timeout")]
    [Arguments(true, "system-timeout")]
    public async Task StartupRetriesUntilRedisAnswersThenProcessesWork(bool jobQueue, string failure)
    {
        await using var fixture = await Fixture.CreateAsync(jobQueue, redis.ConnectionString);
        fixture.Fault.Error = failure switch
        {
            "connection" => new RespireConnectionException("private endpoint"),
            "timeout" => new RespireTimeoutException("startup", TimeSpan.FromSeconds(1)),
            _ => new TimeoutException("private endpoint"),
        };
        fixture.Fault.FailuresRemaining = 2;
        await fixture.Worker.StartAsync(CancellationToken.None);
        await fixture.State.Delivered.Task.WaitAsync(Deadline);
        await Assert.That(fixture.Fault.Attempts).IsEqualTo(3);
        await Assert.That(fixture.State.Warnings.Count).IsEqualTo(2);
        var delays = fixture.State.Warnings.Select(fields => (double)fields.Single(pair => pair.Key == "RetryDelaySeconds").Value!).ToArray();
        await Assert.That(delays.SequenceEqual([1d, 2d])).IsTrue();
        await Assert.That(fixture.Worker.ExecuteTask!.IsCompleted).IsFalse();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task StopCancelsStartupBackoffWithoutStartingConsumers(bool jobQueue)
    {
        await using var fixture = await Fixture.CreateAsync(jobQueue, redis.ConnectionString);
        fixture.Fault.Error = new RespireConnectionException("private endpoint");
        fixture.Fault.FailuresRemaining = int.MaxValue;
        await fixture.Worker.StartAsync(CancellationToken.None);
        await fixture.State.Warning.Reader.ReadAsync().AsTask().WaitAsync(Deadline);
        await fixture.Worker.StopAsync(CancellationToken.None).WaitAsync(Deadline);
        await fixture.Worker.ExecuteTask!.WaitAsync(Deadline);
        await Assert.That(fixture.Fault.Attempts).IsEqualTo(1);
        await Assert.That(fixture.State.Delivered.Task.IsCompleted).IsFalse();
        await Assert.That(fixture.Worker.ExecuteTask.IsCompletedSuccessfully).IsTrue();
    }

    [Test]
    [Arguments(false, "argument")]
    [Arguments(true, "argument")]
    [Arguments(false, "authentication")]
    [Arguments(true, "authentication")]
    [Arguments(false, "server")]
    [Arguments(true, "server")]
    public async Task NonTransientStartupErrorsFailFast(bool jobQueue, string failure)
    {
        await using var fixture = await Fixture.CreateAsync(jobQueue, redis.ConnectionString);
        fixture.Fault.Error = failure switch
        {
            "argument" => new ArgumentException("invalid configuration"),
            "authentication" => new RespireAuthenticationException("invalid credentials"),
            _ => new RespireServerException("WRONGTYPE invalid key"),
        };
        fixture.Fault.FailuresRemaining = int.MaxValue;
        await fixture.Worker.StartAsync(CancellationToken.None);
        Exception? actual = null;
        try { await fixture.Worker.ExecuteTask!.WaitAsync(Deadline); }
        catch (Exception error) { actual = error; }
        await Assert.That(ReferenceEquals(actual, fixture.Fault.Error)).IsTrue();
        await Assert.That(fixture.Fault.Attempts).IsEqualTo(1);
        await Assert.That(fixture.State.Warnings.Count).IsEqualTo(0);
        await Assert.That(fixture.State.Delivered.Task.IsCompleted).IsFalse();
    }

    [Test]
    public async Task GroupRetryDoesNotRepeatSuccessfulRateLimitInitialization()
    {
        await using var fixture = await Fixture.CreateAsync(true, redis.ConnectionString);
        fixture.GroupFault.Error = new RespireConnectionException("private endpoint");
        fixture.GroupFault.FailuresRemaining = 2;
        await fixture.Worker.StartAsync(CancellationToken.None);
        await fixture.State.Delivered.Task.WaitAsync(Deadline);
        await Assert.That(fixture.Fault.Attempts).IsEqualTo(1);
        await Assert.That(fixture.GroupFault.Attempts).IsEqualTo(3);
        await Assert.That(fixture.State.Warnings.Count).IsEqualTo(2);
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(false, true)]
    [Arguments(true, true)]
    public async Task StopCancelsPendingInitialization(bool jobQueue, bool connectionErrorOnStop)
    {
        await using var fixture = await Fixture.CreateAsync(jobQueue, redis.ConnectionString);
        fixture.Fault.WaitForCancellation = true;
        fixture.Fault.ConnectionErrorOnStop = connectionErrorOnStop;
        await fixture.Worker.StartAsync(CancellationToken.None);
        await fixture.Fault.Started.Task.WaitAsync(Deadline);
        await fixture.Worker.StopAsync(CancellationToken.None).WaitAsync(Deadline);
        await fixture.Worker.ExecuteTask!.WaitAsync(Deadline);
        await Assert.That(fixture.State.Delivered.Task.IsCompleted).IsFalse();
        await Assert.That(fixture.State.Warnings.Count).IsEqualTo(0);
        await Assert.That(fixture.Fault.Attempts).IsEqualTo(1);
    }

    [Test]
    public async Task InvalidKeysFailBeforeRateLimitInitialization()
    {
        await using var fixture = await Fixture.CreateAsync(true, redis.ConnectionString, invalidKeys: true);
        await fixture.Worker.StartAsync(CancellationToken.None);
        await Assert.That(async () => await fixture.Worker.ExecuteTask!.WaitAsync(Deadline)).Throws<ArgumentException>();
        await Assert.That(fixture.Fault.Attempts).IsEqualTo(0);
        await Assert.That(fixture.GroupFault.Attempts).IsEqualTo(0);
    }

    private sealed class StartupFault
    {
        public Exception? Error;
        public int FailuresRemaining;
        public int Attempts;
        public bool WaitForCancellation;
        public bool ConnectionErrorOnStop;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public object? Invoke(object inner, MethodInfo method, object?[]? arguments)
        {
            if (method.Name == nameof(IRespireStreamCommands.CreateGroupAsync)
                || method.Name == nameof(IRespireScriptCommands.ExecuteIntegerAsync)
                && arguments?[2] is RespireValue[] { Length: 3 } values && values[2] == (RespireValue)"initialize")
            {
                Interlocked.Increment(ref Attempts);
                if (WaitForCancellation)
                    return method.ReturnType == typeof(ValueTask<bool>)
                        ? (object)new ValueTask<bool>(UntilCanceledAsync<bool>((CancellationToken)arguments![^1]!))
                        : new ValueTask<long>(UntilCanceledAsync<long>((CancellationToken)arguments![^1]!));
                if (Interlocked.Decrement(ref FailuresRemaining) >= 0) throw Error!;
            }
            return Forward(inner, method, arguments);
        }

        private async Task<T> UntilCanceledAsync<T>(CancellationToken token)
        {
            Started.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            catch (OperationCanceledException) when (ConnectionErrorOnStop)
            {
                throw new RespireConnectionException("connection closed during shutdown");
            }
            return default!;
        }
    }

    public class ForwardingProxy : DispatchProxy
    {
        public Func<MethodInfo, object?[]?, object?> Call { get; set; } = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? arguments) => Call(method!, arguments);
    }

    public interface IResolvingClient : IRespireClient, IRespireNameResolver;

    private static T Proxy<T>(Func<MethodInfo, object?[]?, object?> call) where T : class
    {
        var proxy = DispatchProxy.Create<T, ForwardingProxy>();
        ((ForwardingProxy)(object)proxy).Call = call;
        return proxy;
    }

    private static object? Forward(object inner, MethodInfo method, object?[]? arguments)
    {
        try { return method.Invoke(inner, arguments); }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(error.InnerException).Throw();
            throw;
        }
    }

    public sealed class HandlerState
    {
        public TaskCompletionSource Delivered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Channel<bool> Warning { get; } = Channel.CreateUnbounded<bool>();
        public ConcurrentQueue<KeyValuePair<string, object?>[]> Warnings { get; } = new();
    }

    public sealed class Handler(HandlerState state) : IRespireStreamHandler<RespireStreamEntry>, IRespireStreamHandler<RespireJob<string>>
    {
        public ValueTask<RespireStreamWorkerResult> HandleAsync(RespireStreamEntry message, CancellationToken cancellationToken)
            => Complete();

        public ValueTask<RespireStreamWorkerResult> HandleAsync(RespireJob<string> message, CancellationToken cancellationToken)
            => Complete();

        private ValueTask<RespireStreamWorkerResult> Complete()
        {
            state.Delivered.TrySetResult();
            return ValueTask.FromResult(RespireStreamWorkerResult.Ack);
        }
    }

    private sealed class RecordingLogger(HandlerState state) : ILoggerProvider, ILogger
    {
        public ILogger CreateLogger(string categoryName) => this;
        public IDisposable? BeginScope<TState>(TState value) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Dispose() { }
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState value, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel != LogLevel.Warning) return;
            state.Warnings.Enqueue(((IEnumerable<KeyValuePair<string, object?>>)value!).ToArray());
            state.Warning.Writer.TryWrite(true);
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public required RespireClient Client { get; init; }
        public required ServiceProvider Provider { get; init; }
        public required BackgroundService Worker { get; init; }
        public required HandlerState State { get; init; }
        public required StartupFault Fault { get; init; }
        public required StartupFault GroupFault { get; init; }

        public static async Task<Fixture> CreateAsync(bool jobQueue, string connectionString, bool invalidKeys = false)
        {
            var client = await RespireClient.ConnectAsync(connectionString);
            var groupFault = new StartupFault();
            var fault = jobQueue ? new StartupFault() : groupFault;
            var streams = Proxy<IRespireStreamCommands>((method, arguments) => groupFault.Invoke(client.Streams, method, arguments));
            var scripts = Proxy<IRespireScriptCommands>((method, arguments) =>
                jobQueue ? fault.Invoke(client.Scripts, method, arguments) : Forward(client.Scripts, method, arguments));
            var view = Proxy<IResolvingClient>((method, arguments) => method.Name switch
            {
                "get_Streams" => streams,
                "get_Scripts" => scripts,
                _ => Forward(client, method, arguments),
            });
            var state = new HandlerState();
            var services = new ServiceCollection().AddSingleton(state).AddSingleton<IRespireClient>(view);
            services.AddLogging(logging => logging.AddProvider(new RecordingLogger(state)));
            if (jobQueue)
            {
                services.AddRespireJobQueue<Handler, string>("startup", Encoding.UTF8.GetBytes, Encoding.UTF8.GetString,
                    new() { DeadLetterStream = invalidKeys ? "{different-slot}:dead" : null },
                    new() { RateLimit = new() { PermitLimit = 10, Window = TimeSpan.FromMinutes(1) } });
                await new RespireJobQueue<string>(client, "startup", Encoding.UTF8.GetBytes).EnqueueAsync("job", "payload");
            }
            else
            {
                services.AddRespireStreamWorker<Handler>("events", "workers");
                await client.Streams.AddAsync("events", ("payload", "value"));
            }
            var provider = services.BuildServiceProvider();
            return new() { Client = client, Provider = provider, State = state, Fault = fault, GroupFault = groupFault,
                Worker = (BackgroundService)provider.GetRequiredService<IHostedService>() };
        }

        public async ValueTask DisposeAsync()
        {
            using var deadline = new CancellationTokenSource(Deadline);
            await Worker.StopAsync(deadline.Token);
            await Provider.DisposeAsync();
            await Client.DisposeAsync();
        }
    }
}
