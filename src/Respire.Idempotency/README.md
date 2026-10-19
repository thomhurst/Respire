# Respire.Idempotency

Opt-in ASP.NET Core `Idempotency-Key` middleware backed by Redis ownership claims.
Supports .NET 8 and .NET 10, bounded binary response replay, caller/tenant isolation,
configurable retention/status codes, and optional request-body fingerprint verification.

Register `AddRespireIdempotency` with a trusted `CallerScope`, add `UseRespireIdempotency`
after routing and authorization, and mark finite-response endpoints with
`WithRespireIdempotency("endpoint-name")`. The registered `IRespireClient` is shared.

[Read the HTTP guide](https://respire.tomlonghurst.dev/docs/integrations/idempotency)
for a minimal API example, buffering limits, response headers, and failure behavior.
