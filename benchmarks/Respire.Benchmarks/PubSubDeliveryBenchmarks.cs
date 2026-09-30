using System.Linq.Expressions;
using System.Reflection;
using BenchmarkDotNet.Attributes;
using Respire.Internal;
using Respire.Protocol;

namespace Respire.Benchmarks;

/// <summary>Measures receive-frame routing, buffering, and subscription consumption without socket scheduling.</summary>
[MemoryDiagnoser]
public class PubSubDeliveryBenchmarks
{
    private delegate void PushFrame(in RespValue value);
    private RespireClient _client = null!;
    private PushFrame _dispatch = null!;
    private RespValue _frame;
    private IAsyncEnumerator<RespireMessage>[] _readers = null!;

    [Params(1, 4)]
    public int Subscribers { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _client = RespireClient.Create(new RespireOptions { Endpoints = [new("localhost", 6379)] });
        var hub = _client.Core.Hub;
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var routes = (ByteRouteDictionary<List<RespireSubscription>>[])
            typeof(SubscriptionHub).GetField("_routes", flags)!.GetValue(hub)!;
        var subscriptions = new List<RespireSubscription>();
        _readers = new IAsyncEnumerator<RespireMessage>[Subscribers];
        for (var i = 0; i < Subscribers; i++)
        {
            var subscription = new RespireSubscription(hub, SubscriptionKind.Channel,
                ["events"], 16, SubscriptionOverflow.DropOldest);
            subscriptions.Add(subscription);
            _readers[i] = subscription.GetAsyncEnumerator();
        }
        routes[(int)SubscriptionKind.Channel].Add("events", subscriptions);
        _frame = RespValue.Array(RespValue.BulkString("message"),
            RespValue.BulkString("events"), RespValue.BulkString("payload"));

        // Bind once outside measurement. The same fixture supports the pre-epoch baseline
        // and the epoch-aware candidate without reflection invocation on the measured path.
        var method = typeof(SubscriptionHub).GetMethod("OnPush", flags)!;
        var value = Expression.Parameter(typeof(RespValue).MakeByRefType(), "value");
        Expression[] arguments = method.GetParameters().Length == 1
            ? [value]
            : [Expression.Constant(0L), value];
        _dispatch = Expression.Lambda<PushFrame>(
            Expression.Call(Expression.Constant(hub), method, arguments), value).Compile();
        if (RouteAndConsume() != Subscribers * 7)
            throw new InvalidOperationException("The dispatch fixture did not deliver every payload.");
    }

    [Benchmark]
    public int RouteAndConsume()
    {
        _dispatch(in _frame);
        var bytes = 0;
        foreach (var reader in _readers)
        {
            var next = reader.MoveNextAsync();
            if (!next.IsCompletedSuccessfully || !next.Result)
                throw new InvalidOperationException("A buffered dispatch did not complete synchronously.");
            bytes += reader.Current.Payload.Length;
        }
        return bytes;
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        foreach (var reader in _readers)
            await reader.DisposeAsync();
        await _client.DisposeAsync();
        _frame.Dispose();
    }
}
