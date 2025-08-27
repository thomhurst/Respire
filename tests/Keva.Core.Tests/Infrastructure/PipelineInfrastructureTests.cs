using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Keva.Core.Infrastructure;
using Keva.Core.Protocol;

namespace Keva.Core.Tests.Infrastructure;

/// <summary>
/// Comprehensive tests for the new pipeline infrastructure
/// Tests memory pooling, connection management, command queueing, and integration
/// </summary>
public class PipelineInfrastructureTests : IDisposable
{
    private readonly KevaMemoryPool _memoryPool;
    
    public PipelineInfrastructureTests()
    {
        _memoryPool = KevaMemoryPool.Shared;
    }
    
    [Fact]
    public void KevaMemoryPool_RentAndReturnMemory_WorksCorrectly()
    {
        // Arrange & Act
        using var memoryOwner = _memoryPool.RentMemory(1024);
        var memory = memoryOwner.Memory;
        
        // Assert
        Assert.True(memory.Length >= 1024);
    }
    
    [Fact]
    public void KevaMemoryPool_RentAndReturnArray_WorksCorrectly()
    {
        // Arrange
        var array = _memoryPool.RentArray(512);
        
        // Act & Assert
        Assert.True(array.Length >= 512);
        
        // Cleanup
        _memoryPool.ReturnArray(array);
    }
    
    [Fact]
    public void PooledBufferWriter_WriteOperations_WorkCorrectly()
    {
        // Arrange
        using var writer = _memoryPool.CreateBufferWriter();
        var testData = "Hello, Keva!"u8.ToArray();
        
        // Act
        writer.WritePreCompiledCommand(testData);
        var result = writer.WrittenSpan;
        
        // Assert
        Assert.Equal(testData.Length, writer.WrittenCount);
        Assert.True(testData.AsSpan().SequenceEqual(result));
    }
    
    [Fact]
    public void PooledBufferWriter_WriteBulkString_ProducesCorrectRespFormat()
    {
        // Arrange
        using var writer = _memoryPool.CreateBufferWriter();
        var testString = "test_value";
        
        // Act
        writer.WriteBulkString(testString);
        var result = writer.WrittenSpan;
        
        // Assert - should be $10\r\ntest_value\r\n
        var expectedPrefix = "$10\r\n"u8;
        var expectedSuffix = "\r\n"u8;
        var expectedValue = "test_value"u8;
        
        Assert.True(result[..expectedPrefix.Length].SequenceEqual(expectedPrefix));
        Assert.True(result[^expectedSuffix.Length..].SequenceEqual(expectedSuffix));
        
        var valueStart = expectedPrefix.Length;
        var valueLength = expectedValue.Length;
        Assert.True(result.Slice(valueStart, valueLength).SequenceEqual(expectedValue));
    }
    
    [Fact]
    public void PooledBufferWriter_MultipleWrites_ExpandsCorrectly()
    {
        // Arrange
        using var writer = _memoryPool.CreateBufferWriter(100);
        var largeData = new byte[2000];
        Array.Fill<byte>(largeData, 0x42);
        
        // Act
        writer.WritePreCompiledCommand(largeData);
        
        // Assert
        Assert.Equal(2000, writer.WrittenCount);
        Assert.True(writer.Capacity >= 2000);
        Assert.All(writer.WrittenSpan.ToArray(), b => Assert.Equal(0x42, b));
    }
    
    [Fact]
    public void PooledBufferWriter_Clear_ResetsPosition()
    {
        // Arrange
        using var writer = _memoryPool.CreateBufferWriter();
        writer.WritePreCompiledCommand("test"u8);
        
        // Act
        writer.Clear();
        
        // Assert
        Assert.Equal(0, writer.WrittenCount);
        Assert.True(writer.WrittenSpan.IsEmpty);
    }
    
    [Fact] 
    public void RespPipelineReader_ParseSimpleString_WorksCorrectly()
    {
        // Arrange
        var data = "+OK\r\n"u8.ToArray();
        var sequence = new ReadOnlySequence<byte>(data);
        var reader = new RespPipelineReader(sequence);
        
        // Act
        var success = reader.TryReadValue(out var value);
        
        // Assert
        Assert.True(success);
        Assert.Equal(RespDataType.SimpleString, value.Type);
        Assert.Equal("OK", value.AsString());
    }
    
    [Fact]
    public void RespPipelineReader_ParseError_WorksCorrectly()
    {
        // Arrange
        var data = "-ERR something went wrong\r\n"u8.ToArray();
        var sequence = new ReadOnlySequence<byte>(data);
        var reader = new RespPipelineReader(sequence);
        
        // Act
        var success = reader.TryReadValue(out var value);
        
        // Assert
        Assert.True(success);
        Assert.Equal(RespDataType.Error, value.Type);
        Assert.Equal("ERR something went wrong", value.AsString());
    }
    
    [Fact]
    public void RespPipelineReader_ParseInteger_WorksCorrectly()
    {
        // Arrange
        var data = ":12345\r\n"u8.ToArray();
        var sequence = new ReadOnlySequence<byte>(data);
        var reader = new RespPipelineReader(sequence);
        
        // Act
        var success = reader.TryReadValue(out var value);
        
        // Assert
        Assert.True(success);
        Assert.Equal(RespDataType.Integer, value.Type);
        Assert.Equal(12345, value.AsInteger());
    }
    
    [Fact]
    public void RespPipelineReader_ParseNegativeInteger_WorksCorrectly()
    {
        // Arrange
        var data = ":-12345\r\n"u8.ToArray();
        var sequence = new ReadOnlySequence<byte>(data);
        var reader = new RespPipelineReader(sequence);
        
        // Act
        var success = reader.TryReadValue(out var value);
        
        // Assert
        Assert.True(success);
        Assert.Equal(RespDataType.Integer, value.Type);
        Assert.Equal(-12345, value.AsInteger());
    }
    
    [Fact]
    public void RespPipelineReader_ParseBulkString_WorksCorrectly()
    {
        // Arrange
        var data = "$5\r\nhello\r\n"u8.ToArray();
        var sequence = new ReadOnlySequence<byte>(data);
        var reader = new RespPipelineReader(sequence);
        
        // Act
        var success = reader.TryReadValue(out var value);
        
        // Assert
        Assert.True(success);
        Assert.Equal(RespDataType.BulkString, value.Type);
        Assert.Equal("hello", value.AsString());
    }
    
    [Fact]
    public void RespPipelineReader_ParseNullBulkString_WorksCorrectly()
    {
        // Arrange
        var data = "$-1\r\n"u8.ToArray();
        var sequence = new ReadOnlySequence<byte>(data);
        var reader = new RespPipelineReader(sequence);
        
        // Act
        var success = reader.TryReadValue(out var value);
        
        // Assert
        Assert.True(success);
        Assert.Equal(RespDataType.Null, value.Type);
        Assert.True(value.IsNull);
    }
    
    [Fact]
    public void RespPipelineReader_ParseEmptyBulkString_WorksCorrectly()
    {
        // Arrange
        var data = "$0\r\n\r\n"u8.ToArray();
        var sequence = new ReadOnlySequence<byte>(data);
        var reader = new RespPipelineReader(sequence);
        
        // Act
        var success = reader.TryReadValue(out var value);
        
        // Assert
        Assert.True(success);
        Assert.Equal(RespDataType.BulkString, value.Type);
        Assert.Equal("", value.AsString());
    }
    
    [Fact]
    public void RespPipelineReader_ParseBoolean_True_WorksCorrectly()
    {
        // Arrange
        var data = "#t\r\n"u8.ToArray();
        var sequence = new ReadOnlySequence<byte>(data);
        var reader = new RespPipelineReader(sequence);
        
        // Act
        var success = reader.TryReadValue(out var value);
        
        // Assert
        Assert.True(success);
        Assert.Equal(RespDataType.Boolean, value.Type);
        Assert.True(value.AsBoolean());
    }
    
    [Fact]
    public void RespPipelineReader_ParseBoolean_False_WorksCorrectly()
    {
        // Arrange
        var data = "#f\r\n"u8.ToArray();
        var sequence = new ReadOnlySequence<byte>(data);
        var reader = new RespPipelineReader(sequence);
        
        // Act
        var success = reader.TryReadValue(out var value);
        
        // Assert
        Assert.True(success);
        Assert.Equal(RespDataType.Boolean, value.Type);
        Assert.False(value.AsBoolean());
    }
    
    [Fact]
    public void RespPipelineReader_IncompleteData_ReturnsFalse()
    {
        // Arrange
        var data = "+OK"u8.ToArray(); // Missing \r\n
        var sequence = new ReadOnlySequence<byte>(data);
        var reader = new RespPipelineReader(sequence);
        
        // Act
        var success = reader.TryReadValue(out var value);
        
        // Assert
        Assert.False(success);
    }
    
    [Fact]
    public void RespPipelineReader_EmptySequence_ReturnsFalse()
    {
        // Arrange
        var sequence = ReadOnlySequence<byte>.Empty;
        var reader = new RespPipelineReader(sequence);
        
        // Act
        var success = reader.TryReadValue(out var value);
        
        // Assert
        Assert.False(success);
    }
    
    [Fact]
    public void RespPipelineReader_ConsumedAndExaminedPositions_UpdateCorrectly()
    {
        // Arrange
        var data = "+OK\r\n"u8.ToArray();
        var sequence = new ReadOnlySequence<byte>(data);
        var reader = new RespPipelineReader(sequence);
        
        // Act
        var success = reader.TryReadValue(out var value);
        reader.MarkConsumed();
        reader.MarkExamined();
        
        // Assert
        Assert.True(success);
        Assert.False(reader.Consumed.Equals(sequence.Start));
        Assert.False(reader.Examined.Equals(sequence.Start));
    }
    
    // Integration tests would require actual Redis connection
    // These would be in a separate test class with [Fact(Skip = "Integration")] 
    // or conditional based on environment variables
    
    public void Dispose()
    {
        // KevaMemoryPool.Shared should not be disposed as it's shared
    }
}

/// <summary>
/// Integration tests that require actual Redis connections
/// These tests are skipped by default and require Redis to be running
/// </summary>
public class PipelineInfrastructureIntegrationTests
{
    private const string TestRedisHost = "localhost";
    private const int TestRedisPort = 6379;
    
    [Fact(Skip = "Requires Redis server - enable manually for integration testing")]
    public async Task PipelineConnection_ConnectAndPing_WorksCorrectly()
    {
        // Arrange & Act
        using var connection = await PipelineConnection.ConnectAsync(TestRedisHost, TestRedisPort);
        
        // Assert
        Assert.True(connection.IsConnected);
        Assert.Equal(TestRedisHost, connection.Host);
        Assert.Equal(TestRedisPort, connection.Port);
    }
    
    [Fact(Skip = "Requires Redis server - enable manually for integration testing")]
    public async Task PipelineCommandWriter_SendPing_WorksCorrectly()
    {
        // Arrange
        using var connection = await PipelineConnection.ConnectAsync(TestRedisHost, TestRedisPort);
        using var writer = new PipelineCommandWriter(connection);
        
        // Act
        await writer.WritePingAsync();
        var readResult = await connection.ReadAsync();
        var reader = new RespPipelineReader(readResult.Buffer);
        
        // Assert
        Assert.True(reader.TryReadValue(out var response));
        Assert.Equal(RespDataType.SimpleString, response.Type);
        Assert.Equal("PONG", response.AsString());
        
        connection.AdvanceReader(reader.Consumed, reader.Examined);
    }
    
    [Fact(Skip = "Requires Redis server - enable manually for integration testing")]
    public async Task PreCompiledPipelineClient_BasicOperations_WorkCorrectly()
    {
        // Arrange
        using var client = await PreCompiledPipelineClient.CreateAsync(TestRedisHost, TestRedisPort, logger: NullLogger.Instance);
        
        // Act & Assert - Set and Get
        await client.SetAsync("test_key", "test_value");
        var getValue = await client.GetAsync("test_key");
        
        Assert.Equal(RespDataType.BulkString, getValue.Type);
        Assert.Equal("test_value", getValue.AsString());
        
        // Act & Assert - Ping
        var pingResponse = await client.PingWithResponseAsync();
        Assert.Equal(RespDataType.SimpleString, pingResponse.Type);
        Assert.Equal("PONG", pingResponse.AsString());
        
        // Cleanup
        await client.DelAsync("test_key");
    }
    
    [Fact(Skip = "Requires Redis server - enable manually for integration testing")]
    public async Task KevaConnectionMultiplexer_MultipleConnections_WorkCorrectly()
    {
        // Arrange
        using var multiplexer = await KevaConnectionMultiplexer.CreateAsync(
            TestRedisHost, TestRedisPort, connectionCount: 4, logger: NullLogger.Instance);
        
        // Act
        var stats = multiplexer.GetStats();
        
        // Assert
        Assert.Equal(4, stats.TotalConnections);
        Assert.Equal(4, stats.ConnectedConnections);
        Assert.True(multiplexer.IsConnected);
    }
}