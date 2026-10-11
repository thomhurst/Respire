namespace Respire.Networking;

internal abstract partial class PendingResponse
{
    /// <summary>Creates a borrowed view for friend tests; never use it for production coordination.</summary>
    internal TestInspection InspectForTests() => new(this);

    internal readonly ref struct TestInspection(PendingResponse owner)
    {
        /// <summary>The registered token after admission, before registration cleanup or source recycling.</summary>
        /// <remarks>Keep the source alive and prevent concurrent registration/disposal while reading.</remarks>
        internal CancellationToken RegisteredCancellationToken => owner._cancellationRegistration.Token;

        /// <summary>Primitive ownership snapshot while the fixture holds the receive reference.</summary>
        /// <remarks>Prevent reply dequeue and source recycling before inspecting caller consumption.</remarks>
        internal int ReferenceCount => Volatile.Read(ref owner._refs);
    }
}
