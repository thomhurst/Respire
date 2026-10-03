using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Respire.Testing.Containers;

namespace Respire.StressTests;

/// <summary>A closed-loop tail-latency experiment with reproducible server stalls.</summary>
internal static class HedgedReadLatencyProbe
{
    private const int Samples = 1000;
    private const int Warmup = 20;
    private const int PauseEvery = 20;
    private const int PauseMilliseconds = 50;
    private const int ExtraLoadPercent = 5;

    internal static async Task<int> RunAsync(string outputDirectory)
    {
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            var cancellationToken = deadline.Token;
            await using var fixture = await RespireContainerFixture.StartAsync(new()
            {
                Topology = RespireContainerTopology.Sentinel,
            }, cancellationToken).ConfigureAwait(false);
            var options = new RespireOptions
            {
                Endpoints = [fixture.DataEndpoints[0]],
                ReplicaEndpoints = [fixture.DataEndpoints[1]],
                Connections = 1,
                Protocol = RespProtocol.Resp3,
                ReplicaRefreshInterval = TimeSpan.FromHours(1),
            };
            await using var seed = await RespireClient.ConnectAsync(options, cancellationToken).ConfigureAwait(false);
            await seed.SetAsync("hedged-latency", new string('x', 256), cancellationToken: cancellationToken).ConfigureAwait(false);
            while (await seed.WithReadFrom(RespireReadFrom.Replica).GetStringAsync("hedged-latency", cancellationToken).ConfigureAwait(false) is null)
                await Task.Delay(20, cancellationToken).ConfigureAwait(false);
            await using var control = await RespireClient.ConnectAsync(new RespireOptions
            {
                Endpoints = [fixture.DataEndpoints[1]], Connections = 1,
            }, cancellationToken).ConfigureAwait(false);

            var passes = new List<Pass>();
            foreach (var name in new[] { "baseline-before", "hedged", "baseline-after" })
            {
                var hedged = name == "hedged";
                await using var client = await RespireClient.ConnectAsync(options with
                {
                    ReadFrom = RespireReadFrom.ReplicaPreferred,
                    HedgedReads = hedged ? new()
                    {
                        Delay = TimeSpan.FromMilliseconds(5), MaximumExtraLoadPercent = ExtraLoadPercent,
                    } : null,
                }, cancellationToken).ConfigureAwait(false);
                long sent = 0, won = 0;
                using var listener = new MeterListener();
                listener.InstrumentPublished = (instrument, meter) =>
                {
                    if (instrument.Meter.Name == "Respire" && instrument.Name is "respire.read.hedge.sent" or "respire.read.hedge.won")
                        meter.EnableMeasurementEvents(instrument);
                };
                listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
                {
                    foreach (var tag in tags)
                    {
                        if (tag.Key != "server.port" || Convert.ToInt32(tag.Value) != fixture.DataEndpoints[0].Port) continue;
                        if (instrument.Name == "respire.read.hedge.sent") Interlocked.Add(ref sent, value);
                        else Interlocked.Add(ref won, value);
                    }
                });
                listener.Start();
                var milliseconds = new double[Samples];
                for (var i = -Warmup; i < Samples; i++)
                {
                    var pause = i >= 0 && (i + 1) % PauseEvery == 0;
                    if (pause)
                    {
                        using var reply = await control.ExecuteAsync("CLIENT", ["PAUSE", PauseMilliseconds, "ALL"], cancellationToken: cancellationToken).ConfigureAwait(false);
                    }
                    var started = Stopwatch.GetTimestamp();
                    var value = await client.GetStringAsync("hedged-latency", cancellationToken).ConfigureAwait(false);
                    var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                    if (value is not { Length: 256 }) throw new InvalidOperationException("GET returned an unexpected value.");
                    if (i >= 0) milliseconds[i] = elapsed;
                    // Drain each injected stall outside the timed operation. Otherwise the next
                    // logical request measures the previous loser's backlog instead of its own stall.
                    if (pause) await control.PingAsync(cancellationToken).ConfigureAwait(false);
                }
                var sorted = milliseconds.Order().ToArray();
                var maximumHedges = hedged ? (Samples + Warmup) * ExtraLoadPercent / 100 : 0;
                if (sent > maximumHedges) throw new InvalidOperationException($"Extra-load budget exceeded: {sent} > {maximumHedges}.");
                var pass = new Pass(name, sorted[499], sorted[949], sorted[989], sent, won, maximumHedges, milliseconds);
                passes.Add(pass);
                Console.WriteLine($"{name}: p50={pass.P50Milliseconds:F3} ms, p95={pass.P95Milliseconds:F3} ms, p99={pass.P99Milliseconds:F3} ms; hedges={sent}, wins={won}, budget={maximumHedges}");
            }
            Directory.CreateDirectory(outputDirectory);
            var report = new
            {
                CreatedUtc = DateTimeOffset.UtcNow,
                Runtime = RuntimeInformation.FrameworkDescription,
                OS = RuntimeInformation.OSDescription,
                Image = "redis:7.2-alpine", Samples, Warmup, PauseEvery, PauseMilliseconds, ExtraLoadPercent,
                Method = "Sequential GET; pause replica before every twentieth measured read; wait for pause to end outside timed operation. Counts include warmup; latency samples exclude warmup. Three passes share one fixture. Nearest-rank percentiles.",
                Passes = passes,
            };
            await File.WriteAllTextAsync(Path.Combine(outputDirectory, "hedged-read-latency.json"),
                JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }), cancellationToken).ConfigureAwait(false);
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    private sealed record Pass(string Name, double P50Milliseconds, double P95Milliseconds, double P99Milliseconds,
        long HedgesSent, long HedgesWon, int MaximumHedges, double[] SamplesMilliseconds);
}
