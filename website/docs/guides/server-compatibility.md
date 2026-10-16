---
title: Redis-compatible servers
description: Focused Garnet, Dragonfly and KeyDB compatibility checks, known differences, and how to reproduce them.
---

# Redis-compatible servers

Respire uses RESP to connect to Redis-compatible servers. Compatibility depends on the server
version and the features your application uses. The focused compatibility suite exercises Garnet,
Dragonfly and KeyDB independently; it does not certify every Redis command or production topology.

## Tested scope

The suite covers connection negotiation, optional client metadata, core data types, pub/sub,
scripting, transactions, client-side caching and Cluster where supported. Known incompatibilities
have explicit skip reasons in the test report. A feature skipped by a test is not a passing feature.

| Server | Pinned container image | Test configuration |
| --- | --- | --- |
| Garnet 2.1.8 | `ghcr.io/microsoft/garnet:2.1.8` | Lua enabled with `--lua`. |
| Dragonfly 2.0.0 | `docker.dragonflydb.io/dragonflydb/dragonfly:v2.0.0` | One proactor thread; Cluster uses emulated mode. |
| KeyDB 6.3.4 | `eqalpha/keydb:x86_64_v6.3.4` | One server thread, persistence disabled; Linux x64. |

Other versions need their own compatibility run. ARM hosts need x64 emulation for the KeyDB image.

## Compatibility matrix

These results were observed on 15 October 2026 with the pinned images above and .NET 10.
**Pass** means the focused scenario passed. **Unsupported** means the pinned server rejected the
required command; that scenario has an explicit skip reason. **Partial** limits the passing
Cluster result to the topology described below.

| Feature | Garnet 2.1.8 | Dragonfly 2.0.0 | KeyDB 6.3.4 |
| --- | --- | --- | --- |
| RESP2 connection | Pass | Pass | Pass |
| RESP3 `HELLO` and automatic negotiation | Pass | Pass | Pass |
| Strings, hashes, lists, sets and sorted sets | Pass | Pass | Pass |
| Optional `CLIENT SETINFO` metadata | Pass | Pass | Unsupported |
| Pub/sub delivery | Pass | Pass | Pass |
| Lua `EVAL` | Pass, with Lua enabled | Pass | Pass |
| `MULTI`/`EXEC` transactions | Pass | Pass | Pass |
| RESP3 cache hit and tracking invalidation | Unsupported | Pass | Pass |
| Cluster discovery and routing | Partial: one primary | Partial: emulated Cluster | Partial: one primary |

The core data-type checks cover strings, hashes, lists, sets and sorted sets under RESP2 and RESP3,
plus automatic protocol negotiation. Other feature checks run under both explicit protocols,
except client-side caching, which uses RESP3. The caching scenario checks a local cache hit and
invalidation after another connection writes the same key.

Cluster coverage is limited to discovering one primary's slot table and routing keys in different
slots. Dragonfly uses its emulated Cluster mode. These checks do not establish multi-node
failover, slot migration, replication, Sentinel, authentication, TLS, or module compatibility.

### Known differences

- **Garnet client-side caching:** Garnet 2.1.8 rejects `CLIENT TRACKING` as an unknown
  subcommand. Leave `ClientSideCache` disabled for this version. The suite skips the caching
  scenario with that reason; ordinary connections and core commands pass.
- **KeyDB client metadata:** KeyDB 6.3.4 rejects `CLIENT SETINFO` as an unknown subcommand.
  This is an explicit [connection-specific administration API](client-administration.md),
  not an automatic handshake requirement. The suite verifies the rejection and then reads the
  name from the same physical connection before reporting the metadata scenario as skipped.
  RESP2, RESP3 and automatic connection negotiation all work without this optional command.
- **Cluster setup:** Garnet's fixture uses the same selected free port inside and outside Docker,
  with a loopback advertised address. Its pinned release requires the advertised port to match
  the listening port. Dragonfly's emulated Cluster fixture sets `cluster_announce_ip` and
  `announce_port` to the mapped endpoint. These are fixture configuration differences, not
  evidence of full multi-node compatibility.

The configuration choices follow the pinned
[Garnet endpoint validation](https://github.com/microsoft/garnet/blob/v2.1.8/libs/host/Configuration/Options.cs#L665-L679)
and [Dragonfly emulated endpoint settings](https://github.com/dragonflydb/dragonfly/blob/v2.0.0/src/server/cluster/cluster_family.cc#L116-L133).

## Run the compatibility suite

The [Server compatibility workflow](https://github.com/thomhurst/Respire/actions/workflows/server-compatibility.yml)
runs weekly on Tuesday, on manual dispatch, or when a pull request receives the
`run-server-compatibility` label. Ordinary pull request pushes do not start it. Remove and reapply
the label to test a later commit. Each server has its own job, so one failure does not cancel the others.
Artifacts contain the console output and TRX report, including skipped tests and their reasons.

```sh
gh pr edit <number> --add-label run-server-compatibility
gh workflow run server-compatibility.yml
```

To reproduce one server locally, run a Linux Docker engine and the .NET 10 SDK from the repository
root. The suite owns and removes its containers; it does not use a running application server.

```powershell
$env:RESPIRE_COMPATIBILITY_SERVER = 'garnet' # or dragonfly / keydb
& scripts/Invoke-AgentDotNet.ps1 -DotNetArguments @(
    'run', '--project', 'tests/Respire.IntegrationTests', '-c', 'Release', '-f', 'net10.0',
    '--', '--treenode-filter', '/*/*/ServerCompatibilityTests/*', '--report-trx'
)
Remove-Item Env:RESPIRE_COMPATIBILITY_SERVER
```

Without `RESPIRE_COMPATIBILITY_SERVER`, these tests skip before starting containers. This keeps
the extra server matrix out of the ordinary Redis/Valkey integration jobs.

Use the [connection protocol options](../fundamentals/connections.md#protocol-negotiation) to choose
RESP2, RESP3 or automatic negotiation. Server-specific commands are documented separately in
[Dragonfly and KeyDB extensions](server-extensions.md). The public
[container fixtures](testing-containers.md) support Redis and Valkey; the compatibility suite uses
its own fixtures for the additional servers.
