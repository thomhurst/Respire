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
