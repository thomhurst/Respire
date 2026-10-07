using Respire.Networking;
using Respire.Protocol;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Protocol;

public class ReservedWriterTests
{
    [Test]
    [Arguments(0)]
    [Arguments(17)]
    [Arguments(127)]
    public async Task SerializationLeavesThePreviouslyPublishedCountUnchanged(int leadingLength)
    {
        var buffer = new WriteBuffer(256);
        try
        {
            buffer.Append(new byte[leadingLength]);
            var writer = new RespWriter(buffer);
            writer.WriteArrayHeader(3);
            writer.WriteRaw("$3\r\nSET\r\n"u8);
            writer.WriteBulkString("key");
            writer.WriteBulkString("value");
            await Assert.That(buffer.Count).IsEqualTo(leadingLength);
            await Assert.That(buffer.WrittenMemory.Length).IsEqualTo(leadingLength);
        }
        finally { buffer.Release(); }
    }
}
