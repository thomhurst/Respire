using System.Globalization;
using Respire;

var iterations = args.Length == 0 ? 1 : int.Parse(args[0], CultureInfo.InvariantCulture);
if (iterations is < 1 or > 120) throw new ArgumentOutOfRangeException(nameof(iterations), "Use 1 through 120 iterations.");
var options = RespireOptions.Parse(Environment.GetEnvironmentVariable("RESPIRE_CONNECTION")
    ?? "127.0.0.1:27100,127.0.0.1:27101,127.0.0.1:27102,serviceName=sample-primary");
if (string.IsNullOrWhiteSpace(options.SentinelPrimaryName))
    throw new ArgumentException("RESPIRE_CONNECTION must set serviceName.");
using var stopping = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; stopping.Cancel(); };
await using var redis = await RespireClient.ConnectAsync(options, stopping.Token);
var succeeded = 0;
try
{
    for (var iteration = 0; iteration < iterations; iteration++)
    {
        try
        {
            var expected = Guid.NewGuid().ToString();
            await redis.SetAsync("respire:sentinel-sample:value", (RespireValue)expected,
                expiry: TimeSpan.FromMinutes(2), cancellationToken: stopping.Token);
            var actual = await redis.GetStringAsync("respire:sentinel-sample:value", stopping.Token);
            if (actual != expected) throw new InvalidOperationException("Read differs from write; replication/failover may have lost the value.");
            Console.WriteLine($"PASS {++succeeded}: primary {redis.Endpoint.Host}:{redis.Endpoint.Port}");
        }
        catch (Exception error) when (iterations > 1 && !stopping.IsCancellationRequested
            && (error is RespireException or InvalidOperationException))
        {
            // This iteration's write is not replayed. A later iteration is a new operation.
            Console.Error.WriteLine($"Iteration {iteration + 1} failed: {error.GetType().Name}: {error.Message}");
        }
        if (iteration + 1 < iterations) await Task.Delay(TimeSpan.FromSeconds(1), stopping.Token);
    }
}
catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
if (succeeded == 0 && !stopping.IsCancellationRequested)
    throw new InvalidOperationException("No Sentinel round trip succeeded.");
Console.WriteLine($"Sentinel sample completed: {succeeded} successful round trips.");
