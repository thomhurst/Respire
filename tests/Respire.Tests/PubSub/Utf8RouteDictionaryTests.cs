using System.Text;
using Respire.Internal;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.PubSub;

public class Utf8RouteDictionaryTests
{
    [Test]
    public async Task Utf8Lookup_ReturnsCachedNameAndValue()
    {
        var routes = new Utf8RouteDictionary<int>();
        var name = new string("café".AsSpan());
        routes.Add(name, 42);

        var found = routes.TryGetValue(Encoding.UTF8.GetBytes(name), out var cachedName, out var value);

        await Assert.That(found).IsTrue();
        await Assert.That(ReferenceEquals(name, cachedName)).IsTrue();
        await Assert.That(value).IsEqualTo(42);
    }

    [Test]
    public async Task Remove_UpdatesNameAndUtf8Indexes()
    {
        var routes = new Utf8RouteDictionary<int>();
        routes.Add("channel", 42);

        await Assert.That(routes.Remove("channel")).IsTrue();
        await Assert.That(routes.TryGetValue("channel"u8, out _, out _)).IsFalse();
        await Assert.That(routes.TryGetValue("channel", out _)).IsFalse();
    }

    [Test]
    public async Task Add_MalformedUtf16IsRejectedWithoutMutatingIndexes()
    {
        var routes = new Utf8RouteDictionary<int>();
        routes.Add("valid", 42);

        await Assert.That(() => routes.Add("\uD800", 1)).Throws<ArgumentException>();
        await Assert.That(() => routes.Add("\uD801", 2)).Throws<ArgumentException>();

        await Assert.That(routes.Names).IsEquivalentTo(["valid"]);
        await Assert.That(routes.TryGetValue("valid"u8, out var name, out var value)).IsTrue();
        await Assert.That(name).IsEqualTo("valid");
        await Assert.That(value).IsEqualTo(42);
    }

#if NET9_0_OR_GREATER
    [Test]
    public async Task Hash_UsesPerDictionarySeed()
    {
        var first = new Utf8RouteKeyComparer(1);
        var second = new Utf8RouteKeyComparer(2);

        await Assert.That(first.Hash("notifications"u8)).IsNotEqualTo(second.Hash("notifications"u8));
    }

    [Test]
    public async Task Utf8Lookup_DoesNotAllocate()
    {
        var routes = new Utf8RouteDictionary<int>();
        routes.Add("notifications", 42);
        var name = "notifications"u8.ToArray();

        _ = routes.TryGetValue(name, out _, out _);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1_000; i++)
        {
            _ = routes.TryGetValue(name, out _, out _);
        }

        await Assert.That(GC.GetAllocatedBytesForCurrentThread() - before).IsEqualTo(0);
    }
#endif
}
