using DotNet.Testcontainers.Containers;

namespace Respire.Testing.Containers;

internal static class ContainerStartupDiagnostics
{
    internal const int MaximumCharacters = 32 * 1024;
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(2);

    internal static async Task<string> CaptureAsync(IContainer container, int[] ports)
    {
        if (ports.Length == 0) return "Daemon logs unavailable: no daemon ports were selected.";
        // Startup's token is commonly already cancelled. Diagnostics get a separate, short
        // deadline; even a transport that ignores cancellation must not delay fixture cleanup.
        using var deadline = new CancellationTokenSource(s_timeout);
        var logs = await Task.WhenAll(ports.Select(port => CapturePortAsync(container, port, deadline.Token))).ConfigureAwait(false);
        return Bound(string.Join("\n", logs));
    }

    private static async Task<string> CapturePortAsync(IContainer container, int port, CancellationToken cancellationToken)
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
            return Bound($"{path} (exit code: {result.ExitCode})\n{result.Stdout}\n{result.Stderr}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return $"{path}: daemon logs unavailable; collection exceeded {s_timeout}.";
        }
        catch (Exception error)
        {
            return Bound($"{path}: daemon logs unavailable ({error.GetType().Name}): {error.Message}");
        }
    }

    private static async Task ObserveFaultAsync(Task pending)
    {
        try { await pending.ConfigureAwait(false); }
        catch (Exception) { }
    }

    private static string Bound(string text)
    {
        const string marker = "[truncated]\n";
        return text.Length <= MaximumCharacters ? text
            : marker + text[^(MaximumCharacters - marker.Length)..];
    }
}
