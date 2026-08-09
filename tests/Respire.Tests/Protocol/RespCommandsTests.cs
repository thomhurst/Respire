using System.Text;
using Respire.Protocol;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Protocol;

public class RespCommandsTests
{
    [Test]
    [Arguments("key", "value")]
    [Arguments("£", "sterling")]
    [Arguments("𐍈", "\uD800")]
    public async Task BuildSetCommand_MatchesUtf8Encoding(string key, string value)
    {
        Span<byte> buffer = stackalloc byte[128];

        var length = RespCommands.BuildSetCommand(buffer, key, value);

        var expected = Encoding.UTF8.GetBytes(
            $"*3\r\n$3\r\nSET\r\n${Encoding.UTF8.GetByteCount(key)}\r\n{key}\r\n" +
            $"${Encoding.UTF8.GetByteCount(value)}\r\n{value}\r\n");
        await Assert.That(buffer[..length].SequenceEqual(expected)).IsTrue();
    }
}
