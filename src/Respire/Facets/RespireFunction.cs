using System.Runtime.CompilerServices;
using Respire.Commands;
using Respire.Internal;

namespace Respire;

/// <summary>A named Redis function. Read-only calls use FCALL_RO; no replica routing is implied.</summary>
public sealed class RespireFunction
{
    internal RespireFunction(string name, bool readOnly, RespireFunctionLibrary? library)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
        IsReadOnly = readOnly;
        Library = library;
    }

    /// <summary>The server-registered function name.</summary>
    public string Name { get; }
    /// <summary>Whether Redis must reject writes during this invocation.</summary>
    public bool IsReadOnly { get; }
    /// <summary>The optional library used for bounded reload after a missing-function error.</summary>
    public RespireFunctionLibrary? Library { get; }
    /// <summary>References a function that the application loads separately.</summary>
    public static RespireFunction Create(string name, bool readOnly = false) => new(name, readOnly, null);
    internal string Operation => IsReadOnly ? "FCALL_RO" : "FCALL";
    internal Verb Verb => IsReadOnly ? RespireCommands.Scripting.FCALL_RO.Verb : RespireCommands.Scripting.FCALL.Verb;
}

/// <summary>Reusable Redis library source with serialized, bounded missing-function reloads.</summary>
/// <remarks>Existing functions are used as registered. Replacement is opt-in and applies when loading,
/// not on every call. Deferred calls never reload; load the library before execution.</remarks>
public sealed class RespireFunctionLibrary
{
    private readonly ConditionalWeakTable<ClientCore, FunctionReloadState> _reloadStates = new();
    private RespireFunctionLibrary(string source, string name, bool replace)
        => (Source, Name, Replace) = (source, name, replace);

    /// <summary>Source including the Redis engine/name header.</summary>
    public string Source { get; }
    /// <summary>The name declared in the source header.</summary>
    public string Name { get; }
    /// <summary>Whether loading may replace an existing library with different source.</summary>
    public bool Replace { get; }

    /// <summary>Creates a reusable library with an unquoted name header. Redis validates the engine and function definitions when loaded.</summary>
    public static RespireFunctionLibrary Create(string source, bool replace = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        var header = source.Split('\n', 2)[0];
        if (!header.StartsWith("#!", StringComparison.Ordinal))
            throw new ArgumentException("A Redis function library requires an engine/name header.", nameof(source));
        var names = header.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Where(token => token.StartsWith("name=", StringComparison.Ordinal)).ToArray();
        if (names.Length != 1 || names[0].Length == 5)
            throw new ArgumentException("The library header must declare exactly one nonempty name.", nameof(source));
        var name = names[0][5..];
        // Redis also accepts quoted header tokens. Require the simple unquoted form here
        // instead of guessing its shell-style escape rules and caching the wrong name.
        if (name.IndexOfAny(['\"', '\\', '\'']) >= 0)
            throw new ArgumentException("Reusable library names must use an unquoted, unescaped header token.", nameof(source));
        return new(source, name, replace);
    }

    /// <summary>References a function in this library, enabling one reload/retry for immediate execution.</summary>
    public RespireFunction Function(string name, bool readOnly = false) => new(name, readOnly, this);
    internal FunctionReloadState ReloadState(ClientCore core) => _reloadStates.GetValue(core, static _ => new());

    internal sealed class FunctionReloadState
    {
        internal readonly SemaphoreSlim Gate = new(1, 1);
        internal long Generation;
    }
}

/// <summary>How FUNCTION FLUSH releases memory.</summary>
public enum FunctionFlushMode
{
    /// <summary>Use the server lazy-free setting.</summary>
    Default,
    /// <summary>Release memory synchronously.</summary>
    Sync,
    /// <summary>Release memory asynchronously.</summary>
    Async
}
/// <summary>How FUNCTION RESTORE handles existing libraries. Append is Redis's default.</summary>
public enum FunctionRestorePolicy
{
    /// <summary>Append libraries; reject collisions.</summary>
    Append,
    /// <summary>Remove existing libraries before restoration.</summary>
    Flush,
    /// <summary>Replace colliding library names; function name conflicts still fail.</summary>
    Replace
}
/// <summary>Owned metadata for a registered function.</summary>
public sealed record RespireFunctionInfo(string Name, string? Description, string[] Flags);
/// <summary>Owned library metadata. Code is present only when WITHCODE was requested.</summary>
public sealed record RespireFunctionLibraryInfo(string Name, string Engine, RespireFunctionInfo[] Functions, string? Code);
/// <summary>Owned execution details. Command arguments retain their binary representation.</summary>
public sealed record RespireRunningFunction(string Name, byte[][] Command, long DurationMilliseconds);
/// <summary>Counts for one execution engine.</summary>
public sealed record RespireFunctionEngineStats(long LibrariesCount, long FunctionsCount);
/// <summary>Owned FUNCTION STATS result for one server.</summary>
public sealed record RespireFunctionStats(RespireRunningFunction? RunningFunction, IReadOnlyDictionary<string, RespireFunctionEngineStats> Engines);
