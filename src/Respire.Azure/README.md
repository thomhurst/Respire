# Respire.Azure

Authenticate to Azure Managed Redis with renewable Microsoft Entra ID access tokens.

## Install

```sh
dotnet add package Respire.Azure
dotnet add package Azure.Identity
```

## Usage

```csharp
using global::Azure.Identity;
using Respire;
using Respire.Azure;

await using var redis = await RespireClient.ConnectAsync(new RespireOptions
{
    Endpoints = { new RespireEndpoint("my-cache.eastus.redis.azure.net", 10000) },
    UseTls = true,
    Protocol = RespireProtocol.Resp3,
    CredentialProvider = new RespireAzureManagedRedisCredentialProvider(
        new DefaultAzureCredential()),
});
```

Grant the identity Redis access and replace the endpoint. The provider reads the Redis username from
the first access token's `oid` claim and caches it for its lifetime. Tokens without a usable `oid`
claim require the overload that takes an explicit identity object ID. RESP3 allows subscriptions to
renew credentials on the same socket.

[Read the guide](https://respire.tomlonghurst.dev/docs/guides/azure-managed-redis) for configuration, requirements, and further examples.
