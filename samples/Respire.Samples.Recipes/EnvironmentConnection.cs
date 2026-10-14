using Respire;

namespace Respire.Samples.Recipes;

public static class EnvironmentConnection
{
    public static ValueTask<RespireClient> ConnectAsync(CancellationToken cancellationToken = default)
        => ConnectAsync(Environment.GetEnvironmentVariable, cancellationToken);

    public static ValueTask<RespireClient> ConnectAsync(Func<string, string?> lookup,
        CancellationToken cancellationToken = default)
        => RespireClient.ConnectAsync(Resolve(lookup), cancellationToken);

    // Accepting a lookup also lets tests exercise precedence without changing process globals.
    public static string Resolve(Func<string, string?> lookup)
    {
        foreach (var name in new[] { "REDIS_URL", "VALKEY_URL" })
        {
            var value = lookup(name);
            if (!string.IsNullOrWhiteSpace(value)) return value;
        }

        throw new InvalidOperationException("Set REDIS_URL or VALKEY_URL before starting the application.");
    }
}
