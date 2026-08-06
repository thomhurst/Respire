using Respire.StressTests.Clients;

namespace Respire.StressTests.Scenarios;

/// <summary>
/// One sustained workload, expressed against <see cref="IStressClient"/> so both
/// clients execute exactly the same operation sequence. An "operation" is one call
/// to <see cref="Execute"/> — composite scenarios (e.g. LPUSH+LPOP) count the pair
/// as a single operation.
/// </summary>
internal sealed class StressScenario
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required Func<IStressClient, WorkerContext, ValueTask> Execute { get; init; }

    public override string ToString() => Name;
}

internal static class StressScenarios
{
    public static IReadOnlyList<StressScenario> All { get; } =
    [
        new StressScenario
        {
            Name = "ping",
            Description = "PING round-trips (pure protocol + pipeline overhead)",
            Execute = static (client, _) => client.PingAsync()
        },
        new StressScenario
        {
            Name = "get",
            Description = "GET of a seeded key, value read as a string",
            Execute = static async (client, ctx) =>
                _ = await client.GetStringAsync(ctx.SeededStringKey).ConfigureAwait(false)
        },
        new StressScenario
        {
            Name = "set",
            Description = "SET of a per-worker key with the configured payload",
            Execute = static (client, ctx) => client.SetStringAsync(ctx.WorkerStringKey, ctx.Payload)
        },
        new StressScenario
        {
            Name = "incr",
            Description = "INCR of a per-worker counter",
            Execute = static async (client, ctx) =>
                _ = await client.IncrementAsync(ctx.WorkerCounterKey).ConfigureAwait(false)
        },
        new StressScenario
        {
            Name = "hash",
            Description = "HSET followed by HGET on a per-worker hash (pair counts as one operation)",
            Execute = static async (client, ctx) =>
            {
                await client.HashSetAsync(ctx.WorkerHashKey, "field", ctx.Payload).ConfigureAwait(false);
                _ = await client.HashGetAsync(ctx.WorkerHashKey, "field").ConfigureAwait(false);
            }
        },
        new StressScenario
        {
            Name = "list",
            Description = "LPUSH followed by LPOP on a per-worker list, so list size stays constant (pair counts as one operation)",
            Execute = static async (client, ctx) =>
            {
                await client.ListLeftPushAsync(ctx.WorkerListKey, ctx.Payload).ConfigureAwait(false);
                _ = await client.ListLeftPopAsync(ctx.WorkerListKey).ConfigureAwait(false);
            }
        },
        new StressScenario
        {
            Name = "mixed",
            Description = "Cache-style mix: 60% GET, 20% SET, 10% INCR, 10% HGET",
            Execute = static async (client, ctx) =>
            {
                var slot = ctx.Iteration++ % 10;
                if (slot < 6)
                {
                    _ = await client.GetStringAsync(ctx.SeededStringKey).ConfigureAwait(false);
                }
                else if (slot < 8)
                {
                    await client.SetStringAsync(ctx.WorkerStringKey, ctx.Payload).ConfigureAwait(false);
                }
                else if (slot < 9)
                {
                    _ = await client.IncrementAsync(ctx.WorkerCounterKey).ConfigureAwait(false);
                }
                else
                {
                    _ = await client.HashGetAsync(ctx.SeededHashKey, "field").ConfigureAwait(false);
                }
            }
        }
    ];

    public static IReadOnlyList<StressScenario> Select(string scenario)
    {
        if (scenario.Equals("all", StringComparison.OrdinalIgnoreCase))
            return All;

        var match = All.FirstOrDefault(s => s.Name.Equals(scenario, StringComparison.OrdinalIgnoreCase));
        return match is not null
            ? [match]
            : throw new ArgumentException(
                $"Unknown scenario '{scenario}'. Valid scenarios: {string.Join(", ", All.Select(s => s.Name))}, all");
    }
}
