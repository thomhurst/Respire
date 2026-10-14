using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Respire.Analyzers;

/// <summary>Checks subscription enumeration and directly registered delivery-gap handlers.</summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class BlockingSubscriptionHandlerAnalyzer : DiagnosticAnalyzer
{
    internal static readonly DiagnosticDescriptor Rule = new(
        DiagnosticIds.BlockingSubscriptionHandler, "Keep subscription handlers non-blocking",
        "'{0}' blocks subscription processing; use asynchronous work or hand it off to a worker",
        DiagnosticIds.Category, DiagnosticSeverity.Warning, isEnabledByDefault: true,
        description: "Blocking subscription consumers delays messages; blocking DeliveryGap handlers stalls the receive path.",
        helpLinkUri: "https://thomhurst.github.io/Respire/docs/diagnostics/RESP012");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.RegisterCompilationStartAction(start =>
        {
            // A method group can be registered more than once, including from concurrent actions.
            var reported = new ConcurrentDictionary<Location, byte>();
            start.RegisterOperationAction(operation => Analyze(operation, reported),
                OperationKind.Loop, OperationKind.EventAssignment);
        });
    }

    private static void Analyze(OperationAnalysisContext context, ConcurrentDictionary<Location, byte> reported)
    {
        switch (context.Operation)
        {
            case IForEachLoopOperation { IsAsynchronous: true } loop when IsMessageStream(loop.Collection.Type):
                Inspect(context, loop.Body, reported);
                break;
            case IEventAssignmentOperation { Adds: true, EventReference: IEventReferenceOperation reference } assignment
                when reference.Event.Name == "DeliveryGap"
                    && reference.Event.ContainingType.ToDisplayString() == "Respire.RespireSubscription":
                var handler = assignment.HandlerValue;
                while (handler is IDelegateCreationOperation creation) handler = creation.Target;
                if (handler is IAnonymousFunctionOperation lambda) Inspect(context, lambda.Body, reported);
                else if (handler is IMethodReferenceOperation method)
                {
                    foreach (var declaration in method.Method.DeclaringSyntaxReferences)
                    {
                        var syntax = declaration.GetSyntax(context.CancellationToken);
                        var body = syntax switch
                        {
                            MethodDeclarationSyntax m => (SyntaxNode?)m.Body ?? m.ExpressionBody?.Expression,
                            LocalFunctionStatementSyntax f => (SyntaxNode?)f.Body ?? f.ExpressionBody?.Expression,
                            _ => null,
                        };
                        if (body is not null && context.Operation.SemanticModel is { } model
                            && body.SyntaxTree == model.SyntaxTree
                            && model.GetOperation(body, context.CancellationToken) is { } operation)
                            Inspect(context, operation, reported);
                    }
                }
                break;
        }
    }

    private static bool IsMessageStream(ITypeSymbol? type)
    {
        if (type is not INamedTypeSymbol named) return false;
        if (named.ToDisplayString() == "Respire.RespireSubscription") return true;
        return named.OriginalDefinition.ToDisplayString() is
                "System.Collections.Generic.IAsyncEnumerable<T>" or "System.Runtime.CompilerServices.ConfiguredCancelableAsyncEnumerable<T>"
            && named.TypeArguments[0].ToDisplayString() == "Respire.RespireMessage";
    }

    private static void Inspect(OperationAnalysisContext context, IOperation operation, ConcurrentDictionary<Location, byte> reported)
    {
        context.CancellationToken.ThrowIfCancellationRequested();
        // Work scheduled in another delegate/local function does not block this consumer.
        if (operation is IAnonymousFunctionOperation or ILocalFunctionOperation or INameOfOperation) return;
        if (operation is IForEachLoopOperation { IsAsynchronous: true } loop && IsMessageStream(loop.Collection.Type))
            return; // Its own registered action reports the body once.

        string? blocking = null;
        if (SyncOverAsyncAnalyzer.GetAwaitable(operation) is not null)
            blocking = operation is IInvocationOperation call ? call.TargetMethod.Name : "Result";
        else if (operation is IInvocationOperation invocation)
        {
            var method = invocation.TargetMethod;
            var type = method.ContainingType.ToDisplayString();
            if ((type == "System.Threading.Thread" && method.Name is "Sleep" or "Join")
                || (type == "System.Threading.Tasks.Task" && method.Name is "WaitAll" or "WaitAny")
                || (type is "System.Threading.SemaphoreSlim" or "System.Threading.ManualResetEventSlim" && method.Name == "Wait")
                || (type == "System.Threading.WaitHandle" && method.Name is "WaitOne" or "WaitAll" or "WaitAny"))
                blocking = method.Name;
        }

        if (blocking is not null && reported.TryAdd(operation.Syntax.GetLocation(), 0))
            context.ReportDiagnostic(Diagnostic.Create(Rule, operation.Syntax.GetLocation(), blocking));
        foreach (var child in operation.ChildOperations) Inspect(context, child, reported);
    }
}
