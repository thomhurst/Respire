# Respire.Compression.Lz4

Optional LZ4 compression for serializer-backed Respire values.

## Install

```sh
dotnet add package Respire.Compression.Lz4
```

## Usage

```csharp
using Respire;
using Respire.Compression;
using Respire.Compression.Lz4;
using Respire.Serialization;

await using var redis = RespireClient.Create(new RespireOptions
{
    Endpoints = [new RespireEndpoint("localhost", 6379)],
    Serializer = new RespireValueCodecSerializer(
        RespireSerializer.Default, new Lz4ValueCodec()),
});
await redis.SetAsync("profile", new Dictionary<string, string>
{
    ["description"] = new string('x', 4096),
});
```

Compression is opt-in and uses Respire framing. Primitive values such as strings and raw bytes bypass the serializer. Configure matching codecs for readers and writers.

[Read the guide](https://thomhurst.github.io/Respire/docs/guides/value-codecs) for configuration, requirements, and further examples.
