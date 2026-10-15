using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Respire.Analyzers;

/// <summary>Identifies typed Redis reads that materialize a whole keyspace or collection.</summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class UnboundedReadAnalyzer : DiagnosticAnalyzer
{
    internal static readonly DiagnosticDescriptor Rule = new(
        DiagnosticIds.UnboundedRead, "Bound Redis collection reads",
        "'{0}' can return an unbounded result; use {1} for large collections",
        DiagnosticIds.Category, DiagnosticSeverity.Warning, isEnabledByDefault: true,
        description: "Whole-collection reads can stall Redis and allocate large replies. Scan or read bounded pages instead.",
        helpLinkUri: "https://thomhurst.github.io/Respire/docs/diagnostics/RESP011");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.RegisterOperationAction(Analyze, OperationKind.Invocation);
    }

    private static void Analyze(OperationAnalysisContext context)
    {
        var call = (IInvocationOperation)context.Operation;
        var name = call.TargetMethod.Name;
        if (name is not ("KeysAsync" or "MembersAsync" or "Members" or "GetAllAsync" or "GetAll"
            or "RangeAsync" or "Range" or "RangeWithScoresAsync" or "RangeWithScores")) return;
        var type = call.TargetMethod.ContainingType;
        if (type.ContainingNamespace.ToDisplayString() != "Respire") return;

        var command = "";
        var alternative = "";
        switch (type.Name)
        {
            case "RespireServerNode" when name == "KeysAsync":
                command = "KEYS";
                alternative = "SCAN (Keys.ScanAsync)";
                break;
            case "ISetCommands" or "SetCommands" or "IBatchSetCommands" or "BatchSetCommands"
                when name is "MembersAsync" or "Members":
                command = "SMEMBERS";
                alternative = "SSCAN (Sets.ScanAsync)";
                break;
            case "IHashCommands" or "HashCommands" or "IBatchHashCommands" or "BatchHashCommands"
                when name is "GetAllAsync" or "GetAll":
                command = "HGETALL";
                alternative = "HSCAN (Hashes.ScanAsync)";
                break;
            case "IListCommands" or "ListCommands" or "IBatchListCommands" or "BatchListCommands"
                when name is "RangeAsync" or "Range":
                if (!HasConstantArgument(call, "start", 0) || !HasConstantArgument(call, "stop", -1)) return;
                command = "LRANGE 0 -1";
                alternative = "bounded LRANGE pages";
                break;
            case "ISortedSetCommands" or "SortedSetCommands" or "IBatchSortedSetCommands" or "BatchSortedSetCommands"
                when name is "RangeAsync" or "Range" or "RangeWithScoresAsync" or "RangeWithScores":
                if (!HasConstantArgument(call, "start", 0) || !HasConstantArgument(call, "stop", -1)) return;
                command = "ZRANGE 0 -1";
                alternative = "ZSCAN (SortedSets.ScanAsync) or bounded rank ranges";
                break;
        }

        if (command.Length != 0)
            context.ReportDiagnostic(Diagnostic.Create(Rule, call.Syntax.GetLocation(), command, alternative));
    }

    private static bool HasConstantArgument(IInvocationOperation call, string name, long expected)
        => call.Arguments.Any(argument => argument.Parameter?.Name == name
            && argument.Value.ConstantValue is { HasValue: true, Value: long value } && value == expected);
}
