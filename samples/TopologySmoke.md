# Automated Cluster and Sentinel smoke checks

The **Topology sample smoke** workflow runs the actual Cluster and Sentinel sample
applications on .NET 8 and .NET 10 against their Docker Compose projects. It is
separate from the ordinary sample build checks. It runs on request and on pull
requests that change these samples, the controller, or this workflow.

From GitHub Actions, select **Topology sample smoke**, then **Run workflow**. With
GitHub CLI, run:

```sh
gh workflow run topology-sample-smoke.yml
gh run list --workflow topology-sample-smoke.yml
gh run view RUN_ID
gh run download RUN_ID --dir topology-smoke-results
```

Each of the four jobs uploads a `topology-sample-<sample>-<framework>` artifact,
including build output, Compose startup and cleanup output, Redis commands,
server logs, and `sample.log`. Sentinel also records the old and new primary
ports in `promotion.json`, including failed runs: the initial port is saved before
requesting promotion, and the new port is saved immediately when observed. A null
new port means promotion was not observed. Polling overwrites `redis-latest.log`
instead of creating one file per inspection. Failed commands retain their output. Read the failing
step and logs even if some sample operations succeeded.

## Run the same checks locally

On Linux, install Python 3.10 or later, PowerShell 7, the SDK selected by `global.json`,
both target runtimes, and Docker Compose v2 with Linux containers. Use a local
Docker engine. The samples advertise loopback addresses and require their
documented ports: Cluster 7000–7002, Sentinel 7100–7101 and 27100–27102.
Run the commands sequentially: they share build outputs, and two copies of the
same topology cannot bind the same host ports. CI jobs have separate workspaces
and Docker hosts. The controller rejects Windows and macOS before starting work;
its process-group cleanup is tested on Linux. The sample applications themselves
can still be run directly on other supported .NET platforms.

From the repository root:

```sh
python scripts/smoke_topology_samples.py --sample Cluster --framework net8.0 --artifacts artifacts/cluster-net8
python scripts/smoke_topology_samples.py --sample Cluster --framework net10.0 --artifacts artifacts/cluster-net10
python scripts/smoke_topology_samples.py --sample Sentinel --framework net8.0 --artifacts artifacts/sentinel-net8
python scripts/smoke_topology_samples.py --sample Sentinel --framework net10.0 --artifacts artifacts/sentinel-net10
python -m unittest discover -s scripts -p test_topology_sample_smoke.py -v
```

The controller supplies each sample's local connection string, so an inherited
`RESPIRE_CONNECTION` cannot redirect the smoke test to another deployment.
It builds and runs .NET through the repository guard without raising its
600-second/2048-MB defaults. Compose build has a 180-second bound, startup has a
60-second readiness bound and a 90-second outer bound, and sample execution has
a 100-second guard. Sentinel promotion is polled for 30 seconds; each Redis
inspection is separately bounded to 15 seconds. A final in-flight inspection
can extend that poll by at most 15 seconds.

Cluster must discover three shards and print one successful round trip in each
configured slot range. Sentinel starts one 60-iteration sample process, waits
for success on the initial primary, requests promotion, observes the other
primary, and requires success there after initial-primary success, plus normal sample completion. A success
count alone cannot pass. Transient iteration failures remain in the log; the
controller does not replay failed writes or promise lossless failover.

Each invocation chooses a fresh Compose project name. `--project` can override
it, but existing containers (including stopped ones), networks, or volumes with
that project label cause refusal before cleanup ownership is acquired. An existing
default Compose build image also causes refusal. Cleanup removes only that project's
containers, networks, volumes, and locally built images in a `finally`
block. The workflow has an additional `always()` cleanup step gated by the
controller's ownership marker for cancellation recovery. Repeated interruption signals
are ignored during primary cleanup. It never removes
another sample project or stops shared Redis services.

If the Docker daemon itself is unavailable during cleanup, the run fails and
retains `owned-project.txt` and cleanup logs. Once Docker recovers, use the exact
recorded project with the matching sample Compose file and
`down --volumes --remove-orphans --rmi local --timeout 10`.
