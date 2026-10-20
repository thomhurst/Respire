---
sidebar_position: 2
title: Getting started
description: Build a small ASP.NET Core app with typed values, pub/sub, and a batch in five minutes.
---

# Getting started

Build a small ASP.NET Core API that stores a user, publishes updates, and reads two values in a batch. You need the **.NET 10 SDK** and a Redis-compatible server; Docker is enough for this walkthrough.

Respire targets both `net8.0` and `net10.0`. For an existing .NET 8 app, use a **C# 13 or newer compiler** (SDK 9 or newer) and set `<LangVersion>13</LangVersion>` or higher in its `.csproj`. The .NET 8 SDK's C# 12 compiler cannot call Respire's `params ReadOnlySpan<T>` APIs in expanded form. You can keep `<TargetFramework>net8.0</TargetFramework>` while building with the .NET 10 SDK.

## Project template

For a ready-to-run cache-aside API, install the template package with the **.NET 10 SDK**:

Choose the exact template version from its
[GitHub release](https://github.com/thomhurst/Respire/releases), including the
`-rc.N` suffix for a release candidate. Replace `<exact-version>` below before running
the command. Before the first stable template release, an unversioned install
cannot select a candidate.

```bash
dotnet new install Respire.Templates@<exact-version>
dotnet new respire-api -n MyCacheApi
cd MyCacheApi
docker compose up -d --wait
dotnet run
```

Open `http://localhost:5080/health/ready`, then request
`http://localhost:5080/values/hello` twice. The API caches a generated value for five
minutes. Replace its factory with your database or service lookup. The starter
includes a shared DI client, Redis readiness checks, and OpenTelemetry traces and
metrics. Set `OTEL_EXPORTER_OTLP_ENDPOINT` to enable export to your collector.

The container requires Docker Compose and uses Redis 7. Set
`ConnectionStrings__Redis` to use an existing Redis 7+ server instead.
Stop the app with Ctrl+C and run `docker compose down` to remove the local container.
The generated README covers configuration and custom HTTP ports.

### Aspire starter

After installing the same template package, use `respire-aspire` for an AppHost
and minimal API with an Aspire-managed Redis container:

```powershell
dotnet new respire-aspire -n MyRedisApp
cd MyRedisApp
dotnet build MyRedisApp.slnx
cd MyRedisApp.AppHost
aspire start --non-interactive
aspire wait api --non-interactive --timeout 120
aspire describe --non-interactive
```

You need .NET SDK **10.0.401 or later**, [Aspire CLI](https://aspire.dev/get-started/install-cli/)
**13.6 or later**, a running Docker-compatible runtime, and a trusted development
certificate (`dotnet dev-certs https --trust`). The starter uses Aspire 13.6.1
hosting packages and Redis 8.10. Add `--isolated` to `aspire start` when running
multiple copies.

Use the API HTTP URL shown by `describe` or the dashboard; ports are dynamic.
`WithReference` supplies Redis credentials and `WaitFor` gates API startup on
Redis readiness. The API establishes its client connection before serving
`/health/ready`; `/health/live` checks only the running API. Send a JSON string to
`PUT /values/hello`, then call `GET /values/hello` to read it back. Values expire
after five minutes; a missing value returns HTTP 404.

`Aspire.Respire` registers the client, Redis health check, and Respire traces and
metrics. Logs and telemetry export to the Aspire dashboard through its injected
OTLP configuration. See the generated README for runnable requests and
[Aspire integration](integrations/aspire.md) for configuration options.

Run `aspire stop --non-interactive` from the AppHost directory to stop its API,
dashboard and Redis container. This starter uses no persistent volume, so Redis
data is discarded. For a larger example including Valkey and HTTP caching, see
the [Aspire sample](integrations/aspire-sample.md).

Templates are shipped with Respire releases. Generated Respire package references
use that same version. To remove the templates, run
`dotnet new uninstall Respire.Templates`.

The walkthrough below builds a different example by hand, with typed values,
pub/sub, and batching.

## 1. Create the app

```bash
dotnet new web -n RespireQuickstart -f net10.0
cd RespireQuickstart
dotnet add package Respire.DependencyInjection
```

`Respire.DependencyInjection` brings in the core `Respire` package. No other Respire package is needed here. This command selects the latest stable package. The API may change before the first supported major release; see [packages](./packages) for optional integrations.

### Release candidates

The next supported major targets **2.0.0**, skipping the 1.x line because NuGet already
contains an unlisted `Respire 1.0.116` package. Release candidates use `2.0.0-rc.N`
and are published manually as they become ready. After a candidate is published,
opt in with:

```bash
dotnet add package Respire.DependencyInjection --prerelease
```

Without `--prerelease`, NuGet continues to select stable packages. To reproduce a
particular candidate, use the exact `--version` command from its
[GitHub release](https://github.com/thomhurst/Respire/releases). All Respire packages
in an application should use the same version.

If you do not already have a local server, start one:

```bash
docker run --name respire-quickstart-redis --rm -d -p 127.0.0.1:6379:6379 redis:7-alpine
```

## 2. Replace Program.cs

Copy this entire file. `AddRespire` gives every endpoint the same client and disposes it at shutdown. It connects on the first command.

<!-- doc-test-ignore: The complete Program.cs is compiled without harness variables by scripts/Verify-GettingStarted.ps1 in CI on net8.0 and net10.0. -->
```csharp title="Program.cs"
using System.Text.Json.Serialization;
using Respire;
using Respire.DependencyInjection;
using Respire.Serialization;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddRespire(_ => new RespireOptions
{
    Endpoints = { new RespireEndpoint("localhost") },
    Serializer = RespireSystemTextJsonSerializer.FromContext(AppJsonContext.Default),
});
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonContext.Default));

await using var app = builder.Build();

app.MapPost("/users/{id}", async (string id, User user, IRespireClient redis, CancellationToken ct) =>
{
    await redis.SetAsync($"quickstart:user:{id}", user,
        expiry: TimeSpan.FromMinutes(30), cancellationToken: ct);
    await redis.PublishAsync("quickstart:updates", $"Saved user {id}", ct);
    return Results.NoContent();
});

app.MapGet("/users/{id}", async (string id, IRespireClient redis, CancellationToken ct) =>
{
    User? user = await redis.GetAsync<User>($"quickstart:user:{id}", ct);
    return user is null ? Results.NotFound() : Results.Ok(user);
});

app.MapGet("/stats", async (IRespireClient redis, CancellationToken ct) =>
{
    using var batch = redis.CreateBatch();
    var user = batch.Get<User>("quickstart:user:ada");
    var visits = batch.Increment("quickstart:visits");
    await batch.ExecuteAsync(ct);
    return Results.Ok(new Stats(user.Result, visits.Result));
});

// Subscribe before accepting requests, so the first POST can be observed.
var client = app.Services.GetRequiredService<IRespireClient>();
await using var subscription = await client.SubscribeAsync("quickstart:updates");
var listener = LogUpdatesAsync(subscription, app.Lifetime.ApplicationStopping);
try
{
    await app.RunAsync();
}
finally
{
    await subscription.DisposeAsync();
    await listener;
}

static async Task LogUpdatesAsync(RespireSubscription subscription, CancellationToken ct)
{
    try
    {
        await foreach (var message in subscription.WithCancellation(ct))
        {
            if (message.Kind == RespireMessageKind.Message)
                Console.WriteLine(message.Text);
            else
                Console.WriteLine("Updates were missed; read the current user from Redis.");
        }
    }
    catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
}

public sealed record User(string Name, int LoginCount);
public sealed record Stats(User? User, long Visits);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(User))]
[JsonSerializable(typeof(Stats))]
internal partial class AppJsonContext : JsonSerializerContext;
```

The JSON context generates metadata for your POCOs. `RespireSystemTextJsonSerializer.FromContext` makes Respire's serializer safe for trimming and NativeAOT; the HTTP endpoints use the same metadata. Publishing this whole app as NativeAOT has additional [serialization and analyzer requirements](./fundamentals/values-and-serialization#nativeaot-and-trimming).

The subscriber prints notifications to the app's terminal. Pub/sub is live delivery, so missed messages are not replayed; [pub/sub](./guides/pub-sub) explains reconnects and delivery gaps. The batch queues both commands before `ExecuteAsync`; read `.Result` only after execution finishes. A batch is not an atomic transaction.

## 3. Run it

```bash
dotnet run --urls http://localhost:5050
```

In another terminal, create `user.json` containing:

```json
{"name":"Ada","loginCount":7}
```

Then run these commands from that file's directory (in Windows PowerShell, use `curl.exe`):

```bash
curl -i -X POST http://localhost:5050/users/ada -H "Content-Type: application/json" --data-binary "@user.json"
curl http://localhost:5050/users/ada
curl http://localhost:5050/stats
```

The POST returns `204 No Content`, and the app prints `Saved user ada`. The GET returns `{"name":"Ada","loginCount":7}`. With a fresh Redis server, on the first `/stats` request, the response is `{"user":{"name":"Ada","loginCount":7},"visits":1}`; subsequent calls increase `visits`.

Stop the app with Ctrl+C. If you started the walkthrough's Docker container, stop it too:

```bash
docker stop respire-quickstart-redis
```

## Keep going

- [Dependency injection](./integrations/dependency-injection): configuration and named clients.
- [Connections](./fundamentals/connections): connection strings, credentials, TLS, and timeouts.
- [Values and serialization](./fundamentals/values-and-serialization): typed values and NativeAOT.
- [Pub/sub](./guides/pub-sub): channels, cancellation, and delivery gaps.
- [Batches and transactions](./guides/batches-and-transactions): queued results and atomic operations.
- [Strings and keys](./commands/strings-and-keys): explore more commands when you need them.
