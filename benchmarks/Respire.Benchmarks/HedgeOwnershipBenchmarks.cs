using System.Linq.Expressions;
using System.Reflection;
using BenchmarkDotNet.Attributes;
using Respire.Internal;
using Respire.Networking;
using Respire.Protocol;

namespace Respire.Benchmarks;

/// <summary>Measures the actual hedge result handoff without network or timer noise.</summary>
[MemoryDiagnoser]
public class HedgeOwnershipBenchmarks
{
    private RespireClient _client = null!;
    private DispatchResponseSource<int> _caller = null!;
    private DispatchResponseSource<RespValue> _original = null!;
    private DispatchResponseSource<RespValue> _hedge = null!;
    private Func<ValueTask<(RespValue Response, Task<RespValue> Winner)>> _single = null!;
    private Func<ValueTask<(RespValue Response, Task<RespValue> Winner)>> _originalWins = null!;
    private Func<ValueTask<(RespValue Response, Task<RespValue> Winner)>> _hedgeWins = null!;

    [Params(0, 3)]
    public int Attempts { get; set; }

    [GlobalSetup]
    public async Task Setup()
    {
        var host = Environment.GetEnvironmentVariable("REDIS_HOST") ?? "127.0.0.1";
        var port = int.TryParse(Environment.GetEnvironmentVariable("REDIS_PORT"), out var value) ? value : 6379;
        _client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, Connections = 1, ThreadPoolMonitoring = false,
            Endpoints = [new(host, port)],
        });
        _caller = DispatchResponseSource<int>.Start();
        _original = DispatchResponseSource<RespValue>.Start();
        _hedge = DispatchResponseSource<RespValue>.Start();
        _original.Observation.SetAttempts(Attempts);
        _hedge.Observation.SetAttempts(Attempts);
        var completed = Task.FromResult(default(RespValue));
        var pending = new TaskCompletionSource<RespValue>().Task;
        var alternative = _client.Core.Multiplexer.GetConnection();
        _single = Bind(completed, null, alternative);
        _originalWins = Bind(completed, pending, alternative);
        _hedgeWins = Bind(pending, completed, alternative);
        foreach (var resolve in new[] { _single, _originalWins, _hedgeWins })
        {
            _caller.Observation.SetAttempts(0);
            _ = await resolve();
            if (_caller.Observation.Attempts != Attempts * 2)
                throw new InvalidOperationException("Hedge handoff lost the caller or result-leg retry count.");
        }
    }

    // Compile access once so the unchanged fixture also runs on the pinned baseline.
    // Reflection and expression construction are outside all measured invocations.
    private Func<ValueTask<(RespValue Response, Task<RespValue> Winner)>> Bind(
        Task<RespValue> original, Task<RespValue>? hedge, RespireConnection alternative)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var type = typeof(RespireClient).GetNestedType("HedgeRace", BindingFlags.NonPublic)!;
        var race = Expression.Variable(type, "race");
        return Expression.Lambda<Func<ValueTask<(RespValue, Task<RespValue>)>>>(Expression.Block([race],
            Expression.Assign(race, Expression.Default(type)),
            Expression.Assign(Expression.Field(race, type.GetField("Original", flags)!), Expression.Constant(original)),
            Expression.Assign(Expression.Field(race, type.GetField("Hedge", flags)!), Expression.Constant(hedge, typeof(Task<RespValue>))),
            Expression.Assign(Expression.Field(race, type.GetField("OriginalOwner", flags)!), Expression.Constant(_original)),
            Expression.Assign(Expression.Field(race, type.GetField("HedgeOwner", flags)!), Expression.Constant(_hedge)),
            Expression.Assign(Expression.Field(race, type.GetField("Alternative", flags)!), Expression.Constant(alternative)),
            Expression.Call(race, type.GetMethod("ResolveAsync", flags)!, Expression.Constant(_caller.Observation), Expression.Constant(Attempts)))).Compile();
    }

    [Benchmark]
    public int SingleLeg()
    {
        _ = _single().GetAwaiter().GetResult();
        return _caller.Observation.Attempts;
    }

    [Benchmark]
    public int OriginalWinner()
    {
        _ = _originalWins().GetAwaiter().GetResult();
        return _caller.Observation.Attempts;
    }

    [Benchmark]
    public int HedgeWinner()
    {
        _ = _hedgeWins().GetAwaiter().GetResult();
        return _caller.Observation.Attempts;
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        _caller.CompleteInternal();
        _original.CompleteInternal();
        _hedge.CompleteInternal();
        await _client.DisposeAsync();
    }
}
