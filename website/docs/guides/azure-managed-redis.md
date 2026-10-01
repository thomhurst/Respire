---
title: Azure Managed Redis authentication
description: Authenticate Respire with Microsoft Entra ID and renewable Azure access tokens.
---

# Azure Managed Redis authentication

Install `Respire.Extensions.Azure` and `Azure.Identity`:

```sh
dotnet add package Respire.Extensions.Azure
dotnet add package Azure.Identity
```

Add the managed identity or service principal to the Redis resource's Authentication page as a
Redis user. The Redis username is that identity's **object ID**. The access token must target
`https://redis.azure.com/.default`; `AzureManagedRedisCredentialProvider` requests this scope and
returns the token's actual `ExpiresOn` value to Respire's credential refresh lifecycle. The
provider and `TokenCredential` remain caller-owned.

The provider calls `TokenCredential.GetTokenAsync` on every credential request and relies on the
credential's own token caching. The managed identity and service principal credentials that
`DefaultAzureCredential` uses in production cache tokens until shortly before expiry, so large
connection pools do not each reach Microsoft Entra ID. Add caching to a custom `TokenCredential`
that does not cache before passing it to the provider.

```csharp
using Azure.Identity;
using Respire;
using Respire.Extensions.Azure;

var credential = new DefaultAzureCredential();
var redisUserObjectId = "<managed-identity-or-service-principal-object-id>";
var provider = new AzureManagedRedisCredentialProvider(credential, redisUserObjectId);

await using var redis = await RespireClient.ConnectAsync(new RespireOptions
{
    Endpoints = { new RespireEndpoint("my-cache.redis.azure.net", 10000) },
    UseTls = true,
    Protocol = RespProtocol.Resp3,
    CredentialProvider = provider,
});
```

For dependency injection, assign the same provider through `RespireOptionsBuilder.CredentialProvider`:

```csharp
using Azure.Identity;
using Microsoft.Extensions.DependencyInjection;
using Respire;
using Respire.Extensions.Azure;
using Respire.Extensions.DependencyInjection;

var credential = new DefaultAzureCredential();
var redisUserObjectId = "<managed-identity-or-service-principal-object-id>";
var provider = new AzureManagedRedisCredentialProvider(credential, redisUserObjectId);
var services = new ServiceCollection();

services.AddRespire(options =>
{
    options.Endpoints.Add(new RespireEndpoint("my-cache.redis.azure.net", 10000));
    options.UseTls = true;
    options.Protocol = RespProtocol.Resp3;
    options.CredentialProvider = provider;
});
```

Use RESP3 when renewing credentials on subscriptions. Redis permits `AUTH` while subscribed with
RESP3, so renewal can preserve the subscription socket. Respire rejects renewable provider-backed
subscriptions with RESP2 or Auto because Auto may fall back to RESP2, where Redis does not permit
`AUTH` in the subscribed state.

For a sample with direct and dependency-injection configuration that builds for both supported target frameworks, see
[`Respire.Samples.Azure`](https://github.com/thomhurst/Respire/tree/main/samples/Respire.Samples.Azure).

See the Microsoft guides for [Azure Managed Redis Microsoft Entra authentication](https://learn.microsoft.com/azure/redis/entra-for-authentication)
and [Azure Identity credentials](https://learn.microsoft.com/dotnet/azure/sdk/authentication/credential-chains).
