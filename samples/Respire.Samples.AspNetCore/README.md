# ASP.NET Core with Respire

A minimal API where one DI-owned Respire client serves direct commands,
`IDistributedCache`, HybridCache's Redis L2, health checks, and output caching.
OpenTelemetry writes Respire traces and metrics to the console. The sample uses
project references; your app should reference the NuGet packages.

You need the SDK from `global.json`, the .NET 8 or .NET 10 ASP.NET Core runtime,
PowerShell 7 for the smoke script, and Docker with Linux containers. Run commands
from the repository root.

## Run

Start a Redis container for the sample (change the name or port if needed):

```sh
docker run --detach --rm --name respire-aspnet-sample-redis --publish 127.0.0.1:6384:6379 redis:8.10-alpine
docker exec respire-aspnet-sample-redis redis-cli ping
```

After `PONG`, start the API in PowerShell:

```powershell
$env:ConnectionStrings__Redis = 'redis://localhost:6384'
$env:Sample__KeyPrefix = 'respire:aspnet-sample:'
dotnet run --project samples/Respire.Samples.AspNetCore -c Release -f net10.0 -- --urls http://localhost:5084
```

- Use `-f net8.0` for .NET 8. In Bash, `export` the same two variables.
- The connection string is required. For authenticated Redis, use your normal
  secret provider.
- Keep the sample bound to localhost: its write endpoints have no
  authentication, authorization, or rate limits.
- Use a distinct `Sample__KeyPrefix` per deployment. Instances that share caches
  need the same prefix and compatible serialization.

## Try it

In another PowerShell terminal:

```powershell
Invoke-RestMethod http://localhost:5084/health
Invoke-WebRequest http://localhost:5084/redis -Method Post -ContentType application/json -Body '{"value":"hello Redis"}'
Invoke-RestMethod http://localhost:5084/redis
Invoke-WebRequest http://localhost:5084/distributed -Method Post -ContentType application/json -Body '{"value":"hello cache"}'
Invoke-RestMethod http://localhost:5084/distributed
Invoke-RestMethod http://localhost:5084/hybrid
Invoke-RestMethod http://localhost:5084/output
pwsh samples/Respire.Samples.AspNetCore/Smoke.ps1
```

| Endpoint | Behavior |
| --- | --- |
| `/health` | `Healthy`, or HTTP 503 when unhealthy. Checks the existing connection only. |
| `/redis`, `/distributed` | JSON `value`, or null when absent. Writes expire after one minute. |
| `/hybrid` | A generated `version` and timestamp, kept for up to one minute (L1 for five seconds, then Redis L2). `?entry=<GUID>` selects a separate entry. |
| `/output` | Whole anonymous GET response cached in Redis for 30 seconds. Query strings vary the entry. |

- The DI client connects lazily and health probes open no connections, so the
  sample sends a `PING` (five-second limit) before listening and fails to start
  if Redis is down. Choose your own startup and readiness policy in production.
- HybridCache here has no cross-process L1 invalidation: a change made in Redis
  by another process does not evict L1 immediately.
- Output caching follows ASP.NET Core's normal rules, such as skipping
  authenticated requests and responses that set cookies. Health and write
  endpoints are not output-cached.

Keys: direct `respire:aspnet-sample:direct:value`, distributed and HybridCache
`respire:aspnet-sample:cache:`, output `respire:aspnet-sample:output:`. List them
without flushing Redis:

```sh
docker exec respire-aspnet-sample-redis redis-cli --scan --pattern 'respire:aspnet-sample:*'
```

The smoke script needs the API and Redis running. It uses a fresh GUID per run,
waits six seconds to cross the L1 expiry, and fails on unexpected responses or
changed cache versions. It writes only sample values and does not start or stop
infrastructure.

## Telemetry

The API terminal shows `Respire` trace output and metrics, including
`db.client.operation.duration` (seconds). Command metrics are enabled at startup
because the default groups omit them. Output-cache and L1 hits may emit no Redis
span. The one-second console exporter is deliberately verbose; pick a real
exporter and interval for production. HTTP server instrumentation is not
included. See [Respire observability](../../website/docs/integrations/observability.md)
and the [OpenTelemetry .NET exporter guide](https://opentelemetry.io/docs/languages/dotnet/exporters/).

## Stop

Press Ctrl+C so DI disposes the client and telemetry providers, then stop the
container:

```sh
docker stop respire-aspnet-sample-redis
```

The container has no volume, so its data is removed. On a shared Redis, let the
sample TTLs expire the entries; do not flush the database.
