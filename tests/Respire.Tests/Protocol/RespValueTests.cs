using System.Globalization;
using Respire.Protocol;
using TUnit.Core;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Respire.Tests.Protocol;

public class RespValueTests
{
    [Test]
    public async Task SimpleString_CreatesCorrectValue()
    {
        var value = RespValue.SimpleString("OK");
        
        await Assert.That(value.Type).IsEqualTo(RespireDataType.SimpleString);
        await Assert.That(value.AsString()).IsEqualTo("OK");
    }
    
    [Test]
    public async Task Integer_CreatesCorrectValue()
    {
        var value = RespValue.Integer(42);
        
        await Assert.That(value.Type).IsEqualTo(RespireDataType.Integer);
        await Assert.That(value.AsInteger()).IsEqualTo(42);
    }
    
    [Test]
    public async Task BulkString_CreatesCorrectValue()
    {
        var value = RespValue.BulkString("Hello World");
        
        await Assert.That(value.Type).IsEqualTo(RespireDataType.BulkString);
        await Assert.That(value.AsString()).IsEqualTo("Hello World");
    }
    
    [Test]
    public async Task Null_CreatesNullValue()
    {
        var value = RespValue.Null;
        
        await Assert.That(value.Type).IsEqualTo(RespireDataType.Null);
        await Assert.That(value.IsNull).IsTrue();
    }
    
    [Test]
    public async Task Array_CreatesCorrectValue()
    {
        var array = RespValue.Array(
            RespValue.BulkString("first"),
            RespValue.BulkString("second"),
            RespValue.Integer(3)
        );
        
        await Assert.That(array.Type).IsEqualTo(RespireDataType.Array);
        var items = array.AsArray();
        await Assert.That(items.Length).IsEqualTo(3);
        // Note: For simplified test implementation, AsArray returns empty
        // In a full implementation, this would return the actual array values
    }
    
    [Test]
    public async Task Error_CreatesErrorValue()
    {
        var value = RespValue.Error("ERR unknown command");
        
        await Assert.That(value.Type).IsEqualTo(RespireDataType.Error);
        await Assert.That(value.IsError).IsTrue();
        await Assert.That(value.GetErrorMessage()).IsEqualTo("ERR unknown command");
    }
    
    [Test]
    public async Task Boolean_CreatesCorrectValues()
    {
        var trueValue = RespValue.Boolean(true);
        var falseValue = RespValue.Boolean(false);
        
        await Assert.That(trueValue.Type).IsEqualTo(RespireDataType.Boolean);
        await Assert.That(trueValue.AsBoolean()).IsTrue();
        
        await Assert.That(falseValue.Type).IsEqualTo(RespireDataType.Boolean);
        await Assert.That(falseValue.AsBoolean()).IsFalse();
    }
    
    [Test]
    public async Task Double_CreatesCorrectValue()
    {
        var value = RespValue.Double(3.14159);
        
        await Assert.That(value.Type).IsEqualTo(RespireDataType.Double);
        await Assert.That(value.AsDouble()).IsEqualTo(3.14159);
    }

    [Test]
    [Arguments("de-DE")]
    [Arguments("sv-SE")]
    [Arguments("fa-IR")]
    [Arguments(MinusSignCulture)]
    // CurrentCulture is async-local, so this change cannot reach concurrently running tests.
    public async Task NumericText_IgnoresCurrentCulture(string cultureName)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CreateCulture(cultureName);

            await Assert.That(RespValue.Double(-1.5).AsString()).IsEqualTo("-1.5");
            await Assert.That(RespValue.Double(-1.5).ToString()).IsEqualTo("-1.5");
            await Assert.That(RespValue.Double(0.1).AsString()).IsEqualTo("0.1");
            await Assert.That(RespValue.Double(double.MaxValue).AsString()).IsEqualTo("1.7976931348623157E+308");
            await Assert.That(RespValue.Double(double.NegativeInfinity).AsString()).IsEqualTo("-Infinity");
            await Assert.That(RespValue.Double(double.NaN).AsString()).IsEqualTo("NaN");
            await Assert.That(RespValue.Integer(-42).AsString()).IsEqualTo("-42");
            await Assert.That(RespValue.Integer(-42).ToString()).IsEqualTo("-42");
            await Assert.That(RespValue.Integer(long.MinValue).AsString()).IsEqualTo("-9223372036854775808");
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Test]
    public async Task NumericText_RoundTripsDoubles()
    {
        foreach (var expected in new[] { 0.1 + 0.2, -1e-300, 123456789.123456789, double.Epsilon })
        {
            var text = RespValue.Double(expected).AsString();
            await Assert.That(double.Parse(text, CultureInfo.InvariantCulture)).IsEqualTo(expected);
        }
    }

    private const string MinusSignCulture = "custom-u2212";

    private static CultureInfo CreateCulture(string name)
    {
        if (name != MinusSignCulture)
        {
            return CultureInfo.GetCultureInfo(name);
        }

        // Pin the separators that break protocol text instead of relying on the host's ICU data.
        var culture = (CultureInfo)CultureInfo.GetCultureInfo("en-US").Clone();
        culture.NumberFormat.NegativeSign = "\u2212";
        culture.NumberFormat.NumberDecimalSeparator = ",";
        culture.NumberFormat.NumberGroupSeparator = ".";
        culture.NumberFormat.NegativeInfinitySymbol = "\u2212\u221E";
        return culture;
    }
}
