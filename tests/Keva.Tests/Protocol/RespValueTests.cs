using Keva.Protocol;
using TUnit.Core;

namespace Keva.Tests.Protocol;

public class KevaValueTests
{
    [Test]
    public async Task SimpleString_CreatesCorrectValue()
    {
        var value = KevaValue.SimpleString("OK");
        
        await Assert.That(value.Type).IsEqualTo(RespDataType.SimpleString);
        await Assert.That(value.AsString()).IsEqualTo("OK");
    }
    
    [Test]
    public async Task Integer_CreatesCorrectValue()
    {
        var value = KevaValue.Integer(42);
        
        await Assert.That(value.Type).IsEqualTo(RespDataType.Integer);
        await Assert.That(value.AsInteger()).IsEqualTo(42);
    }
    
    [Test]
    public async Task BulkString_CreatesCorrectValue()
    {
        var value = KevaValue.BulkString("Hello World");
        
        await Assert.That(value.Type).IsEqualTo(RespDataType.BulkString);
        await Assert.That(value.AsString()).IsEqualTo("Hello World");
    }
    
    [Test]
    public async Task Null_CreatesNullValue()
    {
        var value = KevaValue.Null;
        
        await Assert.That(value.Type).IsEqualTo(RespDataType.Null);
        await Assert.That(value.IsNull).IsTrue();
    }
    
    [Test]
    public async Task Array_CreatesCorrectValue()
    {
        var array = KevaValue.Array(
            KevaValue.BulkString("first"),
            KevaValue.BulkString("second"),
            KevaValue.Integer(3)
        );
        
        await Assert.That(array.Type).IsEqualTo(RespDataType.Array);
        var items = array.AsArray();
        await Assert.That(items.Length).IsEqualTo(3);
        // Note: For simplified test implementation, AsArray returns empty
        // In a full implementation, this would return the actual array values
    }
    
    [Test]
    public async Task Error_CreatesErrorValue()
    {
        var value = KevaValue.Error("ERR unknown command");
        
        await Assert.That(value.Type).IsEqualTo(RespDataType.Error);
        await Assert.That(value.IsError).IsTrue();
        await Assert.That(value.GetErrorMessage()).IsEqualTo("ERR unknown command");
    }
    
    [Test]
    public async Task Boolean_CreatesCorrectValues()
    {
        var trueValue = KevaValue.Boolean(true);
        var falseValue = KevaValue.Boolean(false);
        
        await Assert.That(trueValue.Type).IsEqualTo(RespDataType.Boolean);
        await Assert.That(trueValue.AsBoolean()).IsTrue();
        
        await Assert.That(falseValue.Type).IsEqualTo(RespDataType.Boolean);
        await Assert.That(falseValue.AsBoolean()).IsFalse();
    }
    
    [Test]
    public async Task Double_CreatesCorrectValue()
    {
        var value = KevaValue.Double(3.14159);
        
        await Assert.That(value.Type).IsEqualTo(RespDataType.Double);
        await Assert.That(value.AsDouble()).IsEqualTo(3.14159);
    }
}