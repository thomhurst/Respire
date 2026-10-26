---
title: Google Cloud Memorystore IAM authentication
description: Connect to Memorystore for Redis Cluster and Valkey with renewable Google OAuth tokens and TLS.
---

# Google Cloud Memorystore IAM authentication

Install `Respire.Gcp` to use IAM authentication with **Memorystore for Redis Cluster** or
**Memorystore for Valkey**. The package includes `Google.Apis.Auth`; no separate extensions package is needed.

```sh
dotnet add package Respire.Gcp
```

Before connecting:

1. Enable IAM authentication and in-transit encryption on the Memorystore resource.
2. Grant the application identity `roles/redis.dbConnectionUser` for Redis Cluster, or
   `roles/memorystore.dbConnectionUser` for Valkey. Restrict the grant to the intended resource where possible.
3. Configure [Application Default Credentials](https://cloud.google.com/docs/authentication/application-default-credentials).
   For local development, run `gcloud auth application-default login`. On Google Cloud, prefer an attached
   service account or Workload Identity Federation. The identity needs the `cloud-platform` OAuth scope;
   local user credentials must already have that scope authorized.
4. Run the application on a network that can reach the discovery endpoint and every advertised node.
   Download the resource's server CA certificate from its connection/security settings.

```csharp
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using Google.Apis.Auth.OAuth2;
using Respire;
using Respire.Gcp;

var credential = await GoogleCredential.GetApplicationDefaultAsync();
var provider = new RespireMemorystoreCredentialProvider(credential);
using var serverCa = X509Certificate2.CreateFromPemFile("server-ca.pem");
var chainPolicy = new X509ChainPolicy
{
    TrustMode = X509ChainTrustMode.CustomRootTrust,
    RevocationMode = X509RevocationMode.NoCheck,
};
chainPolicy.CustomTrustStore.Add(serverCa);

await using var redis = await RespireClient.ConnectAsync(new RespireOptions
{
    Endpoints = { new RespireEndpoint("<discovery-endpoint>", 6379) },
    UseCluster = true,
    UseTls = true,
    TlsOptions = new SslClientAuthenticationOptions { CertificateChainPolicy = chainPolicy },
    Protocol = RespireProtocol.Resp3,
    CredentialProvider = provider,
});

await redis.SetAsync("memorystore:check", "connected");
Console.WriteLine(await redis.GetStringAsync("memorystore:check"));
```

Use the actual endpoint and TLS port shown for the resource. Set `UseCluster = false` and use the
primary endpoint for Valkey with Cluster Mode Disabled. The custom trust store verifies the server
against the downloaded CA; normal certificate identity validation remains enabled. If the endpoint
address differs from the certificate identity, set `TlsOptions.TargetHost` to the documented certificate
name. Keep the CA alive until the client is disposed, and replace the downloaded CA when the service rotates it.
The example disables revocation lookup for the private CA; it still checks the certificate chain,
expiry, and endpoint identity. Never use a callback that accepts every server certificate.

The provider requests `https://www.googleapis.com/auth/cloud-platform` and supplies the token as the
default Redis user's password. It supports Google user, service account, Compute Engine, impersonated,
and external account credentials. Service accounts use OAuth access tokens rather than self-signed JWTs.
Fixed tokens from `GoogleCredential.FromAccessToken` are rejected because they cannot be refreshed.

Google's credential implementation caches tokens and coalesces acquisition across connections.
The provider reads the token's original issuance time and lifetime, so retrieving a cached token never
extends its expiry. Respire's [credential renewal](renewable-credentials.md) starts five minutes before
expiry by default. If Google returns the same cached token, Respire retries until Google refreshes it,
then sends `AUTH` on each live connection. No extra token cache or background worker is needed.
The application owns the credential and provider. Use RESP3 for subscriptions that need live renewal.

Existing `Respire.DependencyInjection` and `Respire.Aspire` configuration callbacks accept this provider
through `options.CredentialProvider`; neither AWS nor Azure has a separate cloud-specific Aspire integration.
Reuse the same TLS options and provider when configuring those callbacks.

## Verify against Memorystore

From the authorized network, run the example with the intended identity and server CA. Confirm that
the SET/GET succeeds, keep the client running through a token refresh, and confirm another SET/GET
after refresh. Open a second client after the original token expires to verify fresh connection
authentication too. Record the resource type, TLS settings, elapsed time, and results without tokens.
Local tests use a fake Google token endpoint and a RESP server; they do not replace this cloud check.

See Google's [Redis Cluster IAM guide](https://cloud.google.com/memorystore/docs/cluster/about-iam-auth),
[Valkey IAM guide](https://cloud.google.com/memorystore/docs/valkey/about-iam-auth), and
[Valkey TLS and IAM examples](https://cloud.google.com/memorystore/docs/valkey/client-library-code-samples).
