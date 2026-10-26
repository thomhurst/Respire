# RespireApi

A .NET 10 minimal API with Respire cache-aside, dependency injection, Redis readiness
checks, and optional OpenTelemetry export. Requires the .NET 10 SDK and Redis 7+.

```sh
docker compose up -d --wait
dotnet run
curl http://localhost:5080/health/ready
curl http://localhost:5080/values/hello
curl http://localhost:5080/values/hello
```

Both value requests return the same generated value for five minutes. Replace the
GUID factory in `Program.cs` with an asynchronous database or service lookup.
The factory may run more than once across application instances; keep it free of
side effects. The client coalesces concurrent misses within this process and
tracks Redis invalidations for its local cache. All endpoints share one DI client,
which is disposed asynchronously when the app exits.

Use `ConnectionStrings__Redis` to override the Redis URL, or edit `appsettings.json`.
Keep credentials in environment variables or a secret store. To use a different
HTTP port: `dotnet run --no-launch-profile -- --urls http://localhost:5081`.
An existing Redis server can replace the local container entirely.

To export Respire traces and metrics, set `OTEL_EXPORTER_OTLP_ENDPOINT` to your
collector URL before starting the app. Without it, no exporter is configured.
Use the standard OpenTelemetry environment variables for exporter authentication
and protocol settings. The Redis client connects in the background when the app
starts and retries until Redis answers. Startup does not wait for Redis;
`/health/ready` reports unhealthy until the connection is established.

Stop the application with Ctrl+C, then remove this project's Redis container:

```sh
docker compose down
```

The local Redis container has no persistent volume. Its cached data is discarded
when removed. This starter exposes anonymous example endpoints for local use;
add the authentication and deployment configuration your application requires.
