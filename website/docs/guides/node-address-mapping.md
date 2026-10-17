---
title: Node address mapping
---

# Node address mapping

Redis Cluster and Sentinel advertise addresses that may be unreachable from outside Docker
or Kubernetes. Use `RespireOptions.NodeAddressMap` to connect through published ports:

```csharp
await using var client = await RespireClient.ConnectAsync(new RespireOptions
{
    UseCluster = true,
    Endpoints = [new("localhost", 7001)],
    NodeAddressMap = new Dictionary<RespireEndpoint, RespireEndpoint>
    {
        ["10.0.0.5:6379"] = "localhost:7001",
        ["10.0.0.6:6379"] = "localhost:7002",
        ["10.0.0.7:6379"] = "localhost:7003",
    },
});
```

Mapping happens once per physical connection attempt, including reconnects. It covers
Cluster topology, `MOVED` and `ASK` redirects, Sentinel primary, replica and peer discovery,
explicit standalone replicas, subscriptions, blocking connections, and `MOVING` maintenance
handoffs. Configured seeds also pass through the map. Unlisted endpoints connect unchanged.
Unix sockets bypass mapping.

Respire keeps the original endpoint for routing, diagnostics, and credential providers.
Mapping changes only the socket destination. Map keys use exact host and port equality;
include the hostnames or IP addresses your servers actually advertise. IPv6 strings use
brackets, for example `[fd00::5]:6379`. Entries must contain valid TCP endpoints.
The client copies the map when connecting, so later dictionary changes do not affect it.

## Docker Compose

Publish a different host port for each node in your Compose cluster:

```yaml
services:
  redis-1:
    image: redis:7-alpine
    ports: ["7001:6379"]
    command: redis-server --cluster-enabled yes --cluster-config-file nodes.conf --cluster-announce-hostname redis-1 --cluster-preferred-endpoint-type hostname
  redis-2:
    image: redis:7-alpine
    ports: ["7002:6379"]
    command: redis-server --cluster-enabled yes --cluster-config-file nodes.conf --cluster-announce-hostname redis-2 --cluster-preferred-endpoint-type hostname
  redis-3:
    image: redis:7-alpine
    ports: ["7003:6379"]
    command: redis-server --cluster-enabled yes --cluster-config-file nodes.conf --cluster-announce-hostname redis-3 --cluster-preferred-endpoint-type hostname
```

After creating the cluster and assigning slots, map `redis-1:6379` to `localhost:7001`,
`redis-2:6379` to `localhost:7002`, and `redis-3:6379` to `localhost:7003`.

For Sentinel, configure `SentinelPrimaryName` and use Sentinel addresses as seeds.
Map the primary and replica addresses returned by Sentinel to their published data ports.
Map internal Sentinel peer addresses to their published Sentinel ports too.

## Kubernetes

Give each Redis node its own reachable destination. For local development, separate port
forwards can expose StatefulSet pods:

```sh
kubectl port-forward pod/redis-0 7001:6379
kubectl port-forward pod/redis-1 7002:6379
kubectl port-forward pod/redis-2 7003:6379
```

Map each advertised pod hostname, for example
`redis-0.redis-headless.default.svc.cluster.local:6379`, to its corresponding local port.
A single service that randomly chooses nodes cannot preserve Cluster node identity.
For production, use stable per-node routes reachable from the client.

## Dynamic resolution

Use `NodeAddressResolver` instead of `NodeAddressMap` for dynamic routes. The callback must
be thread-safe, return a valid TCP endpoint, and return the input for unchanged endpoints.
Callback failures fail that connection attempt. Resolution is synchronous; avoid blocking
network calls inside the callback.

```csharp
NodeAddressResolver = endpoint => endpoint.Host switch
{
    "redis-0.internal" => new("localhost", 7001),
    "redis-1.internal" => new("localhost", 7002),
    _ => endpoint,
},
```

Do not configure both forms. Mapping is not recursive: the result is the socket destination,
even if it also appears as another map key.

## TLS hostname

By default, TLS SNI and certificate hostname validation use the original hostname. Mapping
`redis.internal:6380` to `localhost:7001` still validates a certificate for `redis.internal`.
Set `UseMappedHostForTls = true` to use `localhost` instead. An explicit `TlsOptions.TargetHost`
(or `SentinelTlsOptions.TargetHost` for Sentinel) takes precedence in either mode.
Certificate validation retains the usual platform defaults.

## Connection strings

Both connection string forms accept `nodeAddressMap`. Separate entries with semicolons and
separate each source from its target with `=`:

```text
localhost:7001,cluster=true,nodeAddressMap=redis-1:6379=localhost:7001;redis-2:6379=localhost:7002
```

In a URI, percent-encode the map value:

```text
redis://localhost:7001?cluster=true&nodeAddressMap=redis-1%3A6379%3Dlocalhost%3A7001%3Bredis-2%3A6379%3Dlocalhost%3A7002
```

Both forms also accept `useMappedHostForTls=true`. A callback requires `RespireOptions`.
