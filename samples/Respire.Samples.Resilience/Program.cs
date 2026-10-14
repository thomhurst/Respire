using Microsoft.Extensions.DependencyInjection;
using Polly;
using Polly.CircuitBreaker;
using Polly.Registry;
using Respire;
using Respire.Resilience;
using Respire.Testing;

// An in-process RESP server makes the attempt count reproducible; no Docker is needed.
await using var server = new RespireFakeServer();
using var fault = server.InjectFault("GET", RespireFakeFault.Loading(), occurrences: 2);
var services = new ServiceCollection();
services.AddLogging();
services.AddResilienceEnricher();
var breakerState = new CircuitBreakerStateProvider();
services.AddResiliencePipeline("redis-read", builder => builder
    // This bounds the whole operation, including every retry and backoff.
    .AddTimeout(TimeSpan.FromSeconds(10))
    .AddRetry(new()
    {
        MaxRetryAttempts = 2, // Two retries plus the initial attempt.
        Delay = TimeSpan.FromMilliseconds(10),
        ShouldHandle = new PredicateBuilder().Handle<RespireServerException>(
            error => error.Code is "LOADING" or "TRYAGAIN" or "MASTERDOWN"),
        OnRetry = args =>
        {
            Console.WriteLine($"Polly retry {args.AttemptNumber + 1}");
            return default;
        },
    })
    .AddCircuitBreaker(new()
    {
        MinimumThroughput = 5,
        FailureRatio = 0.5,
        StateProvider = breakerState,
        ShouldHandle = new PredicateBuilder().Handle<RespireServerException>(
            error => error.Code is "LOADING" or "TRYAGAIN" or "MASTERDOWN"),
    }));
services.AddSingleton(provider => new RespireResilienceClient(
    server.CreateOptions() with
    {
        // Even if shared configuration enables Respire retries, the adapter turns them off.
        RetryPolicy = new() { MaxAttempts = 5 },
        CircuitBreaker = null, // This sample chooses the pipeline's operation-level breaker.
    },
    provider.GetRequiredService<ResiliencePipelineProvider<string>>().GetPipeline("redis-read")));

await using var provider = services.BuildServiceProvider();
var redis = provider.GetRequiredService<RespireResilienceClient>();
var attempts = 0;
var value = await redis.ExecuteAsync((client, token) =>
{
    attempts++;
    return client.GetStringAsync("example", token);
});

if (attempts != 3 || fault.MatchedCount != 2 || value is not null)
    throw new InvalidOperationException("Expected two rejected GETs and one successful GET through one retry layer.");
Console.WriteLine($"GET attempts: {attempts}; injected failures: {fault.MatchedCount}; circuit: {breakerState.CircuitState}");
