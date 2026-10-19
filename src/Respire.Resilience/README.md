# Respire.Resilience

Run replay-safe Respire callbacks through a Polly v8 `ResiliencePipeline`, including
pipelines registered with Microsoft.Extensions.Resilience. `RespireResilienceClient`
owns a lazy Respire client and disables its command retry policy, so the supplied
pipeline is the only command retry layer. It preserves configured endpoint circuits.

```csharp
using Polly;
using Respire;
using Respire.Resilience;

var pipeline = new ResiliencePipelineBuilder()
    .AddRetry(new()
    {
        MaxRetryAttempts = 2,
        ShouldHandle = new PredicateBuilder().Handle<RespireServerException>(
            error => error.Code is "LOADING" or "TRYAGAIN" or "MASTERDOWN"),
    })
    .Build();

await using var redis = new RespireResilienceClient(
    new RespireOptions { Endpoints = { new("localhost", 6379) } }, pipeline);
var value = await redis.ExecuteAsync((client, token) => client.GetStringAsync("key", token));
```

The whole callback may run again. Do not wrap accumulating writes, cursor continuations,
streams, failover retry loops, or watched-transaction retry helpers without separately
establishing replay safety. Pass the callback token to commands. The outer pipeline
does not use Respire's per-command retry categories or transport write evidence.

See the [resilience guide](https://respire.tomlonghurst.dev/docs/guides/external-resilience)
for timeout budgets, circuit breaker ownership, telemetry, and Microsoft DI integration.
