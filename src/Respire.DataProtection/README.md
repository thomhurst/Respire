# Respire.DataProtection

Persist ASP.NET Core DataProtection keys in Redis through a shared Respire client.

## Install

```sh
dotnet add package Respire.DataProtection
```

## Usage

```csharp
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Respire;
using Respire.DataProtection;

await using var redis = await RespireClient.ConnectAsync("redis://localhost:6379");
var services = new ServiceCollection();
var thumbprint = Environment.GetEnvironmentVariable("DATA_PROTECTION_CERTIFICATE_THUMBPRINT")
    ?? throw new InvalidOperationException("Configure the encryption certificate.");
services.AddDataProtection()
    .SetApplicationName("my-application")
    .PersistKeysToRespire(() => redis, "DataProtection-Keys")
    .ProtectKeysWithCertificate(thumbprint);
await using var provider = services.BuildServiceProvider();
```

Provision the encryption certificate on every application instance. Keep Redis keys durable and non-evictable; losing the key ring makes existing protected data unreadable.

[Read the guide](https://thomhurst.github.io/Respire/docs/integrations/data-protection) for configuration, requirements, and further examples.
