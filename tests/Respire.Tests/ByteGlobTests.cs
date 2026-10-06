using System.Text;
using Respire.Internal;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

public class ByteGlobTests
{
    [Test]
    [Arguments("", "", true)]
    [Arguments("", "***", true)]
    [Arguments("abc", "a*c", true)]
    [Arguments("axbyc", "*a*b*c", true)]
    [Arguments("acb", "a*b*c", false)]
    [Arguments("x", "*??", false)]
    [Arguments("a*b", @"a\*b", true)]
    [Arguments(@"abc\", @"abc\", true)]
    [Arguments("m", "[z-a]", true)]
    [Arguments("b", "[^a]", true)]
    [Arguments("a", "[^a]", false)]
    [Arguments("a", "[ab", true)]
    [Arguments("x", "[^", true)]
    [Arguments("]", @"[\]]", true)]
    [Arguments("é", "??", true)]
    [Arguments("é", "?", false)]
    public async Task MatchesBytewiseGlob(string value, string pattern, bool expected)
        => await Assert.That(ByteGlob.IsMatch(Encoding.UTF8.GetBytes(value), Encoding.UTF8.GetBytes(pattern)))
            .IsEqualTo(expected);
}
