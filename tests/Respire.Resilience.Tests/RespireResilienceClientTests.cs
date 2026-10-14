using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using Respire.Resilience;
using Respire.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

public class RespireResilienceClientTests
{
    private static PredicateBuilder<object> TransientServerErrors()
        => new PredicateBuilder().Handle<RespireServerException>(
            error => error.Code is "LOADING" or "TRYAGAIN" or "MASTERDOWN");

    private static RetryStrategyOptions RetryOptions() => new()
    {
        MaxRetryAttempts = 2,
        Delay = TimeSpan.Zero,
        ShouldHandle = TransientServerErrors(),
    };

    private static RespireOptions Options(RespireFakeServer server) => server.CreateOptions() with
    {
        Connections = 1,
        RetryPolicy = new() { MaxAttempts = 5, InitialBackoff = TimeSpan.Zero },
    };

    [Test]
    public async Task ExhaustionSendsExactlyThePipelineAttemptCount()
    {
        await using var server = new RespireFakeServer();
        using var fault = server.InjectFault("GET", RespireFakeFault.Loading(), occurrences: null);
        var options = Options(server);
        var retries = 0;
        var retryOptions = RetryOptions();
        retryOptions.OnRetry = _ => { retries++; return default; };
        var pipeline = new ResiliencePipelineBuilder().AddRetry(retryOptions).Build();
        await using var client = new RespireResilienceClient(options, pipeline);
        var callbacks = 0;

        await Assert.That(async () => await client.ExecuteAsync((redis, token) =>
        {
            callbacks++;
            return redis.GetStringAsync("key", token);
        })).Throws<RespireServerException>();

        await Assert.That(callbacks).IsEqualTo(3);
        await Assert.That(fault.MatchedCount).IsEqualTo(3L);
        await Assert.That(retries).IsEqualTo(2);
        await Assert.That(options.RetryPolicy!.MaxAttempts).IsEqualTo(5);
    }

    [Test]
    public async Task EmptyPipelineDoesNotRetryDespiteSuppliedCommandPolicy()
    {
        await using var server = new RespireFakeServer();
        using var fault = server.InjectFault("GET", RespireFakeFault.Loading(), occurrences: null);
        await using var client = new RespireResilienceClient(Options(server), ResiliencePipeline.Empty);
        await Assert.That(async () => await client.ExecuteAsync(
            (redis, token) => redis.GetStringAsync("key", token))).Throws<RespireServerException>();
        await Assert.That(fault.MatchedCount).IsEqualTo(1L);
    }

    [Test]
    public async Task SuccessReturnsResultFromFinalAttempt()
    {
        await using var server = new RespireFakeServer();
        var pipeline = new ResiliencePipelineBuilder().AddRetry(RetryOptions()).Build();
        await using var client = new RespireResilienceClient(Options(server), pipeline);
        await client.ExecuteAsync((redis, token) => redis.SetAsync("key", "value", cancellationToken: token));
        using var fault = server.InjectFault("GET", RespireFakeFault.Loading(), occurrences: 2);
        var callbacks = 0;
        var result = await client.ExecuteAsync((redis, token) =>
        {
            callbacks++;
            return redis.GetStringAsync("key", token);
        });
        await Assert.That(result).IsEqualTo("value");
        await Assert.That(callbacks).IsEqualTo(3);
        await Assert.That(fault.MatchedCount).IsEqualTo(2L);
    }

    [Test]
    public async Task VoidCallbackUsesPipelineAndForwardsCancellation()
    {
        await using var server = new RespireFakeServer();
        using var cancellation = new CancellationTokenSource();
        using var fault = server.InjectFault("GET", RespireFakeFault.Loading(), occurrences: null);
        var retryOptions = RetryOptions();
        retryOptions.OnRetry = _ => { cancellation.Cancel(); return default; };
        var pipeline = new ResiliencePipelineBuilder().AddRetry(retryOptions).Build();
        await using var client = new RespireResilienceClient(Options(server), pipeline);
        var callbacks = 0;
        CancellationToken observedToken = default;
        Func<RespireClient, CancellationToken, ValueTask> callback = async (redis, token) =>
        {
            callbacks++;
            observedToken = token;
            await redis.GetStringAsync("key", token);
        };
        await Assert.That(async () => await client.ExecuteAsync(callback, cancellation.Token))
            .Throws<OperationCanceledException>();
        await Assert.That(observedToken).IsEqualTo(cancellation.Token);
        await Assert.That(callbacks).IsEqualTo(1);
        await Assert.That(fault.MatchedCount).IsEqualTo(1L);
    }

    [Test]
    public async Task PipelineCircuitOpensAndStopsRetriesBeforeAnotherSend()
    {
        await using var server = new RespireFakeServer();
        using var fault = server.InjectFault("GET", RespireFakeFault.Loading(), occurrences: null);
        var state = new CircuitBreakerStateProvider();
        var pipeline = new ResiliencePipelineBuilder()
            .AddRetry(RetryOptions())
            .AddCircuitBreaker(new()
            {
                MinimumThroughput = 2,
                FailureRatio = 1,
                BreakDuration = TimeSpan.FromMinutes(1),
                ShouldHandle = TransientServerErrors(),
                StateProvider = state,
            }).Build();
        await using var client = new RespireResilienceClient(Options(server), pipeline);
        await Assert.That(async () => await client.ExecuteAsync(
            (redis, token) => redis.GetStringAsync("key", token))).Throws<BrokenCircuitException>();
        await Assert.That(state.CircuitState).IsEqualTo(CircuitState.Open);
        await Assert.That(fault.MatchedCount).IsEqualTo(2L);
    }

    [Test]
    public async Task ConfiguredEndpointCircuitRemainsEnabled()
    {
        await using var server = new RespireFakeServer();
        var options = Options(server) with
        {
            CircuitBreaker = new() { MinimumFailureCount = 1, OpenDuration = TimeSpan.FromMinutes(1) },
        };
        await using var client = new RespireResilienceClient(options, ResiliencePipeline.Empty);
        await client.ExecuteAsync((redis, token) => redis.GetStringAsync("key", token));
        using var fault = server.InjectFault("GET", RespireFakeFault.Disconnect(), occurrences: null);
        await Assert.That(async () => await client.ExecuteAsync(
            (redis, token) => redis.GetStringAsync("key", token))).Throws<RespireConnectionException>();
        await Assert.That(async () => await client.ExecuteAsync(
            (redis, token) => redis.GetStringAsync("key", token))).Throws<RespireCircuitOpenException>();
        await Assert.That(fault.MatchedCount).IsEqualTo(1L);
    }

    [Test]
    public async Task PermanentErrorIsNotRetried()
    {
        await using var server = new RespireFakeServer();
        var pipeline = new ResiliencePipelineBuilder().AddRetry(RetryOptions()).Build();
        await using var client = new RespireResilienceClient(Options(server), pipeline);
        var callbacks = 0;
        await Assert.That(async () => await client.ExecuteAsync<string>((_, _) =>
        {
            callbacks++;
            throw new RespireServerException("WRONGTYPE invalid value");
        })).Throws<RespireServerException>();
        await Assert.That(callbacks).IsEqualTo(1);
    }

    [Test]
    public async Task DisposalClosesOwnedClientAndRejectsCallbacks()
    {
        await using var server = new RespireFakeServer();
        await using var client = new RespireResilienceClient(Options(server), ResiliencePipeline.Empty);
        RespireClient? observedClient = null;
        await client.ExecuteAsync((redis, token) =>
        {
            observedClient = redis;
            return redis.GetStringAsync("key", token);
        });
        await Assert.That(observedClient!.IsConnected).IsTrue();
        await client.DisposeAsync();
        await Assert.That(observedClient.IsConnected).IsFalse();
        await Assert.That(async () => await client.ExecuteAsync(
            (_, _) => new ValueTask<int>(42))).Throws<ObjectDisposedException>();
        await client.DisposeAsync();
    }
}
