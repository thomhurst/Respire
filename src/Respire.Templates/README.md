# Respire.Templates

Standalone .NET 10 project templates using Respire and Redis.

```sh
dotnet new install Respire.Templates
dotnet new respire-api -n MyCacheApi
cd MyCacheApi
docker compose up -d
dotnet run
```

The `respire-api` template includes a cache-aside endpoint, shared DI client,
readiness check, and optional OpenTelemetry OTLP export. See the generated README
for endpoints, configuration, and cleanup. Requires the .NET 10 SDK and Docker
(or an existing Redis 7+ server).

For a release candidate, install `Respire.Templates@<exact-version>` from the
GitHub release. Generated Respire package references match the template version.

[Getting started](https://respire.tomlonghurst.dev/docs/getting-started#project-template)
