using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Respire.Pipeline.Modules.LocalMachine;

internal static partial class LocalNugetSource
{
    internal static async Task<string> EnsureRegisteredAsync(
        string directory,
        CancellationToken cancellationToken,
        string? configFile = null,
        Func<string[], CancellationToken, Task<string>>? runCommand = null)
    {
        runCommand ??= RunCommandAsync;
        directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        string[] configArguments = configFile is null ? [] : ["--configfile", Path.GetFullPath(configFile)];
        var output = await runCommand(["nuget", "list", "source", "--format", "Detailed", .. configArguments], cancellationToken);
        var sources = ParseSources(output);
        var matching = sources.Where(source => MatchesDirectory(source.Path, directory))
            .OrderByDescending(source => source.Enabled).FirstOrDefault();
        if (matching is not null)
        {
            if (!matching.Enabled)
            {
                await runCommand(["nuget", "enable", "source", matching.Name, .. configArguments], cancellationToken);
            }

            return matching.Name;
        }

        const string preferredName = "RespireLocal";
        var name = preferredName;
        for (var suffix = 2; sources.Any(source => string.Equals(source.Name, name, StringComparison.OrdinalIgnoreCase)); suffix++)
        {
            name = $"{preferredName}-{suffix}";
        }

        await runCommand(["nuget", "add", "source", directory, "--name", name, .. configArguments], cancellationToken);
        return name;
    }

    private static bool MatchesDirectory(string source, string directory)
    {
        // NuGet lists resolved local paths. Remote feeds cannot match the local directory.
        if (!Path.IsPathFullyQualified(source)) return false;
        return string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(source)), directory,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    private static List<Source> ParseSources(string output)
    {
        var sources = new List<Source>();
        using var reader = new StringReader(output);
        while (reader.ReadLine() is { } line)
        {
            var match = SourceHeader().Match(line);
            if (!match.Success)
            {
                if (string.IsNullOrWhiteSpace(line) || line.Trim() is "Registered Sources:" or "No sources found.") continue;
                throw new InvalidOperationException($"Unrecognized NuGet source listing: {line}");
            }
            var path = reader.ReadLine()?.Trim();
            if (string.IsNullOrEmpty(path)) throw new InvalidOperationException("NuGet listed a source without a path.");
            sources.Add(new Source(match.Groups[1].Value, path, match.Groups[2].Value == "Enabled"));
        }

        // Fail closed if a future CLI changes its output instead of adding duplicate sources.
        if (sources.Count == 0 && !output.Trim().Equals("No sources found.", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Unrecognized NuGet source listing: {output}");
        }

        return sources;
    }

    [GeneratedRegex(@"^\s*\d+\.\s+(.+) \[(Enabled|Disabled)\]\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex SourceHeader();

    private sealed record Source(string Name, string Path, bool Enabled);

    internal static async Task<string> RunCommandAsync(string[] arguments, CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("dotnet")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };
        // Detailed source listings contain names and enabled state. Keep this format independent
        // of the developer's UI language without changing the parent process's environment.
        process.StartInfo.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en-US";
        process.StartInfo.Environment["DOTNET_NOLOGO"] = "1";
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);

        if (!process.Start()) throw new InvalidOperationException("Failed to start dotnet nuget.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { /* The process already exited. */ }
            await process.WaitForExitAsync(CancellationToken.None);
            try { await Task.WhenAll(stdoutTask, stderrTask); }
            catch (OperationCanceledException) { }
            throw;
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"dotnet nuget {arguments[1]} failed with exit code {process.ExitCode}: {stderr}{stdout}");
        }

        return stdout;
    }
}
