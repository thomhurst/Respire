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
    private readonly struct ThrownType(ITypeSymbol? type, bool exact)
    {
        internal ITypeSymbol? Type { get; } = type;
        internal bool Exact { get; } = exact;
    }
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
                && ThrowCannotBypassStatement(semanticModel, caughtThrow, statement, mode, read))
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
        if (enclosingTries.Any(enclosingTry => FinallyRunsReadAfterExit(enclosingTry, node, read)))
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
        return GetThrownTypes(semanticModel, thrown).All(type => ThrowExitsBeforeRead(semanticModel, thrown, type, read));
    }

    // An exit from the protected block or a handler runs the finally, reaching a read
    // there. An exit from inside the finally itself leaves it without reaching the read.
    private static bool FinallyRunsReadAfterExit(TryStatementSyntax enclosingTry, SyntaxNode exit, SyntaxNode read)
        => enclosingTry.Finally is { } finallyClause
           && finallyClause.Span.Contains(read.Span)
           && !finallyClause.Span.Contains(exit.Span);

    private static bool ThrowExitsBeforeRead(
        SemanticModel semanticModel, ThrowStatementSyntax thrown, ThrownType thrownType, SyntaxNode read)
    {
        var enclosingTries = thrown.Ancestors().OfType<TryStatementSyntax>().ToArray();
        if (enclosingTries.Any(enclosingTry => FinallyRunsReadAfterExit(enclosingTry, thrown, read))) return false;
        foreach (var enclosingTry in enclosingTries)
        {
            if (!enclosingTry.Block.Span.Contains(thrown.Span)) continue;
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

    private static bool ThrowCannotBypassStatement(
        SemanticModel semanticModel, ThrowStatementSyntax thrown, StatementSyntax statement,
        ExitMode mode, SyntaxNode? read)
    {
        return GetThrownTypes(semanticModel, thrown).All(CannotBypass);

        bool CannotBypass(ThrownType thrownType)
        {
            foreach (var enclosingTry in thrown.Ancestors().OfType<TryStatementSyntax>())
            {
                if (read is not null && FinallyRunsReadAfterExit(enclosingTry, thrown, read)) return false;

                if (!enclosingTry.Block.Span.Contains(thrown.Span))
                {
                    continue;
                }

                foreach (var handler in enclosingTry.Catches)
                {
                    var match = MatchCatch(semanticModel, thrownType, handler);
                    if (match == CatchMatch.None) continue;
                    // Exits in a local handler are checked by the surrounding syntax walk.
                    // An outer handler must terminate before the read; otherwise it skips the flush.
                    if (!statement.Span.Contains(handler.Span)
                        && (mode == ExitMode.Disposal || !CatchExitsBeforeRead(semanticModel, handler, read!))) return false;
                    if (match == CatchMatch.Guaranteed) return true;
                }
            }

            // An uncaught exception cannot reach a later read, but still requires disposal.
            return mode == ExitMode.FlushProof;
        }
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

        var thrownTypes = new List<ThrownType>();
        if (!TryCollectEscapingThrows(semanticModel, body, thrownTypes))
        {
            return true;
        }

        var precedingHandlers = tryStatement.Catches.TakeWhile(candidate => candidate != handler).ToArray();
        return thrownTypes.Any(thrownType =>
            precedingHandlers.All(previous => MatchCatch(semanticModel, thrownType, previous) != CatchMatch.Guaranteed)
            && MatchCatch(semanticModel, thrownType, handler) != CatchMatch.None);
    }

    /// <summary>
    /// Collects the explicit throws that can leave <paramref name="root"/>. False when any
    /// operation outside a small non-throwing set could raise something else. A nested
    /// try/catch contributes only the throws its handlers may not catch, plus the throws
    /// of the handlers that a known throw can enter.
    /// </summary>
    private static bool TryCollectEscapingThrows(SemanticModel semanticModel, IOperation root, List<ThrownType> thrownTypes)
    {
        var pending = new Stack<IOperation>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var operation = pending.Pop();
            switch (operation)
            {
                case IThrowOperation { Syntax: ThrowStatementSyntax thrown }:
                    thrownTypes.AddRange(GetThrownTypes(semanticModel, thrown));
                    break;
                case ITryOperation { Finally: null, Syntax: TryStatementSyntax nestedTry } nested
                    when nested.Catches.Length == nestedTry.Catches.Count:
                    if (!TryCollectNestedEscapes(semanticModel, nested, nestedTry, thrownTypes)) return false;
                    continue;
                case IConversionOperation { IsImplicit: true, Conversion.IsReference: true, Parent: IThrowOperation }:
                case IConversionOperation { Parent: IThrowOperation, ConstantValue: { HasValue: true, Value: null } }:
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
                    return false;
            }

            foreach (var child in operation.ChildOperations)
            {
                pending.Push(child);
            }
        }

        return true;
    }

    private static bool TryCollectNestedEscapes(
        SemanticModel semanticModel, ITryOperation nested, TryStatementSyntax nestedTry, List<ThrownType> thrownTypes)
    {
        var innerTypes = new List<ThrownType>();
        if (!TryCollectEscapingThrows(semanticModel, nested.Body, innerTypes)) return false;
        var entered = new bool[nested.Catches.Length];
        foreach (var innerType in innerTypes)
        {
            var caught = false;
            for (var index = 0; index < entered.Length && !caught; index++)
            {
                var match = MatchCatch(semanticModel, innerType, nestedTry.Catches[index]);
                if (match == CatchMatch.None) continue;
                entered[index] = true;
                caught = match == CatchMatch.Guaranteed;
            }

            if (!caught) thrownTypes.Add(innerType);
        }

        // An exception raised by a filter is swallowed, so only an entered handler body can
        // add escaping throws. A rethrow there has an unknown type and stays conservative.
        for (var index = 0; index < entered.Length; index++)
        {
            if (entered[index] && !TryCollectEscapingThrows(semanticModel, nested.Catches[index].Handler, thrownTypes))
                return false;
        }

        return true;
    }

    private static ThrownType[] GetThrownTypes(SemanticModel semanticModel, ThrowStatementSyntax thrown)
    {
        var operation = thrown.Expression is { } expression
            ? semanticModel.GetOperation(ScopeWalker.Unwrap(expression)) : null;
        if (GetExactThrownType(semanticModel, operation) is { } exactType)
        {
            return [new(exactType, true)];
        }

        if (operation is ILocalReferenceOperation or IParameterReferenceOperation
            && operation.Type is { TypeKind: not TypeKind.Dynamic } type)
        {
            // Reading a local/parameter cannot throw during evaluation. Its non-null
            // value may be derived; throwing null instead raises NullReferenceException.
            return [new(type, false), new(semanticModel.Compilation.GetTypeByMetadataName("System.NullReferenceException"), true)];
        }
        return [new(null, true)];
    }

    /// <summary>
    /// The exact runtime type raised by throwing <paramref name="operation"/>, or null when it
    /// is unknown: NullReferenceException for a compile-time null, the type of a simple fresh
    /// construction, or the type a never-reassigned local was initialized with.
    /// </summary>
    internal static ITypeSymbol? GetExactThrownType(SemanticModel semanticModel, IOperation? operation)
    {
        if (operation?.ConstantValue is { HasValue: true, Value: null })
        {
            return semanticModel.Compilation.GetTypeByMetadataName("System.NullReferenceException");
        }

        return GetKnownExactExceptionType(semanticModel.Compilation, operation)
               ?? GetUnreassignedCreationType(semanticModel, operation);
    }

    /// <summary>
    /// The created type of a local initialized with <c>new X(...)</c> and never written again,
    /// so every read is that exact non-null instance. A construction exception happens at the
    /// initializer, before any read. Any possible write or by-reference use returns null.
    /// </summary>
    private static ITypeSymbol? GetUnreassignedCreationType(SemanticModel semanticModel, IOperation? operation)
    {
        if (operation is not ILocalReferenceOperation { Local: { IsRef: false, IsConst: false } local }
            || local.DeclaringSyntaxReferences.Length != 1
            || local.DeclaringSyntaxReferences[0].GetSyntax() is not VariableDeclaratorSyntax { Initializer.Value: { } value } declarator
            || declarator.SyntaxTree != semanticModel.SyntaxTree
            || semanticModel.GetOperation(ScopeWalker.Unwrap(value)) is not IObjectCreationOperation { Type: { } createdType }
            // A switch section's locals are scoped to the whole switch block.
            || declarator.Ancestors().FirstOrDefault(static ancestor =>
                ancestor is BlockSyntax or SwitchStatementSyntax or CompilationUnitSyntax) is not { } scope)
        {
            return null;
        }

        foreach (var identifier in scope.DescendantNodes().OfType<IdentifierNameSyntax>())
        {
            if (identifier.Identifier.ValueText == local.Name
                && SymbolEqualityComparer.Default.Equals(semanticModel.GetSymbolInfo(identifier).Symbol, local)
                && IsPotentialWrite(identifier))
            {
                return null;
            }
        }

        return createdType;
    }

    private static bool IsPotentialWrite(IdentifierNameSyntax identifier)
    {
        // Assignment and deconstruction targets, including member writes, are rejected conservatively.
        if (identifier.Ancestors().Any(ancestor =>
                ancestor is AssignmentExpressionSyntax assignment && assignment.Left.Span.Contains(identifier.Span)
                || ancestor is ForEachVariableStatementSyntax loop && loop.Variable.Span.Contains(identifier.Span)))
        {
            return true;
        }

        SyntaxNode node = identifier;
        while (node.Parent is ParenthesizedExpressionSyntax
               || node.Parent is PostfixUnaryExpressionSyntax suppression
               && suppression.IsKind(SyntaxKind.SuppressNullableWarningExpression))
        {
            node = node.Parent;
        }

        return node.Parent switch
        {
            ArgumentSyntax argument => !argument.RefKindKeyword.IsKind(SyntaxKind.None),
            RefExpressionSyntax or MakeRefExpressionSyntax => true,
            PrefixUnaryExpressionSyntax prefix => prefix.Kind() is SyntaxKind.PreIncrementExpression
                or SyntaxKind.PreDecrementExpression or SyntaxKind.AddressOfExpression,
            PostfixUnaryExpressionSyntax postfix => postfix.Kind() is SyntaxKind.PostIncrementExpression
                or SyntaxKind.PostDecrementExpression,
            _ => false,
        };
    }

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
        SemanticModel semanticModel, ThrownType thrownType, CatchClauseSyntax handler)
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
            if (thrownType.Type is null) return CatchMatch.Possible;
            if (semanticModel.Compilation.ClassifyConversion(thrownType.Type, caughtType) is not
                ({ IsImplicit: true, IsReference: true } or { IsIdentity: true }))
            {
                // A derived runtime value can still match a more specific handler. A type
                // parameter's runtime type derives from each of its class constraints.
                IEnumerable<ITypeSymbol> runtimeBases = thrownType.Type is ITypeParameterSymbol typeParameter
                    ? typeParameter.ConstraintTypes.Where(constraint => constraint.TypeKind == TypeKind.Class)
                    : [thrownType.Type];
                return !thrownType.Exact && runtimeBases.All(runtimeBase =>
                    semanticModel.Compilation.ClassifyConversion(caughtType, runtimeBase).IsImplicit)
                    ? CatchMatch.Possible : CatchMatch.None;
            }
        }

        return filterIsGuaranteed ? CatchMatch.Guaranteed : CatchMatch.Possible;
    }

    private static SyntaxNode? ResolveJumpTarget(SyntaxNode jump)
        => jump.Ancestors().FirstOrDefault(ancestor =>
            ancestor is ForStatementSyntax or CommonForEachStatementSyntax or WhileStatementSyntax or DoStatementSyntax
            || (jump is BreakStatementSyntax && ancestor is SwitchStatementSyntax));

}
