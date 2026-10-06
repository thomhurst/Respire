using System.Diagnostics.CodeAnalysis;

[assembly: SuppressMessage("Performance", "CA1815:Override equals and operator equals on value types",
    Scope = "type", Target = "~T:Respire.Coordination.RespireFencedLockAttempt",
    Justification = "Disposable fencing-lock acquisition outcome; consumers inspect Acquired and own the lease, rather than compare outcomes.")]
[assembly: SuppressMessage("Performance", "CA1815:Override equals and operator equals on value types",
    Scope = "type", Target = "~T:Respire.Coordination.RespireReadWriteLockAttempt",
    Justification = "Disposable read/write-lock acquisition outcome; consumers inspect Acquired and own the lease, rather than compare outcomes.")]
[assembly: SuppressMessage("Performance", "CA1815:Override equals and operator equals on value types",
    Scope = "type", Target = "~T:Respire.Coordination.RespireRedlockAttempt",
    Justification = "Disposable quorum-lock acquisition outcome; consumers inspect Acquired and own the lease, rather than compare outcomes.")]
[assembly: SuppressMessage("Performance", "CA1815:Override equals and operator equals on value types",
    Scope = "type", Target = "~T:Respire.Coordination.RespireSemaphorePermitAttempt",
    Justification = "Disposable semaphore acquisition outcome; consumers inspect Acquired and own the permit, rather than compare outcomes.")]
