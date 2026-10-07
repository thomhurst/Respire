namespace Respire.Networking;

internal sealed partial class RespireConnection
{
    /// <summary>Creates a borrowed view for friend tests; never use it for production coordination.</summary>
    internal TestInspection InspectForTests() => new(this);

    internal readonly ref struct TestInspection(RespireConnection owner)
    {
        /// <summary>Borrowed ring. Arrange quiescent admission and reply consumption before inspecting slots.</summary>
        /// <remarks>This does not acquire a source reference or transfer enqueue/dequeue ownership.</remarks>
        internal InflightRing Inflight => owner._inflight;
#if DEBUG
        /// <summary>Primitive copy; inspect only while the controlled receive stream is parked.</summary>
        internal int ResumedScalarCount => owner._resumedScalarCountForTests;
#endif
    }
}
