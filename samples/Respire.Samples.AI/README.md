# Embedding cache sample

Run `dotnet run --project samples/Respire.Samples.AI -f net10.0` against Redis at
`redis://localhost:6379`, or set `REDIS_CONNECTION_STRING` to your server URI.
The deterministic demo generator makes this sample runnable without API keys.
Replace it with your embedding provider in an application.

The first batch has a duplicate input. The second batch mixes hits with a new
input. On a cold cache, only three unique inputs reach the provider across both
batches. Re-running the sample within 30 minutes reuses all three vectors.

See the [embedding cache guide](https://respire.tomlonghurst.dev/docs/guides/embedding-cache).
