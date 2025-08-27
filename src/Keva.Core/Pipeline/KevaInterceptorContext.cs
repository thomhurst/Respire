using System.Collections.Concurrent;
using Keva.Core.Protocol;

namespace Keva.Core.Pipeline;

public sealed class KevaInterceptorContext
{
    private readonly Lazy<ConcurrentDictionary<string, object?>> _items;
    
    public ReadOnlyMemory<byte> Command { get; }
    public RespValue? Response { get; set; }
    public string? CommandName { get; }
    public ReadOnlyMemory<byte>[]? Arguments { get; }
    public IDictionary<string, object?> Items => _items.Value;
    
    public KevaInterceptorContext(ReadOnlyMemory<byte> command, string? commandName = null, ReadOnlyMemory<byte>[]? arguments = null)
    {
        Command = command;
        CommandName = commandName;
        Arguments = arguments;
        Response = null;
        _items = new Lazy<ConcurrentDictionary<string, object?>>(() => new ConcurrentDictionary<string, object?>());
    }
    
    public T? GetItem<T>(string key) where T : class
    {
        if (_items.IsValueCreated && Items.TryGetValue(key, out var value))
        {
            return value as T;
        }
        return null;
    }
    
    public void SetItem<T>(string key, T? value) where T : class
    {
        Items[key] = value;
    }
    
    public bool HasItem(string key) => _items.IsValueCreated && Items.ContainsKey(key);
    
    public void RemoveItem(string key)
    {
        if (_items.IsValueCreated)
        {
            Items.Remove(key);
        }
    }
}