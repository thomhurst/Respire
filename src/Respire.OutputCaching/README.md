# Respire.OutputCaching

An ASP.NET Core output-cache store backed by Respire.

## Install

```sh
dotnet add package Respire.OutputCaching
dotnet add package Respire.DependencyInjection
```

## Usage

```csharp
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Respire.DependencyInjection;
using Respire.OutputCaching;

var builder = WebApplication.CreateBuilder();
builder.Services.AddRespire("redis://localhost:6379");
builder.Services.AddRespireOutputCache(options => options.InstanceName = "myapp:");
var app = builder.Build();
app.UseOutputCache();
app.MapGet("/greeting", () => "hello").CacheOutput();
app.Run();
```

Use this in an ASP.NET Core application. The store reuses the registered client.

[Read the guide](https://thomhurst.github.io/Respire/docs/integrations/output-caching) for configuration, requirements, and further examples.
