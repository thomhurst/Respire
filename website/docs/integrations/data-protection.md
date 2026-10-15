---
title: DataProtection key storage
description: Persist the ASP.NET Core DataProtection key ring in a Redis list shared by application instances.
---

# DataProtection key storage

`Respire.DataProtection` stores the ASP.NET Core DataProtection key ring in Redis. Use it when
several instances must read each other's cookies, antiforgery tokens, and other protected
payloads. It reuses an existing Respire client and the list layout of Microsoft's
`PersistKeysToStackExchangeRedis`.

The package also provides opt-in [client-side payload encryption](../guides/payload-encryption.md)
for cache values, near-cache maps, job arguments, and stream payloads.

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

Install the same RSA certificate, with its private key, where every instance can access it,
and set `DATA_PROTECTION_CERTIFICATE_THUMBPRINT` to its thumbprint. Keep old certificates when
you rotate. See Microsoft's
[key encryption guidance](https://learn.microsoft.com/aspnet/core/security/data-protection/implementation/key-encryption-at-rest)
for certificate requirements and other protection mechanisms.

### Client from dependency injection

For a singleton client in dependency injection, use `PersistKeysToRespireFromServices`:

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

It resolves the client lazily from the root provider. Do not resolve a scoped client or build
a second service provider during registration. See
[dependency injection](dependency-injection.md).

## Client lifetime

- Keep the client alive for the lifetime of the service provider.
- The factory runs on every repository operation. It must be cheap, thread-safe, and return
  an existing client. Returning `null` throws `InvalidOperationException`.
- The repository never disposes the client. A client from the DI container follows normal
  container disposal.
- The Redis key bytes are copied at registration, so you can reuse the buffer.

## Synchronous calls

DataProtection's `IXmlRepository` is synchronous, so repository calls block the calling thread
until the Redis command finishes. Set bounded `RespireOptions.ConnectTimeout` and
`CommandTimeout`, or slow Redis blocks threads during key-ring loading and rotation.

## Reads

Reads always use the primary, even with replica routing, and keep any key prefix. They bypass
the client-side cache, so a delayed invalidation cannot hide new keys or revocations.

## Storage format

The repository appends XML with `RPUSH` and reads all entries with `LRANGE`, using the list
layout of
[`PersistKeysToStackExchangeRedis`](https://github.com/dotnet/aspnetcore/blob/main/src/DataProtection/StackExchangeRedis/src/RedisXmlRepository.cs).
To share existing keys with that repository, use the same Redis database and key,
application name, DataProtection purposes, and key-encryption configuration.

`friendlyName` does not change the list or deduplicate entries. Malformed XML or a Redis
error fails the operation; it never returns a partial key ring.

## Durability and security

:::warning
- The repository sets no expiration. Use Redis persistence and prevent eviction of this key.
  If the key ring is lost, existing cookies and protected payloads become unreadable.
- Stored XML is not encrypted. Configure a `ProtectKeysWith...` option, and grant the Redis
  user only the access it needs.
:::
