using DotNet.Testcontainers.Containers;

namespace Respire.Testing.Containers;

internal static class ContainerStartupDiagnostics
{
    internal const int MaximumCharacters = 32 * 1024;
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(2);
    private static readonly SemaphoreSlim s_reporting = new(1, 1);

    internal static async Task ReportAsync(string diagnostics)
    {
        // A custom TextWriter can block even in WriteLineAsync. Isolate the write and
        // permit only one outstanding report so a stalled sink cannot accumulate workers.
        using var deadline = new CancellationTokenSource(s_timeout);
        try
        {
            await s_reporting.WaitAsync(deadline.Token).ConfigureAwait(false);
            var writer = Console.Error;
            var pending = Task.Run(() =>
            {
                try { writer.WriteLine(diagnostics); }
                catch (Exception) { }
                finally { s_reporting.Release(); }
            });
            await pending.WaitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested) { }
    }

    internal static async Task<string> CaptureAsync(IContainer container, int[] ports, TimeProvider? timeProvider = null)
    {
        if (ports.Length == 0) return "Daemon logs unavailable: no daemon ports were selected.";
        // Startup's token is commonly already cancelled. Diagnostics get a separate, short
        // deadline; even a transport that ignores cancellation must not delay fixture cleanup.
        using var deadline = new CancellationTokenSource(s_timeout, timeProvider ?? TimeProvider.System);
        // Reserve separators before splitting the budget so a noisy port cannot displace
        // every earlier daemon's tail. Keep the final bound for unusually large port arrays.
        var perPortBudget = Math.Max(1, (MaximumCharacters - ports.Length + 1) / ports.Length);
        var logs = await Task.WhenAll(ports.Select(port => CapturePortAsync(container, port, perPortBudget, deadline.Token))).ConfigureAwait(false);
        return Bound(string.Join("\n", logs), MaximumCharacters);
    }

    private static async Task<string> CapturePortAsync(IContainer container, int port, int budget, CancellationToken cancellationToken)
    {
        var path = RespireContainerFixture.DaemonLogPath(port);
        try
        {
            // One file per command uses portable tail options. Labels are generated here,
            // including for a missing log, rather than depending on tail's verbose extension.
            var pending = container.ExecAsync(["tail", "-c", "4096", path], cancellationToken);
            // WaitAsync can detach on timeout. Observe any later transport failure too.
            _ = ObserveFaultAsync(pending);
            var result = await pending.WaitAsync(cancellationToken).ConfigureAwait(false);
            var label = $"{path} (exit code: {result.ExitCode})\n";
            return Bound($"{label}{result.Stdout}\n{result.Stderr}", budget, label);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Bound($"{path}: daemon logs unavailable; collection exceeded {s_timeout}.", budget);
        }
        catch (Exception error)
        {
            var label = $"{path}: daemon logs unavailable ({error.GetType().Name}): ";
            return Bound(label + error.Message, budget, label);
        }
    }

    private static async Task ObserveFaultAsync(Task pending)
    {
        try { await pending.ConfigureAwait(false); }
        catch (Exception) { }
    }

    private static string Bound(string text, int maximumCharacters, string label = "")
    {
        if (text.Length <= maximumCharacters) return text;
        var prefix = "[truncated]\n" + label;
        return prefix.Length >= maximumCharacters ? prefix[..maximumCharacters]
            : prefix + text[^(maximumCharacters - prefix.Length)..];
    }
}
