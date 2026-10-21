---
title: HTTP idempotency
description: Replay bounded ASP.NET Core responses for retries with the same Idempotency-Key.
---

# HTTP idempotency

`Respire.Idempotency` adds opt-in ASP.NET Core middleware on .NET 8 and .NET 10.
It uses [Redis idempotency claims](../guides/coordination.md#idempotency-claims-and-result-replay)
to run one matching request while its claim is live and replay its stored response on retries.
Redis 7+ and RESP2/RESP3 are supported.

## Minimal API setup

```bash
dotnet add package Respire.Idempotency
dotnet add package Respire.DependencyInjection
```

The example uses cookie authentication. Configure your application's authentication and issue
trusted `tenant_id` and name-identifier claims before calling the endpoint.

```csharp
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Respire.DependencyInjection;
using Respire.Idempotency;

var builder = WebApplication.CreateBuilder();
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie();
builder.Services.AddAuthorization();
builder.Services.AddRespire("redis://localhost:6379");
builder.Services.AddRespireIdempotency(options =>
{
    options.KeyPrefix = "orders-api:idempotency:v1:";
    options.TimeToLive = TimeSpan.FromHours(24);
    options.VerifyRequestBody = true;
    options.MaxResponseBodyBytes = 256 * 1024;
    options.CallerScope = context =>
    {
        var tenant = context.User.FindFirst("tenant_id")?.Value;
        var subject = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return tenant is null || subject is null ? null
            : $"{tenant.Length}:{tenant}{subject.Length}:{subject}";
    };
});

var app = builder.Build();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.UseRespireIdempotency();
app.MapPost("/orders", () =>
{
    var orderId = Guid.NewGuid();
    return Results.Created($"/orders/{orderId}", new { Id = orderId });
}).RequireAuthorization().WithRespireIdempotency("create-order-v1");
app.Run();
```

Send a stable `Idempotency-Key` for retries of one operation. Unmarked endpoints and requests
without that header execute normally. A present header must contain exactly one nonblank
value of at most 256 characters; invalid keys receive **400** before the handler executes.

Keep routing, authentication, and authorization before this middleware, so every replay is
authorized. Place exception handling outside (before) it. Place response compression inside
(after) `UseRespireIdempotency`, so the stored bytes include compression and match the stored
`Content-Encoding`. Retries must preserve `Accept-Encoding`, as they replay the first
representation without renegotiating compression. Do not also apply output caching to
these endpoints. All application instances must share the client
database, key prefix, endpoint names, and options. The middleware borrows the registered
`IRespireClient`; register and manage its lifetime with the usual DI integration.

## Caller and endpoint isolation

`CallerScope` is required. Return a stable identity from trusted authentication data, including
both tenant and caller when results are private to the caller. A missing scope returns **400**.
Never use an unauthenticated caller-supplied tenant header as the authority for access. Include
the identity issuer if your application accepts multiple issuers. Length-prefixing components,
as above, prevents ambiguous concatenation. An explicitly shared scope is appropriate only
when every caller in that scope is allowed to see every stored response.

The Redis key hashes the endpoint name, caller scope, HTTP method, host, path base, concrete
path, raw query string, and idempotency key with unambiguous component boundaries. Different
route values and query strings therefore have separate claims. Each hashed component must be
well-formed UTF-16: an unpaired surrogate returns **400** before body fingerprinting, Redis
access, or endpoint execution. This includes values supplied by trusted scope providers and
endpoint names. Valid replacement characters (U+FFFD) and surrogate pairs remain accepted;
existing Redis keys for well-formed identities are unchanged. Set an application-specific
`KeyPrefix`; it must be nonblank and well-formed UTF-16, otherwise startup validation fails.
Change the prefix or endpoint name when a deployment changes response semantics.
Treat it as reserved storage. Options are startup configuration, not mutable per-request state.

## Outcomes and retention

| Request state | Result |
| --- | --- |
| No live claim | Acquire ownership and run the endpoint |
| Matching request still executing | **409**, without running the endpoint |
| Completed matching request | Replay stored status, safe headers, and exact binary body |
| Different body with verification enabled | **422**, while pending or completed |
| Fingerprinted body exceeds the limit | **413**, before acquiring a claim |

`TimeToLive` defaults to 24 hours and must be at least 1 ms. The pending claim expires after
that duration; successful completion starts the same duration again for the result. Reading
a claim or result does not extend it. Choose a duration longer than handler execution and
the intended retry window. A crashed request becomes executable again after expiry.

`StoredStatusCodes` defaults to 200–299. Add or remove codes at registration, for example
`options.StoredStatusCodes.Add(422)` to retain a business validation response. Only final
codes from 200 through 599 are accepted. A nonselected status abandons the claim after the
response, so a later request can execute again.

With `VerifyRequestBody = true`, the middleware hashes the raw body with SHA-256 and restores
its stream position before calling the handler. `MaxRequestBodyBytes` defaults to 1 MiB.
Fingerprint buffering may spill to ASP.NET Core's temporary-file storage. Content-Length and
chunked requests are both bounded. Verification compares bytes, not semantic JSON equality.
Other request headers such as Accept are not fingerprinted: retries with the same scope/key
receive the first representation. Include any additional representation partition in your
caller scope if needed, and preserve operation-relevant headers across retries.

## Response buffering and headers

Only opt in endpoints that produce a finite, bounded response. Output and flushes are delayed
until the handler finishes. `Body`, `BodyWriter`, and file responses go through the buffer.
`MaxResponseBodyBytes` defaults to 1 MiB; both body limits accept 1 byte through 16 MiB.
There are transient copies for serialization and Redis transmission, so budget memory for
concurrent requests as well as the configured body limit.

An oversized response produces an empty **500**, abandons its claim, and stores no partial
result. The handler may already have performed side effects. This is a configuration error;
choose a suitable limit before enabling the endpoint. Explicit `DisableBuffering` throws
`InvalidOperationException`. Do not enable this middleware for SSE, streaming downloads,
WebSockets, or gRPC. Response trailers are not replayed.

The stored header allowlist is `Content-Type`, `Content-Language`, `Content-Encoding`,
`Location`, `ETag`, and `Last-Modified`. Headers nominated by `Connection` are excluded.
Cookies, authentication challenges, connection-specific headers, and arbitrary application
headers are not stored. Replay computes Content-Length from the body, except for HEAD,
which preserves the original representation's declared Content-Length. Safe-header storage
is capped at 16 KiB; a response exceeding that cap is delivered but its claim is abandoned.
OnStarting callbacks run before the first response's status and headers are captured.

## Failure and ownership behavior

Handler exceptions, cancellation, nonstored statuses, and buffer-limit failures use the
original ownership token to abandon the claim. Cleanup has a separate five-second deadline,
so request cancellation does not skip it. A failed cleanup is logged and the claim expires
at its TTL. Completion and abandonment cannot overwrite or remove a newer owner's claim.
An expired owner still receives its own response; it cannot publish that response for replay.

Claim acquisition uses a separate five-second deadline, so a client disconnect does not
cancel the wait for the ownership token. Once acquired, an aborted request abandons its
claim without executing the endpoint. Redis acquisition failures propagate without executing
the endpoint; a timeout or lost reply can still leave a claim until its TTL expires.
Completion failures are logged and the buffered response is still delivered, since its
headers have already started. Cleanup abandons a pending claim, allowing another execution
on retry; if Redis completed the result before its reply was lost, cleanup preserves it.
A completed result is retained even if delivery to the original caller fails.

This does not provide an exactly-once transaction with external effects. Expiry, eviction,
Redis data loss, cancellation, or an abandoned response can permit another execution. Use
application transactions or downstream idempotency for effects that must survive these cases.
