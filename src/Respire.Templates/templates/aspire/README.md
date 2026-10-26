# Respire.Aspire-Starter

An Aspire AppHost runs a Redis container (`cache`) and a minimal API (`api`).
`WithReference(cache)` supplies the connection string and credentials;
`WaitFor(cache)` delays the API until Redis is healthy. `Respire.Aspire` registers
the shared client, Redis readiness check, and Respire OpenTelemetry traces and metrics.

## Requirements

- .NET 10 SDK (10.0.401 or later) and runtime.
- [Aspire CLI](https://aspire.dev/get-started/install-cli/) 13.6 or later. The
  AppHost uses Aspire SDK and hosting packages 13.6.1.
- A running Docker-compatible container runtime; the first start pulls Redis 8.10.
- A trusted development certificate: `dotnet dev-certs https --trust`.

## Build and start

This starter requires `Respire.Aspire` at the same version as the installed
`Respire.Templates` package. As of October 26, 2026, the integration is not yet
published on NuGet, so a generated application cannot restore from NuGet alone.
Use a release containing both packages when available. To try the integration
before publication, build the
[repository sample](https://respire.tomlonghurst.dev/docs/integrations/aspire-sample),
which uses project references.

From this generated directory:

```powershell
dotnet build Respire.Aspire-Starter.slnx
cd Respire.Aspire-Starter.AppHost
aspire start --non-interactive
aspire wait cache --non-interactive --timeout 120
aspire wait api --non-interactive --timeout 120
aspire describe --non-interactive
```

Add `--isolated` to `aspire start` when running multiple copies. Use the dashboard
login URL printed by Aspire and the API's HTTP URL from the dashboard or `describe`;
ports are assigned dynamically. Keep the dashboard login token private.

The Redis client connects in the background when the API starts, so `/health/ready`
turns healthy without waiting for a request. `/health/ready` checks Redis; `/health/live` checks only that the API
can respond. Aspire also uses `/health/ready` for the API resource health state.

## Store and read a value

In another PowerShell terminal, set the URL to the API's HTTP endpoint:

```powershell
$baseUrl = Read-Host 'Paste the api HTTP URL without a trailing slash'
Invoke-WebRequest "$baseUrl/health/ready"
Invoke-RestMethod "$baseUrl/values/hello" -Method Put -ContentType application/json -Body '"hello Redis"'
Invoke-RestMethod "$baseUrl/values/hello"
```

The read returns `key` and `value`. Writes expire after five minutes and use the
`Respire.Aspire-Starter:value:` key prefix. A missing key returns HTTP 404. Replace these
example routes with your application behavior before exposing the service publicly.

## Telemetry and configuration

Select `api` in the dashboard to see structured logs, `Respire` traces and metrics.
The AppHost injects the OTLP collector configuration; export is enabled when
`OTEL_EXPORTER_OTLP_ENDPOINT` is set. Health checks and telemetry are registered by
`AddRespireClient("cache")`; no connection string or password is hardcoded.
See the [Aspire integration guide](https://respire.tomlonghurst.dev/docs/integrations/aspire)
for `Aspire:Respire` settings and client customization.

## Cleanup

From the AppHost directory:

```powershell
aspire stop --non-interactive
```

This stops this AppHost, API, dashboard and Redis container. Redis has no persistent
volume, so stopping discards its data. Other applications and containers are untouched.
If startup fails, inspect `aspire logs api --non-interactive` and
`aspire describe --non-interactive`, then stop this AppHost before rebuilding.

All generated Respire package references use the installed template release version.
Update Aspire SDK and hosting packages together when upgrading Aspire.
