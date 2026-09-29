using Respire.Internal;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.PubSub;

public class ByteRouteDictionaryTests
{
    [Test]
    public async Task RandomOperationsMatchLosslessReferenceDictionary()
    {
        var random = new Random(300);
        var channels = Enumerable.Range(0, 80).Select(length =>
        {
            var bytes = new byte[length];
            random.NextBytes(bytes);
            return new RespireChannel(bytes);
        }).ToArray();
        var routes = new ByteRouteDictionary<int>();
        var expected = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var operation = 0; operation < 1000; operation++)
        {
            var channel = channels[random.Next(channels.Length)];
            var key = Convert.ToHexString(channel.Bytes.Span);
            switch (random.Next(4))
            {
                case 0:
                    if (expected.TryAdd(key, operation)) routes.Add(channel, operation);
                    else await Assert.That(() => routes.Add(channel, operation)).Throws<ArgumentException>();
                    break;
                case 1:
                    await Assert.That(routes.Remove(channel)).IsEqualTo(expected.Remove(key));
                    break;
                default:
                    var found = expected.TryGetValue(key, out var expectedValue);
                    await Assert.That(routes.TryGetValue(channel.Bytes.Span, out var cached, out var value)).IsEqualTo(found);
                    if (found)
                    {
                        await Assert.That(cached).IsEqualTo(channel);
                        await Assert.That(value).IsEqualTo(expectedValue);
                    }
                    break;
            }

            await Assert.That(routes.Names.Select(name => Convert.ToHexString(name.Bytes.Span)))
                .IsEquivalentTo(expected.Keys);
            await Assert.That(routes.Values).IsEquivalentTo(expected.Values);
            if (operation % 200 == 199)
            {
                routes.Clear();
                expected.Clear();
            }
        }
    }

    [Test]
    public async Task BinaryNamesRemainDistinctAndLongNamesRoundTrip()
    {
        var routes = new ByteRouteDictionary<int>();
        RespireChannel first = new byte[] { 0xff, 0 };
        RespireChannel second = new byte[] { 0xfe, 0 };
        RespireChannel longName = Enumerable.Repeat((byte)0xff, 4096).ToArray();
        routes.Add(first, 1);
        routes.Add(second, 2);
        routes.Add(longName, 3);
        await Assert.That(routes.TryGetValue(first.Bytes.Span, out var cached, out var value)).IsTrue();
        await Assert.That(cached).IsEqualTo(first);
        await Assert.That(value).IsEqualTo(1);
        await Assert.That(routes.TryGetValue(second, out value)).IsTrue();
        await Assert.That(value).IsEqualTo(2);
        await Assert.That(routes.TryGetValue(longName.Bytes.Span, out _, out value)).IsTrue();
        await Assert.That(value).IsEqualTo(3);
        await Assert.That(() => routes.Add(new RespireChannel(first.Bytes), 4)).Throws<ArgumentException>();
        await Assert.That(routes.Remove(first)).IsTrue();
        await Assert.That(routes.ContainsKey(second)).IsTrue();
        routes.Clear();
        await Assert.That(routes.TryGetValue(longName.Bytes.Span, out _, out _)).IsFalse();
        await Assert.That(routes.Names).IsEmpty();
    }

    [Test]
    public async Task Utf8Lookup_ReturnsCachedNameAndValue()
    {
        var routes = new ByteRouteDictionary<int>();
        RespireChannel name = "café";
        routes.Add(name, 42);

        var found = routes.TryGetValue(name.Bytes.Span, out var cachedName, out var value);

        await Assert.That(found).IsTrue();
        await Assert.That(name.Bytes.Equals(cachedName.Bytes)).IsTrue();
        await Assert.That(value).IsEqualTo(42);
    }

    [Test]
    public async Task Remove_UpdatesNameAndUtf8Indexes()
    {
        var routes = new ByteRouteDictionary<int>();
        routes.Add("channel", 42);

        await Assert.That(routes.Remove("channel")).IsTrue();
        await Assert.That(routes.TryGetValue("channel"u8, out _, out _)).IsFalse();
        await Assert.That(routes.TryGetValue("channel", out _)).IsFalse();
    }

    [Test]
    public async Task Add_MalformedUtf16IsRejectedWithoutMutatingIndexes()
    {
        var routes = new ByteRouteDictionary<int>();
        routes.Add("valid", 42);

        await Assert.That(() => routes.Add("\uD800", 1)).Throws<ArgumentException>();
        await Assert.That(() => routes.Add("\uD801", 2)).Throws<ArgumentException>();

        await Assert.That(routes.Names).IsEquivalentTo([(RespireChannel)"valid"]);
        await Assert.That(routes.TryGetValue("valid"u8, out var name, out var value)).IsTrue();
        await Assert.That(name).IsEqualTo((RespireChannel)"valid");
        await Assert.That(value).IsEqualTo(42);
    }

    [Test]
    public async Task Hash_UsesPerDictionarySeed()
    {
        var first = new ByteRouteHasher(1);
        var second = new ByteRouteHasher(2);

        await Assert.That(first.Hash("notifications"u8)).IsNotEqualTo(second.Hash("notifications"u8));
    }

    [Test]
    public async Task Utf8Lookup_DoesNotAllocate()
    {
        var routes = new ByteRouteDictionary<int>();
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
}
