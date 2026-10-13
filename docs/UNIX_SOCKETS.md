User documentation for Unix domain sockets lives at [website/docs/guides/unix-sockets.md](../website/docs/guides/unix-sockets.md). This file keeps only maintainer notes.

# Unix domain sockets: maintainer notes

## Integration tests

Linux integration tests start Redis in a container and share its socket directory with
the test host. Docker Desktop's cross-OS bind mounts do not carry Unix sockets to
Windows, so these container tests run only on Linux.

## TCP comparison

The **Unix socket benchmarks** workflow (`benchmark-unix-sockets.yml`) starts when a pull
request gets the `run-unix-sockets-benchmarks` label, or by manual dispatch. It runs two BenchmarkDotNet cases on Linux
against the same local Redis server and 128-byte value: `TcpLoopbackGet` (baseline)
and `UnixSocketGet`. It validates with a Dry job, then publishes the measured table
and logs as workflow artifacts and in the job summary. No performance result is
assumed from the transport choice alone.

To run that targeted comparison on a prepared Linux benchmark host, set
`RESPIRE_UNIX_SOCKET` and optionally `RESPIRE_TCP_CONNECTION` (default `127.0.0.1:6379`),
then select `*UnixSocketBenchmarks*` in the existing benchmark project. Both
endpoints must reach the same Redis database.
