---
title: Azure Managed Redis authentication
description: Authenticate Respire with Microsoft Entra ID and renewable Azure access tokens.
---

# Azure Managed Redis authentication

Use `Respire.Azure` to authenticate to Azure Managed Redis with Microsoft Entra ID. Install it with
`Azure.Identity`:

```sh
dotnet add package Respire.Azure
dotnet add package Azure.Identity
```

```csharp
using global::Azure.Identity;
using Respire;
using Respire.Azure;

var credential = new DefaultAzureCredential();
var provider = new RespireAzureManagedRedisCredentialProvider(credential);

await using var redis = await RespireClient.ConnectAsync(new RespireOptions
{
    Endpoints = { new RespireEndpoint("my-cache.eastus.redis.azure.net", 10000) },
    UseTls = true,
    Protocol = RespireProtocol.Resp3,
    CredentialProvider = provider,
});
```

Setup:

1. Add the managed identity or service principal as a Redis user on the resource's Authentication page.
2. Let the provider read that identity's **object ID** from the first access token's `oid` claim.
3. Connect to `<cache-name>.<region>.redis.azure.net` on TLS port 10000.

`RespireAzureManagedRedisCredentialProvider` requests the `https://redis.azure.com/.default` scope and
passes the token's `ExpiresOn` to Respire's [credential renewal](renewable-credentials.md). The
provider and `TokenCredential` stay caller-owned.

The constructor without a username caches the first resolved `oid` for the provider's lifetime,
including concurrent connection requests and token renewals. It does not validate the token's
signature; token acquisition remains the credential's responsibility. If the first token is not a
JWT with a non-empty string `oid` claim, credential retrieval throws `InvalidOperationException`
without including the token in the error. A later request can retry resolution.

To pin a Redis ACL user explicitly, use the existing overload:

```csharp
var provider = new RespireAzureManagedRedisCredentialProvider(credential, redisUserObjectId);
```

The explicit username must be the managed identity's or service principal's object ID, not its client
ID. This overload does not inspect token claims.

The provider calls `TokenCredential.GetTokenAsync` on every request and relies on the credential's
own caching. The production credentials behind `DefaultAzureCredential` cache tokens until shortly
before expiry. Add caching to a custom `TokenCredential` that lacks it, or large pools will each
call Microsoft Entra ID.

Use RESP3 so subscriptions can renew credentials on the same socket. Respire rejects provider-backed
subscriptions with RESP2 or Auto, because RESP2 does not allow `AUTH` while subscribed.

For dependency injection, assign the provider through `RespireOptionsBuilder.CredentialProvider`:

```csharp
using global::Azure.Identity;
using Microsoft.Extensions.DependencyInjection;
using Respire;
using Respire.Azure;
using Respire.DependencyInjection;

var credential = new DefaultAzureCredential();
var provider = new RespireAzureManagedRedisCredentialProvider(credential);
var services = new ServiceCollection();

services.AddRespire(options =>
{
    options.Endpoints.Add(new RespireEndpoint("my-cache.eastus.redis.azure.net", 10000));
    options.UseTls = true;
    options.Protocol = RespireProtocol.Resp3;
    options.CredentialProvider = provider;
});
```

See the [`Respire.Samples.Azure`](https://github.com/thomhurst/Respire/tree/main/samples/Respire.Samples.Azure)
sample, and the Microsoft guides for [Azure Managed Redis Microsoft Entra authentication](https://learn.microsoft.com/azure/redis/entra-for-authentication)
and [Azure Identity credentials](https://learn.microsoft.com/dotnet/azure/sdk/authentication/credential-chains).
