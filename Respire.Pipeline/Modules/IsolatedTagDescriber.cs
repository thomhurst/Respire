using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Respire.Pipeline.Modules;

internal static class IsolatedTagDescriber
{
    internal static async Task<string?> DescribeAsync(
        string repositoryRoot, CancellationToken cancellationToken, Func<string?, bool> isStableTag, ILogger? logger)
    {
        // Read local refs directly: clone/fetch advertisements can hide release tags.
        // Capture the calling worktree's HEAD and shared object/shallow paths together.
        var metadata = (await GitCommand.RunAsync(repositoryRoot, cancellationToken, "rev-parse",
            "--show-object-format", "--path-format=absolute", "--git-path", "objects", "--git-path", "shallow",
            "--git-path", "info/grafts", "HEAD"))
            .Split('\n').Select(line => line.TrimEnd('\r')).ToArray();
        if (metadata.Length != 5) throw new InvalidOperationException("Git returned unexpected repository metadata.");
        var (objectFormat, objects, shallow, grafts, head) = (metadata[0], metadata[1], metadata[2], metadata[3], metadata[4]);
        // rev-parse reports the default shallow path even when --shallow-file/GIT_SHALLOW_FILE
        // selects another boundary; Git resolves a relative value from the worktree root.
        if (Environment.GetEnvironmentVariable("GIT_SHALLOW_FILE") is { Length: > 0 } shallowFile)
            shallow = Path.GetFullPath(shallowFile, repositoryRoot);

        // Copy replacements only when the source applies them. The isolated commands inherit
        // GIT_REPLACE_REF_BASE and GIT_NO_REPLACE_OBJECTS, so they read the same namespace.
        var replaceRefBase = Environment.GetEnvironmentVariable("GIT_REPLACE_REF_BASE") is { Length: > 0 } configuredBase
            ? configuredBase
            : "refs/replace/";
        var useReplaceRefs = Environment.GetEnvironmentVariable("GIT_NO_REPLACE_OBJECTS") is null
            && await GitCommand.RunAsync(repositoryRoot, cancellationToken,
                "config", "--type=bool", "--default", "true", "--get", "core.useReplaceRefs") == "true";
        string[] refPatterns = useReplaceRefs ? ["refs/tags", replaceRefBase] : ["refs/tags"];
        var localRefs = await GitCommand.RunAsync(repositoryRoot, cancellationToken,
            ["for-each-ref", "--format=%(objectname) %(refname)", .. refPatterns]);
        var packedRefs = new StringBuilder();
        foreach (var line in localRefs.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = line.IndexOf(' ');
            var name = line[(separator + 1)..];
            if (name.StartsWith("refs/tags/", StringComparison.Ordinal) && !isStableTag(name[10..])) continue;
            packedRefs.Append(line).Append('\n');
        }

        // Isolate filtered refs while borrowing objects. Include replacements and the
        // shallow/graft boundaries so Git walks the same local history without touching source refs.
        // Packed refs preserve case-distinct names on Windows and keep argv constant-size.
        using var temporary = new TemporaryRepository(logger);
        await GitCommand.RunIsolatedAsync(repositoryRoot, temporary.FullName, cancellationToken,
            "-c", "init.defaultRefFormat=files", "init", "--bare", "--quiet", "--template=", $"--object-format={objectFormat}");
        await File.WriteAllTextAsync(Path.Combine(temporary.FullName, "objects", "info", "alternates"),
            objects + "\n", cancellationToken);
        if (File.Exists(shallow)) File.Copy(shallow, Path.Combine(temporary.FullName, "shallow"));
        if (File.Exists(grafts))
        {
            Directory.CreateDirectory(Path.Combine(temporary.FullName, "info"));
            File.Copy(grafts, Path.Combine(temporary.FullName, "info", "grafts"));
        }
        // No sorted/peeled header is claimed: Git sorts entries and peels annotated tags.
        await File.WriteAllTextAsync(Path.Combine(temporary.FullName, "packed-refs"), packedRefs.ToString(), cancellationToken);

        // Git retains native distance, merge-history and annotated-tag precedence.
        // --always reports a commit hash only when no retained tag is reachable;
        // unlike catching Git failures, this leaves repository errors observable.
        var selected = await GitCommand.RunIsolatedAsync(repositoryRoot, temporary.FullName, cancellationToken,
            "describe", "--tags", "--abbrev=0", "--always", head);
        // The hash fallback cannot satisfy the shared stable-tag grammar.
        return isStableTag(selected) ? selected : null;
    }

    private sealed class TemporaryRepository(ILogger? logger) : IDisposable
    {
        private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("respire-stable-version-");
        internal string FullName => _directory.FullName;

        public void Dispose()
        {
            // Temporary metadata cleanup is best-effort: a locked file must not replace
            // the Git failure/cancellation, or prevent returning an already computed version.
            try
            {
                foreach (var file in _directory.EnumerateFiles("*", SearchOption.AllDirectories))
                    file.Attributes &= ~FileAttributes.ReadOnly;
                _directory.Delete(recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                if (logger is not null)
                    logger.LogWarning(exception, "Could not remove temporary Git metadata at {Path}", _directory.FullName);
                else
                    Trace.TraceWarning("Could not remove temporary Git metadata at {0}: {1}", _directory.FullName, exception.Message);
            }
        }
    }
}
