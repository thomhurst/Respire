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

#if NET9_0_OR_GREATER
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
