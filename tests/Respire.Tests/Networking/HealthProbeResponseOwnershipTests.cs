using System.Runtime.InteropServices;
using System.Text;
using Respire.Internal;
using Respire.Networking;
using Respire.Protocol;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class HealthProbeResponseOwnershipTests
{
    [Test]
    [NotInParallel]
    [Arguments(false)]
    [Arguments(true)]
    public async Task FailedProbeCannotAliasAReplicaRoleReply(bool bulkError)
    {
        const string message = "LOADING Redis is loading the dataset in memory";
        var error = Encoding.ASCII.GetBytes(bulkError
            ? $"!{message.Length}\r\n{message}\r\n" : $"-{message}\r\n");
        await using var server = new FakeRespServer(error);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var roleWire = "*5\r\n$5\r\nslave\r\n$9\r\n127.0.0.1\r\n:6379\r\n$9\r\nconnected\r\n:0\r\n"u8.ToArray();

        // The LOADING payload and the complete ROLE frame both use the 64-byte bucket.
        // Hold more than its 256 retained slots, including all stripes, so a duplicate
        // return cannot be hidden by a full bucket or unrelated cached buffers.
        var held = new byte[257][];
        var role = default(RespValue);
        byte[]? roleBuffer = null;
        byte[]? unrelatedReply = null;
        try
        {
            for (var i = 0; i < held.Length; i++) held[i] = RespirePools.ResponsePayloads.Rent(roleWire.Length);
            var results = await client.ProbeHealthAsync();
            await Assert.That(results.Length).IsEqualTo(1);
            await Assert.That(results[0].Error).IsTypeOf<RespireServerException>();
            await Assert.That(results[0].Error!.Message).IsEqualTo(message);

            var position = 0;
            await Assert.That(RespParser.TryParseValue(roleWire, ref position, out role)).IsEqualTo(RespParseStatus.Done);
            await Assert.That(MemoryMarshal.TryGetArray(role.AsArray()[0].AsMemory(), out var storage)).IsTrue();
            roleBuffer = storage.Array;
            await Assert.That(ReplicaHealth<object>.IsReplica(in role, out var linked) && linked).IsTrue();

            // Model another connection receiving a reply while ROLE is awaiting validation.
            // A twice-returned error buffer is rented for both live replies and corrupts ROLE.
            unrelatedReply = RespirePools.ResponsePayloads.Rent(roleWire.Length);
            unrelatedReply.AsSpan().Fill((byte)'x');
            await Assert.That(ReplicaHealth<object>.IsReplica(in role, out linked) && linked).IsTrue();
            await Assert.That(ReferenceEquals(roleBuffer, unrelatedReply)).IsFalse();
        }
        finally
        {
            // Keep a failing regression from returning the aliased buffer a third time.
            if (unrelatedReply is not null && !ReferenceEquals(unrelatedReply, roleBuffer))
                RespirePools.ResponsePayloads.Return(unrelatedReply);
            role.Dispose();
            foreach (var buffer in held)
                if (buffer is not null) RespirePools.ResponsePayloads.Return(buffer);
        }
    }
}
