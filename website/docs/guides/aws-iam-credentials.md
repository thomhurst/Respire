---
title: AWS IAM credentials
description: Authenticate Respire connections to Amazon ElastiCache and MemoryDB with renewable IAM tokens.
---

# AWS IAM credentials

Install `Respire.Aws` to authenticate to Amazon ElastiCache or MemoryDB with IAM. Its credential
providers sign a 15-minute IAM token from an AWS SDK `AWSCredentials` instance (AWS SDK for .NET
v4, SigV4), and Respire [renews it on live connections](renewable-credentials.md) before expiry.

```bash
dotnet add package Respire.Aws
```

```csharp
using Amazon;
using Amazon.Runtime.Credentials;
using Respire;
using Respire.Aws;

var awsCredentials = DefaultAWSCredentialsIdentityResolver.GetCredentials();

var elasticache = new RespireOptions
{
    Endpoints = { new RespireEndpoint("cache.example.amazonaws.com", 6379) },
    UseTls = true,
    CredentialProvider = new RespireElastiCacheIamCredentialProvider(
        awsCredentials, RegionEndpoint.USEast1, "my-cache", "my-user", isServerless: true),
};

var memoryDb = new RespireOptions
{
    Endpoints = { new RespireEndpoint("cluster.example.amazonaws.com", 6379) },
    UseTls = true,
    CredentialProvider = new RespireMemoryDbIamCredentialProvider(
        awsCredentials, RegionEndpoint.USEast1, "my-cluster", "my-user"),
};
```

| | ElastiCache | MemoryDB |
| --- | --- | --- |
| Resource name | Serverless cache name or provisioned replication group ID (lowercase) | Cluster name |
| User | IAM-enabled user whose user ID matches its username | IAM-enabled username |
| Versions | Redis OSS 7.0+, Valkey 7.2+ | Redis OSS or Valkey 7.0+ |

- `isServerless: true` adds the `ResourceType=ServerlessCache` signing parameter.
- Both services need IAM permission to connect to the resource and user.
- Keep TLS enabled. ElastiCache IAM authentication requires it.
- Tokens expire after 15 minutes. Keep `CredentialRefreshBeforeExpiry` (default 5 minutes) below 15 minutes.
- AWS closes IAM-authenticated connections after 12 hours unless the client reauthenticates with a fresh token.
- Use `Protocol = RespireProtocol.Resp3` with Pub/Sub, so subscribed connections can reauthenticate without dropping subscriptions.
- The provider passes its cancellation token to AWS credential resolution and signing. Respire does not own or dispose the AWS credentials provider.

:::warning
Keep credentials and signed tokens out of logs.
:::
