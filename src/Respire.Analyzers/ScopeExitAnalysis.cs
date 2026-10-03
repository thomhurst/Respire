using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace Respire.Analyzers;

// Shared intra-scope exit proofs for disposal and flush analysis. The graph walker
// also uses the same exception-construction evidence when selecting catch regions.
internal static class ScopeExitAnalysis
{
    internal enum ExitMode { Disposal, FlushProof }
    private enum CatchMatch { None, Possible, Guaranteed }
    private static readonly string[] SimpleExceptionTypes =
        ["System.Exception", "System.InvalidOperationException", "System.ArgumentException"];
    private static readonly ConditionalWeakTable<Compilation, INamedTypeSymbol?[]> SimpleExceptionSymbols = new();

    /// <summary>Whether an explicit exit can skip the statement following this one.</summary>
    public static bool CanBypassFollowingStatement(
        SemanticModel semanticModel, StatementSyntax statement,
        ExitMode mode, SyntaxNode? read = null)
    {
        if (mode == ExitMode.FlushProof && read is null) throw new ArgumentNullException(nameof(read));
        foreach (var node in statement.DescendantNodesAndSelf(node =>
                     node is not AnonymousFunctionExpressionSyntax and not LocalFunctionStatementSyntax
                     && (node is not CatchClauseSyntax handler || CanEnterHandler(semanticModel, handler))))
        {
            // A terminal path cannot invalidate a flush proof for a read it never reaches.
            // Disposal must release values even on terminal paths.
            if (mode == ExitMode.FlushProof && ExitsBeforeRead(semanticModel, node, read!))
            {
                continue;
            }

            // Handler exits are visited separately below. A throw guaranteed to be
            // caught inside this statement does not itself bypass its successor.
            if (node is ThrowStatementSyntax caughtThrow
                && IsCaughtInsideStatement(semanticModel, caughtThrow, statement))
            {
                continue;
            }

            if (node is BreakStatementSyntax or ContinueStatementSyntax)
            {
                var target = ResolveJumpTarget(node);
                // A jump confined to this statement still reaches its successor.
                if (target is null || !statement.Span.Contains(target.Span))
                {
                    return true;
                }
            }
            else if (node is YieldStatementSyntax yieldStatement)
            {
                // Reads after suspension still cross a following flush. Disposal
                // must also cover callers abandoning the iterator at yield return.
                if (mode == ExitMode.Disposal || yieldStatement.IsKind(SyntaxKind.YieldBreakStatement))
                {
                    return true;
                }
            }
            else if (node is ReturnStatementSyntax or ThrowStatementSyntax or GotoStatementSyntax)
            {
                // Goto remains a conservative bypass even when its target is local.
                return true;
            }
        }

        return false;
    }

    private static bool ExitsBeforeRead(SemanticModel semanticModel, SyntaxNode node, SyntaxNode read)
    {
        if (node is not ReturnStatementSyntax and not ThrowStatementSyntax
            and not BreakStatementSyntax and not ContinueStatementSyntax
            && !node.IsKind(SyntaxKind.YieldBreakStatement))
        {
            return false;
        }

        var enclosingTries = node.Ancestors().OfType<TryStatementSyntax>().ToArray();
        // Returning or throwing still executes enclosing finally blocks.
        if (enclosingTries.Any(enclosingTry => enclosingTry.Finally?.Span.Contains(read.Span) == true))
        {
            return false;
        }

        if (node is BreakStatementSyntax or ContinueStatementSyntax)
        {
            var loopBody = ResolveJumpTarget(node) switch
            {
                ForStatementSyntax loop => loop.Statement,
                CommonForEachStatementSyntax loop => loop.Statement,
                WhileStatementSyntax loop => loop.Statement,
                DoStatementSyntax loop => loop.Statement,
                _ => null,
            };
            // The jump skips the rest of this iteration. Reads in a loop condition,
            // increment, enclosing finally, or after the loop are not covered by this proof.
            return loopBody?.Span.Contains(read.Span) == true && read.SpanStart > node.Span.End;
        }

        if (node is not ThrowStatementSyntax thrown) return true;
        var thrownType = GetKnownThrownType(semanticModel, thrown);
        foreach (var enclosingTry in enclosingTries)
        {
            if (!enclosingTry.Block.Span.Contains(node.Span)) continue;
            foreach (var handler in enclosingTry.Catches)
            {
                var match = MatchCatch(semanticModel, thrownType, handler);
                if (match == CatchMatch.None) continue;
                if (!CatchExitsBeforeRead(semanticModel, handler, read)) return false;
                // A guaranteed handler prevents this throw reaching later or outer
                // catches. Throws from the handler itself are checked recursively.
                if (match == CatchMatch.Guaranteed) return true;
            }
        }

        return true;
    }

    private static bool CatchExitsBeforeRead(
        SemanticModel semanticModel, CatchClauseSyntax handler, SyntaxNode read)
    {
        if (handler.Span.Contains(read.Span)
            || semanticModel.AnalyzeControlFlow(handler.Block) is not { Succeeded: true, EndPointIsReachable: false } flow)
        {
            return false;
        }

        return flow.ExitPoints.All(exit => ExitsBeforeRead(semanticModel, exit, read));
    }

    private static bool IsCaughtInsideStatement(
        SemanticModel semanticModel, ThrowStatementSyntax thrown, StatementSyntax statement)
    {
        var thrownType = GetKnownThrownType(semanticModel, thrown);
        foreach (var enclosingTry in thrown.Ancestors().OfType<TryStatementSyntax>())
        {
            if (!statement.Span.Contains(enclosingTry.Span))
            {
                break;
            }

            if (!enclosingTry.Block.Span.Contains(thrown.Span))
            {
                continue;
            }

            if (enclosingTry.Catches.Any(handler => MatchCatch(semanticModel, thrownType, handler) == CatchMatch.Guaranteed))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// False only when every exception the try block can raise is a known throw that this
    /// handler cannot catch, or that an earlier handler always catches. Any operation outside
    /// a small non-throwing set keeps the handler reachable.
    /// </summary>
    private static bool CanEnterHandler(SemanticModel semanticModel, CatchClauseSyntax handler)
    {
        if (handler.Parent is not TryStatementSyntax tryStatement
            || semanticModel.GetOperation(tryStatement.Block) is not { } body)
        {
            return true;
        }

        var throws = new List<ThrowStatementSyntax>();
        foreach (var operation in body.DescendantsAndSelf())
        {
            switch (operation)
            {
                case IThrowOperation { Syntax: ThrowStatementSyntax thrown }:
                    throws.Add(thrown);
                    break;
                case IConversionOperation { IsImplicit: true, Conversion.IsReference: true, Parent: IThrowOperation }:
                case IObjectCreationOperation { Parent: IThrowOperation or IConversionOperation { Parent: IThrowOperation } }:
                case IBlockOperation:
                case IConditionalOperation:
                case IExpressionStatementOperation { Operation: IThrowOperation }:
                case ILiteralOperation:
                case ILocalReferenceOperation:
                case IParameterReferenceOperation:
                case IArgumentOperation:
                case IUnaryOperation { OperatorMethod: null, OperatorKind: UnaryOperatorKind.Not }:
                case IBinaryOperation { OperatorMethod: null, OperatorKind:
                    BinaryOperatorKind.ConditionalAnd or BinaryOperatorKind.ConditionalOr
                    or BinaryOperatorKind.Equals or BinaryOperatorKind.NotEquals }:
                    break;
                default:
                    return true;
            }
        }

        var precedingHandlers = tryStatement.Catches.TakeWhile(candidate => candidate != handler).ToArray();
        return throws.Any(thrown =>
        {
            var thrownType = GetKnownThrownType(semanticModel, thrown);
            return precedingHandlers.All(previous => MatchCatch(semanticModel, thrownType, previous) != CatchMatch.Guaranteed)
                   && MatchCatch(semanticModel, thrownType, handler) != CatchMatch.None;
        });
    }

    private static ITypeSymbol? GetKnownThrownType(SemanticModel semanticModel, ThrowStatementSyntax thrown)
        => thrown.Expression is { } expression
            ? GetKnownExactExceptionType(semanticModel.Compilation, semanticModel.GetOperation(ScopeWalker.Unwrap(expression)))
            : null;

    /// <summary>
    /// The exact type of a fresh exception whose construction cannot itself raise a different
    /// exception, or null. Shared by the syntactic exit analysis and the flow-graph walker.
    /// </summary>
    internal static ITypeSymbol? GetKnownExactExceptionType(Compilation compilation, IOperation? operation)
    {
        // User constructors (including new T()) and argument evaluation can throw a
        // different exception before the explicit throw. Keep their catches possible.
        if (operation is not IObjectCreationOperation creation
            || creation.Initializer is not null
            || creation.Arguments.Any(argument => !argument.Value.ConstantValue.HasValue
                && argument.Value is not ILocalReferenceOperation and not IParameterReferenceOperation))
            return null;

        // These framework constructors only store the supplied message/inner exception.
        // Other constructors remain opaque; this is not an interprocedural exception proof.
        var simpleTypes = SimpleExceptionSymbols.GetValue(compilation, static candidate =>
            SimpleExceptionTypes.Select(candidate.GetTypeByMetadataName).ToArray());
        return simpleTypes.Any(type => SymbolEqualityComparer.Default.Equals(creation.Type, type))
            ? creation.Type
            : null;
    }

    private static CatchMatch MatchCatch(
        SemanticModel semanticModel, ITypeSymbol? thrownType, CatchClauseSyntax handler)
    {
        // The graph walker models normal/finally successors. Keep catch dispatch
        // explicit until its exception-region traversal also models these paths.
        var filterIsGuaranteed = handler.Filter is null;
        if (handler.Filter is { } filter
            && semanticModel.GetConstantValue(filter.FilterExpression) is { HasValue: true, Value: bool matches })
        {
            if (!matches) return CatchMatch.None;
            filterIsGuaranteed = true;
        }

        if (handler.Declaration is not null)
        {
            if (semanticModel.GetTypeInfo(handler.Declaration.Type).Type is not { } caughtType)
                return CatchMatch.Possible;
            if (SymbolEqualityComparer.Default.Equals(caughtType,
                    semanticModel.Compilation.GetTypeByMetadataName("System.Exception")))
                return filterIsGuaranteed ? CatchMatch.Guaranteed : CatchMatch.Possible;
            if (thrownType is null) return CatchMatch.Possible;
            if (semanticModel.Compilation.ClassifyConversion(thrownType, caughtType) is not
                ({ IsImplicit: true, IsReference: true } or { IsIdentity: true }))
                return CatchMatch.None;
        }

        return filterIsGuaranteed ? CatchMatch.Guaranteed : CatchMatch.Possible;
    }

    private static SyntaxNode? ResolveJumpTarget(SyntaxNode jump)
        => jump.Ancestors().FirstOrDefault(ancestor =>
            ancestor is ForStatementSyntax or CommonForEachStatementSyntax or WhileStatementSyntax or DoStatementSyntax
            || (jump is BreakStatementSyntax && ancestor is SwitchStatementSyntax));

}
