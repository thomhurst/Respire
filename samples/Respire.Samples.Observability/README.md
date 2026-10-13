# Prometheus dashboard smoke

Exports Respire metrics through the OpenTelemetry Prometheus exporter and checks
the scrapes against the published Redis client Grafana dashboard. `Smoke.ps1`
starts its own Redis container.

From the repository root, with PowerShell 7, Docker, and the repository .NET SDK:

```powershell
pwsh samples/Respire.Samples.Observability/Smoke.ps1 -OutputDirectory ./observability-evidence
pwsh samples/Respire.Samples.Observability/Smoke.ps1 -Framework net8.0 -OutputDirectory ./observability-evidence-net8
```

| Parameter | Default |
| --- | --- |
| `-Framework` | `net10.0` |
| `-OutputDirectory` | A new temp folder, printed as `Evidence: <path>` |

- Agents must choose an output directory outside the worktree.
- The script runs through `scripts/Invoke-AgentDotNet.ps1`. Each container gets
  128 MiB and one CPU, and the script removes only its own containers.
- The workload has a 45-second deadline. Uncached inputs need internet access.

Pins (packages are in `Directory.Packages.props`, the SDK in `global.json`):

- Redis `8.2.1`, digest `sha256:5fa2edb1e408fa8235e6db8fab01d1afaaae96c9403ba67b70feceb8661e8621`.
- OpenTelemetry SDK/hosting `1.19.1`, Prometheus ASP.NET Core exporter `1.19.1-beta.1`.
- Prometheus/promtool `3.5.0`, digest `sha256:63805ebb8d2b3920190daf1cb14a60871b16fd38bed42b857a3182bc621f4996`.
- [Published Redis dashboard revision `033fe86e47440da7f365ed2d8dd7f5d6217a575a`](https://github.com/redis-developer/redis-client-observability/blob/033fe86e47440da7f365ed2d8dd7f5d6217a575a/grafana/dashboards/redis-client-observability.json),
  SHA-256 `533cf0ff7d37f985d3fc4c3bb093ab42c9d63d4c53cf26430c82266832819582`. The download must match.

The workload runs once with default metric groups and once with all groups. It
covers `SET`/`GET`, cache hits, misses, and evictions, a `WRONGTYPE` error,
pub/sub, `XADD`/`XREAD`, `RecordProcessingStart()`, and a blocking list pop on
the dedicated pool. No synthetic metrics are emitted. Unsupported metric families
must stay absent, and corrupted scrape copies prove the checks catch them.
`--verify-only <output> <dashboard> <dedicated-pool-label>` replays saved scrapes
without Redis.

Expected output includes three `PASS` lines. The evidence folder contains:

| File | Contents |
| --- | --- |
| `default.prom` | Default groups: connection count, create time, errors |
| `busy.prom` | Pending replies and used connections during the blocking pop |
| `optional.prom` | All groups: histograms, cache, pub/sub, stream lag, idle sockets |
| `closed.prom` | Application-close counter |
| `dashboard-pinned.json`, `dashboard-adapted.json`, `dashboard-report.json` | All 47 dashboard queries and their status (42 verified, 3 need feature events, 2 unsupported) |
| `dashboard-promql-tests.yml` | promtool tests of each supported query against exported series (checks compatibility, not performance) |
| `negative-*` | Corrupted scrapes and their expected failures |

See the [observability guide](../../website/docs/integrations/observability.md#prometheus-and-the-published-redis-dashboard)
for dashboard adaptations and unavailable measurements.
