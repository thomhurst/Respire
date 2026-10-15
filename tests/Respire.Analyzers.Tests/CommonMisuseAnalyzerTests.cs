using System.Collections.Immutable;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Analyzers.Tests;

public class CommonMisuseAnalyzerTests
{
    [Test]
    [Arguments("net8.0")]
    [Arguments("net10.0")]
    public Task ClientOwnership(string framework) => Verify(framework, new ShortLivedClientAnalyzer(), """
        class Consumer
        {
            private readonly RespireClient shared = RespireClient.Create("localhost");
            RespireClient Factory() => RespireClient.Create("localhost");
            async Task<RespireClient> AsyncFactory() => await RespireClient.ConnectAsync("localhost");
            async Task Request()
            {
                await using var client = {|RESP010:RespireClient.Create("localhost")|};
                await using (var connected = await {|RESP010:RespireClient.ConnectAsync("localhost")|}) { }
                await using var any = await {|RESP010:RespireClient.ConnectAnyAsync(new[] { RespireOptions.Parse("localhost") })|};
                await using var nested = await FactoryTask();
            }
            async Task<RespireClient> FactoryTask() => await RespireClient.ConnectAsync("localhost");
            void Loop()
            {
                for (var i = 0; i < 2; i++)
                {
                    var client = {|RESP010:RespireClient.Create("localhost")|};
                    Func<RespireClient> factory = () => RespireClient.Create("localhost");
                }
                for (var once = RespireClient.Create("localhost"); ; ) { break; }
                foreach (var once in new[] { RespireClient.Create("localhost") }) { }
            }
        }
        """);

    [Test]
    public Task EntryPointOwnsApplicationLifetime() => Verify("net10.0", new ShortLivedClientAnalyzer(), """
        class Program
        {
            static async Task Main()
            {
                await using var client = await RespireClient.ConnectAsync("localhost");
                for (var i = 0; i < 2; i++)
                {
                    await using var repeated = {|RESP010:RespireClient.Create("localhost")|};
                }
            }
        }
        """, OutputKind.ConsoleApplication);

    [Test]
    public Task TopLevelOwnsApplicationLifetime() => Verify("net10.0", new ShortLivedClientAnalyzer(), """
        await using var client = await RespireClient.ConnectAsync("localhost");
        Func<Task> endpoint = async () =>
        {
            await using var perRequest = {|RESP010:RespireClient.Create("localhost")|};
        };
        async Task Local()
        {
            await using var perOperation = await {|RESP010:RespireClient.ConnectAsync("localhost")|};
        }
        """, OutputKind.ConsoleApplication);

    [Test]
    [Arguments("net8.0")]
    [Arguments("net10.0")]
    public Task TypedUnboundedReads(string framework) => Verify(framework, new UnboundedReadAnalyzer(), """
        class Consumer
        {
            async Task Read(IRespireClient client, RespireServerNode node, long stop)
            {
                _ = await {|RESP011:node.KeysAsync("*")|};
                _ = await {|RESP011:client.Sets.MembersAsync("set")|};
                _ = await {|RESP011:client.Sets.MembersAsync<int>("set")|};
                _ = await {|RESP011:client.Hashes.GetAllAsync("hash")|};
                _ = await {|RESP011:client.Hashes.GetAllAsync<int>("hash")|};
                _ = await {|RESP011:client.Lists.RangeAsync("list")|};
                const long All = -1;
                _ = await {|RESP011:client.Lists.RangeAsync(stop: All, key: "list", start: 0)|};
                _ = await {|RESP011:client.Lists.RangeAsync<int>("list", 0, -1)|};
                _ = await client.Lists.RangeAsync("list", 0, 99);
                _ = await client.Lists.RangeAsync("list", -10, -1);
                _ = await client.Lists.RangeAsync("list", 0, stop);
                await foreach (var key in client.Keys.ScanAsync()) { }
                await foreach (var value in client.Sets.ScanAsync("set")) { }
                await foreach (var value in client.Hashes.ScanAsync("hash")) { }
            }
            void Queued(RespireBatch batch, RespireTransaction transaction)
            {
                _ = {|RESP011:batch.Sets.Members("set")|};
                _ = {|RESP011:transaction.Hashes.GetAll<int>("hash")|};
                _ = {|RESP011:batch.Lists.Range("list")|};
                _ = batch.Lists.Range("list", 0, 9);
            }
        }
        """);

    [Test]
    [Arguments("net8.0")]
    [Arguments("net10.0")]
    public Task BlockingMessageConsumer(string framework) => Verify(framework, new BlockingSubscriptionHandlerAnalyzer(), """
        class Consumer
        {
            async Task Read(RespireSubscription subscription, Task<int> work, SemaphoreSlim gate)
            {
                await foreach (var message in subscription.WithCancellation(default).ConfigureAwait(false))
                {
                    {|RESP012:Thread.Sleep(1)|};
                    _ = {|RESP012:work.Result|};
                    {|RESP012:work.Wait()|};
                    _ = {|RESP012:work.ConfigureAwait(false).GetAwaiter().GetResult()|};
                    {|RESP012:Task.WaitAll(work)|};
                    {|RESP012:gate.Wait()|};
                    await Task.Delay(1);
                    await gate.WaitAsync();
                    await work;
                    _ = Task.Run(() => Thread.Sleep(1));
                    void Deferred() { Thread.Sleep(1); }
                    _ = nameof(work.Result);
                }
                Thread.Sleep(1);
                await foreach (var other in subscription)
                    await foreach (var inner in subscription)
                        {|RESP012:Thread.Sleep(2)|};
            }
        }
        """);

    [Test]
    [Arguments("net8.0")]
    [Arguments("net10.0")]
    public Task NestedMessageLoopCollections(string framework) => Verify(framework, new BlockingSubscriptionHandlerAnalyzer(), """
        class Consumer
        {
            async Task Read(RespireSubscription subscription, Task<CancellationToken> tokenTask)
            {
                // Initial collection setup is outside a subscription consumer.
                await foreach (var message in subscription.WithCancellation(tokenTask.Result))
                {
                    await foreach (var inner in subscription.WithCancellation({|RESP012:tokenTask.Result|}).ConfigureAwait(false))
                        {|RESP012:Thread.Sleep(1)|};
                    _ = Task.Run(async () =>
                    {
                        await foreach (var deferred in subscription.WithCancellation(tokenTask.Result)) { }
                    });
                }
                // Also cover a nested loop used directly as the outer loop body.
                await foreach (var message in subscription)
                    await foreach (var inner in subscription.WithCancellation({|RESP012:tokenTask.Result|}))
                        {|RESP012:Thread.Sleep(2)|};
                subscription.DeliveryGap += async gap =>
                {
                    await foreach (var inner in subscription.WithCancellation({|RESP012:tokenTask.Result|}))
                        {|RESP012:Thread.Sleep(3)|};
                };
            }
        }
        """);

    [Test]
    [Arguments("net8.0")]
    [Arguments("net10.0")]
    public Task MessageStreamImplementations(string framework) => Verify(framework, new BlockingSubscriptionHandlerAnalyzer(), """
        interface IMessageStream : IAsyncEnumerable<RespireMessage> { }
        class MessageStream : IMessageStream
        {
            public IAsyncEnumerator<RespireMessage> GetAsyncEnumerator(CancellationToken token = default)
                => throw new NotImplementedException();
        }
        class Consumer
        {
            async Task Read<T>(MessageStream concrete, IMessageStream abstraction, T generic)
                where T : IAsyncEnumerable<RespireMessage>
            {
                await foreach (var message in concrete) {|RESP012:Thread.Sleep(1)|};
                await foreach (var message in abstraction) {|RESP012:Thread.Sleep(1)|};
                await foreach (var message in generic) {|RESP012:Thread.Sleep(1)|};
            }
            async Task Unrelated(IAsyncEnumerable<string> other)
            {
                await foreach (var item in other) Thread.Sleep(1);
            }
        }
        """);

    [Test]
    [Arguments("net8.0")]
    [Arguments("net10.0")]
    public Task ZeroTimeoutWaits(string framework) => Verify(framework, new BlockingSubscriptionHandlerAnalyzer(), """
        class Consumer
        {
            async Task Read(RespireSubscription subscription, Task work, SemaphoreSlim gate,
                ManualResetEventSlim signal, WaitHandle handle, Thread thread, int timeout)
            {
                await foreach (var message in subscription)
                {
                    const int NoWait = 0;
                    gate.Wait(NoWait);
                    gate.Wait(TimeSpan.Zero);
                    gate.Wait(cancellationToken: default, millisecondsTimeout: 0);
                    signal.Wait(0);
                    signal.Wait(TimeSpan.Zero);
                    handle.WaitOne(0);
                    handle.WaitOne(TimeSpan.Zero);
                    WaitHandle.WaitAll(new[] { handle }, 0);
                    WaitHandle.WaitAny(new[] { handle }, TimeSpan.Zero);
                    work.Wait(0);
                    work.Wait(TimeSpan.Zero);
                    Task.WaitAll(new[] { work }, 0);
                    Task.WaitAny(new[] { work }, TimeSpan.Zero);
                    thread.Join(0);
                    thread.Join(TimeSpan.Zero);
                    {|RESP012:gate.Wait(1)|};
                    {|RESP012:gate.Wait(timeout)|};
                    {|RESP012:gate.Wait(Timeout.Infinite)|};
                    {|RESP012:work.Wait(1)|};
                    {|RESP012:Thread.Sleep(0)|};
                    // The non-blocking outer wait must not hide blocking argument evaluation.
                    Task.WaitAny(new[] { Task.FromResult({|RESP012:Task.FromResult(0).Result|}) }, 0);
                }
                subscription.DeliveryGap += gap =>
                {
                    gate.Wait(0);
                    gate.Wait(TimeSpan.Zero);
                    {|RESP012:gate.Wait(1)|};
                };
            }
        }
        """);

    [Test]
    public Task GapHandlers() => Verify("net10.0", new BlockingSubscriptionHandlerAnalyzer(), """
        class Consumer
        {
            void Register(RespireSubscription subscription)
            {
                subscription.DeliveryGap += gap => {|RESP012:Thread.Sleep(1)|};
                subscription.DeliveryGap += Handle;
                subscription.DeliveryGap += Handle;
                subscription.DeliveryGap += Local;
                subscription.DeliveryGap -= Handle;
                subscription.DeliveryGap += gap => { _ = Task.Run(() => Thread.Sleep(1)); };
                void Local(RespireSubscriptionGap gap) { {|RESP012:Thread.Sleep(2)|}; }
            }
            void Handle(RespireSubscriptionGap gap) => {|RESP012:Thread.Sleep(3)|};
        }
        """);

    [Test]
    public async Task UnrelatedNamesDoNotWarn()
    {
        const string source = """
            class RespireClient : IDisposable
            {
                public static RespireClient Create(string host) => new();
                public void Dispose() { }
                public Task<int> MembersAsync(string key) => Task.FromResult(1);
                public event Action<int> DeliveryGap;
                void Use()
                {
                    using var client = RespireClient.Create("localhost");
                    _ = client.MembersAsync("key");
                    DeliveryGap += gap => Thread.Sleep(1);
                }
                async Task Read(IAsyncEnumerable<int> numbers)
                {
                    await foreach (var number in numbers) Thread.Sleep(1);
                }
            }
            """;
        await Verify("net10.0", new ShortLivedClientAnalyzer(), source);
        await Verify("net10.0", new UnboundedReadAnalyzer(), source);
        await Verify("net10.0", new BlockingSubscriptionHandlerAnalyzer(), source);
    }

    [Test]
    public Task GeneratedCodeIsIgnored() => Verify("net10.0", new UnboundedReadAnalyzer(), """
        class Consumer
        {
            void Read(ISetCommands sets) { _ = sets.MembersAsync("key"); }
        }
        """, generated: true);

    private static async Task Verify(string framework, DiagnosticAnalyzer analyzer, string source,
        OutputKind outputKind = OutputKind.DynamicallyLinkedLibrary, bool generated = false)
    {
        source = (generated ? "// <auto-generated/>\n" : "") + """
            using System;
            using System.Collections.Generic;
            using System.Threading;
            using System.Threading.Tasks;
            using Respire;

            """ + "\n" + source;
        var expected = new List<string>();
        var removed = 0;
        source = Regex.Replace(source, @"\{\|(RESP\d+):(.*?)\|\}", match =>
        {
            var value = match.Groups[2].Value;
            expected.Add($"{match.Groups[1].Value}:{match.Index - removed}:{value.Length}");
            removed += match.Length - value.Length;
            return value;
        }, RegexOptions.Singleline);
        var references = GeneratorTestReferences.All
            .Where(reference => !Path.GetFileName(reference.Display)!.StartsWith("Respire", StringComparison.Ordinal))
            .Append(MetadataReference.CreateFromFile(Path.Combine(AppContext.BaseDirectory,
                "local-initialization-audit", framework, "Respire.dll")));
        var compilation = CSharpCompilation.Create("Consumer", [CSharpSyntaxTree.ParseText(source)], references,
            new CSharpCompilationOptions(outputKind));
        var errors = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error);
        await Assert.That(string.Join("\n", errors)).IsEqualTo("");
        var diagnostics = await compilation.WithAnalyzers(ImmutableArray.Create(analyzer)).GetAnalyzerDiagnosticsAsync();
        var actual = diagnostics.OrderBy(d => d.Location.SourceSpan.Start)
            .Select(d => $"{d.Id}:{d.Location.SourceSpan.Start}:{d.Location.SourceSpan.Length}");
        await Assert.That(string.Join("\n", actual)).IsEqualTo(string.Join("\n", expected));
    }
}
