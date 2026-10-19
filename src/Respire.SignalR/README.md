# Respire.SignalR

A SignalR scale-out backplane over Respire pub/sub, interoperable with Microsoft's Redis backplane.

## Install

```sh
dotnet add package Respire.SignalR
dotnet add package Respire.DependencyInjection
```

## Usage

```csharp
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Respire.DependencyInjection;
using Respire.SignalR;

var builder = WebApplication.CreateBuilder();
builder.Services.AddRespire("redis://localhost:6379");
builder.Services.AddSignalR().AddRespire(options => options.ChannelPrefix = "myapp:");
```

Use this in an ASP.NET Core application and map your hubs with MapHub<THub>. Servers sharing a backplane must use matching hub names, protocols, and channel prefixes.

[Read the guide](https://respire.tomlonghurst.dev/docs/integrations/signalr) for configuration, requirements, and further examples.
