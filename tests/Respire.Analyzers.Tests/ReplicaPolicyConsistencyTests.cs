using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Analyzers.Tests;

/// <summary>Checks the shared policy decision while leaving each layer's independent gates intact.</summary>
public class ReplicaPolicyConsistencyTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task CallerAndRouterRecognizeExactlyTheSameReadyReplicaPolicies(bool net10)
    {
        var sources = TestInspectionSource.ReadLibrarySources().ToArray();
        var enumRoot = TestInspectionSource.Parse(sources.Single(source => source.Path == "LibrarySource/Respire/RespireReadFrom.cs").Text, net10);
        foreach (var (path, methodName, variable, excluded) in new[]
        {
            ("LibrarySource/Respire/RespireClient.ReplicaReadySend.cs", "TryGetDirectReplicaConnection", "_readFrom", true),
            ("LibrarySource/Respire/Internal/ReadEndpointRouter.cs", "TryAcquireReadyConnection", "readFrom", true),
            ("LibrarySource/Respire/Internal/ReadEndpointRouter.cs", "GetConnectionAsync", "readFrom", false)
        })
        {
            var root = TestInspectionSource.Parse(sources.Single(source => source.Path == path).Text, net10);
            var method = root.DescendantNodes().OfType<MethodDeclarationSyntax>()
                .Single(item => item.Identifier.ValueText == methodName
                    && item.Parent is ClassDeclarationSyntax owner
                    && owner.Identifier.ValueText == (path.Contains("RespireClient", StringComparison.Ordinal) ? "RespireClient" : "ReadEndpointRouter"));
            var pattern = method.DescendantNodes().OfType<IsPatternExpressionSyntax>()
                .Single(item => item.Expression is IdentifierNameSyntax name && name.Identifier.ValueText == variable);
            await Assert.That(IsBranchGate(pattern, excluded)).IsTrue();
            var compilation = TestInspectionSource.CreateCompilation([root.SyntaxTree, enumRoot.SyntaxTree]);
            var model = compilation.GetSemanticModel(root.SyntaxTree);
            foreach (var policy in Enum.GetValues<RespireReadFrom>())
            {
                var eligible = Matches(pattern.Pattern, (int)policy, model) != excluded;
                await Assert.That(eligible).IsEqualTo(policy is RespireReadFrom.Replica or RespireReadFrom.ReplicaPreferred);
            }
        }
    }

    [Test]
    [Arguments("||", true, true)]
    [Arguments("&&", true, false)]
    [Arguments("&&", false, true)]
    [Arguments("||", false, false)]
    public async Task PolicyPredicateMustStillGateItsBranch(string combination, bool excluded, bool valid)
    {
        var root = CSharpSyntaxTree.ParseText("class Control { void Use(int policy, bool otherGate) { if (policy is (1 or 2) "
            + combination + " otherGate) return; } }").GetRoot();
        var pattern = root.DescendantNodes().OfType<IsPatternExpressionSyntax>().Single();
        await Assert.That(IsBranchGate(pattern, excluded)).IsEqualTo(valid);
    }

    // Rejected policies must independently force the null branch (OR). Accepted policies
    // must be necessary for the ready branch (AND), alongside each layer's other gates.
    private static bool IsBranchGate(IsPatternExpressionSyntax pattern, bool excluded)
    {
        SyntaxNode current = pattern;
        while (current.Parent is not IfStatementSyntax)
        {
            if (current.Parent is ParenthesizedExpressionSyntax parentheses) current = parentheses;
            else if (current.Parent is BinaryExpressionSyntax binary
                && binary.IsKind(excluded ? SyntaxKind.LogicalOrExpression : SyntaxKind.LogicalAndExpression)) current = binary;
            else return false;
        }
        return current.Parent is IfStatementSyntax branch && branch.Condition == current;
    }

    [Test]
    [Arguments("RespireReadFrom.Replica or RespireReadFrom.ReplicaPreferred", false, true)]
    [Arguments("not (RespireReadFrom.Replica or RespireReadFrom.ReplicaPreferred)", true, true)]
    [Arguments("RespireReadFrom.Replica", false, false)]
    [Arguments("RespireReadFrom.Replica or RespireReadFrom.ReplicaPreferred or RespireReadFrom.Primary", false, false)]
    [Arguments("not (RespireReadFrom.Replica or RespireReadFrom.PrimaryPreferred)", true, false)]
    public async Task GuardDetectsMissingAndExtraPolicies(string text, bool excluded, bool consistent)
    {
        var enumRoot = TestInspectionSource.Parse(TestInspectionSource.ReadLibrarySources()
            .Single(source => source.Path == "LibrarySource/Respire/RespireReadFrom.cs").Text, true);
        var root = CSharpSyntaxTree.ParseText("namespace Respire; class Control { bool Use(RespireReadFrom policy) => policy is (" + text + "); }",
            new CSharpParseOptions(LanguageVersion.Preview)).GetRoot();
        var compilation = TestInspectionSource.CreateCompilation([root.SyntaxTree, enumRoot.SyntaxTree]);
        await Assert.That(compilation.GetDiagnostics().Where(item => item.Severity == DiagnosticSeverity.Error).ToArray()).IsEmpty();
        var pattern = root.DescendantNodes().OfType<IsPatternExpressionSyntax>().Single();
        var model = compilation.GetSemanticModel(root.SyntaxTree);
        var matches = Enum.GetValues<RespireReadFrom>().All(policy =>
            (Matches(pattern.Pattern, (int)policy, model) != excluded)
            == (policy is RespireReadFrom.Replica or RespireReadFrom.ReplicaPreferred));
        await Assert.That(matches).IsEqualTo(consistent);
    }

    // Fail on an unrecognized shape rather than silently approving a changed policy predicate.
    private static bool Matches(PatternSyntax pattern, int value, SemanticModel model)
        => pattern switch
        {
            ConstantPatternSyntax constant when model.GetConstantValue(constant.Expression) is { HasValue: true, Value: int policy } => value == policy,
            ParenthesizedPatternSyntax parentheses => Matches(parentheses.Pattern, value, model),
            UnaryPatternSyntax unary when unary.IsKind(SyntaxKind.NotPattern) => !Matches(unary.Pattern, value, model),
            BinaryPatternSyntax binary when binary.IsKind(SyntaxKind.OrPattern) => Matches(binary.Left, value, model) || Matches(binary.Right, value, model),
            BinaryPatternSyntax binary when binary.IsKind(SyntaxKind.AndPattern) => Matches(binary.Left, value, model) && Matches(binary.Right, value, model),
            _ => throw new InvalidOperationException("Unrecognized replica policy predicate: " + pattern)
        };
}
