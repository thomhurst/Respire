using Microsoft.Extensions.AI;
using Respire;
using Respire.AI;

// Run against ordinary Redis; no Search module or paid embedding provider is required.
await using var redis = await RespireClient.ConnectAsync(
    Environment.GetEnvironmentVariable("REDIS_CONNECTION_STRING") ?? "redis://localhost:6379");
var provider = new DemoEmbeddingGenerator();
using var embeddings = new EmbeddingGeneratorBuilder<string, Embedding<float>>(provider)
    .UseRespireEmbeddingCache(redis, new()
    {
        CacheName = "embedding-sample:demo-v1",
        TimeToLive = TimeSpan.FromMinutes(30),
    })
    .Build();

await embeddings.GenerateAsync(["Redis", "Valkey", "Redis"]);
var result = await embeddings.GenerateAsync(["Valkey", "KeyDB", "Redis"]);
Console.WriteLine($"Returned {result.Count} embeddings in input order.");
Console.WriteLine($"Provider received {provider.InputCount} inputs (3 on a cold cache; 0 when warm).");

// Replace this generator with your real IEmbeddingGenerator<string, Embedding<float>>.
sealed class DemoEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    public int InputCount { get; private set; }

    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var inputs = values.ToArray();
        InputCount += inputs.Length;
        return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(inputs.Select(value =>
            new Embedding<float>(new float[] { value.Length, value.Count(char.IsUpper) }) { ModelId = "demo-v1" })));
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
        => serviceType == typeof(EmbeddingGeneratorMetadata)
            ? new EmbeddingGeneratorMetadata("demo", defaultModelId: "demo-v1", defaultModelDimensions: 2) : null;

    public void Dispose() { }
}
