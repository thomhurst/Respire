using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using StackExchange.Redis;

namespace Respire.Analyzers.Tests;

[NotInParallel]
public sealed class StackExchangeMigrationTests
{
    private static readonly ImmutableDictionary<string, ReportDiagnostic> Enabled =
        new Dictionary<string, ReportDiagnostic>
        {
            [DiagnosticIds.StackExchangeMigration] = ReportDiagnostic.Info,
            [DiagnosticIds.StackExchangeManualMigration] = ReportDiagnostic.Warn,
        }.ToImmutableDictionary();

    private static readonly MetadataReference[] References = GeneratorTestReferences.All
        .Append(MetadataReference.CreateFromFile(typeof(IDatabase).Assembly.Location))
        .DistinctBy(r => r.Display).ToArray();

    [Test]
    public async Task Sample_FixAll_CompilesAgainstRealLibraries()
    {
        using var stream = typeof(StackExchangeMigrationTests).Assembly.GetManifestResourceStream("StackExchangeMigration.cs.txt")!;
        using var reader = new StreamReader(stream);
        using var workspace = new AdhocWorkspace();
        var document = CreateDocument(workspace, await reader.ReadToEndAsync());
        await AssertCompiles(document.Project);
        var diagnostics = await Analyze(document);
        await Assert.That(diagnostics.Length).IsEqualTo(2);
        await Assert.That(diagnostics.All(d => d.Id == DiagnosticIds.StackExchangeMigration)).IsTrue();

        var fixedSolution = await FixAll(document, FixAllScope.Document);
        var fixedDocument = fixedSolution.GetDocument(document.Id)!;
        await AssertCompiles(fixedDocument.Project);
        await Assert.That((await Analyze(fixedDocument)).Length).IsEqualTo(0);
        var text = (await fixedDocument.GetTextAsync()).ToString();
        await Assert.That(text.Contains("Strings.GetStringAsync(key).ConfigureAwait(false)")).IsTrue();
        await Assert.That(text.Contains("Strings.GetBytesAsync(key)")).IsTrue();
        await Assert.That(text.Contains("Keys.DeleteAsync(key, cancellationToken: default).ConfigureAwait(false) != 0")).IsTrue();
        await Assert.That(text.Contains("Strings.SetAsync(key, \"ready\").ConfigureAwait(false)")).IsTrue();
        await Assert.That(text.Contains("IDatabase database")).IsFalse();
    }

    [Test]
    [Arguments("return (string?)await db.StringGetAsync(key, CommandFlags.PreferReplica);", "Task<string?>")]
    [Arguments("return (string?)await db.StringGetAsync(key, CommandFlags.None);", "Task<string?>")]
    [Arguments("return await db.StringGetAsync(key);", "Task<RedisValue>")]
    [Arguments("return await db.StringGetAsync(new RedisKey[] { key });", "Task<RedisValue[]>")]
    [Arguments("return await db.StringSetAsync(key, \"v\", System.TimeSpan.FromSeconds(10));", "Task<bool>")]
    [Arguments("return await db.StringSetAsync(key, \"v\", when: When.NotExists);", "Task<bool>")]
    [Arguments("return await db.StringSetAsync(key, \"v\", expiry: null, keepTtl: true);", "Task<bool>")]
    [Arguments("return await db.StringSetAsync(key, RedisValue.Null);", "Task<bool>")]
    [Arguments("return await db.StringSetAsync(key, (string?)null);", "Task<bool>")]
    [Arguments("return await db.StringSetAsync(key, key);", "Task<bool>")]
    [Arguments("return await db.StringIncrementAsync(key);", "Task<long>")]
    [Arguments("return await db.KeyDeleteAsync(new RedisKey[] { key });", "Task<long>")]
    [Arguments("var task = db.KeyExistsAsync(key); return await task;", "Task<bool>")]
    [Arguments("return (string?)await db.StringGetAsync((RedisKey)key);", "Task<string?>")]
    [Arguments("return (string?)await db.StringGetAsync(new byte[] { 1, 2 });", "Task<string?>")]
    [Arguments("var alias = db; return await db.KeyExistsAsync(key);", "Task<bool>")]
    [Arguments("System.Func<Task<bool>> read = async () => await db.KeyExistsAsync(key); return await read();", "Task<bool>")]
    public async Task UnsupportedShapes_AreManualOnly(string body, string returnType)
    {
        using var workspace = new AdhocWorkspace();
        var document = CreateDocument(workspace, Wrap(body, returnType));
        await AssertCompiles(document.Project);
        var diagnostics = await Analyze(document);
        await Assert.That(diagnostics.Length > 0).IsTrue();
        await Assert.That(diagnostics.All(d => d.Id == DiagnosticIds.StackExchangeManualMigration)).IsTrue();
        foreach (var diagnostic in diagnostics)
            await Assert.That((await Actions(document, diagnostic)).Count).IsEqualTo(0);
    }

    [Test]
    [Arguments("return await db.KeyExistsAsync(key);", "Task<bool>", "Keys.ExistsAsync")]
    [Arguments("return await db.KeyDeleteAsync(key);", "Task<bool>", "!= 0")]
    [Arguments("await db.KeyDeleteAsync(key);", "Task", "Keys.DeleteAsync")]
    [Arguments("return await db.StringSetAsync(value: \"v\", key: key);", "Task<bool>", "value: \"v\", key: key")]
    [Arguments("return (string?)await db.StringGetAsync(key);", "Task<string?>", "Strings.GetStringAsync")]
    [Arguments("return (byte[]?)await db.StringGetAsync(key);", "Task<byte[]?>", "Strings.GetBytesAsync")]
    [Arguments("var ok = await db.StringSetAsync(key, \"v\"); return ok;", "Task<bool>", "Strings.SetAsync")]
    [Arguments("return (string?)await db.StringGetAsync((string?)await db.StringGetAsync(key));", "Task<string?>", "db.Strings.GetStringAsync(key)")]
    [Arguments("return await db.StringSetAsync((string?)await db.StringGetAsync(key), \"v\");", "Task<bool>", "Strings.SetAsync")]
    [Arguments("return (string?)await db.StringGetAsync((string?)await db.StringGetAsync(key).ConfigureAwait(false)).ConfigureAwait(false);", "Task<string?>", "ConfigureAwait(false)")]
    public async Task SupportedCalls_CompileAfterFix(string body, string returnType, string expected)
    {
        using var workspace = new AdhocWorkspace();
        var document = CreateDocument(workspace, Wrap(body, returnType));
        await AssertCompiles(document.Project);
        var diagnostic = (await Analyze(document)).Single();
        var action = (await Actions(document, diagnostic)).Single();
        var solution = await Apply(action);
        var updated = solution.GetDocument(document.Id)!;
        await AssertCompiles(updated.Project);
        await Assert.That((await updated.GetTextAsync()).ToString().Contains(expected)).IsTrue();
        await Assert.That((await Analyze(updated)).Length).IsEqualTo(0);
    }

    [Test]
    public async Task ConnectionDatabaseSelection_IsManual()
    {
        using var workspace = new AdhocWorkspace();
        var document = CreateDocument(workspace, """
            using StackExchange.Redis;
            class C { IDatabase Open(IConnectionMultiplexer connection) => connection.GetDatabase(3); }
            """);
        await AssertCompiles(document.Project);
        var diagnostic = (await Analyze(document)).Single();
        await Assert.That(diagnostic.Id).IsEqualTo(DiagnosticIds.StackExchangeManualMigration);
        await Assert.That(diagnostic.GetMessage().Contains("database explicitly")).IsTrue();
    }

    [Test]
    public async Task UnrelatedApis_AndSpoofedNamespace_AreIgnored()
    {
        using var workspace = new AdhocWorkspace();
        var document = CreateDocument(workspace, """
            using System.Threading.Tasks;
            namespace StackExchange.Redis {
                public interface IDatabase { Task<string> StringGetAsync(string key); }
            }
            class C {
                public static async Task<string> Read(StackExchange.Redis.IDatabase db) => await db.StringGetAsync("k");
                public static async Task<string> Read(Other db) => await db.StringGetAsync("k");
            }
            class Other { public Task<string> StringGetAsync(string key) => Task.FromResult(key); }
            """);
        await AssertCompiles(document.Project);
        await Assert.That((await Analyze(document)).Length).IsEqualTo(0);
    }

    [Test]
    public async Task Rules_AreOptIn()
    {
        using var workspace = new AdhocWorkspace();
        var document = CreateDocument(workspace, Wrap("return await db.KeyExistsAsync(key);", "Task<bool>"), enabled: false);
        await Assert.That((await Analyze(document)).Length).IsEqualTo(0);
    }

    [Test]
    public async Task MixedSupportedAndUnsupportedCalls_DoNotPartiallyRewriteParameter()
    {
        using var workspace = new AdhocWorkspace();
        var document = CreateDocument(workspace, Wrap("await db.KeyExistsAsync(key); await db.StringIncrementAsync(key);", "Task"));
        var diagnostics = await Analyze(document);
        await Assert.That(diagnostics.Length).IsEqualTo(2);
        await Assert.That(diagnostics.All(d => d.Id == DiagnosticIds.StackExchangeManualMigration)).IsTrue();
    }

    [Test]
    public async Task OtherDatabaseReceiver_IsNotHiddenBySupportedParameter()
    {
        using var workspace = new AdhocWorkspace();
        var document = CreateDocument(workspace, """
            using System.Threading.Tasks;
            using StackExchange.Redis;
            class C {
                static IDatabase other = null!;
                public static async Task Run(IDatabase db) {
                    await db.KeyExistsAsync("k");
                    await other.StringIncrementAsync("k");
                }
            }
            """);
        await AssertCompiles(document.Project);
        var diagnostics = await Analyze(document);
        await Assert.That(diagnostics.Length).IsEqualTo(2);
        await Assert.That(diagnostics.All(d => d.Id == DiagnosticIds.StackExchangeManualMigration)).IsTrue();
    }

    [Test]
    public async Task ExistingCaller_PreventsSignatureBreakingFix()
    {
        using var workspace = new AdhocWorkspace();
        var document = CreateDocument(workspace, Wrap("return await db.KeyExistsAsync(key);", "Task<bool>") + """
            class Caller { public Task<bool> Call(IDatabase db) => C.Run(db, "k"); }
            """);
        await AssertCompiles(document.Project);
        var diagnostic = (await Analyze(document)).Single();
        await Assert.That((await Actions(document, diagnostic)).Count).IsEqualTo(0);
    }

    [Test]
    [Arguments("Task<bool> Call(IDatabase db) => C.Run(db, \"k\");")]
    [Arguments("System.Func<IDatabase, string, Task<bool>> Call() => C.Run;")]
    [Arguments("Task<bool> Call(Respire.RespireClient db) => C.Run(db, \"k\");")]
    public async Task ReferencedOverloads_RequireManualMigration(string caller)
    {
        using var workspace = new AdhocWorkspace();
        var document = CreateDocument(workspace, $$"""
            using System.Threading.Tasks;
            using StackExchange.Redis;
            public static class C {
                public static async Task<bool> Run(IDatabase db, string key) => await db.KeyExistsAsync(key);
                public static Task<bool> Run(object db, string key) => Task.FromResult(false);
            }
            class Caller { {{caller}} }
            """);
        await AssertCompiles(document.Project);
        await Assert.That((await Actions(document, (await Analyze(document)).Single())).Count).IsEqualTo(0);
    }

    [Test]
    public async Task NullableDatabaseParameter_PreservesAnnotation()
    {
        using var workspace = new AdhocWorkspace();
        var document = CreateDocument(workspace, Wrap("if (db is null) return false; return await db.KeyExistsAsync(key);", "Task<bool>")
            .Replace("IDatabase db", "IDatabase? db"));
        // Any non-receiver use remains manual, including a null guard.
        await Assert.That((await Analyze(document)).Single().Id).IsEqualTo(DiagnosticIds.StackExchangeManualMigration);
        document = document.WithText(Microsoft.CodeAnalysis.Text.SourceText.From(
            Wrap("return await db.KeyExistsAsync(key);", "Task<bool>").Replace("IDatabase db", "IDatabase? db")));
        var solution = await Apply((await Actions(document, (await Analyze(document)).Single())).Single());
        var updated = solution.GetDocument(document.Id)!;
        await AssertCompiles(updated.Project);
        await Assert.That((await updated.GetTextAsync()).ToString().Contains("RespireClient? db")).IsTrue();
    }

    [Test]
    public async Task UnrelatedDocumentErrors_DoNotSuppressFix()
    {
        using var workspace = new AdhocWorkspace();
        var document = CreateDocument(workspace, Wrap("return await db.KeyExistsAsync(key);", "Task<bool>"));
        var other = document.Project.AddDocument("Broken.cs", "class Broken { MissingType Field; }");
        document = other.Project.GetDocument(document.Id)!;
        await Assert.That((await Actions(document, (await Analyze(document)).Single())).Count).IsEqualTo(1);
    }

    [Test]
    public async Task PartialTypeSignatureCollision_WithholdsFix()
    {
        using var workspace = new AdhocWorkspace();
        var document = CreateDocument(workspace, Wrap("return await db.KeyExistsAsync(key);", "Task<bool>")
            .Replace("static class C", "static partial class C"));
        var other = document.Project.AddDocument("Other.cs", """
            using System.Threading.Tasks;
            public static partial class C {
                public static Task<bool> Run(Respire.RespireClient db, string key) => Task.FromResult(false);
            }
            """);
        document = other.Project.GetDocument(document.Id)!;
        await AssertCompiles(document.Project);
        await Assert.That((await Actions(document, (await Analyze(document)).Single())).Count).IsEqualTo(0);
    }

    [Test]
    public async Task ManySupportedCalls_ProduceOneMethodDiagnostic()
    {
        using var workspace = new AdhocWorkspace();
        var document = CreateDocument(workspace, Wrap(string.Concat(Enumerable.Repeat("await db.KeyExistsAsync(key);", 100)), "Task"));
        var diagnostic = (await Analyze(document)).Single();
        await Assert.That(diagnostic.Id).IsEqualTo(DiagnosticIds.StackExchangeMigration);
        var solution = await Apply((await Actions(document, diagnostic)).Single());
        await AssertCompiles(solution.GetDocument(document.Id)!.Project);
    }

    [Test]
    public async Task DatabaseSelectionOutsideMethod_IsManual()
    {
        using var workspace = new AdhocWorkspace();
        var document = CreateDocument(workspace, """
            using StackExchange.Redis;
            class C { IConnectionMultiplexer connection = null!; IDatabase Database => connection.GetDatabase(3); }
            """);
        await AssertCompiles(document.Project);
        await Assert.That((await Analyze(document)).Single().Id).IsEqualTo(DiagnosticIds.StackExchangeManualMigration);
    }

    [Test]
    public async Task DependentProjectCaller_PreventsSignatureBreakingFix()
    {
        using var workspace = new AdhocWorkspace();
        var document = CreateDocument(workspace, Wrap("return await db.KeyExistsAsync(key);", "Task<bool>"));
        var solution = document.Project.Solution;
        var callerId = ProjectId.CreateNewId();
        solution = solution.AddProject(callerId, "Caller", "Caller", LanguageNames.CSharp)
            .WithProjectCompilationOptions(callerId, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddMetadataReferences(callerId, References)
            .AddProjectReference(callerId, new ProjectReference(document.Project.Id))
            .AddDocument(DocumentId.CreateNewId(callerId), "Caller.cs", """
                using System.Threading.Tasks;
                using StackExchange.Redis;
                class Caller { Task<bool> Call(IDatabase db) => C.Run(db, "k"); }
                """);
        document = solution.GetDocument(document.Id)!;
        await AssertCompiles(solution.GetProject(callerId)!);
        var diagnostic = (await Analyze(document)).Single();
        await Assert.That((await Actions(document, diagnostic)).Count).IsEqualTo(0);
    }

    [Test]
    public async Task FixAll_WithCombinedOverloadCollision_IsWithheld()
    {
        using var workspace = new AdhocWorkspace();
        var document = CreateDocument(workspace, """
            using System.Threading.Tasks;
            using StackExchange.Redis;
            using Respire;
            public static class C {
                public static async Task<bool> Run(IDatabase db, RespireClient other) => await db.KeyExistsAsync("k");
                public static async Task<bool> Run(RespireClient other, IDatabase db) => await db.KeyExistsAsync("k");
            }
            """);
        await AssertCompiles(document.Project);
        var diagnostics = await Analyze(document);
        await Assert.That(diagnostics.Length).IsEqualTo(2);
        foreach (var diagnostic in diagnostics)
            await Assert.That((await Actions(document, diagnostic)).Count).IsEqualTo(1);
        var provider = new StackExchangeMigrationCodeFixProvider();
        var context = new FixAllContext(document, provider, FixAllScope.Document,
            "Migrate method and IDatabase parameter to RespireClient", provider.FixableDiagnosticIds,
            new MigrationDiagnosticProvider(), CancellationToken.None);
        await Assert.That(await provider.GetFixAllProvider().GetFixAsync(context)).IsNull();
    }

    [Test]
    public async Task MissingRespireReference_WithholdsFix()
    {
        using var workspace = new AdhocWorkspace();
        var document = CreateDocument(workspace, Wrap("return await db.KeyExistsAsync(key);", "Task<bool>"));
        var project = document.Project.WithMetadataReferences(References.Where(r =>
            !string.Equals(r.Display, typeof(RespireClient).Assembly.Location, StringComparison.OrdinalIgnoreCase)));
        document = project.GetDocument(document.Id)!;
        await AssertCompiles(project);
        var diagnostic = (await Analyze(document)).Single();
        await Assert.That((await Actions(document, diagnostic)).Count).IsEqualTo(0);
    }

    [Test]
    [Arguments("public async Task<bool> Run(IDatabase db) => await db.KeyExistsAsync(\"k\");")]
    [Arguments("private IDatabase db = null!; public async Task<bool> Run() => await db.KeyExistsAsync(\"k\");")]
    [Arguments("public static async Task<bool> Run(IDatabaseAsync db) => await db.KeyExistsAsync(\"k\");")]
    [Arguments("public static async Task<bool> Run(IDatabase db, IDatabase other) => await db.KeyExistsAsync(\"k\");")]
    [Arguments("public static async Task<bool> Run(IDatabase db) => await db.KeyExistsAsync((string)null!);")]
    [Arguments("public static async Task<bool> Run(this IDatabase db) => await db.KeyExistsAsync(\"k\");")]
    public async Task UnsupportedMethodBoundaries_AreManual(string member)
    {
        using var workspace = new AdhocWorkspace();
        var document = CreateDocument(workspace, "using System.Threading.Tasks; using StackExchange.Redis; "
            + (member.Contains("this IDatabase") ? "static " : "") + "class C { " + member + " }");
        await AssertCompiles(document.Project);
        var diagnostic = (await Analyze(document)).Single();
        await Assert.That(diagnostic.Id).IsEqualTo(DiagnosticIds.StackExchangeManualMigration);
    }

    [Test]
    [Arguments(FixAllScope.Project)]
    [Arguments(FixAllScope.Solution)]
    public async Task FixAll_UpdatesMultipleDocuments(FixAllScope scope)
    {
        using var workspace = new AdhocWorkspace();
        var first = CreateDocument(workspace, Wrap("return await db.KeyExistsAsync(key);", "Task<bool>"));
        var second = first.Project.AddDocument("Second.cs", Wrap("return await db.KeyDeleteAsync(key);", "Task<bool>").Replace("class C", "class D"));
        first = second.Project.GetDocument(first.Id)!;
        var solution = await FixAll(first, scope);
        foreach (var document in solution.GetProject(first.Project.Id)!.Documents)
            await Assert.That((await Analyze(document)).Length).IsEqualTo(0);
        await AssertCompiles(solution.GetProject(first.Project.Id)!);
    }

    private static string Wrap(string body, string returnType) => $$"""
        #nullable enable
        using System.Threading.Tasks;
        using StackExchange.Redis;
        public static class C {
            public static async {{returnType}} Run(IDatabase db, string key) { {{body}} }
        }
        """;

    private static Document CreateDocument(AdhocWorkspace workspace, string source, bool enabled = true)
    {
        var project = workspace.AddProject("MigrationTest", LanguageNames.CSharp)
            .WithParseOptions(new CSharpParseOptions(LanguageVersion.CSharp12))
            .WithCompilationOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                specificDiagnosticOptions: enabled ? Enabled : null))
            .AddMetadataReferences(References);
        return project.AddDocument("Sample.cs", source);
    }

    private static async Task<ImmutableArray<Diagnostic>> Analyze(Document document)
    {
        var compilation = (await document.Project.GetCompilationAsync())!;
        var diagnostics = await compilation.WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new StackExchangeMigrationAnalyzer()))
            .GetAnalyzerDiagnosticsAsync();
        var tree = await document.GetSyntaxTreeAsync();
        return diagnostics.Where(d => d.Location.SourceTree == tree || d.Location == Location.None)
            .OrderBy(d => d.Location.SourceSpan.Start).ToImmutableArray();
    }

    private static async Task<List<CodeAction>> Actions(Document document, Diagnostic diagnostic)
    {
        var actions = new List<CodeAction>();
        var provider = new StackExchangeMigrationCodeFixProvider();
        if (provider.FixableDiagnosticIds.Contains(diagnostic.Id))
            await provider.RegisterCodeFixesAsync(new CodeFixContext(document, diagnostic, (action, _) => actions.Add(action), CancellationToken.None));
        return actions;
    }

    private static async Task<Solution> Apply(CodeAction action)
        => (await action.GetOperationsAsync(CancellationToken.None)).OfType<ApplyChangesOperation>().Single().ChangedSolution;

    private static async Task<Solution> FixAll(Document document, FixAllScope scope)
    {
        var provider = new StackExchangeMigrationCodeFixProvider();
        var context = new FixAllContext(document, provider, scope,
            "Migrate method and IDatabase parameter to RespireClient", provider.FixableDiagnosticIds,
            new MigrationDiagnosticProvider(), CancellationToken.None);
        var action = await provider.GetFixAllProvider().GetFixAsync(context);
        await Assert.That(action).IsNotNull();
        return await Apply(action!);
    }

    private static async Task AssertCompiles(Project project)
    {
        var compilation = (await project.GetCompilationAsync())!;
        var errors = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
        if (errors.Length != 0) throw new InvalidOperationException(string.Join(Environment.NewLine, errors.Select(d => d.ToString())));
    }

    private sealed class MigrationDiagnosticProvider : FixAllContext.DiagnosticProvider
    {
        public override async Task<IEnumerable<Diagnostic>> GetDocumentDiagnosticsAsync(Document document, CancellationToken cancellationToken)
            => await Analyze(document);

        public override Task<IEnumerable<Diagnostic>> GetProjectDiagnosticsAsync(Project project, CancellationToken cancellationToken)
            => Task.FromResult(Enumerable.Empty<Diagnostic>());

        public override async Task<IEnumerable<Diagnostic>> GetAllDiagnosticsAsync(Project project, CancellationToken cancellationToken)
        {
            var diagnostics = new List<Diagnostic>();
            foreach (var document in project.Documents) diagnostics.AddRange(await Analyze(document));
            return diagnostics;
        }
    }
}
