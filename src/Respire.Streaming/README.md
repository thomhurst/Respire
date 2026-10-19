# Respire.Streaming

Hosted Redis stream consumers with scoped handlers, bounded concurrency, recovery, and explicit acknowledgements.

For caller-driven message processing without hosted handlers, use
[`RespireReliableQueue<T>`](https://respire.tomlonghurst.dev/docs/guides/reliable-queues)
with visibility timeouts, ACK/NACK, delivery limits, dead letters and optional send deduplication.

For durable fanout, use
[`RespireReliableTopic<T>`](https://respire.tomlonghurst.dev/docs/guides/reliable-topics).
Each named subscription receives every retained publication, with competing consumers,
ACK/NACK, independent dead letters, retention bounds and replay by ID or timestamp.

## Install

```sh
dotnet add package Respire.Streaming
dotnet add package Respire.DependencyInjection
dotnet add package Microsoft.Extensions.Hosting
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

[Read the guide](https://respire.tomlonghurst.dev/docs/guides/stream-workers) for configuration, requirements, and further examples.
