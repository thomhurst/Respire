# RespireWorker

A standalone .NET 10 application with a typed Respire job queue, hosted sample
handler, enqueue API, operator dashboard, Redis health check and OpenTelemetry.
Requires the .NET 10 SDK and Redis 7+ (Docker Compose supplies local Redis).

Start Redis, then choose a local operator password in your current shell:

```sh
docker compose up -d --wait
export Dashboard__Password='replace-with-your-own-password'
dotnet run
```

In PowerShell, use `$env:Dashboard__Password = 'replace-with-your-own-password'`
instead of `export`. No password is stored in the generated files; startup fails
when it is missing. The launch profile selects Development and localhost:5080.

Open `http://localhost:5080/workers/` in your browser and sign in as `operator`
using your password. The dashboard uses ASP.NET Core authentication and the named
`QueueOperators` policy, including its role requirement. Its POST actions retain
the dashboard's antiforgery checks. Anonymous users cannot view or mutate queues.

Enqueue a job from another shell (curl prompts for the same password):

```sh
curl -u operator -H 'Content-Type: application/json' -d '{"message":"hello worker"}' http://localhost:5080/jobs/welcome-1
curl -u operator http://localhost:5080/jobs/welcome-1
curl http://localhost:5080/health/ready
```

On Windows use `curl.exe` if your shell aliases `curl`. POST returns HTTP 202 with
the stream entry ID and an `enqueued` flag. The handler uppercases the message,
stores the result in Redis for one hour, then acknowledges the delivery. GET may
return 404 until that result exists, and returns `HELLO WORKER` afterward. It also
returns 404 when the result expires. Repeating the POST with the same application
ID within the default one-day deduplication window does not enqueue another job;
the first payload wins. The dashboard shows retained entries, pending deliveries,
dead letters and one hour of completion metrics. Refresh after processing.

Replace `SampleJobHandler` with your operation. Deliveries can repeat after a
failure: keep side effects idempotent using `job.Id`. The sample's deterministic
overwrite tolerates redelivery. Returning `Ack` acknowledges successful work;
exceptions are retried according to the queue's policy, with terminal failures
sent to the dead-letter stream. Source and event streams retain entries; choose
an application-specific trimming/retention policy before sustained use.

Use `ConnectionStrings__Redis` to override `appsettings.json`, including when
using an existing server instead of Docker. The host can start while Redis is
unavailable. Worker initialization retries connection failures and timeouts with
delays starting at one second and doubling up to 30 seconds, until Redis answers
or the host stops. Each failure logs a warning; invalid configuration and
authentication errors still fail startup. Exhausted reconnect limits also fail
startup because the client must be recreated. `/health/ready` reports unhealthy until
Redis is connected; it does not assert that every job has finished. The queue name
and Redis result key prefix follow the generated project name; instances with the
same name share work.

Set `OTEL_EXPORTER_OTLP_ENDPOINT` to export both `Respire` and `Respire.Streaming`
traces and metrics. Without it, no exporter is enabled. Standard OpenTelemetry
environment variables configure collector credentials and protocol.

The starter uses HTTP Basic authentication for its operator account. It allows
unencrypted credentials only over loopback in Development. Outside Development,
authenticated endpoints require HTTPS. Before deployment, replace
`OperatorAuthenticationHandler` with your identity provider, retain the
`QueueOperators` policy and dashboard antiforgery protection, and configure HTTPS
and your deployment's trusted proxies. Never commit operator or Redis credentials.
Do not enable Development on an exposed deployment. The enqueue API also requires
the operator policy; replace it with your application's producer authorization
when integrating clients.

Forwarded headers are not enabled by this starter. Behind a TLS-terminating
proxy, the application sees HTTP and returns 403 for authenticated endpoints,
even when the browser used HTTPS. Configure ASP.NET Core Forwarded Headers
Middleware for your deployment's known proxies or networks, and call
`app.UseForwardedHeaders()` before authentication so that `Request.IsHttps`
reflects the original connection. Never accept forwarded headers from arbitrary
clients: a forged `X-Forwarded-Proto` could bypass the HTTPS requirement.

For another HTTP port, run
`dotnet run --no-launch-profile -- --urls http://localhost:5081` and explicitly
set `ASPNETCORE_ENVIRONMENT=Development` for the local HTTP authentication exception.
Stop the app with Ctrl+C, then remove this project's Redis container:

```sh
docker compose down
```

The local container has no persistent volume: removing it discards all queued
jobs and results. Use persistent Redis storage for real workloads.
