namespace Respire;

internal sealed partial class ClientSideCacheCoordinator
{
    /// <summary>Creates a borrowed view for friend tests; never use it for production coordination.</summary>
    internal TestInspection InspectForTests() => new(this);

    internal readonly ref struct TestInspection(ClientSideCacheCoordinator owner)
    {
        /// <summary>Borrowed gate used to hold a controlled shared-read admission barrier.</summary>
        /// <remarks>Own an EnterScope lease before inspecting gate-protected state; never dispose the gate.</remarks>
        internal Lock SharedReadGate => owner._sharedReadLock;
    }
}
