using System.Globalization;

namespace Respire.StressTests;

internal sealed class StressTestOptions
{
    public int DurationSeconds { get; private init; } = 5 * 60;
    public int WarmupSeconds { get; private init; } = 10;
    public string Scenario { get; private init; } = "all";
    public string Client { get; private init; } = "all";
    public int Concurrency { get; private init; } = 50;
    public int ValueSizeBytes { get; private init; } = 1024;
    public string OutputPath { get; private init; } = "./results";

    public const string Usage = """
        Respire Stress Test Runner — sustained Respire vs StackExchange.Redis comparison

        Usage:
          dotnet run -c Release -- [options]

        Options:
          --duration <minutes>       Measured duration per scenario/client pass (default: 5)
          --duration-seconds <sec>   Measured duration in seconds; overrides --duration (for smoke runs)
          --warmup <seconds>         Unmeasured warmup before each pass (default: 10)
          --scenario <name>          ping, get, set, incr, hash, list, mixed, all (default: all)
          --client <name>            respire, stackexchange, all (default: all)
          --concurrency <n>          Concurrent workers per pass (default: 50)
          --value-size <bytes>       Payload size for written values (default: 1024)
          --output <path>            Output directory for JSON results and the markdown report (default: ./results)

        Environment:
          REDIS_HOST / REDIS_PORT    Use an external Redis; otherwise a throwaway Redis Testcontainer is started.
        """;

    public static StressTestOptions Parse(string[] args)
    {
        double durationMinutes = 5;
        int? durationSeconds = null;
        var warmupSeconds = 10;
        var scenario = "all";
        var client = "all";
        var concurrency = 50;
        var valueSizeBytes = 1024;
        var outputPath = "./results";

        for (var i = 0; i < args.Length; i++)
        {
            var value = i + 1 < args.Length
                ? args[i + 1]
                : throw new ArgumentException($"Missing value for option '{args[i]}'.\n\n{Usage}");

            switch (args[i].ToLowerInvariant())
            {
                case "--duration":
                    durationMinutes = ParsePositiveDouble(args[i], value);
                    break;
                case "--duration-seconds":
                    durationSeconds = ParsePositiveInt(args[i], value);
                    break;
                case "--warmup":
                    warmupSeconds = ParsePositiveInt(args[i], value);
                    break;
                case "--scenario":
                    scenario = value;
                    break;
                case "--client":
                    client = value;
                    break;
                case "--concurrency":
                    concurrency = ParsePositiveInt(args[i], value);
                    break;
                case "--value-size":
                    valueSizeBytes = ParsePositiveInt(args[i], value);
                    break;
                case "--output":
                    outputPath = value;
                    break;
                default:
                    throw new ArgumentException($"Unknown option '{args[i]}'.\n\n{Usage}");
            }

            i++;
        }

        return new StressTestOptions
        {
            DurationSeconds = durationSeconds ?? (int)Math.Round(durationMinutes * 60),
            WarmupSeconds = warmupSeconds,
            Scenario = scenario,
            Client = client,
            Concurrency = concurrency,
            ValueSizeBytes = valueSizeBytes,
            OutputPath = outputPath
        };
    }

    private static int ParsePositiveInt(string option, string value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
            ? parsed
            : throw new ArgumentException($"Option '{option}' requires a positive integer, got '{value}'.");

    private static double ParsePositiveDouble(string option, string value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
            ? parsed
            : throw new ArgumentException($"Option '{option}' requires a positive number, got '{value}'.");
}
