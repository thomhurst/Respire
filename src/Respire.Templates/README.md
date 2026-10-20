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

[Getting started](https://respire.tomlonghurst.dev/docs/getting-started#project-template)
