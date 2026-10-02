using Respire.Infrastructure;
using Respire.Internal;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class ClusterReplicaSelectorTests
{
    [Test]
    public async Task EmptySetHasNoCandidate()
    {
        var selector = new ClusterReplicaSelector(new([], TimeSpan.Zero));
        await Assert.That(selector.TryNext(out _)).IsFalse();
    }

    [Test]
    public async Task EachPassRotatesAndVisitsEachCandidateOnce()
    {
        await using var first = RespireConnectionMultiplexer.Create("first", 6379);
        await using var second = RespireConnectionMultiplexer.Create("second", 6379);
        await using var third = RespireConnectionMultiplexer.Create("third", 6379);
        var routes = new ClusterReplicaSet([first, second, third], TimeSpan.Zero);
        RespireConnectionMultiplexer[][] expected = [[second, third, first], [third, first, second], [first, second, third]];
        foreach (var pass in expected)
        {
            var selector = new ClusterReplicaSelector(routes);
            foreach (var node in pass)
            {
                await Assert.That(selector.TryNext(out var candidate)).IsTrue();
                await Assert.That(candidate).IsSameReferenceAs(node);
            }
            await Assert.That(selector.TryNext(out _)).IsFalse();
        }
    }

    [Test]
    public async Task SkipsCandidatesRetiredAfterSelectionStarts()
    {
        await using var first = RespireConnectionMultiplexer.Create("first", 6379);
        await using var second = RespireConnectionMultiplexer.Create("second", 6379);
        var selector = new ClusterReplicaSelector(new([first, second], TimeSpan.Zero));
        await second.RetireAsync();
        await Assert.That(selector.TryNext(out var candidate)).IsTrue();
        await Assert.That(candidate).IsSameReferenceAs(first);
        await Assert.That(selector.TryNext(out _)).IsFalse();
    }
}
