using System.Text;

namespace Respire.Testing;

public sealed partial class RespireFakeServer
{
    private readonly List<Expectation> _expectations = [];
    private readonly List<ReceivedCommand> _commandHistory = [];
    private RespireFakeExpectationMode _expectationMode;

    /// <summary>Controls matching and history. Enable after connecting to exclude handshake commands.</summary>
    /// <remarks>Changing mode keeps existing expectations and history. Disabled stops recording new requests.</remarks>
    public RespireFakeExpectationMode ExpectationMode
    {
        get { lock (_gate) return _expectationMode; }
        set
        {
            if (!Enum.IsDefined(value)) throw new ArgumentOutOfRangeException(nameof(value));
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _expectationMode = value;
            }
        }
    }

    /// <summary>Registers an exact invocation count. Arguments exclude the command name; names ignore case.</summary>
    /// <remarks>The first matching rule with remaining occurrences wins, in registration order, atomically across
    /// connections. No global call order is required. Enables Loose mode if disabled. Replies override handlers,
    /// including unsupported commands, but cannot override connection, subscription or transaction control.
    /// A command is counted on receipt before faults, once even when queued in MULTI or later discarded.</remarks>
    public void Expect(string command, IReadOnlyList<RespireFakeArgument> arguments, int occurrences = 1,
        RespireFakeResponse? response = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        if (command.Any(char.IsWhiteSpace) || command.Any(char.IsControl))
            throw new ArgumentException("Specify one command name without whitespace or controls.", nameof(command));
        ArgumentNullException.ThrowIfNull(arguments);
        if (occurrences <= 0) throw new ArgumentOutOfRangeException(nameof(occurrences));
        var copied = arguments.ToArray();
        if (copied.Any(argument => argument is null)) throw new ArgumentException("Matchers cannot be null; use Any.", nameof(arguments));
        command = command.ToUpperInvariant();
        if (response is not null && command is "HELLO" or "CLIENT" or "MULTI" or "EXEC" or "DISCARD"
            or "WATCH" or "UNWATCH" or "SUBSCRIBE" or "UNSUBSCRIBE")
            throw new ArgumentException("Control commands can be observed but their replies cannot be overridden.", nameof(command));
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _expectations.Add(new(command, copied, occurrences, response?.Reply));
            if (_expectationMode == RespireFakeExpectationMode.Disabled) _expectationMode = RespireFakeExpectationMode.Loose;
        }
    }

    /// <summary>Returns an owned snapshot of received commands while expectations were enabled.</summary>
    public IReadOnlyList<RespireFakeCommand> CommandHistory
    {
        get
        {
            lock (_gate)
                return _commandHistory.Select((item, index) => new RespireFakeCommand(index + 1L, item.ConnectionId,
                    Token(item.Arguments[0]), item.Arguments, item.Matched)).ToArray();
        }
    }

    /// <summary>Verifies exact counts and strict-mode rejections, with expected and received command diagnostics.</summary>
    /// <remarks>Call after awaiting callers. This snapshots current receipts; it does not wait for execution or verify on disposal.</remarks>
    public void VerifyAllExpectations()
    {
        lock (_gate)
        {
            if (_expectations.All(item => item.Received == item.Occurrences) && !_commandHistory.Any(item => item.Rejected)) return;
            var message = new StringBuilder("Fake server expectation verification failed.\nExpected commands:\n");
            foreach (var item in _expectations)
                message.Append("  ").Append(item.Command).Append(' ').AppendJoin(" ", item.Arguments.Select(argument => argument.ToString()))
                    .Append(": expected ").Append(item.Occurrences).Append(", received ").Append(item.Received).AppendLine();
            message.AppendLine("Received commands (receipt order; unmatched includes mismatched arguments or excess calls):");
            for (var index = 0; index < _commandHistory.Count; index++)
            {
                var item = _commandHistory[index];
                message.Append("  #").Append(index + 1).Append(" connection ").Append(item.ConnectionId).Append(' ')
                    .Append(FormatCommand(item.Arguments)).Append(item.Matched ? " [matched]" : " [unexpected/unmatched]")
                    .Append(item.Rejected ? " [strict rejection]" : "").AppendLine();
            }
            throw new InvalidOperationException(message.ToString());
        }
    }

    /// <summary>Clears expectations and history and disables recording. Does not clear data, faults or queued overrides.</summary>
    public void ResetExpectations()
    {
        lock (_gate)
        {
            _expectations.Clear();
            _commandHistory.Clear();
            _expectationMode = RespireFakeExpectationMode.Disabled;
        }
    }

    private (FakeReply? Response, bool Rejected) ObserveCommand(Connection connection, byte[][] arguments)
    {
        lock (_gate)
        {
            if (_expectationMode == RespireFakeExpectationMode.Disabled) return default;
            var command = Token(arguments[0]);
            Expectation? exhausted = null;
            foreach (var item in _expectations)
            {
                if (item.Command != command || !item.Matches(arguments)) continue;
                if (item.Received >= item.Occurrences) { exhausted ??= item; continue; }
                item.Received++;
                // Own these bytes separately: handlers can retain or mutate their input arrays.
                Record(matched: true, rejected: false);
                return (item.Response, false);
            }
            if (exhausted is not null) exhausted.Received++;
            var rejected = _expectationMode == RespireFakeExpectationMode.Strict;
            Record(matched: false, rejected);
            return (rejected ? FakeReply.Error("ERR Unexpected command: " + FormatCommand(arguments)) : null, rejected);

            void Record(bool matched, bool rejected) => _commandHistory.Add(new(connection.Id,
                arguments.Select(argument => argument.ToArray()).ToArray(), matched, rejected));
        }
    }

    private static string FormatCommand(byte[][] arguments) => Token(arguments[0]) + (arguments.Length == 1 ? "" : " " +
        string.Join(" ", arguments.Skip(1).Select(argument => RespireFakeArgument.Format(argument))));

    private sealed record ReceivedCommand(long ConnectionId, byte[][] Arguments, bool Matched, bool Rejected);

    private sealed class Expectation(string command, RespireFakeArgument[] arguments, int occurrences, FakeReply? response)
    {
        internal string Command { get; } = command;
        internal RespireFakeArgument[] Arguments { get; } = arguments;
        internal int Occurrences { get; } = occurrences;
        internal long Received { get; set; }
        internal FakeReply? Response { get; } = response;
        internal bool Matches(byte[][] actual)
        {
            if (actual.Length != Arguments.Length + 1) return false;
            for (var index = 0; index < Arguments.Length; index++)
                if (!Arguments[index].Matches(actual[index + 1])) return false;
            return true;
        }
    }
}
