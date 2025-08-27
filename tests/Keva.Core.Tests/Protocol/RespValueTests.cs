using Keva.Core.Protocol;
using TUnit.Core;

namespace Keva.Core.Tests.Protocol;

public class RespValueTests
{
    [Test]
    public async Task SimpleString_CreatesCorrectValue()
    {
        var value = RespValue.SimpleString("OK");
        
        await Assert.That(value.Type).IsEqualTo(RespDataType.SimpleString);
        await Assert.That(value.AsString()).IsEqualTo("OK");
    }
    
    [Test]
    public async Task Integer_CreatesCorrectValue()
    {
        var value = RespValue.Integer(42);
        
        await Assert.That(value.Type).IsEqualTo(RespDataType.Integer);
        await Assert.That(value.AsInteger()).IsEqualTo(42);
    }
    
    [Test]
    public async Task BulkString_CreatesCorrectValue()
    {
        var value = RespValue.BulkString("Hello World");
        
        await Assert.That(value.Type).IsEqualTo(RespDataType.BulkString);
        await Assert.That(value.AsString()).IsEqualTo("Hello World");
    }
    
    [Test]
    public async Task Null_CreatesNullValue()
    {
        var value = RespValue.Null;
        
        await Assert.That(value.Type).IsEqualTo(RespDataType.Null);
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
        
        await Assert.That(array.Type).IsEqualTo(RespDataType.Array);
        var items = array.AsArray();
        await Assert.That(items.Length).IsEqualTo(3);
        await Assert.That(items.Span[0].AsString()).IsEqualTo("first");
        await Assert.That(items.Span[1].AsString()).IsEqualTo("second");
        await Assert.That(items.Span[2].AsInteger()).IsEqualTo(3);
    }
    
    [Test]
    public async Task Error_CreatesErrorValue()
    {
        var value = RespValue.Error("ERR unknown command");
        
        await Assert.That(value.Type).IsEqualTo(RespDataType.Error);
        await Assert.That(value.IsError).IsTrue();
        await Assert.That(value.GetErrorMessage()).IsEqualTo("ERR unknown command");
    }
    
    [Test]
    public async Task Boolean_CreatesCorrectValues()
    {
        var trueValue = RespValue.Boolean(true);
        var falseValue = RespValue.Boolean(false);
        
        await Assert.That(trueValue.Type).IsEqualTo(RespDataType.Boolean);
        await Assert.That(trueValue.AsBoolean()).IsTrue();
        
        await Assert.That(falseValue.Type).IsEqualTo(RespDataType.Boolean);
        await Assert.That(falseValue.AsBoolean()).IsFalse();
    }
    
    [Test]
    public async Task Double_CreatesCorrectValue()
    {
        var value = RespValue.Double(3.14159);
        
        await Assert.That(value.Type).IsEqualTo(RespDataType.Double);
        await Assert.That(value.AsDouble()).IsEqualTo(3.14159);
    }
}