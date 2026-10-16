namespace Respire.Testing;

/// <summary>A snapshot of one received command, including transaction queueing but not internal EXEC traversal.</summary>
public sealed class RespireFakeCommand
{
    /// <summary>The receipt order across all connections, starting at one after reset.</summary>
    public long Sequence { get; }
    /// <summary>The fake connection which received this command.</summary>
    public long ConnectionId { get; }
    /// <summary>The uppercase command name.</summary>
    public string Command { get; }
    /// <summary>Copies of the complete wire arguments, excluding the command name.</summary>
    public IReadOnlyList<ReadOnlyMemory<byte>> Arguments { get; }
    /// <summary>Whether an expectation with remaining occurrences matched.</summary>
    public bool Matched { get; }

    internal RespireFakeCommand(long sequence, long connectionId, string command, byte[][] arguments, bool matched)
    {
        Sequence = sequence;
        ConnectionId = connectionId;
        Command = command;
        Arguments = System.Array.AsReadOnly(arguments.Skip(1).Select(value => (ReadOnlyMemory<byte>)value.ToArray()).ToArray());
        Matched = matched;
    }

    /// <summary>Returns the command and escaped arguments for diagnostics.</summary>
    public override string ToString() => Command + (Arguments.Count == 0 ? "" : " " +
        string.Join(" ", Arguments.Select(value => RespireFakeArgument.Format(value.Span))));
}

/// <summary>Controls whether received commands must match registered expectations.</summary>
public enum RespireFakeExpectationMode
{
    /// <summary>No matching or command history. The default for a new server.</summary>
    Disabled,
    /// <summary>Unmatched commands use the normal fake behavior. Registered counts are still verified.</summary>
    Loose,
    /// <summary>Unmatched commands return an error and cause verification to fail.</summary>
    Strict,
}
