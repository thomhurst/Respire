using Respire.Internal;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class ReadRetirementFailuresTests
{
    [Test]
    public async Task RepeatedFailurePreservesItsIdentityWithoutDuplication()
    {
        var failures = new ReadRetirementFailures();
        var error = new IOException("cleanup failed");
        failures.Add(error);
        failures.Add(error);
        var snapshot = failures.Snapshot();
        await Assert.That(snapshot.Count).IsEqualTo(1);
        var observed = await Assert.That(() => CleanupTasks.Rethrow(snapshot)).ThrowsExactly<IOException>();
        await Assert.That(observed).IsSameReferenceAs(error);
    }

    [Test]
    public async Task OverflowKeepsFirstFailuresAndReportsEveryOmittedOccurrence()
    {
        var failures = new ReadRetirementFailures();
        var errors = Enumerable.Range(0, ReadRetirementFailures.MaximumRetained + 7)
            .Select(index => new IOException($"cleanup {index}")).ToArray();
        foreach (var error in errors) failures.Add(error);
        failures.Add(errors[0]); // An already-retained identity does not consume another slot.
        failures.Add(errors[^1]); // Omitted occurrences are counted without retaining their identities.
        var snapshot = failures.Snapshot();
        await Assert.That(snapshot.Count).IsEqualTo(ReadRetirementFailures.MaximumRetained + 1);
        foreach (var error in errors.Take(ReadRetirementFailures.MaximumRetained))
            await Assert.That(snapshot.Any(retained => ReferenceEquals(retained, error))).IsTrue();
        foreach (var error in errors.Skip(ReadRetirementFailures.MaximumRetained))
            await Assert.That(snapshot.Any(retained => ReferenceEquals(retained, error))).IsFalse();
        var aggregate = await Assert.That(() => CleanupTasks.Rethrow(snapshot)).ThrowsExactly<AggregateException>();
        var summary = aggregate!.InnerExceptions.Single(error => error.Data.Contains("OmittedRetirementFailureCount"));
        await Assert.That((long)summary.Data["OmittedRetirementFailureCount"]!).IsEqualTo(8L);
        await Assert.That(summary.Message).Contains("8 additional replica retirement cleanup failures");
    }
}
