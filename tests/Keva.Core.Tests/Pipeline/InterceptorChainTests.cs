using Keva.Core.Pipeline;
using Keva.Core.Protocol;
using TUnit.Core;

namespace Keva.Core.Tests.Pipeline;

public class InterceptorChainTests
{
    [Test]
    public async Task EmptyChain_CallsTerminalHandler()
    {
        var wasCalled = false;
        InterceptorDelegate terminal = async (context, ct) =>
        {
            wasCalled = true;
            return RespValue.SimpleString("OK");
        };

        var chain = InterceptorChain.CreateBuilder(terminal).Build();
        var context = new KevaInterceptorContext(ReadOnlyMemory<byte>.Empty);
        
        var result = await chain(context, CancellationToken.None);
        
        await Assert.That(wasCalled).IsTrue();
        await Assert.That(result.AsString()).IsEqualTo("OK");
    }

    [Test]
    public async Task SingleInterceptor_ExecutesInOrder()
    {
        var executionOrder = new List<string>();
        
        InterceptorDelegate terminal = async (context, ct) =>
        {
            executionOrder.Add("terminal");
            return RespValue.SimpleString("OK");
        };

        var interceptor = new TestInterceptor("first", executionOrder);
        
        var chain = InterceptorChain.CreateBuilder(terminal)
            .Add(interceptor)
            .Build();
            
        var context = new KevaInterceptorContext(ReadOnlyMemory<byte>.Empty);
        var result = await chain(context, CancellationToken.None);
        
        await Assert.That(executionOrder.Count).IsEqualTo(3);
        await Assert.That(executionOrder[0]).IsEqualTo("first-before");
        await Assert.That(executionOrder[1]).IsEqualTo("terminal");
        await Assert.That(executionOrder[2]).IsEqualTo("first-after");
        await Assert.That(result.AsString()).IsEqualTo("OK");
    }

    [Test]
    public async Task MultipleInterceptors_ExecuteInCorrectOrder()
    {
        var executionOrder = new List<string>();
        
        InterceptorDelegate terminal = async (context, ct) =>
        {
            executionOrder.Add("terminal");
            return RespValue.SimpleString("OK");
        };

        var first = new TestInterceptor("first", executionOrder);
        var second = new TestInterceptor("second", executionOrder);
        var third = new TestInterceptor("third", executionOrder);
        
        var chain = InterceptorChain.CreateBuilder(terminal)
            .Add(first)
            .Add(second)
            .Add(third)
            .Build();
            
        var context = new KevaInterceptorContext(ReadOnlyMemory<byte>.Empty);
        var result = await chain(context, CancellationToken.None);
        
        await Assert.That(executionOrder.Count).IsEqualTo(7);
        await Assert.That(executionOrder[0]).IsEqualTo("first-before");
        await Assert.That(executionOrder[1]).IsEqualTo("second-before");
        await Assert.That(executionOrder[2]).IsEqualTo("third-before");
        await Assert.That(executionOrder[3]).IsEqualTo("terminal");
        await Assert.That(executionOrder[4]).IsEqualTo("third-after");
        await Assert.That(executionOrder[5]).IsEqualTo("second-after");
        await Assert.That(executionOrder[6]).IsEqualTo("first-after");
        await Assert.That(result.AsString()).IsEqualTo("OK");
    }

    [Test]
    public async Task Interceptor_CanModifyContext()
    {
        InterceptorDelegate terminal = async (context, ct) =>
        {
            return RespValue.SimpleString(context.Items["modified"]?.ToString() ?? "false");
        };

        var interceptor = new ModifyingInterceptor();
        
        var chain = InterceptorChain.CreateBuilder(terminal)
            .Add(interceptor)
            .Build();
            
        var context = new KevaInterceptorContext(ReadOnlyMemory<byte>.Empty);
        var result = await chain(context, CancellationToken.None);
        
        await Assert.That(result.AsString()).IsEqualTo("True");
    }

    [Test]
    public async Task Interceptor_CanShortCircuit()
    {
        var wasCalled = false;
        InterceptorDelegate terminal = async (context, ct) =>
        {
            wasCalled = true;
            return RespValue.SimpleString("terminal");
        };

        var interceptor = new ShortCircuitingInterceptor();
        
        var chain = InterceptorChain.CreateBuilder(terminal)
            .Add(interceptor)
            .Build();
            
        var context = new KevaInterceptorContext(ReadOnlyMemory<byte>.Empty);
        var result = await chain(context, CancellationToken.None);
        
        await Assert.That(wasCalled).IsFalse();
        await Assert.That(result.AsString()).IsEqualTo("short-circuited");
    }

    [Test]
    public async Task Interceptor_CanModifyResponse()
    {
        InterceptorDelegate terminal = async (context, ct) =>
        {
            return RespValue.SimpleString("original");
        };

        var interceptor = new ResponseModifyingInterceptor();
        
        var chain = InterceptorChain.CreateBuilder(terminal)
            .Add(interceptor)
            .Build();
            
        var context = new KevaInterceptorContext(ReadOnlyMemory<byte>.Empty);
        var result = await chain(context, CancellationToken.None);
        
        await Assert.That(result.AsString()).IsEqualTo("modified");
    }

    [Test]
    public async Task Interceptor_PropagatesCancellation()
    {
        var cts = new CancellationTokenSource();
        var wasTokenCancelled = false;
        
        InterceptorDelegate terminal = async (context, ct) =>
        {
            wasTokenCancelled = ct.IsCancellationRequested;
            return RespValue.SimpleString("OK");
        };

        var chain = InterceptorChain.CreateBuilder(terminal).Build();
        var context = new KevaInterceptorContext(ReadOnlyMemory<byte>.Empty);
        
        cts.Cancel();
        
        try
        {
            await chain(context, cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Expected
        }
        
        await Assert.That(wasTokenCancelled).IsTrue();
    }

    private class TestInterceptor : IKevaInterceptor
    {
        private readonly string _name;
        private readonly List<string> _executionOrder;

        public TestInterceptor(string name, List<string> executionOrder)
        {
            _name = name;
            _executionOrder = executionOrder;
        }

        public async ValueTask<RespValue> InterceptAsync(
            KevaInterceptorContext context,
            InterceptorDelegate next,
            CancellationToken cancellationToken = default)
        {
            _executionOrder.Add($"{_name}-before");
            var result = await next(context, cancellationToken);
            _executionOrder.Add($"{_name}-after");
            return result;
        }
    }

    private class ModifyingInterceptor : IKevaInterceptor
    {
        public async ValueTask<RespValue> InterceptAsync(
            KevaInterceptorContext context,
            InterceptorDelegate next,
            CancellationToken cancellationToken = default)
        {
            context.Items["modified"] = true;
            return await next(context, cancellationToken);
        }
    }

    private class ShortCircuitingInterceptor : IKevaInterceptor
    {
        public ValueTask<RespValue> InterceptAsync(
            KevaInterceptorContext context,
            InterceptorDelegate next,
            CancellationToken cancellationToken = default)
        {
            // Don't call next - short circuit
            return ValueTask.FromResult(RespValue.SimpleString("short-circuited"));
        }
    }

    private class ResponseModifyingInterceptor : IKevaInterceptor
    {
        public async ValueTask<RespValue> InterceptAsync(
            KevaInterceptorContext context,
            InterceptorDelegate next,
            CancellationToken cancellationToken = default)
        {
            var result = await next(context, cancellationToken);
            return RespValue.SimpleString("modified");
        }
    }
}