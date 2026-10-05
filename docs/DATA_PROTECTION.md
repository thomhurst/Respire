# DataProtection key storage

Install `Respire.DataProtection` and reuse an application-owned client:

```csharp
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Respire;
using Respire.DataProtection;

await using var client = await RespireClient.ConnectAsync("localhost:6379");
var services = new ServiceCollection();
services.AddDataProtection()
    .SetApplicationName("my-application")
    .PersistKeysToRespire(() => client, "DataProtection-Keys");
```

Keep the client alive for the lifetime of the service provider. The factory is
called for each repository operation; it should return an existing client, not
open a new connection. Neither the repository nor provider disposal owns that client.
DataProtection's `IXmlRepository` API is synchronous, so repository calls block
until the underlying Respire command finishes or fails.

The repository appends XML with `RPUSH` and reads all entries with `LRANGE`.
It uses the same list layout as
[`PersistKeysToStackExchangeRedis`](https://github.com/dotnet/aspnetcore/blob/main/src/DataProtection/StackExchangeRedis/src/RedisXmlRepository.cs).
To share existing keys, use the same Redis database and key, application name,
DataProtection purposes, and key-encryption configuration. `friendlyName` does
not alter the list or deduplicate entries. Malformed XML or Redis errors fail the
operation; no partial key ring is returned.

No expiration is set. Use Redis persistence and prevent eviction of this key:
losing the key ring makes previously protected cookies and payloads unreadable.
The repository does not encrypt stored XML. Configure a suitable DataProtection
`ProtectKeysWith...` option to protect keys at rest, and grant the Redis client
only the access the application needs.
