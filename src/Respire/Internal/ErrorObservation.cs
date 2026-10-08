using Reservoir;

namespace Respire.Internal;

/// <summary>
/// Failure-only ownership for one logical caller. This does not own a response or consume a ValueTask.
/// Keep the default owner on successful paths; acquire storage only at the first failure or retry.
/// </summary>
internal static class ErrorObservation
{
    private static readonly ObjectPool<Observation, PoolPolicy> Pool = new(32);

    internal static FinalOwner StartFailure()
    {
        var observation = Pool.Rent();
        lock (observation.Gate)
        {
            observation.Generation = unchecked(observation.Generation + 1);
            observation.References = 1;
            observation.RetryAttempts = 0;
            observation.FinalPublished = false;
            return new(new Lease(observation, observation.Generation));
        }
    }

    // Copies share one lease and completion right. Borrow creates a distinct completion right.
    internal readonly struct FinalOwner
    {
        private readonly Lease? _lease;
        internal FinalOwner(Lease lease) => _lease = lease;

        internal Borrower Borrow() => new(_lease?.Borrow());
        internal bool RecordHandled(Exception error) => _lease?.RecordHandled(error) ?? false;
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

        internal Lease? Borrow()
        {
            lock (observation.Gate)
            {
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
                if (observation.RetryAttempts < int.MaxValue) observation.RetryAttempts++;
                retryAttempts = observation.RetryAttempts;
            }
            // Never invoke an exporter while holding the ownership gate. The captured count
            // remains this event's count even if another retry, final inspection or reuse wins.
            RespireTelemetry.RecordError(error, internallyHandled: true, retryAttempts);
            return true;
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
