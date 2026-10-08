using System.Runtime.ExceptionServices;
using Respire.Protocol;

namespace Respire.Networking;

/// <summary>Transport evidence for one completed logical send, independent of its exception type.</summary>
internal enum CommandWriteOutcome : byte
{
    /// <summary>No reliable evidence is available. This never authorizes an unsafe resend.</summary>
    Unknown,
    /// <summary>No part of this attempt's frame can have reached the transport.</summary>
    DefinitelyUnwritten,
    /// <summary>The frame was submitted, or remains eligible for submission after caller completion.</summary>
    EffectMayHaveReachedServer,
}

/// <summary>
/// A completed attempt and an immutable write outcome. Consuming the result preserves the original
/// exception, stack and cancellation token. Successful responses retain their normal ownership.
/// </summary>
internal readonly struct CommandAttemptResult(
    RespValue response, CommandWriteOutcome writeOutcome, ExceptionDispatchInfo? failure = null)
{
    internal CommandWriteOutcome WriteOutcome { get; } = writeOutcome;

    internal RespValue GetResult()
    {
        failure?.Throw();
        return response;
    }
}
