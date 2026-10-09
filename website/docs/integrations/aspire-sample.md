---
title: .NET Aspire sample
description: Run Redis and Valkey with an Aspire AppHost, HTTP caches, health checks, and dashboard telemetry.
---

# .NET Aspire sample

The [AppHost](https://github.com/thomhurst/Respire/tree/main/samples/Respire.Samples.Aspire.AppHost)
models Redis (`cache`), Valkey (`valkey`), and an HTTP service (`api`). The service uses the
public `Aspire.Respire` integration and its HybridCache/output-cache companions. Project
references use the repository versions of those packages; no integration logic is copied
into the sample.

`WithReference` injects both named connection strings, including generated credentials.
`WaitFor` starts the service after both servers are healthy. No server ports, passwords,
or dashboard ports belong in application configuration.

## Requirements and build

- The .NET 10 SDK selected by the repository's `global.json` (currently 10.0.401 or a
  compatible later feature band), and the .NET 10 runtime. Both sample projects target
  `net10.0`; the client integration also supports .NET 8, but this AppHost does not.
- Aspire CLI 13.6, compatible with the repository's Aspire 13.6.1 hosting packages.
  Follow the [Aspire CLI installation guide](https://aspire.dev/get-started/install-cli/).
- A running Docker-compatible container runtime, with access to the Redis 8.10 and
  Valkey 9 images. The first start downloads images.
- PowerShell 7 for the optional `Smoke.ps1` helper.
- A trusted ASP.NET Core development certificate for HTTPS dashboard access.

From the repository root:

```powershell
dotnet dev-certs https --trust
dotnet build samples/Respire.Samples.Aspire.AppHost -c Release -f net10.0
```

Building the AppHost also builds its HTTP service. CI explicitly builds the .NET 10 sample
and runs `Aspire.Respire.IntegrationTests`, which starts this same AppHost with real
Redis/Valkey resources and verifies readiness plus all three cache APIs.

## Start, readiness, and dashboard

Run all subsequent Aspire commands from the AppHost directory so they select this sample:

```powershell
cd samples/Respire.Samples.Aspire.AppHost
aspire start --non-interactive
aspire wait cache --non-interactive --timeout 120
aspire wait valkey --non-interactive --timeout 120
aspire wait api --non-interactive --timeout 120
aspire describe --non-interactive
```

Add `--isolated` to `aspire start` in a worktree or when running multiple instances. It
randomizes local endpoints and isolates AppHost secrets. Endpoints are dynamic in either
mode: use the dashboard login URL printed by `start`, and the `api` HTTP URL from
`describe` or the dashboard Resources page. Keep the generated dashboard login token
local; do not paste it into issues or commits.

`aspire wait api` checks the AppHost's HTTP readiness probe at `/health`. The service
issues bounded startup PINGs through both registered clients before serving requests.
The integration's health checks reuse those established connections; health checks do
not connect a previously unused client. `/health` aggregates Redis and Valkey readiness,
rather than reporting process liveness alone.

## Example requests and cache smoke check

Set `$baseUrl` to the discovered **HTTP** URL, without a trailing slash. No fixed port is
required. `Invoke-WebRequest` shows plain string responses; JSON endpoints use
`Invoke-RestMethod`:

```powershell
$baseUrl = Read-Host 'Paste the api HTTP URL from aspire describe'
Invoke-WebRequest "$baseUrl/health"                 # Healthy, HTTP 200
Invoke-RestMethod "$baseUrl/ping"                  # Redis PING latency
Invoke-RestMethod "$baseUrl/valkey"                # Keyed Valkey PING latency
Invoke-RestMethod "$baseUrl/distributed/example"   # value equals stored
Invoke-WebRequest "$baseUrl/hybrid/example-hybrid"
Invoke-WebRequest "$baseUrl/hybrid/example-hybrid/distributed"
Invoke-WebRequest "$baseUrl/output"
pwsh -NoProfile -File ./Smoke.ps1 -BaseUrl $baseUrl
```

The distributed endpoint creates a value and reads it back from Redis. Repeated HybridCache
requests reuse their value; the `/distributed` variant bypasses L1 and throws on a miss,
so it proves the Redis-backed L2 contains the value. Use different keys for distributed
and HybridCache requests because their stored formats differ. Repeated output requests
reuse the response until its cache policy expires. `Smoke.ps1` uses fresh keys and fails
on any health, connectivity, or cache mismatch.

## Dashboard telemetry and failure behavior

After making requests, select `api` in the dashboard:

- **Structured logs** show application requests and cache hits/misses. The application
  enables OpenTelemetry logging with formatted messages.
- **Traces** show spans from the `Respire` activity source, including PING and cache
  commands. The client integration registers that source automatically.
- **Metrics** expose the `Respire` meter. The sample enables the command metric group,
  including command duration, before creating clients. Select an instrument and generate
  more requests to inspect new measurements.

The service exports telemetry to the AppHost-provided OTLP endpoint. Export runs in
batches, so allow several seconds for new data to appear. CLI inspection uses the same
dashboard data:

```powershell
aspire otel logs api --non-interactive --limit 20
aspire otel spans api --non-interactive --limit 20
```

To demonstrate unhealthy readiness, stop **only this sample's** Redis resource:

```powershell
aspire resource cache stop --non-interactive
aspire wait cache --status down --non-interactive --timeout 30
Invoke-WebRequest "$baseUrl/health" -SkipHttpErrorCheck
aspire describe api --non-interactive
aspire resource cache start --non-interactive
aspire wait cache --non-interactive --timeout 120
aspire wait api --non-interactive --timeout 120
```

After the disconnected connection is observed, `/health` returns HTTP 503 with `Unhealthy`,
and the AppHost probe marks `api` unhealthy. Recovery follows the client's reconnect
policy. Stopping Valkey also makes aggregate readiness unhealthy. Cached L1/output
responses alone do not establish Redis availability; use `/health` and the distributed
round trip. The sample is for local learning, not a production availability guarantee.

## Owned-resource cleanup

From the same AppHost directory:

```powershell
aspire stop --non-interactive
```

This stops this AppHost, its service, dashboard, and its session-owned Redis/Valkey
containers. The sample does not request persistent volumes or persistent container
lifetimes; cache data is disposable. Do not stop unrelated AppHosts, shared lock Redis,
or other containers. If startup fails, inspect `aspire logs api --non-interactive` and
`aspire describe --non-interactive`, then stop this sample before rebuilding.

The [ASP.NET Core sample](https://github.com/thomhurst/Respire/tree/main/samples/Respire.Samples.AspNetCore)
and [Cluster/Sentinel samples](https://github.com/thomhurst/Respire/tree/main/samples)
are separate sample families tracked by [#898](https://github.com/thomhurst/Respire/issues/898).
