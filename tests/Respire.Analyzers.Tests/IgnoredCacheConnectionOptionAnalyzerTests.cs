using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Respire.Extensions.Caching;
using TUnit.Assertions;
using TUnit.Core;
using Verify = Respire.Analyzers.Tests.AnalyzerVerifier<Respire.Analyzers.IgnoredCacheConnectionOptionAnalyzer>;

namespace Respire.Analyzers.Tests;

public class IgnoredCacheConnectionOptionAnalyzerTests
{
    [Test]
    [Arguments("ConnectionString", "\"unused:6379\"")]
    [Arguments("ClientOptions", "_ => new RespireOptions()")]
    public async Task RealCachingAssemblyProducesWarning(string property, string value)
    {
        var source = $$"""
            using Respire;
            using Respire.Extensions.Caching;
            public class Caller
            {
                public void Run(IRespireClient client)
                {
                    client.AsDistributedCache(new RespireCacheOptions { {{property}} = {{value}} });
                }
            }
            """;
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Append(typeof(IRespireClient).Assembly.Location)
            .Append(typeof(RespireCacheOptions).Assembly.Location)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create("RealCacheConsumer",
            [CSharpSyntaxTree.ParseText(source)], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        await Assert.That(compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)).IsEmpty();
        var diagnostics = await compilation.WithAnalyzers([new IgnoredCacheConnectionOptionAnalyzer()])
            .GetAnalyzerDiagnosticsAsync();
        await Assert.That(diagnostics.Length).IsEqualTo(1);
        await Assert.That(diagnostics[0].Id).IsEqualTo("RESP004");
        await Assert.That(source.Substring(diagnostics[0].Location.SourceSpan.Start,
            diagnostics[0].Location.SourceSpan.Length)).IsEqualTo(property);
    }

    [Test]
    [Arguments("RespireClient", "ConnectionString", "\"unused:6379\"")]
    [Arguments("IRespireClient", "ConnectionString", "\"unused:6379\"")]
    [Arguments("RespireClient", "ClientOptions", "_ => new RespireOptions()")]
    [Arguments("IRespireClient", "ClientOptions", "_ => new RespireOptions()")]
    public async Task ExplicitConnectionOptionIsFlagged(string receiver, string property, string value)
        => await Verify.VerifyAsync($$"""
            using Respire;
            using Respire.Extensions.Caching;
            public class Caller
            {
                public void Run({{receiver}} client)
                {
                    client.AsDistributedCache(new RespireCacheOptions { {|RESP004:{{property}}|} = {{value}} });
                }
            }
            """);

    [Test]
    public async Task StaticCallWithNamedArgumentsAndTargetTypedOptionsIsFlagged() => await Verify.VerifyAsync(
        """
        using Respire;
        using Respire.Extensions.Caching;
        public class Caller
        {
            public void Run(IRespireClient client)
            {
                RespireDistributedCacheClientExtensions.AsDistributedCache(
                    options: new() { {|RESP004:ConnectionString|} = "unused", {|RESP004:ClientOptions|} = _ => new RespireOptions() },
                    client: client);
            }
        }
        """);

    [Test]
    public async Task ParenthesizedInitializerWithNonconstantValueIsFlagged() => await Verify.VerifyAsync(
        """
        using Respire;
        using Respire.Extensions.Caching;
        public class Caller
        {
            public void Run(IRespireClient client, string endpoint)
            {
                client.AsDistributedCache((RespireCacheOptions)(new RespireCacheOptions
                {
                    {|RESP004:ConnectionString|} = endpoint,
                }));
            }
        }
        """);

    [Test]
    public async Task CacheOptionsAndExplicitNullsAreNotFlagged() => await Verify.VerifyAsync(
        """
        using Respire;
        using Respire.Extensions.Caching;
        public class Caller
        {
            public void Run(IRespireClient client, Respire.Compression.IRespireValueCodec codec)
            {
                client.AsDistributedCache(new RespireCacheOptions
                {
                    InstanceName = "cache:", ValueCodec = codec,
                    ConnectionString = null, ClientOptions = null,
                });
                client.AsDistributedCache();
                client.AsDistributedCache(null);
            }
        }
        """);

    [Test]
    public async Task SharedOrUnknownOptionsAreNotFlagged() => await Verify.VerifyAsync(
        """
        using Respire;
        using Respire.Extensions.Caching;
        public class Caller
        {
            public void Run(IRespireClient client, RespireCacheOptions unknown)
            {
                var shared = new RespireCacheOptions { ConnectionString = "shared" };
                client.AsDistributedCache(shared);
                client.AsDistributedCache(unknown);
            }
        }
        """);

    [Test]
    public async Task UnrelatedMethodWithSameNameIsNotFlagged() => await Verify.VerifyAsync(
        """
        using Respire;
        using Respire.Extensions.Caching;
        public class Caller
        {
            public void Run(IRespireClient client)
            {
                Other.AsDistributedCache(client, new RespireCacheOptions { ConnectionString = "used" });
            }
        }
        public static class Other
        {
            public static object AsDistributedCache(IRespireClient client, RespireCacheOptions options) => new object();
        }
        """);

    [Test]
    public async Task WarningCanBeSuppressed() => await Verify.VerifyAsync(
        """
        using Respire;
        using Respire.Extensions.Caching;
        public class Caller
        {
            public void Run(IRespireClient client)
            {
        #pragma warning disable RESP004
                client.AsDistributedCache(new RespireCacheOptions { ConnectionString = "intentionally ignored" });
        #pragma warning restore RESP004
            }
        }
        """);
}
