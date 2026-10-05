using System.Collections.Frozen;

namespace Respire;

/// <summary>Redis observability metric groups. These do not select Respire-specific diagnostics or traces.</summary>
[Flags]
public enum RespireMetricGroups
{
    /// <summary>No Redis metric groups.</summary>
    None = 0,
    /// <summary>Availability and recovery.</summary>
    Resiliency = 1,
    /// <summary>Basic connection lifecycle measurements.</summary>
    ConnectionBasic = 2,
    /// <summary>Detailed connection measurements.</summary>
    ConnectionAdvanced = 4,
    /// <summary>Logical command latency.</summary>
    Command = 8,
    /// <summary>Client-side cache lookups and removals.</summary>
    ClientSideCaching = 16,
    /// <summary>Pub/sub processing measurements.</summary>
    PubSub = 32,
    /// <summary>Stream processing measurements.</summary>
    Streaming = 64,
    /// <summary>The Redis specification's default groups.</summary>
    Default = Resiliency | ConnectionBasic,
    /// <summary>All Redis metric groups.</summary>
    All = Default | ConnectionAdvanced | Command | ClientSideCaching | PubSub | Streaming,
}

/// <summary>Process-wide Redis metric selection. Configure before creating clients or attaching exporters.</summary>
public sealed record RespireMetricsOptions
{
    /// <summary>Enabled groups. Selecting a group does not create measurements that Respire does not implement.</summary>
    public RespireMetricGroups Groups { get; init; } = RespireMetricGroups.Default;

    /// <summary>Allowed command names, case insensitive. Empty allows all commands. Collections are copied on configuration.</summary>
    public IReadOnlyCollection<string> CommandAllowList { get; init; } = [];

    /// <summary>Blocked command names, case insensitive. Blocking takes precedence over allowing. Collections are copied on configuration.</summary>
    public IReadOnlyCollection<string> CommandBlockList { get; init; } = [];
}

/// <summary>Selects Redis metrics for every Respire client in the process, independently of tracing.</summary>
/// <remarks>Respire does not create or own an OpenTelemetry provider. Configure is atomic and
/// last-writer-wins across the process; configure once in application startup when possible. In-flight
/// operations retain their selection when telemetry starts, including selection captured before
/// connection acquisition. Respire-specific instruments are unaffected.</remarks>
public static class RespireMetrics
{
    private static Selection _selection = new(new());

    /// <summary>Returns an owned copy of the current configuration.</summary>
    public static RespireMetricsOptions Configuration => Current.ToOptions();

    /// <summary>Validates and copies configuration, then atomically replaces process-wide selection.</summary>
    public static void Configure(RespireMetricsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var selection = new Selection(options);
        Volatile.Write(ref _selection, selection);
    }

    internal static Selection Current => Volatile.Read(ref _selection);

    internal sealed class Selection
    {
        internal readonly RespireMetricGroups Groups;
        internal readonly bool HasCommandFilters;
        private readonly FrozenSet<string> _allow;
        private readonly FrozenSet<string> _block;

        internal Selection(RespireMetricsOptions options)
        {
            if ((options.Groups & ~RespireMetricGroups.All) != 0)
                throw new ArgumentOutOfRangeException(nameof(options), "Unknown Redis metric group.");
            Groups = options.Groups;
            _allow = CopyCommands(options.CommandAllowList, nameof(options.CommandAllowList));
            _block = CopyCommands(options.CommandBlockList, nameof(options.CommandBlockList));
            HasCommandFilters = _allow.Count != 0 || _block.Count != 0;
        }

        /// <summary>Requires every bit in a composite group; None is always included.</summary>
        internal bool Includes(RespireMetricGroups group) => (Groups & group) == group;
        internal bool IncludesCommand(string operation)
            => !_block.Contains(operation) && (_allow.Count == 0 || _allow.Contains(operation));

        internal RespireMetricsOptions ToOptions() => new()
        {
            Groups = Groups,
            CommandAllowList = _allow.ToArray(),
            CommandBlockList = _block.ToArray(),
        };

        private static FrozenSet<string> CopyCommands(IEnumerable<string> commands, string parameter)
        {
            ArgumentNullException.ThrowIfNull(commands, parameter);
            var copy = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var command in commands)
            {
                if (string.IsNullOrEmpty(command) || command.Length > 64)
                    throw new ArgumentException("Command names must contain 1 to 64 ASCII characters.", parameter);
                var previousSpace = true;
                foreach (var character in command)
                {
                    if (character == ' ')
                    {
                        if (previousSpace) throw new ArgumentException("Command names cannot contain leading or repeated spaces.", parameter);
                        previousSpace = true;
                    }
                    else
                    {
                        if (!(char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-'))
                            throw new ArgumentException("Command names must use ASCII letters, digits, '.', '_', '-', or single spaces.", parameter);
                        previousSpace = false;
                    }
                }
                if (previousSpace) throw new ArgumentException("Command names cannot contain trailing spaces.", parameter);
                copy.Add(command.ToUpperInvariant());
            }
            return copy.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
        }
    }
}
