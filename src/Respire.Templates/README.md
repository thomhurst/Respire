# Respire.Templates

Standalone .NET 10 project templates using Respire and Redis.

Choose the exact template version from its
[GitHub release](https://github.com/thomhurst/Respire/releases), including the
`-rc.N` suffix for a release candidate. Replace `<exact-version>` below before running
the command. Generated Respire package references match the template version.

```sh
dotnet new install Respire.Templates@<exact-version>
dotnet new respire-api -n MyCacheApi
cd MyCacheApi
docker compose up -d
dotnet run
```

The `respire-api` template includes a cache-aside endpoint, shared DI client,
readiness check, and optional OpenTelemetry OTLP export. See the generated README
for endpoints, configuration, and cleanup. Requires the .NET 10 SDK and Docker
(or an existing Redis 7+ server).

For an Aspire AppHost and minimal API with Redis, health checks and dashboard telemetry:

> **Package availability:** As of October 26, 2026, `Respire.Aspire` is not yet
> published on NuGet. The Aspire starter cannot restore from NuGet alone until a
> release contains both this template and the matching `Respire.Aspire` version.
> To try the integration now, use the
> [repository sample](https://respire.tomlonghurst.dev/docs/integrations/aspire-sample),
> which uses project references.

```sh
dotnet new respire-aspire -n MyRedisApp
cd MyRedisApp
dotnet build MyRedisApp.slnx
cd MyRedisApp.AppHost
aspire start --non-interactive
```

The `respire-aspire` starter requires .NET SDK 10.0.401+, Aspire CLI 13.6+, a running
Docker-compatible runtime, and a trusted development certificate
(`dotnet dev-certs https --trust`). Aspire supplies Redis credentials and dynamic
endpoints. Use `aspire describe --non-interactive` to find the API URL and
`aspire stop --non-interactive` from the AppHost directory to stop its resources.
See the generated README for requests, telemetry and cleanup.

Use `dotnet new respire-worker -n MyWorker` for a typed job queue, hosted sample
handler, enqueue API and authorized operator dashboard. Set `Dashboard__Password`
before running it, then sign in at `/workers/` as `operator`. Its README covers
enqueueing, processing, authentication, health checks, telemetry and Redis cleanup.

[Getting started](https://respire.tomlonghurst.dev/docs/getting-started#project-template)
