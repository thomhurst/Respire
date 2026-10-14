# Respire.StackExchangeCompat

Binary-safe StackExchange.Redis value conversion, native command bridging, and a limited IConnectionMultiplexer adapter for incremental migration.

## Install

```sh
dotnet add package Respire.StackExchangeCompat
```

## Usage

```csharp
using Respire;
using Respire.StackExchangeCompat;
using StackExchange.Redis;

await using var redis = await RespireClient.ConnectAsync("redis://localhost:6379");
RedisKey key = "customer:42";
RedisValue value = new byte[] { 0xff, 0, 0x80 };
await redis.Strings.SetAsync(key.ToRespireKey(), value.ToRespireValue());
```

This is a compatibility subset. Check the guide before replacing a multiplexer; unsupported members throw NotSupportedException.

[Read the guide](https://thomhurst.github.io/Respire/docs/guides/stackexchange-interop) for configuration, requirements, and further examples.
