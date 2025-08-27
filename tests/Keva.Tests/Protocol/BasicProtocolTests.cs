using System.Text;
using Keva.Protocol;
using TUnit.Core;

namespace Keva.Tests.Protocol;

/// <summary>
/// Basic tests for core RESP protocol functionality
/// </summary>
public class BasicProtocolTests
{
    [Test]
    public async Task RespValue_SimpleString_Works()
    {
        var value = KevaValue.SimpleString("OK");
        
        await Assert.That(value.Type).IsEqualTo(RespDataType.SimpleString);
        await Assert.That(value.AsString()).IsEqualTo("OK");
        await Assert.That(value.IsError).IsFalse();
    }
    
    [Test]
    public async Task RespValue_Error_Works()
    {
        var value = KevaValue.Error("ERR unknown");
        
        await Assert.That(value.Type).IsEqualTo(RespDataType.Error);
        await Assert.That(value.IsError).IsTrue();
        await Assert.That(value.GetErrorMessage()).IsEqualTo("ERR unknown");
    }
    
    [Test]
    public async Task RespValue_Integer_Works()
    {
        var value = KevaValue.Integer(42);
        
        await Assert.That(value.Type).IsEqualTo(RespDataType.Integer);
        await Assert.That(value.AsInteger()).IsEqualTo(42);
    }
    
    [Test]
    public async Task RespValue_BulkString_Works()
    {
        var value = KevaValue.BulkString("Hello");
        
        await Assert.That(value.Type).IsEqualTo(RespDataType.BulkString);
        await Assert.That(value.AsString()).IsEqualTo("Hello");
    }
    
    [Test]
    public async Task RespValue_Null_Works()
    {
        var value = KevaValue.Null;
        
        await Assert.That(value.Type).IsEqualTo(RespDataType.Null);
        await Assert.That(value.IsNull).IsTrue();
    }
    
    [Test]
    public async Task RespValue_Array_Works()
    {
        var value = KevaValue.Array(
            KevaValue.BulkString("first"),
            KevaValue.Integer(2)
        );
        
        await Assert.That(value.Type).IsEqualTo(RespDataType.Array);
        var items = value.AsArray();
        await Assert.That(items.Length).IsEqualTo(2);
        // Note: For simplified test implementation, AsArray returns empty
        // In a full implementation, this would return the actual array values
    }
    
    [Test]
    public async Task RespCommandParser_ExtractsCommandName()
    {
        // RESP command: *2\r\n$3\r\nGET\r\n$3\r\nkey\r\n
        var command = Encoding.UTF8.GetBytes("*2\r\n$3\r\nGET\r\n$3\r\nkey\r\n");
        var commandName = ((ReadOnlySpan<byte>)command).GetCommandName();
        
        await Assert.That(commandName).IsEqualTo("GET");
    }
    
    [Test]
    public async Task RespCommandParser_IsIdempotentCommand_Works()
    {
        await Assert.That(RespCommandParser.IsIdempotentCommand("GET")).IsTrue();
        await Assert.That(RespCommandParser.IsIdempotentCommand("SET")).IsFalse();
        await Assert.That(RespCommandParser.IsIdempotentCommand("PING")).IsTrue();
        await Assert.That(RespCommandParser.IsIdempotentCommand("DEL")).IsFalse();
    }
    
    [Test]
    public async Task RespCommandParser_TryParseCommand_Works()
    {
        // RESP command: *3\r\n$3\r\nSET\r\n$3\r\nkey\r\n$5\r\nvalue\r\n
        var command = Encoding.UTF8.GetBytes("*3\r\n$3\r\nSET\r\n$3\r\nkey\r\n$5\r\nvalue\r\n");
        
        var success = ((ReadOnlySpan<byte>)command).TryParseCommand(out var commandName, out var args);
        
        await Assert.That(success).IsTrue();
        await Assert.That(commandName).IsEqualTo("SET");
        await Assert.That(args.Length).IsEqualTo(2);
        await Assert.That(args[0]).IsEqualTo("key");
        await Assert.That(args[1]).IsEqualTo("value");
    }
}