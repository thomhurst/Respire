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
            observation.Generation = checked(observation.Generation + 1);
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

        internal Borrower Borrow() => new(RequiredLease().Borrow());
        internal void RecordHandled(Exception error) => RequiredLease().RecordHandled(error);
        internal bool PublishFinal(Exception error) => RequiredLease().PublishFinal(error);
        internal void Complete() => _lease?.Complete();

        private Lease RequiredLease() => _lease
            ?? throw new InvalidOperationException("Acquire an error observation only after a failure or retry.");
    }

    internal readonly struct Borrower
    {
        private readonly Lease? _lease;
        internal Borrower(Lease lease) => _lease = lease;

        internal Borrower Borrow() => new(RequiredLease().Borrow());
        internal void RecordHandled(Exception error) => RequiredLease().RecordHandled(error);
        internal void Complete() => _lease?.Complete();

        private Lease RequiredLease() => _lease
            ?? throw new InvalidOperationException("A borrower requires a live error observation.");
    }

    internal sealed class Observation
    {
        internal readonly object Gate = new();
        internal long Generation;
        internal int References;
        internal int RetryAttempts;
        internal bool FinalPublished;
    }

    internal sealed class Lease(Observation observation, long generation)
    {
        private bool _completed;

        internal Lease Borrow()
        {
            lock (observation.Gate)
            {
                RequireOpen();
                var lease = new Lease(observation, generation);
                observation.References = checked(observation.References + 1);
                return lease;
            }
        }

        internal void RecordHandled(Exception error)
        {
            ArgumentNullException.ThrowIfNull(error);
            int retryAttempts;
            lock (observation.Gate)
            {
                RequireOpen();
                observation.RetryAttempts = checked(observation.RetryAttempts + 1);
                retryAttempts = observation.RetryAttempts;
            }
            // Never invoke an exporter while holding the ownership gate. The captured count
            // remains this event's count even if another retry, final inspection or reuse wins.
            RespireTelemetry.RecordError(error, internallyHandled: true, retryAttempts);
        }

        internal bool PublishFinal(Exception error)
        {
            ArgumentNullException.ThrowIfNull(error);
            int retryAttempts;
            lock (observation.Gate)
            {
                RequireGeneration();
                if (observation.FinalPublished) return false;
                RequireOpen();
                observation.FinalPublished = true;
                retryAttempts = observation.RetryAttempts;
            }
            RespireTelemetry.RecordError(error, internallyHandled: false, retryAttempts);
            return true;
        }

        internal void Complete()
        {
            lock (observation.Gate)
            {
                if (_completed) return;
                RequireGeneration();
                _completed = true;
                if (--observation.References == 0) Pool.Return(observation);
            }
        }

        private void RequireGeneration()
        {
            if (observation.Generation != generation)
                throw new InvalidOperationException("The error observation lease belongs to a reused generation.");
        }

        private void RequireOpen()
        {
            RequireGeneration();
            if (_completed || observation.FinalPublished)
                throw new InvalidOperationException("The error observation lease has completed.");
        }
    }

    private readonly struct PoolPolicy : IPooledObjectPolicy<Observation>
    {
        public Observation Create() => new();
        public bool TryReset(Observation observation) => true;
    }
}
