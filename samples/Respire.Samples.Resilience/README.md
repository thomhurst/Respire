# One retry layer with Microsoft.Extensions.Resilience

```shell
dotnet run --project samples/Respire.Samples.Resilience -f net10.0
```

The sample registers a named Polly v8 pipeline through Microsoft DI, enables
Microsoft.Extensions.Resilience telemetry enrichment, and injects two `LOADING`
replies into an in-process fake Redis server. It checks that exactly three GET
callbacks execute even though the supplied Respire configuration enables five
command attempts. No Redis installation or Docker is needed.

Expected output ends with:

```text
GET attempts: 3; injected failures: 2; circuit: Closed
```

For a real server, replace `server.CreateOptions()` with a `RespireOptions` instance
containing your endpoints and credentials, remove fault injection and the attempt
assertion, and retain the named pipeline and callback pattern. See the
[guide](../../website/docs/guides/external-resilience.md) before applying retries to writes.
