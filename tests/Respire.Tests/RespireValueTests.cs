using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

public class RespireValueTests
{
    [Test]
    public async Task NullStringAndByteArray_ConvertToNullValue()
    {
        string? nullString = null;
        byte[]? nullBytes = null;

        RespireValue stringValue = nullString;
        RespireValue bytesValue = nullBytes;

        await Assert.That(stringValue.IsNull).IsTrue();
        await Assert.That(bytesValue.IsNull).IsTrue();
    }
}
