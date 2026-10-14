# Respire.Streaming

Hosted Redis stream consumers with scoped handlers, bounded concurrency, recovery, and explicit acknowledgements.

## Install

```sh
dotnet add package Respire.Streaming
dotnet add package Respire.DependencyInjection
```

## Usage

<!-- doc-test-tail-declaration: split-before=public sealed class OrderHandler -->
```csharp
using Microsoft.Extensions.Hosting;
using Respire;
using Respire.DependencyInjection;
using Respire.Streaming;

var builder = Host.CreateApplicationBuilder();
builder.Services.AddRespire("redis://localhost:6379");
builder.Services.AddRespireStreamWorker<OrderHandler>("orders", "fulfillment", new()
{
    ConsumerCount = 4,
    BatchSize = 8,
});
await builder.Build().RunAsync();

public sealed class OrderHandler : IRespireStreamHandler<RespireStreamEntry>
{
    public ValueTask<RespireStreamWorkerResult> HandleAsync(
        RespireStreamEntry entry, CancellationToken cancellationToken)
    {
        Console.WriteLine(entry.GetString("order-id"));
        return ValueTask.FromResult(RespireStreamWorkerResult.Ack);
    }
}
```

Handlers must be idempotent: recovery and lost acknowledgements can deliver an entry more than once. Return Ack only after completing the application operation.

[Read the guide](https://thomhurst.github.io/Respire/docs/guides/stream-workers) for configuration, requirements, and further examples.
