# ASP.NET Core with Respire

This minimal API uses one DI-owned Respire client for direct commands,
`IDistributedCache`, HybridCache's Redis L2, health checks, and output caching.
OpenTelemetry exports Respire traces and metrics to the console. Repository
project references keep the sample aligned with the libraries built in CI; in
your application, reference the corresponding released NuGet packages.

Use the SDK selected by the repository's `global.json`, the .NET 8 or .NET 10
ASP.NET Core runtime, PowerShell 7 for the smoke script, and Docker with Linux
containers for the local Redis example. Run these commands from the repository root.

## Run

Start a Redis container owned by this sample (choose another name/port if needed):

```sh
docker run --detach --rm --name respire-aspnet-sample-redis --publish 127.0.0.1:6384:6379 redis:8.4-alpine
docker exec respire-aspnet-sample-redis redis-cli ping
```

Wait for `PONG`, then start the API in PowerShell:

```powershell
$env:ConnectionStrings__Redis = 'redis://localhost:6384'
$env:Sample__KeyPrefix = 'respire:aspnet-sample:'
dotnet run --project samples/Respire.Samples.AspNetCore -c Release -f net10.0 -- --urls http://localhost:5084
```

Use `-f net8.0` to run on .NET 8. In Bash, set the same configuration before
the command with `export ConnectionStrings__Redis=redis://localhost:6384` and
`export Sample__KeyPrefix=respire:aspnet-sample:`. The application requires a
connection string; it does not log that configuration or store credentials.
Use your normal external configuration/secret provider for authenticated Redis.

Keep this unauthenticated demonstration bound to localhost. The write endpoints
are examples, not a public API with authentication, authorization or rate limits.
Use a distinct `Sample__KeyPrefix` for each independent deployment. Instances
intended to share caches should use the same prefix and compatible serialization.

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

DI creates its client lazily, and health probes do not open connections. The
sample explicitly sends a startup PING with a five-second bound before listening;
startup fails if Redis is unavailable. `/health` then checks that existing
connection and returns `Healthy` after a successful probe (HTTP 503 when
unhealthy). Direct and distributed writes expire after one minute. `/redis`
and `/distributed` return a JSON `value`, or null when absent.

`/hybrid` returns a generated `version` and timestamp. Repeated requests keep
that version for up to one minute: L1 lives for five seconds, and subsequent
misses can load the same value from Redis L2. The smoke script waits six seconds
to exercise that transition. This uses standard HybridCache expiration and
stampede protection. It does **not** provide cross-process server-assisted L1
invalidation; another process changing Redis does not immediately invalidate L1.

`/output` caches the complete anonymous GET response in Redis for 30 seconds.
Repeated requests return the same version while cached. Output caching follows
ASP.NET Core's normal policy, including its restrictions on authenticated
requests and responses that set cookies. Neither health nor write endpoints are
output-cached.

The direct key uses `respire:aspnet-sample:direct`. Distributed and HybridCache
entries use the `respire:aspnet-sample:cache:` namespace; output caching uses
`respire:aspnet-sample:output:`. Inspect this sample's keys without flushing Redis:

```sh
docker exec respire-aspnet-sample-redis redis-cli --scan --pattern 'respire:aspnet-sample:*'
```

The smoke script needs a running API and Redis. It fails on unexpected HTTP
responses or changed cache versions. It writes only these sample values and
does not start or stop infrastructure. CI builds the application on both target
frameworks; a build alone is not a Redis smoke test.

## Telemetry

Watch the API terminal while running the requests. Trace output names the
`Respire` activity source. Metric output includes `db.client.operation.duration`
in seconds and enabled connection instruments. Command metrics are explicitly
enabled once at startup because the default metric groups omit them. Output
cache hits and HybridCache L1 hits may do no Redis work and therefore need not
emit a Redis command span.

The one-second console exporter interval makes this example observable without
another service. It is intentionally verbose; choose a suitable exporter and
interval for production. This sample subscribes to Respire instrumentation, not
HTTP server instrumentation. See [Respire observability](../../website/docs/integrations/observability.md)
and the [OpenTelemetry .NET exporter guide](https://opentelemetry.io/docs/languages/dotnet/exporters/).

## Stop

Press Ctrl+C in the API terminal so DI disposes the client and telemetry providers.
Then stop only the container you created:

```sh
docker stop respire-aspnet-sample-redis
```

The `--rm` container has no persistent volume. Stopping it removes this sample's
Redis data. When using an external Redis instead, sample value TTLs expire the
entries; do not flush a shared database.
