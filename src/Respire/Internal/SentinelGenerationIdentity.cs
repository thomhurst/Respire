namespace Respire.Internal;

/// <summary>
/// One token per generation. Carries no transport or mutable generation state. Reducer inputs and
/// retirement effects accept only this token, so the reducer never reads a live transport.
/// </summary>
internal sealed class SentinelGenerationIdentity;
