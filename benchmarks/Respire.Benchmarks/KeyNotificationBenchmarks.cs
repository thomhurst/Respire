using System.Text;
using BenchmarkDotNet.Attributes;
using Respire.Serialization;

namespace Respire.Benchmarks;

// Permanent parser coverage: compare layouts and subkey counts, and retain historical
// allocation/latency measurements. Message ownership/setup is outside measured parsing.
[MemoryDiagnoser]
public class KeyNotificationBenchmarks
{
    private RespireMessage _message;

    [Params("keyspace", "keyevent", "subkeys-one", "subkeys-many")]
    public string Layout { get; set; } = "keyspace";

    [GlobalSetup]
    public void Setup()
    {
        var (channel, payload) = Layout switch
        {
            "keyspace" => ("__keyspace@0__:tenant:42", "hset"),
            "keyevent" => ("__keyevent@0__:hset", "tenant:42"),
            "subkeys-one" => ("__subkeyspace@0__:tenant:42", "hset|5:field"),
            _ => ("__subkeyevent@0__:hset", "9:tenant:42|5:first,6:second,0:,3:x,y"),
        };
        _message = new(channel, null, Encoding.UTF8.GetBytes(payload), new SystemTextJsonSerializer());
        if (!_message.TryParseKeyNotification(out _)) throw new InvalidOperationException("Invalid benchmark input.");
    }

    [Benchmark]
    public int ParseAndInspect()
    {
        if (!_message.TryParseKeyNotification("tenant:"u8, out var notification)) return -1;
        var bytes = notification.KeyBytes.Length + notification.RawType.Length;
        foreach (var subkey in notification.GetSubKeys()) bytes += subkey.Length;
        return bytes;
    }
}
