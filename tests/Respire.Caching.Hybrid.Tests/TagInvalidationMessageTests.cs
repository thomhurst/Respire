using System.Buffers.Binary;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Caching.Hybrid.Tests;

public class TagInvalidationMessageTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ValidTimestampExtremesRoundtrip(bool maximum)
    {
        var timestamp = maximum ? DateTimeOffset.MaxValue : DateTimeOffset.MinValue;
        var protocol = new TagInvalidationMessage("n", 64);
        var frame = protocol.Encode(Guid.NewGuid(), "tag", timestamp);
        await Assert.That(protocol.TryDecode(frame, out _, out _, out var decoded)).IsTrue();
        await Assert.That(decoded).IsEqualTo(timestamp);
    }

    [Test]
    public async Task BoundedFramesPreserveUnicodeAndTheOriginalTimestamp()
    {
        var protocol = new TagInvalidationMessage("namespace:雪", 128);
        var sender = Guid.NewGuid();
        var timestamp = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero).AddTicks(123);
        var frame = protocol.Encode(sender, "tag:🍃", timestamp);
        await Assert.That(protocol.TryDecode(frame, out var decodedSender, out var tag, out var decodedTimestamp)).IsTrue();
        await Assert.That(decodedSender).IsEqualTo(sender);
        await Assert.That(tag).IsEqualTo("tag:🍃");
        await Assert.That(decodedTimestamp).IsEqualTo(timestamp);
        await Assert.That(new TagInvalidationMessage("another namespace", 128).TryDecode(frame, out _, out _, out _)).IsFalse();
        var exact = new TagInvalidationMessage("n", 34);
        await Assert.That(exact.Encode(sender, "a", timestamp).Length).IsEqualTo(34);
        await Assert.That(() => exact.Encode(sender, "aa", timestamp)).Throws<ArgumentException>();
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(3)]
    [Arguments(4)]
    [Arguments(5)]
    [Arguments(6)]
    [Arguments(7)]
    [Arguments(8)]
    public async Task MalformedFramesAreRejected(int mutation)
    {
        var protocol = new TagInvalidationMessage("n", 64);
        var frame = protocol.Encode(Guid.NewGuid(), "tag", DateTimeOffset.UtcNow);
        switch (mutation)
        {
            case 0: frame = []; break;
            case 1: frame = frame[..31]; break;
            case 2: frame[3] = (byte)'2'; break;
            case 3: BinaryPrimitives.WriteInt64LittleEndian(frame.AsSpan(20), -1); break;
            case 4: BinaryPrimitives.WriteInt64LittleEndian(frame.AsSpan(20), long.MaxValue); break;
            case 5: BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(28), int.MaxValue); break;
            case 6: frame[33] = 255; break;
            case 7: frame = new byte[65]; break;
            case 8: frame = protocol.Encode(Guid.NewGuid(), "   ", DateTimeOffset.UtcNow); break;
        }
        await Assert.That(protocol.TryDecode(frame, out _, out _, out _)).IsFalse();
    }

    [Test]
    [Arguments("channel", null)]
    [Arguments(null, "namespace")]
    [Arguments("", "namespace")]
    [Arguments("channel", "")]
    public async Task PropagationRequiresAnExplicitChannelAndNamespace(string? channel, string? cacheNamespace)
    {
        var services = new ServiceCollection();
        var builder = services.AddRespireHybridCache("redis://localhost");
        await Assert.That(() => builder.WithRespireClientSideCoherence(options =>
        {
            options.TagInvalidationChannel = channel;
            options.TagInvalidationNamespace = cacheNamespace;
        })).Throws<ArgumentException>();
    }

}
