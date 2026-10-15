# Respire.Aws

Renewable AWS IAM credentials for Amazon ElastiCache and MemoryDB connections.

## Install

```sh
dotnet add package Respire.Aws
```

## Usage

```csharp
using Amazon;
using Amazon.Runtime.Credentials;
using Respire;
using Respire.Aws;

var credentials = DefaultAWSCredentialsIdentityResolver.GetCredentials();
await using var redis = await RespireClient.ConnectAsync(new RespireOptions
{
    Endpoints = { new RespireEndpoint("cache.example.amazonaws.com", 6379) },
    UseTls = true,
    CredentialProvider = new RespireElastiCacheIamCredentialProvider(
        credentials, RegionEndpoint.USEast1, "my-cache", "my-user", isServerless: true),
});
```

Replace the endpoint, region, cache, and user with your IAM-enabled resource. The AWS identity needs connect permissions. Use RespireMemoryDbIamCredentialProvider for MemoryDB.

[Read the guide](https://thomhurst.github.io/Respire/docs/guides/aws-iam-credentials) for configuration, requirements, and further examples.
