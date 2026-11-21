---
title: Redis-compatible servers
description: Using Respire with Garnet, Dragonfly, KeyDB, Valkey and KiviDB, and the known differences between them.
---

# Redis-compatible servers

Respire speaks standard RESP2 and RESP3, so it works with Redis-compatible servers as well as Redis
itself. The table below shows what works on each server version that Respire is checked against
every week. Other versions usually behave the same, but compatibility depends on the server.

## Compatibility matrix

| Feature | Garnet 2.2.12 | Dragonfly 2.0.0 | KeyDB 6.3.4 | Valkey 9.2 RC1 | KiviDB 1.1.2 (Lua) |
| --- | --- | --- | --- | --- | --- |
| RESP2 connection | Yes | Yes | Yes | Yes | Yes |
| RESP3 and automatic negotiation | Yes | Yes | Yes | Yes | Yes |
| Strings, hashes, lists, sets, sorted sets | Yes | Yes | Yes | Yes | Yes |
| `CLIENT SETINFO` metadata | Yes | Yes | No | Yes | Yes |
| Pub/sub | Yes | Yes | Yes | Yes | Yes |
| Lua `EVAL` | Yes, with `--lua --lua-transaction-mode` | Yes | Yes | Yes | Yes |
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
  - Start Garnet with `--lua --lua-transaction-mode` to use scripting. Without
    `--lua-transaction-mode`, Garnet does not run a script atomically: two scripts on the same keys
    can interleave. Respire's locks and cache-aside leases rely on atomic scripts; without the flag,
    for example, a `GetOrSetAsync` factory can run twice for one expiry.
  - With `--lua-transaction-mode`, Garnet 2.2.12 never replies to a transaction that runs a script
    with keys, and those keys stay locked for every connection. Do not queue scripts that take keys
    on a transaction against Garnet.
  - Garnet 2.2.12 does not support `CLIENT TRACKING`, so leave `ClientSideCache` disabled.
  - Garnet 2.2.12 has no native compare-and-delete or `SET ... IFEQ`, so `client.Locks` uses its Lua
    scripts there. Start Garnet with `--lua --lua-transaction-mode` to use locks.
  - Other gaps in 2.2.12: streams, `SORT`, `COPY`, `FUNCTION`, `SET` with `EXAT` or `PXAT`,
    `RESTORE` options, sharded pub/sub outside Cluster mode, and expiry times after the year 9999,
    such as a lock held for `TimeSpan.MaxValue`.
  - On RESP3, Garnet 2.2.12 closes the connection when `ZMPOP` pops a member whose score
    is infinite. Use RESP2 for multi-key sorted-set pops that can return such scores.
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
