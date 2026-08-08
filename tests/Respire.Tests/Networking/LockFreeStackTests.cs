using Respire.Networking;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class LockFreeStackTests
{
    [Test]
    public async Task FullStack_ScansAndUsesEveryStripe()
    {
        const int capacity = 64;
        var stack = new LockFreeStack<object>(capacity);
        var items = Enumerable.Range(0, capacity).Select(_ => new object()).ToArray();

        foreach (var item in items)
        {
            await Assert.That(stack.TryPush(item)).IsTrue();
        }

        await Assert.That(stack.TryPush(new object())).IsFalse();

        var popped = new HashSet<object>(ReferenceEqualityComparer.Instance);
        while (stack.TryPop(out var item))
        {
            popped.Add(item);
        }

        await Assert.That(popped.Count).IsEqualTo(capacity);
        await Assert.That(items.All(popped.Contains)).IsTrue();
        await Assert.That(stack.TryPop(out _)).IsFalse();
    }
}
