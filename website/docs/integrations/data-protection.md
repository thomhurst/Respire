---
title: DataProtection key storage
description: Persist the ASP.NET Core DataProtection key ring in a Redis list shared by application instances.
---

# DataProtection key storage

`Respire.DataProtection` stores the ASP.NET Core DataProtection key ring in Redis. Use it when
several application instances must share keys, so that cookies, antiforgery tokens, and other
protected payloads created by one instance can be read by another. It reuses an existing
Respire client and uses the same list layout as Microsoft's `PersistKeysToStackExchangeRedis`.

## Install

```bash
dotnet add package Respire.DataProtection
```

## Setup

Pass a factory that returns an application-owned client:

```csharp
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Respire;
using Respire.DataProtection;

await using var client = await RespireClient.ConnectAsync("localhost:6379");
var services = new ServiceCollection();
var certificateThumbprint = Environment.GetEnvironmentVariable("DATA_PROTECTION_CERTIFICATE_THUMBPRINT")
    ?? throw new InvalidOperationException("Configure the DataProtection encryption certificate.");
services.AddDataProtection()
    .SetApplicationName("my-application")
    .PersistKeysToRespire(() => client, "DataProtection-Keys")
    .ProtectKeysWithCertificate(certificateThumbprint);
```

Provision the same RSA certificate, including its private key, in a certificate store that
each application instance can access. Set `DATA_PROTECTION_CERTIFICATE_THUMBPRINT` to its
thumbprint. Keep old decryption certificates when you rotate certificates. See Microsoft's
[key encryption guidance](https://learn.microsoft.com/aspnet/core/security/data-protection/implementation/key-encryption-at-rest)
for certificate requirements and other protection mechanisms.

### Client from dependency injection

If the client is a singleton in dependency injection, use
`PersistKeysToRespireFromServices` instead:

```csharp
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Respire;
using Respire.DataProtection;
using Respire.DependencyInjection;

var services = new ServiceCollection();
services.AddRespire("localhost:6379");
services.AddDataProtection()
    .SetApplicationName("my-application")
    .PersistKeysToRespireFromServices(
        provider => provider.GetRequiredService<IRespireClient>(), "DataProtection-Keys");
// Add a ProtectKeysWith... call, as in the previous example.
```

This method resolves the client lazily from the root service provider. Do not resolve a
scoped client, and do not build a second service provider during registration. See
[dependency injection](dependency-injection.md) for client registration.

## Client lifetime

- Keep the client alive for the lifetime of the service provider.
- The factory runs for each repository operation. It must be cheap and thread-safe, and it
  should return an existing client, not open a new connection. A factory that returns
  `null` throws `InvalidOperationException`.
- The repository never disposes the client. An externally supplied client stays
  caller-owned. A client created by the DI container follows normal container ownership
  and disposal rules.
- The repository copies the key bytes at construction or registration, so you can reuse
  the original buffer.

## Synchronous calls

DataProtection's `IXmlRepository` API is synchronous, and Respire has no synchronous command
API. Repository calls block the calling thread until the Respire command finishes or fails.
Set bounded `RespireOptions.ConnectTimeout` and `CommandTimeout` values, because slow Redis
operations block threads during key-ring loading and rotation.

## Reads

Reads always use the primary, even when the client uses replica routing. The primary view
keeps any key prefix and shares the existing connections. Reads also bypass the client-side
cache, so a delayed invalidation cannot hide newly generated keys or revocations when
DataProtection refreshes its key ring.

## Storage format

The repository appends XML with `RPUSH` and reads all entries with `LRANGE`. It uses the same
list layout as
[`PersistKeysToStackExchangeRedis`](https://github.com/dotnet/aspnetcore/blob/main/src/DataProtection/StackExchangeRedis/src/RedisXmlRepository.cs).
To share existing keys with that repository, use the same Redis database and key,
application name, DataProtection purposes, and key-encryption configuration.

`friendlyName` does not change the list or deduplicate entries. Malformed XML or a Redis
error fails the operation; the repository never returns a partial key ring.

## Durability and security

The repository sets no expiration. Use Redis persistence and prevent eviction of this key.
If the key ring is lost, previously protected cookies and payloads become unreadable.

The repository does not encrypt stored XML. Configure a `ProtectKeysWith...` option to
protect keys at rest, and grant the Redis user only the access the application needs.
