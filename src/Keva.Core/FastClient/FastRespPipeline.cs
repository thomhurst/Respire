using System.Buffers;
using System.Text;

namespace Keva.Core.FastClient;

public sealed class FastRespPipeline
{
    private readonly FastRespClient _client;
    private readonly ArrayPool<byte> _arrayPool;
    private byte[] _buffer;
    private int _written;
    private int _commandCount;
    
    internal FastRespPipeline(FastRespClient client)
    {
        _client = client;
        _arrayPool = ArrayPool<byte>.Shared;
        _buffer = _arrayPool.Rent(4096); // Start with 4KB, will grow if needed
        _written = 0;
        _commandCount = 0;
    }
    
    public FastRespPipeline Set(string key, string value)
    {
        EnsureCapacity(key.Length + value.Length + 50); // Estimate space needed
        _written += _client.WriteCommand(_buffer.AsSpan()[_written..], "SET", new[] { key, value });
        _commandCount++;
        return this;
    }
    
    public FastRespPipeline Get(string key)
    {
        EnsureCapacity(key.Length + 30);
        _written += _client.WriteCommand(_buffer.AsSpan()[_written..], "GET", new[] { key });
        _commandCount++;
        return this;
    }
    
    public FastRespPipeline Del(string key)
    {
        EnsureCapacity(key.Length + 30);
        _written += _client.WriteCommand(_buffer.AsSpan()[_written..], "DEL", new[] { key });
        _commandCount++;
        return this;
    }
    
    public FastRespPipeline Incr(string key)
    {
        EnsureCapacity(key.Length + 30);
        _written += _client.WriteCommand(_buffer.AsSpan()[_written..], "INCR", new[] { key });
        _commandCount++;
        return this;
    }
    
    private void EnsureCapacity(int additionalBytes)
    {
        if (_written + additionalBytes > _buffer.Length)
        {
            var newSize = Math.Max(_buffer.Length * 2, _written + additionalBytes);
            var newBuffer = _arrayPool.Rent(newSize);
            Buffer.BlockCopy(_buffer, 0, newBuffer, 0, _written);
            _arrayPool.Return(_buffer);
            _buffer = newBuffer;
        }
    }
    
    internal void Execute()
    {
        try
        {
            // Send all commands at once
            _client.SendBuffer(_buffer, _written);
            
            // Read all responses
            for (int i = 0; i < _commandCount; i++)
            {
                _client.ReadResponse();
            }
        }
        finally
        {
            // Always return the buffer to the pool
            _arrayPool.Return(_buffer);
        }
    }
}