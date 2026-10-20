# Respire.Testing.StackExchangeCompat

Test the documented StackExchange.Redis compatibility subset using real Respire
clients over in-memory pipes, without sockets or Redis containers.

```csharp
using Respire.Testing.StackExchangeCompat;
using StackExchange.Redis;

await using var fixture = new RespireFakeConnection();
IConnectionMultiplexer connection = fixture.Connection;
IDatabase database = connection.GetDatabase();
await database.HashSetAsync("greeting", "text", "hello");
var value = await database.HashGetAsync("greeting", "text");
```

This package shares the adapter's experimental `RSP0006` opt-in. Add `RSP0006`
to your project's `NoWarn` when choosing to use it.

The default constructor owns an isolated server. Pass a `RespireFakeServer` to
share state or configure a fake clock and faults; the caller retains ownership
of that server. Dispose fixtures before a caller-owned server. Fixture disposal
joins pending operations and disposes the multiplexer/clients before an owned
server. Do not leave fault gates paused during disposal.

Only database zero is supported. Other database selections and unimplemented
adapter members throw `NotSupportedException`. Commands supported by the adapter
but not by the fake (such as arbitrary Lua) throw `RespireServerException`.
This is not general StackExchange.Redis parity or a replacement for real-server
integration tests.

See the [in-memory testing guide](https://respire.tomlonghurst.dev/docs/guides/in-memory-testing)
for the supported intersection and direct composition without this helper.
