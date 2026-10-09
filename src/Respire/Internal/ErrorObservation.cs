using Reservoir;

namespace Respire.Internal;

/// <summary>
/// Failure-only ownership for one logical caller. This does not own a response or consume a ValueTask.
/// Keep the default owner on successful paths; acquire storage only at the first failure or retry.
/// </summary>
internal static class ErrorObservation
{
    private static readonly ObjectPool<Observation, PoolPolicy> Pool = new(32);
    private static long _rentalCount;

    // Allocation measurements cannot detect reuse of warmed pooled observations.
    internal static long RentalCountForTests => Interlocked.Read(ref _rentalCount);

    internal static FinalOwner StartFailure(int retryAttempts = 0)
    {
        var observation = Pool.Rent();
        Interlocked.Increment(ref _rentalCount);
        lock (observation.Gate)
        {
            observation.Generation = unchecked(observation.Generation + 1);
            observation.References = 1;
            observation.RetryAttempts = retryAttempts;
            observation.FinalPublished = false;
            return new(new Lease(observation, observation.Generation));
        }
    }

    // Called only after the final response owner has finished its cleanup. A supplied
    // owner transfers its completion right here; borrowed routes disable publication.
    internal static void FinishFinal(FinalOwner owner, Exception? error, bool observeErrors = true, int retryAttempts = 0)
    {
        try
        {
            if (error is not null && observeErrors)
            {
                if (owner.IsEmpty) owner = StartFailure(retryAttempts);
                owner.PublishFinal(error);
            }
        }
        finally { owner.Complete(); }
    }

    // Copies share one lease and completion right. Borrow creates a distinct completion right.
    internal readonly struct FinalOwner
    {
        private readonly Lease? _lease;
        internal FinalOwner(Lease lease) => _lease = lease;

        internal bool IsEmpty => _lease is null;
        internal int RetryAttempts => _lease?.RetryAttempts ?? 0;
        internal void SetRetryAttempts(int retryAttempts) => _lease?.SetRetryAttempts(retryAttempts);

        internal Borrower Borrow() => new(_lease?.Borrow());
        internal bool RecordHandled(Exception error) => _lease?.RecordHandled(error) ?? false;
        internal bool RecordRetry() => _lease?.RecordRetry() ?? false;
        internal bool PublishFinal(Exception error) => _lease?.PublishFinal(error) ?? false;
        internal void Complete() => _lease?.Complete();
    }

    internal readonly struct Borrower
    {
        private readonly Lease? _lease;
        internal Borrower(Lease? lease) => _lease = lease;

        internal Borrower Borrow() => new(_lease?.Borrow());
        internal bool RecordHandled(Exception error) => _lease?.RecordHandled(error) ?? false;
        internal void Complete() => _lease?.Complete();
    }

    internal sealed class Observation
    {
        internal readonly Lock Gate = new();
        internal long Generation;
        internal int References;
        internal int RetryAttempts;
        internal bool FinalPublished;
    }

    internal sealed class Lease(Observation observation, long generation)
    {
        private bool _completed;

        internal int RetryAttempts
        {
            get
            {
                lock (observation.Gate)
                    return observation.Generation == generation && !_completed ? observation.RetryAttempts : 0;
            }
        }

        internal void SetRetryAttempts(int retryAttempts)
        {
            lock (observation.Gate)
                if (IsOpen) observation.RetryAttempts = Math.Max(0, retryAttempts);
        }

        internal Lease? Borrow()
        {
            lock (observation.Gate)
            {
                // Borrow before final publication or completion. Closed leases return an
                // empty borrower; retry history cannot be recovered after this boundary.
                if (!IsOpen || observation.References == int.MaxValue) return null;
                var lease = new Lease(observation, generation);
                observation.References++;
                return lease;
            }
        }

        internal bool RecordHandled(Exception error)
        {
            if (error is null) return false;
            int retryAttempts;
            lock (observation.Gate)
            {
                if (!IsOpen) return false;
                retryAttempts = observation.RetryAttempts;
                if (observation.RetryAttempts < int.MaxValue) observation.RetryAttempts++;
            }
            // Never invoke an exporter while holding the ownership gate. The captured count
            // remains this event's count even if another retry, final inspection or reuse wins.
            RespireTelemetry.RecordError(error, internallyHandled: true, retryAttempts);
            return true;
        }

        internal bool RecordRetry()
        {
            lock (observation.Gate)
            {
                if (!IsOpen) return false;
                if (observation.RetryAttempts < int.MaxValue) observation.RetryAttempts++;
                return true;
            }
        }

        internal bool PublishFinal(Exception error)
        {
            if (error is null) return false;
            int retryAttempts;
            lock (observation.Gate)
            {
                if (!IsOpen) return false;
                observation.FinalPublished = true;
                retryAttempts = observation.RetryAttempts;
            }
            RespireTelemetry.RecordError(error, internallyHandled: false, retryAttempts);
            return true;
        }

        internal void Complete()
        {
            bool returnToPool;
            lock (observation.Gate)
            {
                if (_completed) return;
                _completed = true;
                if (observation.Generation != generation) return;
                returnToPool = --observation.References == 0;
            }
            if (returnToPool) Pool.Return(observation);
        }

        // Check under Gate. Telemetry rejection must never replace the caller's error.
        private bool IsOpen => observation.Generation == generation && !_completed && !observation.FinalPublished;
    }

    private readonly struct PoolPolicy : IPooledObjectPolicy<Observation>
    {
        public Observation Create() => new();
        public bool TryReset(Observation observation) => true;
    }
}
