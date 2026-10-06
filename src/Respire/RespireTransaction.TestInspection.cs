using Respire.Networking;

namespace Respire;

public abstract partial class RespireTransactionBase
{
    /// <summary>Creates a borrowed view for friend tests; never use it for production coordination.</summary>
    internal TestInspection InspectForTests() => new(this);

    internal readonly ref struct TestInspection(RespireTransactionBase owner)
    {
        /// <summary>Borrowed pinned connection, or null for a transaction without WATCH.</summary>
        /// <remarks>The transaction owns its lease; do not return or dispose it. Do not race transaction disposal.</remarks>
        internal RespireConnection? WatchConnection => owner._watchConnection;
    }
}
