using System.Globalization;
using DotNet.Testcontainers.Containers;

namespace Respire.Testing.Containers;

internal static class ContainerStartupDiagnostics
{
    internal const int MaximumCharacters = 32 * 1024;
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(2);

    internal static async Task<string> CaptureAsync(IContainer container, int[] ports)
    {
        // Startup's token is commonly already cancelled. Diagnostics get a separate, short
        // deadline; even a transport that ignores cancellation must not delay fixture cleanup.
        using var deadline = new CancellationTokenSource(s_timeout);
        try
        {
            string[] command = ["tail", "-v", "-c", "4096", "--",
                .. ports.Select(port => $"/tmp/respire-fixture/{port.ToString(CultureInfo.InvariantCulture)}.log")];
            var pending = container.ExecAsync(command, deadline.Token);
            // WaitAsync can detach on timeout. Observe any later transport failure too.
            _ = pending.ContinueWith(static completed => { _ = completed.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            var result = await pending.WaitAsync(deadline.Token).ConfigureAwait(false);
            return Bound($"Daemon log command exit code: {result.ExitCode}\n{result.Stdout}\n{result.Stderr}");
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            return $"Daemon logs unavailable: collection exceeded {s_timeout}.";
        }
        catch (Exception error)
        {
            return Bound($"Daemon logs unavailable ({error.GetType().Name}): {error.Message}");
        }
    }

    private static string Bound(string text)
    {
        const string marker = "[truncated]\n";
        return text.Length <= MaximumCharacters ? text
            : marker + text[^(MaximumCharacters - marker.Length)..];
    }
}
