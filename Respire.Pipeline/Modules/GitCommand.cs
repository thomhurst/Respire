using System.Diagnostics;

namespace Respire.Pipeline.Modules;

internal static class GitCommand
{
    private static readonly string[] RepositoryEnvironmentVariables =
    [
        "GIT_DIR", "GIT_WORK_TREE", "GIT_COMMON_DIR", "GIT_INDEX_FILE",
        "GIT_OBJECT_DIRECTORY",
        "GIT_GRAFT_FILE", "GIT_SHALLOW_FILE", "GIT_PREFIX", "GIT_IMPLICIT_WORK_TREE",
        "GIT_CONFIG", "GIT_CONFIG_PARAMETERS", "GIT_CONFIG_COUNT",
    ];

    internal static Task<string> RunAsync(string repositoryRoot, CancellationToken cancellationToken, params string[] arguments)
        => RunCoreAsync(repositoryRoot, cancellationToken, isolated: false, arguments);

    internal static Task<string> RunIsolatedAsync(
        string repositoryRoot, string gitDirectory, CancellationToken cancellationToken, params string[] arguments)
        // Keep the source cwd so relative, read-only alternate object paths retain
        // their meaning. The explicit git-dir and cleared selectors own all metadata.
        => RunCoreAsync(repositoryRoot, cancellationToken, isolated: true, [$"--git-dir={gitDirectory}", .. arguments]);

    private static async Task<string> RunCoreAsync(
        string repositoryRoot,
        CancellationToken cancellationToken,
        bool isolated,
        string[] arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = repositoryRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            }
        };

        if (isolated)
        {
            // Preserve the caller's environment for source discovery, but never let
            // repository-selection variables redirect writes from temporary metadata.
            foreach (var name in RepositoryEnvironmentVariables)
                process.StartInfo.Environment.Remove(name);
        }

        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        if (!process.Start())
        {
            throw new InvalidOperationException("Failed to start git.");
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Join this owned process before the caller removes its temporary repository.
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { /* The process already exited. */ }
            await process.WaitForExitAsync(CancellationToken.None);
            try { await Task.WhenAll(stdoutTask, stderrTask); }
            catch (OperationCanceledException) { }
            throw;
        }

        var stdout = (await stdoutTask).Trim();
        var stderr = (await stderrTask).Trim();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed with exit code {process.ExitCode}: {stderr}");
        }

        return stdout;
    }
}
