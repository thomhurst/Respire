# Respire.Gcp

Authenticate to Google Cloud Memorystore for Redis Cluster and Memorystore for Valkey with renewable IAM OAuth access tokens.

```sh
dotnet add package Respire.Gcp
```

```csharp
using Google.Apis.Auth.OAuth2;
using Respire.Gcp;

var credential = await GoogleCredential.GetApplicationDefaultAsync();
var provider = new RespireMemorystoreCredentialProvider(credential);
```

Set `RespireOptions.CredentialProvider` to this provider. Enable TLS and trust the instance's server CA.
Google caches and refreshes access tokens; Respire re-authenticates live connections before their actual expiry.

[Read the guide](https://thomhurst.github.io/Respire/docs/guides/google-cloud-memorystore) for IAM roles, cluster configuration, TLS, and verification.
