using System.Diagnostics;

namespace Respire.Tests;

// The "Respire" ActivitySource and Meter are process-wide, so a listener also sees every
// concurrently running test's clients. Filter on the test's own fake server ports instead of
// serializing the test. Port 6379 is the default and carries no server.port tag.
// Activity callbacks also observe activities that another listener (such as TUnit's) sampled,
// whatever this listener's Sample returned, so also filter on the operation name.
internal static class TestTelemetry
{
    internal static bool IsFrom(Activity activity, params ReadOnlySpan<int> ports)
        => activity.GetTagItem("server.port") is int port && ports.Contains(port);

    internal static bool IsFrom(ReadOnlySpan<KeyValuePair<string, object?>> tags, params ReadOnlySpan<int> ports)
    {
        foreach (var tag in tags)
        {
            if (tag.Key == "server.port" && tag.Value is int port && ports.Contains(port)) return true;
        }

        return false;
    }
}
