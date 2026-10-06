using System.Diagnostics.CodeAnalysis;

// Reviewed public contracts, scoped to exact symbols. New types and properties still get diagnosed.
// See docs/PERFORMANCE_ANALYZERS.md before adding or changing an exception.

[assembly: SuppressMessage("Performance", "CA1815:Override equals and operator equals on value types",
    Scope = "type", Target = "~T:Respire.BitFieldOperation",
    Justification = "Command-building token consumed by the encoder; it is not an equality or hash-key abstraction.")]
[assembly: SuppressMessage("Performance", "CA1815:Override equals and operator equals on value types",
    Scope = "type", Target = "~T:Respire.GeoSearchOrigin",
    Justification = "Command-building choice consumed by the GEO encoder; it is not an equality or hash-key abstraction.")]
[assembly: SuppressMessage("Performance", "CA1815:Override equals and operator equals on value types",
    Scope = "type", Target = "~T:Respire.GeoSearchShape",
    Justification = "Command-building choice consumed by the GEO encoder; it is not an equality or hash-key abstraction.")]
[assembly: SuppressMessage("Performance", "CA1815:Override equals and operator equals on value types",
    Scope = "type", Target = "~T:Respire.RespireCommand",
    Justification = "Command descriptor includes cached encoding and routing metadata; callers use its named properties rather than descriptor equality.")]
[assembly: SuppressMessage("Performance", "CA1815:Override equals and operator equals on value types",
    Scope = "type", Target = "~T:Respire.RespireCommandInterpolatedStringHandler",
    Justification = "Mutable compiler-driven command builder; comparing partially built handlers has no supported meaning.")]
[assembly: SuppressMessage("Performance", "CA1815:Override equals and operator equals on value types",
    Scope = "type", Target = "~T:Respire.RespireExpiryTime",
    Justification = "Decoded expiry status is consumed through Exists and UnixTimeMilliseconds, not as an equality or hash-key abstraction.")]
[assembly: SuppressMessage("Performance", "CA1815:Override equals and operator equals on value types",
    Scope = "type", Target = "~T:Respire.RespireKeyNotification",
    Justification = "Notification view retains message storage; consumers inspect its event and key rather than compare storage-backed views.")]
[assembly: SuppressMessage("Performance", "CA1815:Override equals and operator equals on value types",
    Scope = "type", Target = "~T:Respire.RespireLease",
    Justification = "Disposable pooled-buffer ownership handle; equality would confuse payload comparison with shared lease identity.")]
[assembly: SuppressMessage("Performance", "CA1815:Override equals and operator equals on value types",
    Scope = "type", Target = "~T:Respire.RespireLockAttempt",
    Justification = "Disposable lock-acquisition outcome; callers inspect Acquired and the owned Lock rather than compare attempts.")]
[assembly: SuppressMessage("Performance", "CA1815:Override equals and operator equals on value types",
    Scope = "type", Target = "~T:Respire.RespireMessage",
    Justification = "Delivery view includes serializer and owned payload storage; consumers compare explicit channel or payload values instead.")]
[assembly: SuppressMessage("Performance", "CA1815:Override equals and operator equals on value types",
    Scope = "type", Target = "~T:Respire.RespirePendingAwaiter`1",
    Justification = "Await protocol adapter for a pending operation, not a value intended for equality comparison.")]
[assembly: SuppressMessage("Performance", "CA1815:Override equals and operator equals on value types",
    Scope = "type", Target = "~T:Respire.RespireResult",
    Justification = "Disposable protocol view can borrow a parent's lifetime; it is not a value-equality or hash-key abstraction.")]
[assembly: SuppressMessage("Performance", "CA1815:Override equals and operator equals on value types",
    Scope = "type", Target = "~T:Respire.RespireSubKeyEnumerable",
    Justification = "Enumeration view over message storage; comparing view backing fields does not compare the subkey sequence.")]
[assembly: SuppressMessage("Performance", "CA1815:Override equals and operator equals on value types",
    Scope = "type", Target = "~T:Respire.RespireSubKeyEnumerable.Enumerator",
    Justification = "Mutable forward-only enumerator; equality of traversal state is not part of its contract.")]
[assembly: SuppressMessage("Performance", "CA1815:Override equals and operator equals on value types",
    Scope = "type", Target = "~T:Respire.RespireTtl",
    Justification = "Decoded TTL status is consumed through Exists and TimeToLive, not as an equality or hash-key abstraction.")]

// This response indexer returns existing storage, not a new defensive array on every access.
[assembly: SuppressMessage("Performance", "CA1819:Properties should not return arrays",
    Scope = "member", Target = "~P:Respire.RespireStreamEntry.Item(System.String)",
    Justification = "DTO property exposes existing response storage without allocating a copy; retain its established array API.")]
