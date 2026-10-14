using Respire.Internal;
using Respire.Networking;

namespace Respire.Tests;

public sealed class CommandRetryPolicyTests
{
    private static readonly Exception ConnectionFailure = new RespireConnectionException("lost reply");

    [Test]
    public async Task DefaultPolicyAllowsOnlyAuditedReadsAfterPossibleExecution()
    {
        var policy = new CommandRetryPolicy(new() { Jitter = 0 });
        foreach (var category in Enum.GetValues<RespireCommandRetryCategory>())
        {
            var allowed = category is RespireCommandRetryCategory.Always or RespireCommandRetryCategory.Connection
                or RespireCommandRetryCategory.ReadOnly;
            foreach (var outcome in new[] { CommandWriteOutcome.Unknown, CommandWriteOutcome.EffectMayHaveReachedServer })
                await Assert.That(CanRetry(policy, category, outcome)).IsEqualTo(allowed);
            await Assert.That(CanRetry(policy, category, CommandWriteOutcome.DefinitelyUnwritten))
                .IsEqualTo(category != RespireCommandRetryCategory.Never);
        }
    }

    [Test]
    public async Task EveryMaximumCategoryHasAnExplicitRiskBoundary()
    {
        // Independent expected sets: numeric ordering of the enum is not the policy contract.
        var permitted = new Dictionary<RespireCommandRetryCategory, RespireCommandRetryCategory[]>
        {
            [RespireCommandRetryCategory.Never] = [],
            [RespireCommandRetryCategory.Always] = [RespireCommandRetryCategory.Always],
            [RespireCommandRetryCategory.Connection] = [RespireCommandRetryCategory.Always, RespireCommandRetryCategory.Connection],
            [RespireCommandRetryCategory.ReadOnly] = [RespireCommandRetryCategory.Always, RespireCommandRetryCategory.Connection, RespireCommandRetryCategory.ReadOnly],
            [RespireCommandRetryCategory.WriteChecked] = [RespireCommandRetryCategory.Always, RespireCommandRetryCategory.Connection, RespireCommandRetryCategory.ReadOnly, RespireCommandRetryCategory.WriteChecked],
            [RespireCommandRetryCategory.WriteLastWins] = [RespireCommandRetryCategory.Always, RespireCommandRetryCategory.Connection, RespireCommandRetryCategory.ReadOnly, RespireCommandRetryCategory.WriteChecked, RespireCommandRetryCategory.WriteLastWins],
            [RespireCommandRetryCategory.WriteAccumulating] = [RespireCommandRetryCategory.Always, RespireCommandRetryCategory.Connection, RespireCommandRetryCategory.ReadOnly, RespireCommandRetryCategory.WriteChecked, RespireCommandRetryCategory.WriteLastWins, RespireCommandRetryCategory.WriteAccumulating],
            [RespireCommandRetryCategory.ServerAdmin] = [RespireCommandRetryCategory.Always, RespireCommandRetryCategory.Connection, RespireCommandRetryCategory.ReadOnly, RespireCommandRetryCategory.WriteChecked, RespireCommandRetryCategory.WriteLastWins, RespireCommandRetryCategory.WriteAccumulating, RespireCommandRetryCategory.ServerAdmin],
        };
        foreach (var (maximum, expected) in permitted)
        {
            var policy = new CommandRetryPolicy(new() { MaxCommandRetryCategory = maximum });
            foreach (var category in Enum.GetValues<RespireCommandRetryCategory>())
            {
                foreach (var outcome in new[] { CommandWriteOutcome.Unknown, CommandWriteOutcome.EffectMayHaveReachedServer })
                    await Assert.That(CanRetry(policy, category, outcome)).IsEqualTo(expected.Contains(category));
                await Assert.That(CanRetry(policy, category, CommandWriteOutcome.DefinitelyUnwritten))
                    .IsEqualTo(maximum != RespireCommandRetryCategory.Never && category != RespireCommandRetryCategory.Never);
            }
        }
    }

    [Test]
    public async Task ExceptionNotSubmittedFlagCannotUpgradeTransportEvidence()
    {
        var failure = new RespireConnectionException("not submitted flag is not attempt evidence") { IsCommandNotSubmitted = true };
        var policy = new CommandRetryPolicy(new());
        await Assert.That(CanRetry(policy, RespireCommandRetryCategory.WriteAccumulating, CommandWriteOutcome.Unknown, failure)).IsFalse();
        await Assert.That(CanRetry(policy, RespireCommandRetryCategory.WriteAccumulating, CommandWriteOutcome.EffectMayHaveReachedServer, failure)).IsFalse();
        await Assert.That(CanRetry(policy, RespireCommandRetryCategory.WriteAccumulating, CommandWriteOutcome.DefinitelyUnwritten, failure)).IsTrue();
    }

    [Test]
    public async Task InvalidInvocationMetadataFailsClosed()
    {
        var policy = new CommandRetryPolicy(new());
        await Assert.That(CanRetry(policy, (RespireCommandRetryCategory)255, CommandWriteOutcome.DefinitelyUnwritten)).IsFalse();
        await Assert.That(CanRetry(policy, RespireCommandRetryCategory.ReadOnly, (CommandWriteOutcome)255)).IsFalse();
        // A recognized caller-supplied descriptor still has no audited or explicit retry category.
        await Assert.That(CanRetry(policy, RespireCommand.Create("GET").RetryCategory, CommandWriteOutcome.DefinitelyUnwritten)).IsFalse();
        await Assert.That(CanRetry(policy, RespireCommands.String.GET.RetryCategory, CommandWriteOutcome.Unknown)).IsTrue();
    }

    [Test]
    public async Task SelectedTransientErrorsStillRequireSafeExecutionRisk()
    {
        var cases = new (RespireRetryableErrors Selection, Exception Failure)[]
        {
            (RespireRetryableErrors.ConnectionLoss, ConnectionFailure),
            (RespireRetryableErrors.Loading, new RespireServerException("LOADING loading data")),
            (RespireRetryableErrors.TryAgain, new RespireServerException("TRYAGAIN migration")),
            (RespireRetryableErrors.MasterDown, new RespireServerException("MASTERDOWN unavailable")),
            (RespireRetryableErrors.ReadOnly, new RespireServerException("READONLY replica")),
        };
        foreach (var (selection, failure) in cases)
        {
            var selected = new CommandRetryPolicy(new() { RetryableErrors = selection });
            var disabled = new CommandRetryPolicy(new() { RetryableErrors = RespireRetryableErrors.None });
            await Assert.That(CanRetry(selected, RespireCommandRetryCategory.ReadOnly, CommandWriteOutcome.Unknown, failure)).IsTrue();
            await Assert.That(CanRetry(selected, RespireCommandRetryCategory.WriteAccumulating, CommandWriteOutcome.Unknown, failure)).IsFalse();
            await Assert.That(CanRetry(disabled, RespireCommandRetryCategory.ReadOnly, CommandWriteOutcome.DefinitelyUnwritten, failure)).IsFalse();
            foreach (var (otherSelection, otherFailure) in cases)
                await Assert.That(CanRetry(selected, RespireCommandRetryCategory.ReadOnly, CommandWriteOutcome.DefinitelyUnwritten, otherFailure))
                    .IsEqualTo(otherSelection == selection);
        }
    }

    [Test]
    public async Task AdmissionCancellationAndPermanentFailuresAreNeverRetried()
    {
        var failures = new Exception[]
        {
            new RespireCircuitOpenException(new("redis.test"), TimeSpan.Zero),
            new RespireAuthenticationException("denied"),
            new RespireReconnectLimitException("exhausted"),
            new RespireProtocolException("invalid frame"),
            new RespireConfigurationException("invalid options"),
            new OperationCanceledException(), new TimeoutException(), new ObjectDisposedException("client"),
            new RespireCrossSlotException("MGET", 1, 2),
            new RespireServerException("CROSSSLOT server validation"),
            new RespireServerException("ERR invalid argument"), new RespireServerException("WRONGTYPE wrong kind"),
            new RespireServerException("BUSY script running"), new RespireServerException("CLUSTERDOWN unavailable"),
            new RespireServerException("MOVED 1 redis.test:6379"), new RespireServerException("ASK 1 redis.test:6379"),
            new RespireException("wrapper", new RespireServerException("TRYAGAIN nested")),
        };
        var policy = new CommandRetryPolicy(new() { MaxCommandRetryCategory = RespireCommandRetryCategory.ServerAdmin });
        foreach (var failure in failures)
            foreach (var outcome in Enum.GetValues<CommandWriteOutcome>())
                await Assert.That(CanRetry(policy, RespireCommandRetryCategory.Always, outcome, failure)).IsFalse();
    }

    [Test]
    public async Task AttemptsCancellationAndOneLogicalDeadlineBoundRetries()
    {
        var policy = new CommandRetryPolicy(new() { Jitter = 0 });
        await Assert.That(Decide(policy, 1, TimeSpan.FromMilliseconds(11), default, out var delay)).IsTrue();
        await Assert.That(delay).IsEqualTo(TimeSpan.FromMilliseconds(10));
        await Assert.That(Decide(policy, 2, TimeSpan.FromMilliseconds(21), default, out delay)).IsTrue();
        await Assert.That(delay).IsEqualTo(TimeSpan.FromMilliseconds(20));
        await Assert.That(Decide(policy, 3, TimeSpan.MaxValue, default, out delay)).IsFalse();
        await Assert.That(delay).IsEqualTo(TimeSpan.Zero);
        await Assert.That(Decide(policy, 1, TimeSpan.FromMilliseconds(10), default, out delay)).IsFalse();
        await Assert.That(delay).IsEqualTo(TimeSpan.Zero);
        await Assert.That(Decide(policy, 1, TimeSpan.Zero, default, out _)).IsFalse();
        await Assert.That(Decide(policy, 1, TimeSpan.FromTicks(-1), default, out _)).IsFalse();
        using var caller = new CancellationTokenSource();
        await caller.CancelAsync();
        await Assert.That(Decide(policy, 1, TimeSpan.MaxValue, caller.Token, out _)).IsFalse();
        await Assert.That(Decide(new(new() { MaxAttempts = 1 }), 1, TimeSpan.MaxValue, default, out _)).IsFalse();
        await Assert.That(Decide(new(new() { InitialBackoff = TimeSpan.Zero }), 1, TimeSpan.FromTicks(1), default, out delay)).IsTrue();
        await Assert.That(delay).IsEqualTo(TimeSpan.Zero);
    }

    [Test]
    public async Task BackoffCapsExponentialGrowthAndJitterEndpoints()
    {
        foreach (var jitter in new[] { 0d, 0.25, 1d })
        {
            var policy = new CommandRetryPolicy(new()
            {
                InitialBackoff = TimeSpan.FromMilliseconds(10), MaxBackoff = TimeSpan.FromMilliseconds(25), Jitter = jitter,
            });
            foreach (var attempt in new[] { 1, 2, 3, 100, int.MaxValue })
            {
                var ceiling = attempt == 1 ? 10 : attempt == 2 ? 20 : 25;
                await Assert.That(policy.GetDelay(attempt, 1)).IsEqualTo(TimeSpan.FromMilliseconds(ceiling));
                await Assert.That(policy.GetDelay(attempt, 0)).IsEqualTo(TimeSpan.FromMilliseconds(ceiling * (1 - jitter)));
                await Assert.That(policy.GetDelay(attempt, 0.5)).IsEqualTo(TimeSpan.FromMilliseconds(ceiling * (1 - jitter / 2)));
            }
        }
        var zero = new CommandRetryPolicy(new() { InitialBackoff = TimeSpan.Zero });
        await Assert.That(zero.GetDelay(int.MaxValue, 1)).IsEqualTo(TimeSpan.Zero);
    }

    [Test]
    public async Task InvalidPolicyValuesAreRejectedBeforeUse()
    {
        var invalid = new RespireRetryPolicy[]
        {
            new() { MaxAttempts = 0 }, new() { MaxAttempts = 101 },
            new() { MaxCommandRetryCategory = (RespireCommandRetryCategory)255 },
            new() { RetryableErrors = (RespireRetryableErrors)(-1) }, new() { RetryableErrors = (RespireRetryableErrors)32 },
            new() { InitialBackoff = TimeSpan.FromTicks(-1) }, new() { InitialBackoff = TimeSpan.FromMinutes(2) },
            new() { MaxBackoff = TimeSpan.Zero }, new() { MaxBackoff = TimeSpan.FromMinutes(2) },
            new() { Jitter = -0.1 }, new() { Jitter = 1.1 }, new() { Jitter = double.NaN },
            new() { Jitter = double.PositiveInfinity }, new() { Jitter = double.NegativeInfinity },
        };
        foreach (var settings in invalid)
            await Assert.That(() => new CommandRetryPolicy(settings)).ThrowsExactly<ArgumentOutOfRangeException>();
        await Assert.That(() => new CommandRetryPolicy(null!)).ThrowsExactly<ArgumentNullException>();
        // Maximum valid settings remain bounded even at extreme attempt numbers.
        var maximum = new CommandRetryPolicy(new() { MaxAttempts = 100, InitialBackoff = TimeSpan.FromMinutes(1), MaxBackoff = TimeSpan.FromMinutes(1) });
        await Assert.That(maximum.GetDelay(int.MaxValue, 1)).IsEqualTo(TimeSpan.FromMinutes(1));
    }

    [Test]
    public async Task InvalidDecisionInputsAreRejected()
    {
        var policy = new CommandRetryPolicy(new());
        await Assert.That(() => Decide(policy, 0, TimeSpan.MaxValue, default, out _)).ThrowsExactly<ArgumentOutOfRangeException>();
        await Assert.That(() => policy.TryGetDelay(null!, CommandWriteOutcome.Unknown, RespireCommandRetryCategory.ReadOnly,
            1, TimeSpan.MaxValue, default, out _)).ThrowsExactly<ArgumentNullException>();
        foreach (var sample in new[] { -0.1, 1.1, double.NaN, double.PositiveInfinity, double.NegativeInfinity })
            await Assert.That(() => policy.GetDelay(1, sample)).ThrowsExactly<ArgumentOutOfRangeException>();
    }

    private static bool CanRetry(CommandRetryPolicy policy, RespireCommandRetryCategory category,
        CommandWriteOutcome outcome, Exception? failure = null)
        => policy.TryGetDelay(failure ?? ConnectionFailure, outcome, category, 1, TimeSpan.MaxValue, default, out _);

    private static bool Decide(CommandRetryPolicy policy, int completedAttempts, TimeSpan remaining,
        CancellationToken cancellationToken, out TimeSpan delay)
        => policy.TryGetDelay(ConnectionFailure, CommandWriteOutcome.Unknown, RespireCommandRetryCategory.ReadOnly,
            completedAttempts, remaining, cancellationToken, out delay);
}
