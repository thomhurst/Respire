---
title: Redis-compatible servers
description: Using Respire with Garnet, Dragonfly, KeyDB, Valkey and KiviDB, and the known differences between them.
---

# Redis-compatible servers

Respire speaks standard RESP2 and RESP3, so it works with Redis-compatible servers as well as Redis
itself. The table below shows what works on each server version that Respire is checked against
every week. Other versions usually behave the same, but compatibility depends on the server.

## Compatibility matrix

| Feature | Garnet 2.1.8 | Dragonfly 2.0.0 | KeyDB 6.3.4 | Valkey 9.2 RC1 | KiviDB 1.1.2 (Lua) |
| --- | --- | --- | --- | --- | --- |
| RESP2 connection | Yes | Yes | Yes | Yes | Yes |
| RESP3 and automatic negotiation | Yes | Yes | Yes | Yes | Yes |
| Strings, hashes, lists, sets, sorted sets | Yes | Yes | Yes | Yes | Yes |
| `CLIENT SETINFO` metadata | Yes | Yes | No | Yes | Yes |
| Pub/sub | Yes | Yes | Yes | Yes | Yes |
| Lua `EVAL` | Yes, with `--lua` | Yes | Yes | Yes | Yes |
| `MULTI`/`EXEC` transactions | Yes | Yes | Yes | Yes | Yes |
| Client-side caching | No | Yes | Yes | Yes | Yes |
| Cluster discovery and routing | Single primary | Emulated Cluster mode | Single primary | Single primary | Single primary |

Cluster results cover discovering the slot table and routing keys across slots on the configuration
shown. Multi-node failover, slot migration, replication, Sentinel, TLS, and modules have not been
checked on these servers; use Redis or Valkey for those.

Respire's full integration suite also runs against Valkey 9.2. See [Valkey 9.2 features](valkey-92.md)
for its new commands.

## Known differences

- **Garnet**
  - Start Garnet with `--lua` to use scripting.
  - Garnet 2.1.8 does not support `CLIENT TRACKING`, so leave `ClientSideCache` disabled.
  - `client.Locks` fails on Garnet 2.1.8: it rejects the conditional-delete commands the lock API uses.
  - Other gaps in 2.1.8: streams, `SORT`, `COPY`, `TOUCH`, `FUNCTION`, `SET` with `EXAT` or `PXAT`,
    `RESTORE` options, sharded pub/sub outside Cluster mode, and multi-key sorted-set pops.
  - Garnet allows at most 256 logical databases.
  - In Cluster mode, Garnet requires the advertised port to match the listening port.
- **Dragonfly:** for Cluster, use emulated Cluster mode and set `cluster_announce_ip` and
  `announce_port` to the endpoint clients connect to.
- **KeyDB:** KeyDB 6.3.4 rejects `CLIENT SETINFO`. Connections and protocol negotiation do not need it;
  only the explicit [client administration API](client-administration.md)
  call fails. The KeyDB image is x64 only; ARM hosts need emulation.
- **KiviDB:** use an image variant that includes what you need. `v1.1.2-lua` includes Lua but not TLS,
  the default image includes neither, and the `-tls` and `-full` variants add TLS. KiviDB listens on
  port 6380 by default.

## Related

- [Protocol negotiation](../fundamentals/connections.md#protocol-negotiation) to choose RESP2, RESP3 or automatic negotiation.
- [Dragonfly and KeyDB extensions](server-extensions.md) for server-specific commands.
- [Container fixtures](testing-containers.md) for Redis and Valkey test servers.
