using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using StackExchange.Redis;

namespace Respire.ComparisonBenchmarks;

public partial class PubSubComparisonBenchmarks
{
    // A diagnostic experiment, deliberately outside BDN's timed methods. Timestamping,
    // validation and deadlines perturb these samples; use BDN for client comparisons.
    internal static async Task ProfileAsync(string output)
    {
        var fixture = new PubSubComparisonBenchmarks();
        await fixture.Setup();
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            await fixture._messages.DisposeAsync();
            fixture._messages = fixture._subscription.GetAsyncEnumerator(deadline.Token);
            const int warmup = 500;
            const int count = 5_000;
            var samples = new PhaseSample[count * 2];
            for (var i = -warmup; i < count; i++)
            {
                // Alternate client order to reduce systematic warm-up/drift bias.
                var first = await fixture.SampleAsync(respire: i % 2 == 0, deadline.Token);
                var second = await fixture.SampleAsync(respire: i % 2 != 0, deadline.Token);
                if (i < 0) continue;
                samples[i * 2] = first;
                samples[i * 2 + 1] = second;
            }

            var report = new
            {
                SchemaVersion = 1,
                Runtime = RuntimeInformation.FrameworkDescription,
                OS = RuntimeInformation.OSDescription,
                Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                StackExchangeRedis = typeof(ConnectionMultiplexer).Assembly
                    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
                StopwatchFrequency = Stopwatch.Frequency,
                Protocol = "RESP3",
                Subscribers = 1,
                PayloadBytes = 18,
                WarmupPerClient = warmup,
                Notes = "Sequential publish acknowledgement then consumption, matching PubSubComparisonBenchmarks. " +
                    "Ack includes client scheduling and the server round trip. AfterAck is residual consumer time, " +
                    "not independent delivery latency: delivery can overlap acknowledgement. " +
                    "Instrumented diagnostics, not latency or allocation comparison results.",
                Samples = samples,
            };
            await File.WriteAllTextAsync(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            foreach (var group in samples.GroupBy(sample => sample.Client))
            {
                Console.WriteLine($"{group.Key}: ack mean {group.Average(s => s.AckMicroseconds):F3} us; " +
                    $"after-ack mean {group.Average(s => s.AfterAckMicroseconds):F3} us; " +
                    $"synchronous reads {group.Count(s => s.ReadCompletedSynchronously)}/{count}");
            }
        }
        finally
        {
            await fixture.Cleanup();
        }
    }

    private async Task<PhaseSample> SampleAsync(bool respire, CancellationToken cancellationToken)
    {
        long acknowledgement;
        long consumed;
        bool synchronous;
        string? payload;
        var start = Stopwatch.GetTimestamp();
        if (respire)
        {
            var recipients = await _respire.PublishAsync(_respireChannel, _payload, cancellationToken);
            acknowledgement = Stopwatch.GetTimestamp();
            var read = _messages.MoveNextAsync();
            synchronous = read.IsCompletedSuccessfully;
            if (!await read || _messages.Current.Kind == RespireMessageKind.Gap)
                throw new InvalidOperationException("Subscription ended or reported a delivery gap during profiling.");
            payload = _messages.Current.Text;
            consumed = Stopwatch.GetTimestamp();
            if (recipients != 1) throw new InvalidOperationException($"Expected one recipient, got {recipients}.");
        }
        else
        {
            var recipients = await _subscriber.PublishAsync(_stackExchangeChannel, _payload).WaitAsync(cancellationToken);
            acknowledgement = Stopwatch.GetTimestamp();
            var read = _queue.ReadAsync(cancellationToken);
            synchronous = read.IsCompletedSuccessfully;
            payload = (await read).Message;
            consumed = Stopwatch.GetTimestamp();
            if (recipients != 1) throw new InvalidOperationException($"Expected one recipient, got {recipients}.");
        }
        if (payload != _payload) throw new InvalidOperationException("Unexpected payload during profiling.");
        return new PhaseSample(respire ? "Respire" : "StackExchange.Redis",
            (acknowledgement - start) * 1_000_000.0 / Stopwatch.Frequency,
            (consumed - acknowledgement) * 1_000_000.0 / Stopwatch.Frequency,
            synchronous);
    }

    private sealed record PhaseSample(string Client, double AckMicroseconds,
        double AfterAckMicroseconds, bool ReadCompletedSynchronously);
}
