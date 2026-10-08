using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using TUnit.Assertions;
using TUnit.Core;

namespace Respire.Analyzers.Tests;

[NotInParallel]
public class RespireHashGeneratorTests
{
    [Test]
    [Arguments("[RespireHash(\"user:{Id}\")] public partial record User(string Id, string Name, int? Age);")]
    [Arguments("[RespireHash(\"user:{Id}\")] public partial class User { public required string Id { get; init; } public decimal Amount { get; set; } }")]
    [Arguments("[RespireHash(\"constant\")] internal partial record User(bool Value, Guid? Id, DateTimeOffset? Created);")]
    [Arguments("namespace @event { [RespireHash(\"{event}\")] internal partial record @class(string @event); }")]
    [Arguments("[RespireHash(\"{Id}\")] public partial class User { public int Id { get; set; } public User(int id) { Id = id; } }")]
    [Arguments("[RespireHash(\"{Id}\")] public partial class User { public int Id { get; set; } protected internal User() { } }")]
    [Arguments("[RespireHash(\"{Id}\")] public partial class User { public int Id { get; set; } protected internal User(int id) { Id = id; } }")]
    [Arguments("[RespireHash(\"{Id}\")] public partial class User { public required int Id { get; set; } public User(int id) { Id = id; } }")]
    [Arguments("[RespireHash(\"{Id}\")] public partial class User { public required int Id { get; set; } [System.Diagnostics.CodeAnalysis.SetsRequiredMembers] public User(int id) { Id = id; } }")]
    [Arguments("[RespireHash(\"{Id}\")] internal partial class User { public int Id { get; set; } internal required string Secret { get; init; } [System.Diagnostics.CodeAnalysis.SetsRequiredMembers] public User() { Secret = \"secret\"; } }")]
    [Arguments("[RespireHash(\"{Id}\")] internal partial class User { public int Id { get; set; } internal required string Secret; [System.Diagnostics.CodeAnalysis.SetsRequiredMembers] public User() { Secret = \"secret\"; } }")]
    [Arguments("[RespireHash(\"{Id}\")] public partial record User(string Id, [property: RespireFieldTtl(2500)] string? Token);")]
    public async Task SupportedModelsCompileWithoutWarnings(string declaration)
    {
        var (_, generated, diagnostics) = Generate(declaration);
        await Assert.That(diagnostics).IsEmpty();
        await Assert.That(generated).Contains(" ToFields(");
        await Assert.That(generated).Contains(" FromFields(");
        await Assert.That(generated).Contains(" SetAsync(");
        await Assert.That(generated).Contains(" GetPartialAsync(");
        await Assert.That(generated.Contains("Reflection")).IsFalse();
        await Assert.That(generated.Contains(".As<")).IsFalse();
    }

    [Test]
    [Arguments("[RespireHash(\"{Id}\")] public record User(string Id);")]
    [Arguments("[RespireHash(\"{Id}\")] public partial record User<T>(string Id);")]
    [Arguments("public class Outer { [RespireHash(\"{Id}\")] public partial record User(string Id); }")]
    [Arguments("[RespireHash(\"{Id}\")] public partial record struct User(string Id);")]
    [Arguments("[RespireHash(\"{Id}\")] public abstract partial record User(string Id);")]
    [Arguments("public class Base { } [RespireHash(\"{Id}\")] public partial class User : Base { public string Id { get; set; } = \"\"; }")]
    [Arguments("[RespireHash(\"{Id}\")] public partial record User(string Id, object Unsupported);")]
    [Arguments("[RespireHash(\"{Id}\")] public partial record User(string Id, string[] Unsupported);")]
    [Arguments("[RespireHash(\"{Id}\")] public partial record User(string? Id);")]
    [Arguments("[RespireHash(\"{Missing}\")] public partial record User(string Id);")]
    [Arguments("[RespireHash(\"{Id\")] public partial record User(string Id);")]
    [Arguments("[RespireHash(\"Id}\")] public partial record User(string Id);")]
    [Arguments("[RespireHash(\"\")] public partial record User(string Id);")]
    [Arguments("[RespireHash(null)] public partial record User(string Id);")]
    [Arguments("[RespireHash(\"{Id}\")] public partial record User(string Id); public class UserHashMapper { }")]
    [Arguments("namespace Models { [RespireHash(\"{Id}\")] public partial record User(string Id); } namespace Models.UserHashMapper { }")]
    [Arguments("[RespireHash(\"{Id}\")] internal partial class User { public int Id { get; set; } internal required string Secret { get; init; } }")]
    [Arguments("[RespireHash(\"{Id}\")] internal partial class User { public int Id { get; set; } internal required string Secret; }")]
    [Arguments("[RespireHash(\"{Id}\")] public partial class User { public string Id { get; } = \"a\"; }")]
    [Arguments("[RespireHash(\"{Id}\")] public partial class User { public string Id { get; private set; } = \"a\"; }")]
    [Arguments("[RespireHash(\"{Id}\")] public partial class User { public string Id = \"a\"; }")]
    [Arguments("[RespireHash(\"{Id}\")] public partial class User { public string Id { get; set; } = \"a\"; private User() { } }")]
    [Arguments("[RespireHash(\"{Id}\")] public partial class User { public string Id { get; set; } = \"a\"; protected User() { } }")]
    [Arguments("[RespireHash(\"{Id}\")] public partial class User { public string Id { get; set; } = \"a\"; private protected User() { } }")]
    [Arguments("[RespireHash(\"{Id}\")] public partial class User { public string Id { get; set; } = \"a\"; public User(object unknown) { } }")]
    [Arguments("[RespireHash(\"constant\")] public partial class User { }")]
    [Arguments("[RespireHash(\"constant\")] public partial record User(string Id, string id);")]
    [Arguments("[RespireHash(\"{Id}\")] public partial record User(string Id, [property: RespireFieldTtl(0)] string Token);")]
    [Arguments("[RespireHash(\"{Id}\")] public partial record User(string Id, [property: RespireFieldTtl(-1)] string Token);")]
    public async Task InvalidModelsHaveActionableDiagnosticWithoutOutput(string declaration)
    {
        var (_, generated, diagnostics) = Generate(declaration);
        var diagnostic = diagnostics.Single(item => item.Id == DiagnosticIds.InvalidGeneratedHash);
        await Assert.That(diagnostic.Severity).IsEqualTo(DiagnosticSeverity.Error);
        await Assert.That(diagnostic.Descriptor.HelpLinkUri).EndsWith("/guides/generated-hash-codecs");
        await Assert.That(generated).IsEqualTo("");
        await Assert.That(diagnostics.Any(item => item.Id == "CS8785")).IsFalse();
    }

    [Test]
    public async Task MultipleModelsUseDistinctNamespacesAndPartialDeclarations()
    {
        var (_, generated, diagnostics) = Generate("""
            namespace One {
                [RespireHash("{Id}")] public partial class User { public string Id { get; set; } = ""; }
                public partial class User { public bool Enabled { get; set; } }
            }
            namespace Two { [RespireHash("{Id}")] internal partial record User(long Id); }
            """);
        await Assert.That(diagnostics).IsEmpty();
        await Assert.That(generated).Contains("\"Enabled\"");
        await Assert.That(generated).Contains("namespace Two");
    }

    [Test]
    public async Task PartialReadTypeAvoidsPropertyNameCollision()
    {
        var (_, generated, diagnostics) = Generate("[RespireHash(\"constant\")] public partial record User(string PartialRead, int PartialRead_);");
        await Assert.That(diagnostics).IsEmpty();
        await Assert.That(generated).Contains("class PartialRead__");
    }

    [Test]
    public async Task UnrelatedSyntaxChangeCachesRenderedOutput()
    {
        var compilation = CreateCompilation("[RespireHash(\"{Id}\")] public partial record User(string Id);");
        GeneratorDriver driver = CSharpGeneratorDriver.Create([new RespireHashGenerator().AsSourceGenerator()],
            parseOptions: ParseOptions, driverOptions: new GeneratorDriverOptions(default, trackIncrementalGeneratorSteps: true));
        driver = driver.RunGenerators(compilation);
        driver = driver.RunGenerators(compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText("class Unrelated { }", ParseOptions)));
        var result = driver.GetRunResult().Results.Single();
        await Assert.That(result.TrackedSteps["RespireHashModels"].SelectMany(step => step.Outputs)
            .All(output => output.Reason is IncrementalStepRunReason.Cached or IncrementalStepRunReason.Unchanged)).IsTrue();
        await Assert.That(result.TrackedOutputSteps.SelectMany(step => step.Value).SelectMany(step => step.Outputs)
            .All(output => output.Reason == IncrementalStepRunReason.Cached)).IsTrue();
    }

    [Test]
    public async Task DocumentationExampleCompilesWithTopLevelModel()
    {
        const string source = """
            using System.Collections.Generic;
            var user = new User("42", "Ada", null);
            RespireKey key = UserHashMapper.GetKey(user); // user:{42}
            Dictionary<string, string> fields = UserHashMapper.ToFields(user);
            User copy = UserHashMapper.FromFields(fields);
            await using var root = await RespireClient.ConnectAsync("localhost:6379");
            var client = root.WithKeyPrefix("app:");
            await UserHashMapper.SetAsync(client, user);
            User? stored = await UserHashMapper.GetAsync(client, UserHashMapper.GetKey(user));
            RespireKey binaryKey = new byte[] { 0xff, 0, 0x80 };
            await UserHashMapper.SetAsync(client, binaryKey, user);
            var partial = await UserHashMapper.GetPartialAsync(client, binaryKey,
                [nameof(User.Name), nameof(User.SessionToken)]);
            bool requested = partial.SessionToken.Selected;
            bool exists = partial.SessionToken.Found;
            string? name = partial.Name.Value;
            bool idRequested = partial.Id.Selected;

            var tracker = UserHashMapper.Track(client, key);
            await tracker.UpdateAsync(user);
            await tracker.UpdateAsync(user with { Name = "Grace", SessionToken = null });
            var existing = UserHashMapper.Track(client, user);
            await existing.UpdateAsync(user with { Name = "Grace" });
            var expiring = new ExpiringUser("42", "Ada", "session");
            await ExpiringUserHashMapper.SetAsync(client, expiring);
            await ExpiringUserHashMapper.SetAsync(client, RespireHashExpiryMode.HSetThenExpire, expiring);
            await UserHashMapper.SetAsync(client, user, default);
            await UserHashMapper.SetAsync(client, user, default(System.Threading.CancellationToken));
            await UserHashMapper.SetAsync(client, binaryKey, user, default);
            await UserHashMapper.SetAsync(client, binaryKey, user, default(System.Threading.CancellationToken));
            System.Func<IRespireClient, User, System.Threading.CancellationToken, System.Threading.Tasks.ValueTask> save = UserHashMapper.SetAsync;
            System.Func<IRespireClient, RespireKey, User, System.Threading.CancellationToken, System.Threading.Tasks.ValueTask> saveAtKey = UserHashMapper.SetAsync;
            await save(client, user, default);
            await saveAtKey(client, binaryKey, user, default);
            var fallbackTracker = ExpiringUserHashMapper.Track(client, key, expiryMode: RespireHashExpiryMode.HSetThenExpire);
            await fallbackTracker.UpdateAsync(expiring);

            [RespireHash("user:{{{Id}}}")]
            public partial record User(string Id, string Name, string? SessionToken);
            [RespireHash("user:{Id}")]
            public partial record ExpiringUser(string Id, string Name,
                [property: RespireFieldTtl(60000)] string? SessionToken);
            """;
        GeneratorDriver driver = CSharpGeneratorDriver.Create([new RespireHashGenerator().AsSourceGenerator()],
            parseOptions: ParseOptions);
        driver.RunGeneratorsAndUpdateCompilation(
            CreateCompilation(source).WithOptions(new CSharpCompilationOptions(OutputKind.ConsoleApplication,
                nullableContextOptions: NullableContextOptions.Enable)), out var output, out var diagnostics);

        await Assert.That(diagnostics.Concat(output.GetDiagnostics())
            .Where(item => item.Severity >= DiagnosticSeverity.Warning).ToArray()).IsEmpty();
    }

    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.CSharp12);

    private static CSharpCompilation CreateCompilation(string source)
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Append(typeof(RespireHashAttribute).Assembly.Location).Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => MetadataReference.CreateFromFile(path));
        return CSharpCompilation.Create("HashConsumer", [CSharpSyntaxTree.ParseText(
            "#nullable enable\nusing Respire; using System;\n" + source, ParseOptions)], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
    }

    private static (GeneratorDriver Driver, string Source, Diagnostic[] Diagnostics) Generate(string source)
    {
        GeneratorDriver driver = CSharpGeneratorDriver.Create([new RespireHashGenerator().AsSourceGenerator()],
            parseOptions: ParseOptions, driverOptions: new GeneratorDriverOptions(default, trackIncrementalGeneratorSteps: true));
        driver = driver.RunGeneratorsAndUpdateCompilation(CreateCompilation(source), out var output, out var diagnostics);
        return (driver, string.Join("\n", driver.GetRunResult().GeneratedTrees.Select(tree => tree.ToString())),
            diagnostics.Concat(output.GetDiagnostics()).Where(item => item.Severity >= DiagnosticSeverity.Warning).ToArray());
    }
}
