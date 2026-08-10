using Respire.Protocol;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

public class RespireLeaseTests
{
    [Test]
    public async Task CopyHelpers_PreserveBinaryPayload()
    {
        using var lease = new RespireLease(RespValue.BulkString(new byte[] { 0, 1, 255 }));
        Span<byte> destination = stackalloc byte[3];

        var copied = lease.TryCopyTo(destination);
        var copiedBytes = destination.ToArray();

        await Assert.That(lease.IsDisposed).IsFalse();
        await Assert.That(copied).IsTrue();
        await Assert.That(copiedBytes).IsEquivalentTo(new byte[] { 0, 1, 255 });
        await Assert.That(lease.ToArray()).IsEquivalentTo(new byte[] { 0, 1, 255 });
    }

    [Test]
    public async Task CopyTo_ReturnsFalseWhenDestinationIsTooSmall()
    {
        using var lease = new RespireLease(RespValue.BulkString("value"));
        Span<byte> destination = stackalloc byte[4];
        var copied = lease.TryCopyTo(destination);

        await Assert.That(copied).IsFalse();
    }

    [Test]
    public async Task StateAfterDispose_RemainsInspectableWhilePayloadAccessThrows()
    {
        var lease = new RespireLease(RespValue.BulkString("value"));
        lease.Dispose();

        await Assert.That(lease.IsDisposed).IsTrue();
        await Assert.That(lease.IsNull).IsTrue();
        await Assert.That(lease.Length).IsEqualTo(0);
        await Assert.That(() => lease.ToString()).ThrowsExactly<ObjectDisposedException>();
        await Assert.That(() => lease.ToArray()).ThrowsExactly<ObjectDisposedException>();
        await Assert.That(() => lease.TryCopyTo(new byte[5])).ThrowsExactly<ObjectDisposedException>();
    }

    [Test]
    public async Task DefaultLease_ReportsNullEmptyDisposedState()
    {
        var lease = default(RespireLease);

        await Assert.That(lease.IsDisposed).IsTrue();
        await Assert.That(lease.IsNull).IsTrue();
        await Assert.That(lease.Length).IsEqualTo(0);
    }
}
