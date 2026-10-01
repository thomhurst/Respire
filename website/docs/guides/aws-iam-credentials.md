---
title: AWS IAM credentials
description: Authenticate Respire connections to Amazon ElastiCache and MemoryDB with renewable IAM tokens.
---

# AWS IAM credentials

Install `Respire.Extensions.Aws` and configure an AWS SDK `AWSCredentials` provider. The package
uses AWS SDK for .NET's SigV4 signer to create 15 minute tokens; Respire renews live connections
before token expiry through `IRespireCredentialProvider`.

```bash
dotnet add package Respire.Extensions.Aws
```

```csharp
using Amazon;
using Amazon.Runtime;
using Respire;
using Respire.Extensions.Aws;

var awsCredentials = FallbackCredentialsFactory.GetCredentials();

var elasticache = new RespireOptions
{
    Endpoints = { new RespireEndpoint("cache.example.amazonaws.com", 6379) },
    UseTls = true,
    CredentialProvider = new ElastiCacheIamCredentialProvider(
        awsCredentials, RegionEndpoint.USEast1, "my-cache", "my-user", isServerless: true),
};

var memoryDb = new RespireOptions
{
    Endpoints = { new RespireEndpoint("cluster.example.amazonaws.com", 6379) },
    UseTls = true,
    CredentialProvider = new MemoryDbIamCredentialProvider(
        awsCredentials, RegionEndpoint.USEast1, "my-cluster", "my-user"),
};
```

For ElastiCache, pass the serverless cache name or provisioned replication group ID. Serverless
caches add the `ResourceType=ServerlessCache` signing parameter; provisioned deployments omit it.
ElastiCache requires lowercase cache names and the IAM-enabled user's user ID to match its
username. ElastiCache IAM auth supports Redis OSS 7.0+ and Valkey 7.2+; MemoryDB supports Redis OSS
or Valkey 7.0+. MemoryDB uses the cluster name and IAM-enabled username.

Both services require IAM permission to connect to the selected resource and user. ElastiCache
IAM authentication also requires TLS. Keep TLS enabled for both services. IAM tokens expire after
15 minutes; the default `CredentialRefreshBeforeExpiry` is five minutes. Do not set it to 15
minutes or more. AWS terminates IAM-authenticated connections after 12 hours unless the client
reauthenticates with a fresh token.

Use `Protocol = RespProtocol.Resp3` when provider-backed connections use Pub/Sub. Respire needs
RESP3 to reauthenticate a subscribed connection without dropping its subscriptions. The credential
provider receives the cancellation token for AWS credential resolution and signing. Respire does
not own or dispose the AWS credentials provider. Keep credentials and signed tokens out of logs.
