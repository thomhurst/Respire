using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Respire.Analyzers;

/// <summary>Queued pending results do not need to be awaited to execute their commands.</summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class PendingResultSuppressor : DiagnosticSuppressor
{
    private static readonly SuppressionDescriptor Rule = new(
        "RESPSPR001", "CS4014",
        "RespirePending<T> represents a queued result; await the batch execution or transaction commit instead.");

    public override ImmutableArray<SuppressionDescriptor> SupportedSuppressions { get; } = ImmutableArray.Create(Rule);

    public override void ReportSuppressions(SuppressionAnalysisContext context)
    {
        var pendingType = context.Compilation.GetTypeByMetadataName(PendingReadBeforeFlushAnalyzer.PendingTypeName);
        if (pendingType is null)
        {
            return;
        }

        foreach (var diagnostic in context.ReportedDiagnostics)
        {
            if (diagnostic.Id != Rule.SuppressedDiagnosticId
                || diagnostic.Location.SourceTree is not { } tree)
            {
                continue;
            }

            var node = tree.GetRoot(context.CancellationToken)
                .FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true);
            if (node is not ExpressionSyntax expression)
            {
                continue;
            }

            // Match the result type, not method names or receiver types: facets and user
            // helpers have the same deferred-result contract as IRespireCommandQueue.
            var type = context.GetSemanticModel(tree).GetTypeInfo(expression, context.CancellationToken).Type;
            if (type is not null && SymbolEqualityComparer.Default.Equals(type.OriginalDefinition, pendingType))
            {
                context.ReportSuppression(Suppression.Create(Rule, diagnostic));
            }
        }
    }
}
