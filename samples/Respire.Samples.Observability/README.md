# Prometheus dashboard smoke

From the repository root, with PowerShell 7, Docker, and the repository .NET SDK installed:

```powershell
pwsh samples/Respire.Samples.Observability/Smoke.ps1 -OutputDirectory ./observability-evidence
pwsh samples/Respire.Samples.Observability/Smoke.ps1 -Framework net8.0 -OutputDirectory ./observability-evidence-net8
```

Agents must choose an output directory outside the worktree. The script uses
`scripts/Invoke-AgentDotNet.ps1` with unchanged resource limits. Each container is limited to
128 MiB and one CPU. It removes only its own uniquely named containers, never shared Redis.
The HTTP exporter binds an ephemeral loopback port and stops before returning. The workload
has a 45-second cancellation deadline. An internet connection is needed for uncached inputs.

Pins:

- Redis `8.2.1`, digest `sha256:5fa2edb1e408fa8235e6db8fab01d1afaaae96c9403ba67b70feceb8661e8621`.
- OpenTelemetry SDK/hosting `1.19.1`, Prometheus ASP.NET Core exporter `1.19.1-beta.1`.
- Prometheus/promtool `3.5.0`, digest `sha256:63805ebb8d2b3920190daf1cb14a60871b16fd38bed42b857a3182bc621f4996`.
- [Published Redis dashboard revision `033fe86e47440da7f365ed2d8dd7f5d6217a575a`](https://github.com/redis-developer/redis-client-observability/blob/033fe86e47440da7f365ed2d8dd7f5d6217a575a/grafana/dashboards/redis-client-observability.json),
  SHA-256 `533cf0ff7d37f985d3fc4c3bb093ab42c9d63d4c53cf26430c82266832819582`.

The downloaded dashboard must match its checksum. Packages are pinned in `Directory.Packages.props`
and the SDK in `global.json`.

The same actual workload runs with default groups and all groups: `SET`/`GET` round trips,
cache hit/miss/capacity eviction, Redis `WRONGTYPE`, acknowledged publication and subscription
receipt, `XADD`/`XREAD`, and explicit application `RecordProcessingStart()`. A blocking list pop
holds a dedicated socket until a scrape proves pending replies and used state; `RPUSH` releases it.
The same exact dedicated pool must then have a positive idle count and no positive used count;
an idle shared socket cannot satisfy this check. Disposal produces actual physical closes.
No synthetic metrics are emitted.

Unsupported families must remain absent from every scrape. Feature-event families must have no
positive samples; a zero-valued relaxed-timeout gauge is expected. Five corrupted scrape copies
prove that newly exported unsupported families and positive feature events fail classification.
The `--verify-only <output> <dashboard> <dedicated-pool-label>` executable mode replays saved scrapes
without starting Redis or the HTTP exporter.

Expected output includes three `PASS` lines. Evidence contains:

- `default.prom`: connection count/create time and errors, with optional families absent.
- `busy.prom`: positive pending replies and used state.
- `optional.prom`: seconds bucket/sum/count histograms; positive cache hit/miss/evictions,
  incoming/outgoing messages, stream lag, and returned idle sockets.
- `closed.prom`: positive application-close counter. Values/timings and ephemeral pool ports vary.
- `dashboard-pinned.json`, `dashboard-adapted.json`, `dashboard-report.json`: all 47 original/adapted
  query targets and dispositions (42 verified, 3 require feature events, 2 unsupported).
- `dashboard-promql-tests.yml`: every distinct supported adapted query evaluated by promtool
  against actual exported series. Constant repeated samples check query/label compatibility,
  not throughput, error rates, or latency performance. Positive exact-pool/service and missing-value
  controls check selector discrimination. The five exception queries parse/evaluate against empty
  inputs, so all 47 targets receive syntax coverage. Missing features receive no fake series.
- `negative-*`: corrupted scrape copies and expected verification failures for classification guards.

See the [observability guide](../../website/docs/integrations/observability.md#prometheus-and-the-published-redis-dashboard)
for adaptations, optional panels, unavailable measurements, and query scope limits.
