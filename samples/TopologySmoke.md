# Automated Cluster and Sentinel smoke checks

The **Topology sample smoke** workflow runs the Cluster and Sentinel samples on
.NET 8 and .NET 10 against their Docker Compose projects. It runs on request and
on pull requests that change these samples, the controller, or the workflow.

Start it from GitHub Actions (**Topology sample smoke** > **Run workflow**) or with
GitHub CLI:

```sh
gh workflow run topology-sample-smoke.yml
gh run list --workflow topology-sample-smoke.yml
gh run view RUN_ID
gh run download RUN_ID --dir topology-smoke-results
```

Each of the four jobs uploads a `topology-sample-<sample>-<framework>` artifact
with build, Compose, Redis command, and server logs, plus `sample.log`. Sentinel
also writes `promotion.json` with the old and new primary ports; a null new port
means no promotion was seen. Read the failing step and logs even if some sample
operations succeeded.

## Run the same checks locally

Requirements:

- Linux (the controller refuses Windows and macOS; the samples themselves run anywhere)
- Python 3.10+, PowerShell 7, the SDK from `global.json`, and both target runtimes
- A local Docker engine with Compose v2 and Linux containers
- Free ports: Cluster 7000–7002, Sentinel 7100–7101 and 27100–27102

Run the commands one at a time from the repository root; they share build output
and host ports:

```sh
python3 scripts/smoke_topology_samples.py --sample Cluster --framework net8.0 --artifacts artifacts/cluster-net8
python3 scripts/smoke_topology_samples.py --sample Cluster --framework net10.0 --artifacts artifacts/cluster-net10
python3 scripts/smoke_topology_samples.py --sample Sentinel --framework net8.0 --artifacts artifacts/sentinel-net8
python3 scripts/smoke_topology_samples.py --sample Sentinel --framework net10.0 --artifacts artifacts/sentinel-net10
python3 -m unittest discover -s scripts -p test_topology_sample_smoke.py -v
```

A passing run ends with `PASS: <sample> <framework> smoke`, for example
`PASS: Cluster net10.0 smoke`. Logs go to the `--artifacts` directory.

## What passes

- **Cluster** must discover three shards and complete one round trip in each slot range.
- **Sentinel** runs one 60-iteration process. It needs success on the first
  primary, a requested promotion, then success on the new primary, and normal
  completion. A success count alone cannot pass. Failed writes are logged, not
  replayed; lossless failover is not promised.

The controller sets each sample's connection string, so an inherited
`RESPIRE_CONNECTION` cannot redirect it.

| Step | Time limit |
| --- | --- |
| .NET build and run | Repository guard defaults (600 seconds, 2048 MB) |
| Compose build | 180 seconds |
| Compose startup | 60-second readiness, 90 seconds overall |
| Sample run | 100 seconds |
| Sentinel promotion polling | 30 seconds, plus up to 15 seconds for a final inspection |
| Each Redis inspection | 15 seconds |

## Cleanup

Each run uses a fresh Compose project name. `--project` overrides it, but the
controller refuses a name that already has containers (even stopped), networks,
volumes, or a default build image. Cleanup removes only that project's
containers, networks, volumes, and locally built images, and never touches other
projects or shared Redis.

If Docker is unavailable during cleanup, the run fails and keeps
`owned-project.txt` and the cleanup logs. When Docker recovers, run the matching
sample Compose file for the recorded project with
`down --volumes --remove-orphans --rmi local --timeout 10`.
