namespace Respire;

/// <summary>Process-wide command trace selection, independent of metric selection.</summary>
public sealed class RespireTracingOptions
{
    /// <summary>
    /// Returns whether to trace a command name, including its subcommand (for example,
    /// <c>CLIENT LIST</c>). Null allows all commands. No keys or values are supplied.
    /// </summary>
    /// <remarks>
    /// Called synchronously only when trace listeners exist. The predicate must be thread-safe,
    /// fast and nonblocking. Exceptions suppress the trace without affecting command execution
    /// or metrics. Pipelines and transactions require every member to pass.
    /// </remarks>
    public Func<string, bool>? CommandFilter { get; init; }
}

/// <summary>Selects command traces for every Respire client in the process.</summary>
/// <remarks>
/// Configure once at startup when possible. Configuration replacement is atomic and the last
/// writer wins. Selection is evaluated when a span would start; existing spans are unaffected.
/// The delegate is retained, so any state it captures remains the caller's responsibility.
/// This does not select maintenance spans or the separate Respire.Streaming activity source.
/// </remarks>
public static class RespireTracing
{
    private static Func<string, bool>? _commandFilter;

    /// <summary>Returns a configuration copy containing the current predicate.</summary>
    public static RespireTracingOptions Configuration => new() { CommandFilter = CommandFilter };

    /// <summary>Atomically replaces process-wide command trace selection.</summary>
    public static void Configure(RespireTracingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Volatile.Write(ref _commandFilter, options.CommandFilter);
    }

    internal static Func<string, bool>? CommandFilter => Volatile.Read(ref _commandFilter);

    internal static bool IncludesCommand(Func<string, bool>? filter, string operation)
    {
        if (filter is null) return true;
        try { return filter(operation); }
        catch (Exception) { return false; } // Diagnostics must not change command outcomes.
    }
}
