---
title: .NET Aspire sample
description: Run Redis and Valkey with an Aspire AppHost, HTTP caches, health checks, and dashboard telemetry.
---

# .NET Aspire sample

The [AppHost](https://github.com/thomhurst/Respire/tree/main/samples/Respire.Samples.Aspire.AppHost)
runs Redis (`cache`), Valkey (`valkey`), and an HTTP service (`api`) that uses `Respire.Aspire`
and its HybridCache and output-cache companions. `WithReference` injects the connection strings
and credentials, and `WaitFor` starts the service once both servers are healthy, so the app has no
ports or passwords in its configuration. For configuration details, see
[Aspire integration](aspire.md).

## Requirements and build

- The .NET 10 SDK from `global.json` (10.0.402 or a later compatible band) and runtime. The
  sample targets `net10.0` only; the integration itself also supports .NET 8.
- [Aspire CLI](https://aspire.dev/get-started/install-cli/) 13.6 (the repository uses Aspire 13.6.2
  hosting packages).
- A running Docker-compatible runtime. The first start downloads the Redis 8.10 and Valkey 9 images.
- PowerShell 7 for the optional `Smoke.ps1` helper.
- A trusted ASP.NET Core development certificate for the HTTPS dashboard.

From the repository root:

```powershell
dotnet dev-certs https --trust
dotnet build samples/Respire.Samples.Aspire.AppHost -c Release -f net10.0
```

Building the AppHost also builds the HTTP service.

The AppHost sets `AspireUseCliBundle=true`, so orchestration (DCP) and the dashboard come from
the Aspire CLI bundle. The SDK can prepare the bundle during the first build; without a compatible
CLI on `PATH`, it uses the SDK-paired CLI package through .NET 10's `dnx`. That first build needs
network access. The integration test starts this same AppHost through
`DistributedApplicationTestingBuilder` and uses its resolved bundle paths; the test project does
not need its own AppHost SDK or bundle setting.

## Start, readiness, and dashboard

Run the remaining Aspire commands from the AppHost directory:

```powershell
cd samples/Respire.Samples.Aspire.AppHost
aspire start --non-interactive
aspire wait cache --non-interactive --timeout 120
aspire wait valkey --non-interactive --timeout 120
aspire wait api --non-interactive --timeout 120
aspire describe --non-interactive
```

- Add `--isolated` to `aspire start` in a worktree or to run several instances. It randomizes
  endpoints and isolates AppHost secrets.
- Endpoints are dynamic. Use the dashboard login URL printed by `start`, and the `api` HTTP URL
  from `describe` or the dashboard Resources page.
- Keep the dashboard login token private; do not paste it into issues or commits.

`aspire wait api` checks `/health`, which reports Redis and Valkey readiness. The service sends
a bounded `PING` through both clients at startup, because health checks never connect an unused
client.

## Example requests and cache smoke check

Set `$baseUrl` to the **HTTP** URL, without a trailing slash:

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

- `/distributed/{key}` writes a value and reads it back from Redis.
- `/hybrid/{key}` returns the same value on repeat. Its `/distributed` variant skips L1 and throws
  on a miss, proving the value is in Redis.
- Use different keys for distributed and HybridCache requests; their stored formats differ.
- `/output` returns the cached response until its policy expires.
- `Smoke.ps1` uses fresh keys and fails on any health, connection, or cache mismatch.

## Dashboard telemetry and failure behavior

After making requests, select `api` in the dashboard:

- **Structured logs** show requests and cache hits and misses.
- **Traces** show `Respire` spans, including `PING` and cache commands.
- **Metrics** show the `Respire` meter. The sample calls `RespireMetrics.Configure` before
  creating clients to add `RespireMetricGroups.Command` (command duration).

Telemetry is exported in batches, so new data takes a few seconds to appear. The CLI shows the same
data:

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

Once the client notices the disconnect, `/health` returns HTTP 503 `Unhealthy` and `api` is
marked unhealthy; stopping Valkey does the same. The client recovers according to its reconnect
policy. Cached L1 and output responses do not prove Redis is up; use `/health` and the distributed
round trip.

## Owned-resource cleanup

From the same AppHost directory:

```powershell
aspire stop --non-interactive
```

This stops the AppHost, service, dashboard, and this session's Redis and Valkey containers (no
persistent volumes, so cache data is discarded). Other AppHosts and containers are untouched. If
startup fails, check `aspire logs api --non-interactive` and `aspire describe --non-interactive`,
then stop the sample before rebuilding.

For other hosting models, see the
[ASP.NET Core sample](https://github.com/thomhurst/Respire/tree/main/samples/Respire.Samples.AspNetCore)
and the [Cluster and Sentinel samples](https://github.com/thomhurst/Respire/tree/main/samples).
