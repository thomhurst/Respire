# Respire - High-Performance RESP Protocol Client for .NET

Respire is a lightning-fast, zero-allocation C# .NET client for interacting with key-value datastores that use the RESP protocol (Redis, Valkey, KeyDB, etc.).

## Features

- **Zero Allocations**: Designed from the ground up to minimize heap allocations
- **High Performance**: Uses `Span<T>`, `Memory<T>`, and `System.IO.Pipelines` for maximum throughput
- **Pipeline Architecture**: Flexible interceptor pipeline for cross-cutting concerns
- **Resilient Connections**: Automatic reconnection, health monitoring, and connection pooling
- **RESP2 & RESP3**: Full support for both protocol versions
- **Modular Design**: Core library with minimal dependencies, optional packages for specific features

## Architecture

### Core Components

- **Respire**: Core protocol implementation with zero dependencies beyond .NET BCL
  - RESP protocol reader/writer with zero allocations
  - Pipeline architecture for interceptors
  - Connection abstractions

- **Respire.Client**: High-level client with dependency injection support

### Optional Packages

- **Respire.Compression**: GZip, Brotli compression support
- **Respire.Serialization.Json**: JSON serialization with System.Text.Json
- **Respire.Serialization.MessagePack**: MessagePack serialization
- **Respire.Resilience**: Circuit breaker, retry policies with Polly
- **Respire.Telemetry**: Logging, metrics, distributed tracing
- **Respire.Caching**: Local caching layer

## Usage

### Basic Usage

```csharp
var client = new RespireClient("localhost:6379");
await client.SetAsync("key", "value");
var value = await client.GetAsync("key");
```

### With Dependency Injection

```csharp
services.AddRespire(builder => builder
    .ConfigureConnection(options => {
        options.Endpoints = new[] { "localhost:6379" };
        options.EnableAutoReconnect = true;
        options.EnableHealthChecks = true;
    })
    .UseCompression()
    .UseJson()
    .UseRetry(maxAttempts: 3)
    .UseCircuitBreaker()
);
```

### Custom Interceptor

```csharp
public class LoggingInterceptor : DelegatingInterceptor
{
    protected override async ValueTask<RespValue> OnRequestAsync(
        RespireInterceptorContext context,
        CancellationToken cancellationToken)
    {
        Console.WriteLine($"Executing: {context.CommandName}");
        return default;
    }
    
    protected override async ValueTask<RespValue> OnResponseAsync(
        RespireInterceptorContext context,
        RespValue response,
        CancellationToken cancellationToken)
    {
        Console.WriteLine($"Response: {response.Type}");
        return response;
    }
}

// Register the interceptor
services.AddRespire(builder => builder
    .AddInterceptor<LoggingInterceptor>()
);
```

## Performance

Zero-allocation design principles:
- Stack-allocated buffers for small operations
- ArrayPool/MemoryPool for larger buffers
- Struct-based value types
- ValueTask for hot paths
- Span/Memory APIs throughout
- Pipeline-based I/O with System.IO.Pipelines

## Building

```bash
dotnet build
dotnet test
dotnet run --project benchmarks/Respire.Benchmarks
```

## License

MIT