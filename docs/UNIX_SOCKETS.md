# Unix domain sockets: maintainer notes

How Unix socket support is tested and benchmarked. User documentation is in
[website/docs/guides/unix-sockets.md](../website/docs/guides/unix-sockets.md).

## Integration tests

Linux integration tests start Redis in a container and share its socket directory with the test
host. They run only on Linux: Docker Desktop's cross-OS bind mounts do not carry Unix sockets to
Windows.

## TCP comparison

The `benchmark-unix-sockets.yml` workflow starts when a pull request gets the
`run-unix-sockets-benchmarks` label, or by manual dispatch. On Linux, against one local Redis server
and a 128-byte value, it runs two BenchmarkDotNet cases: `TcpLoopbackGet` (baseline) and
`UnixSocketGet`. It validates with a Dry job, then publishes the table and logs as artifacts and in
the job summary. Do not assume a performance result from the transport choice alone.

To run it on a prepared Linux host:

1. Set `RESPIRE_UNIX_SOCKET`, and optionally `RESPIRE_TCP_CONNECTION` (default `127.0.0.1:6379`).
   Both must reach the same Redis database.
2. Select `*UnixSocketBenchmarks*` in the benchmark project.
