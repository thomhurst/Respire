using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Analyzers.Tests;

public class DispatchAdmissionArchitectureTests
{
    private static readonly string[] PublicationMethods =
        ["TryEnqueue", "TryEnqueueDirect", "TryEnqueueGathered", "AppendStreamingPreludeAsync", "AppendStreamingEnd"];
    private static readonly Dictionary<string, string[]> HelperCallers = new(StringComparer.Ordinal)
    {
        ["TryEnqueueDirect"] = ["TryEnqueue", "TryEnqueueGathered"],
        ["TryEnqueueGathered"] = ["TryEnqueue"],
        ["AppendStreamingPreludeAsync"] = ["SendStreamedSetAsync"],
        ["AppendStreamingEnd"] = ["SendStreamedSetAsync"],
    };

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task NativePublicationRequiresReviewedAdmissionPaths(bool net10)
    {
        var roots = TestInspectionSource.ReadLibrarySources()
            .Where(source => source.Path.StartsWith("LibrarySource/Respire/Networking/RespireConnection", StringComparison.Ordinal))
            .Select(source => TestInspectionSource.Parse(source.Text, net10)).ToArray();
        await Assert.That(InspectNativePublication(roots)).IsEmpty();
    }

    [Test]
    [Arguments("missing")]
    [Arguments("late")]
    [Arguments("publication")]
    [Arguments("helper")]
    [Arguments("delegate")]
    [Arguments("overload")]
    [Arguments("qualified-publication")]
    [Arguments("property")]
    [Arguments("property-publication")]
    public async Task NativeAdmissionGuardDetectsBypasses(string failure)
    {
        await Assert.That(InspectNativePublication([TestInspectionSource.Parse(NativeControl, true)])).IsEmpty();
        var changed = failure switch
        {
            "missing" => NativeControl.Replace("_cacheMutationAdmission?.ValidateDispatchAdmission(in command);", "", StringComparison.Ordinal),
            "late" => NativeControl.Replace(
                "_cacheMutationAdmission?.ValidateDispatchAdmission(in command); command.Write(ref writer);",
                "command.Write(ref writer); _cacheMutationAdmission?.ValidateDispatchAdmission(in command);", StringComparison.Ordinal),
            "publication" => NativeControl.Replace("class RespireConnection {",
                "class RespireConnection { void Bypass() { _inflight.TryEnqueue(null, 0); }", StringComparison.Ordinal),
            "helper" => NativeControl.Replace("class RespireConnection {",
                "class RespireConnection { void Bypass() { TryEnqueueDirect(); }", StringComparison.Ordinal),
            "delegate" => NativeControl.Replace("class RespireConnection {",
                "class RespireConnection { void Bypass() { Action sender = TryEnqueueDirect; }", StringComparison.Ordinal),
            "qualified-publication" => NativeControl.Replace("class RespireConnection {",
                "class RespireConnection { void Bypass() { this._inflight.TryEnqueue(null, 0); }", StringComparison.Ordinal),
            "property" => NativeControl.Replace("class RespireConnection {",
                "class RespireConnection { bool Bypass => TryEnqueueDirect();", StringComparison.Ordinal),
            "property-publication" => NativeControl.Replace("class RespireConnection {",
                "class RespireConnection { bool Bypass => _inflight.TryEnqueue(null, 0);", StringComparison.Ordinal),
            _ => NativeControl.Replace("void TryEnqueue(int unused) => TryEnqueue();",
                "void TryEnqueue(int unused) => TryEnqueueDirect();", StringComparison.Ordinal),
        };
        await Assert.That(InspectNativePublication([TestInspectionSource.Parse(changed, true)]).Count).IsGreaterThan(0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ReadOnlyDeclarationsStayInAuditedFactories(bool net10)
    {
        var calls = 0;
        foreach (var source in TestInspectionSource.ReadLibrarySources())
        {
            var root = TestInspectionSource.Parse(source.Text, net10);
            foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>()
                .Where(IsReadOnlyFactory))
            {
                calls++;
                await Assert.That(IsAuditedFactoryCaller(source.Path, invocation)).IsTrue();
            }
            if (source.Path == "LibrarySource/Respire/Commands/ReadOnlyCommand.cs")
            {
                var owner = root.DescendantNodes().OfType<StructDeclarationSyntax>()
                    .Single(type => type.Identifier.ValueText == "ReadOnlyCommand");
                var constructor = owner.Members.OfType<ConstructorDeclarationSyntax>().Single();
                await Assert.That(constructor.Modifiers.Any(token => token.Text == "private")).IsTrue();
                foreach (var factory in owner.Members.OfType<MethodDeclarationSyntax>()
                    .Where(method => method.Identifier.ValueText is "ForAuditedScript" or "ForNodeRead"))
                    await Assert.That(factory.ParameterList.Parameters.All(parameter => parameter.Default is null)).IsTrue();
            }
        }
        await Assert.That(calls).IsEqualTo(4);
    }

    [Test]
    public async Task ReadOnlyFactoryGuardRejectsUnreviewedCallers()
    {
        var root = TestInspectionSource.Parse(
            "class RespireClient { void SendScriptCommandAsync() => ReadOnlyCommand<Cmd2N>.ForAuditedScript(default, true); }", true);
        var call = root.DescendantNodes().OfType<InvocationExpressionSyntax>().Single();
        await Assert.That(IsReadOnlyFactory(call)).IsTrue();
        await Assert.That(IsAuditedFactoryCaller("LibrarySource/Respire/RespireClient.cs", call)).IsTrue();
        await Assert.That(IsAuditedFactoryCaller("LibrarySource/Respire/Other.cs", call)).IsFalse();
        var unreviewed = TestInspectionSource.Parse(
            "class RespireClient { void SetAsync() => ReadOnlyCommand<Cmd2N>.ForAuditedScript(default, true); }", true);
        await Assert.That(IsAuditedFactoryCaller("LibrarySource/Respire/RespireClient.cs",
            unreviewed.DescendantNodes().OfType<InvocationExpressionSyntax>().Single())).IsFalse();
    }

    private static List<string> InspectNativePublication(IEnumerable<SyntaxNode> roots)
    {
        var owners = roots.SelectMany(root => root.DescendantNodes().OfType<ClassDeclarationSyntax>())
            .Where(owner => owner.Identifier.ValueText == "RespireConnection").ToArray();
        var methods = owners.SelectMany(owner => owner.Members.OfType<MethodDeclarationSyntax>())
            .ToArray();
        var errors = new List<string>();
        var publications = owners.SelectMany(owner => owner.DescendantNodes().OfType<InvocationExpressionSyntax>()).Where(invocation =>
            invocation.Expression is MemberAccessExpressionSyntax access && IsInflightReceiver(access.Expression)
            && access.Name.Identifier.ValueText is "TryEnqueue" or "TryEnqueueDiscard").ToArray();
        var publishers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var publication in publications)
        {
            var method = publication.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();
            if (method is null || !methods.Contains(method)) errors.Add("Native publication outside a reviewed method.");
            else publishers.Add(method.Identifier.ValueText);
        }
        if (!publishers.SetEquals(PublicationMethods)) errors.Add("Native publication inventory changed.");
        foreach (var driver in new[] { "TryEnqueue", "SendStreamedSetAsync" })
        {
            var drivers = methods.Where(item => item.Identifier.ValueText == driver).ToArray();
            if (drivers.Length == 0) errors.Add($"Missing admission driver {driver}.");
            foreach (var method in drivers)
            {
                // The short TryEnqueue overload only forwards to the guarded overload. Any
                // additional work or a different target must undergo admission itself.
                if (driver == "TryEnqueue" && method.ExpressionBody?.Expression is InvocationExpressionSyntax forward
                    && InvokedName(forward) == driver
                    && !forward.DescendantNodes().OfType<InvocationExpressionSyntax>().Any())
                    continue;
                var calls = method.DescendantNodes().OfType<InvocationExpressionSyntax>().ToArray();
                var guard = calls.Where(call => InvokedName(call) == "ValidateDispatchAdmission")
                    .Select(call => call.SpanStart).DefaultIfEmpty(int.MaxValue).Min();
                var dispatch = calls.Where(call => InvokedName(call) is "Write" or "TryEnqueue" or "TryEnqueueDiscard"
                    or "TryEnqueueDirect" or "TryEnqueueGathered" or "AppendStreamingPreludeAsync" or "AppendStreamingStart" or "AppendStreamingEnd")
                    .Select(call => call.SpanStart).DefaultIfEmpty(int.MaxValue).Min();
                if (guard == int.MaxValue || guard >= dispatch) errors.Add($"Admission does not precede dispatch in {driver}.");
            }
        }
        foreach (var owner in owners)
            foreach (var reference in owner.DescendantNodes().OfType<SimpleNameSyntax>()
                .Where(name => HelperCallers.ContainsKey(name.Identifier.ValueText)))
            {
                var caller = reference.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault()?.Identifier.ValueText;
                var invocation = reference.Parent as InvocationExpressionSyntax
                    ?? reference.Parent?.Parent as InvocationExpressionSyntax;
                if (invocation is null || caller is null || !HelperCallers[reference.Identifier.ValueText].Contains(caller, StringComparer.Ordinal))
                    errors.Add($"Unreviewed publication helper reference in {caller ?? "non-method member"}.");
            }
        return errors;
    }

    private static bool IsInflightReceiver(ExpressionSyntax expression) => expression switch
    {
        IdentifierNameSyntax name => name.Identifier.ValueText == "_inflight",
        MemberAccessExpressionSyntax access => access.Name.Identifier.ValueText == "_inflight",
        _ => false,
    };

    private static string? InvokedName(InvocationExpressionSyntax call) => call.Expression switch
    {
        SimpleNameSyntax name => name.Identifier.ValueText,
        MemberAccessExpressionSyntax access => access.Name.Identifier.ValueText,
        MemberBindingExpressionSyntax binding => binding.Name.Identifier.ValueText,
        _ => null,
    };

    private static bool IsReadOnlyFactory(InvocationExpressionSyntax call)
        => call.Expression is MemberAccessExpressionSyntax { Expression: GenericNameSyntax owner } access
            && owner.Identifier.ValueText == "ReadOnlyCommand"
            && access.Name.Identifier.ValueText is "ForAuditedScript" or "ForNodeRead";

    private static bool IsAuditedFactoryCaller(string path, InvocationExpressionSyntax call)
    {
        var method = call.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault()?.Identifier.ValueText;
        return InvokedName(call) switch
        {
            "ForAuditedScript" => path == "LibrarySource/Respire/RespireClient.cs"
                && method is "ExecuteScriptCoreAsync" or "SendScriptCommandAsync",
            "ForNodeRead" => path == "LibrarySource/Respire/Facets/RespireServerNode.cs" && method == "ExecuteAsync",
            _ => false,
        };
    }

    private const string NativeControl = """
        class RespireConnection {
            void TryEnqueue(int unused) => TryEnqueue();
            void TryEnqueue() { _cacheMutationAdmission?.ValidateDispatchAdmission(in command); command.Write(ref writer); TryEnqueueDirect(); TryEnqueueGathered(); _inflight.TryEnqueue(null, 0); }
            void TryEnqueueDirect() { _inflight.TryEnqueue(null, 0); }
            void TryEnqueueGathered() { TryEnqueueDirect(); _inflight.TryEnqueue(null, 0); }
            void SendStreamedSetAsync() { _cacheMutationAdmission?.ValidateDispatchAdmission(in command); AppendStreamingPreludeAsync(); AppendStreamingStart(); AppendStreamingEnd(); }
            void AppendStreamingPreludeAsync() { _inflight.TryEnqueue(null, 0); }
            void AppendStreamingEnd() { _inflight.TryEnqueue(null, 0); }
        }
        """;
}
