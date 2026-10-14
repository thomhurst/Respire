# ASP.NET Core upstream compatibility coverage

These tests adapt the MIT-licensed [ASP.NET Core v10.0.12 source](https://github.com/dotnet/aspnetcore/tree/v10.0.12).
The source headers retain the .NET Foundation copyright/license notices. The
[upstream MIT license](https://github.com/dotnet/aspnetcore/blob/v10.0.12/LICENSE.txt)
applies to these adaptations.

Both `Microsoft.Extensions.Caching.StackExchangeRedis` and
`Microsoft.AspNetCore.DataProtection.StackExchangeRedis` are pinned to **10.0.13**
in `Directory.Packages.props`, on net8.0 and net10.0. They use the pinned
**StackExchange.Redis 3.4.0** interfaces. No test uses a production
StackExchange.Redis connection in place of the shim.

The adapted test sources remain based on v10.0.12; the package versions above
identify the releases those tests currently run against.

## Complete upstream class inventory

The [cache test directory](https://github.com/dotnet/aspnetcore/tree/v10.0.12/src/Caching/StackExchangeRedis/test)
contains four test classes. The [DataProtection test directory](https://github.com/dotnet/aspnetcore/tree/v10.0.12/src/DataProtection/StackExchangeRedis/test)
contains two test classes; its remaining C# files configure conditional Redis access.

| Upstream class | Methods ported / total | Executed cases | Exclusions |
| --- | ---: | ---: | --- |
| `RedisCacheSetAndRemoveTests` | 10 / 10 | 15 | None; string theories retain all seven argument rows |
| `TimeExpirationTests` | 13 / 13 | 13 | None |
| `TimeExpirationAsyncTests` | 13 / 13 | 13 | None |
| `CacheServiceExtensionsTests` | 6 / 7 | 6 | `AddStackExchangeRedisCache_HybridCacheDetected` (two rows) |
| `DataProtectionRedisTests` | 4 / 4 | 4 | None; the conditional live-Redis fact always runs with Testcontainers |
| `RedisDataProtectionBuilderExtensionsTest` | 1 / 1 | 1 | None |
| **Total** | **47 / 48** | **52** | **Two internal-only cases** |

The excluded [HybridCache detection test](https://github.com/dotnet/aspnetcore/blob/v10.0.12/src/Caching/StackExchangeRedis/test/CacheServiceExtensionsTests.cs)
calls `RedisCache.IsHybridCacheActive()`, an internal method accessible to the
official friend test assembly. It verifies ASP.NET Core's internal service
detection, without opening Redis or using an `IConnectionMultiplexer`. This test
project does not have that friend grant. We do not bypass private access or
rename the assembly to test an internal framework implementation. The public
service registration tests remain included. They verify the published package's
DI behavior; they do not independently establish Redis wire compatibility.

## Adaptations

- xUnit discovery attributes become TUnit `[Test]`/`[Arguments]`. Original method
  names and behavioral assertions are retained. Assertion helpers verify the
  same argument names, values and message prefixes across runtimes.
- A session-wide Testcontainers Redis server gives each test a separate logical
  database. `RESPIRE_TEST_PROTOCOL=2` or `3` selects the native protocol; CI runs
  all cases with each protocol on each supported framework.
- Cache construction uses `ConnectionMultiplexerFactory` returning
  `RespireConnectionMultiplexer`. DI construction tests use the same factory
  where they resolve the cache. The chaining test preserves the upstream empty
  options callback and never resolves a connection.
- DataProtection mock database replies are arranged in real Redis through the
  shim. Mock call verification becomes an independent returned XML or list-state
  assertion. The builder extension receives the real lazy adapter instead of a
  mocked multiplexer. The round-trip uses separate adapter lifetimes.
- Caches, adapters and service providers are disposed. Expiration tests retain
  upstream timings and run without parallel blocking tests starving refresh work.

The broader compatibility project additionally tests buffer-cache operations,
binary result shapes, native exception types, sync/async commands, batch and
transaction ordering, shutdown, borrowed-client ownership, callback delivery and
unsubscribe. Hangfire has its own [pinned upstream inventory](../../Respire.Hangfire.Acceptance.Tests/Upstream/NOTICE.md).
SignalR queue-based conformance is not planned
([#1268](https://github.com/thomhurst/Respire/issues/1268)): the adapter does not
provide queue-returning subscriptions, so Microsoft's SignalR backplane is out of
scope. SignalR scale-out uses the native `Respire.SignalR` backplane. This inventory
does not claim general StackExchange.Redis parity or close #889.
