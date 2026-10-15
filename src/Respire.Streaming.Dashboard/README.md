# Respire.Streaming.Dashboard

An ASP.NET Core dashboard for configured Redis stream workers and reliable job queues.
Inspect consumer groups, pending deliveries, dead letters, and the group delivery rate.
Retry or delete individual dead letters using authorized, antiforgery-protected forms.

## Install

```sh
dotnet add package Respire.Streaming.Dashboard
```

Register an `IRespireClient`, call `AddRespireStreamingDashboard` with an explicit list of
streams, and map `MapRespireStreamingDashboard("/workers", "QueueOperators")` with a named
authorization policy. Use your application's authentication scheme. There is no anonymous
dashboard mode or automatic Redis key discovery.

Requires ASP.NET Core on .NET 8 or .NET 10 and Redis 7 or later. Dead-letter actions use
the envelope written by `Respire.Streaming`. Source and dead-letter keys must resolve to
different keys in the same Redis Cluster slot, including on standalone servers.

[Read the setup guide](https://thomhurst.github.io/Respire/docs/guides/stream-dashboard)
for configuration, authorization, retry semantics, and operational limits.
