using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Testing;

namespace Respire.Analyzers.Tests;

/// <summary>
/// Runs one analyzer over a snippet plus the Respire stub. Diagnostics are expected inline with
/// the harness' markup syntax: <c>{|RESP001:result|}</c>.
/// </summary>
internal static class AnalyzerVerifier<TAnalyzer>
    where TAnalyzer : DiagnosticAnalyzer, new()
{
    public static Task VerifyAsync(string source)
    {
        var test = new CSharpAnalyzerTest<TAnalyzer, DefaultVerifier>
        {
            ReferenceAssemblies = TestReferences.Assemblies,
        };

        test.TestState.Sources.Add(source);
        test.TestState.Sources.Add(RespireApiStub.Source);

        return test.RunAsync(CancellationToken.None);
    }
}

/// <summary>Runs an analyzer and its code fix, asserting the fixed snippet.</summary>
internal static class CodeFixVerifier<TAnalyzer, TCodeFix>
    where TAnalyzer : DiagnosticAnalyzer, new()
    where TCodeFix : CodeFixProvider, new()
{
    public static Task VerifyAsync(string source, string fixedSource)
    {
        var test = new CSharpCodeFixTest<TAnalyzer, TCodeFix, DefaultVerifier>
        {
            ReferenceAssemblies = TestReferences.Assemblies,
        };

        test.TestState.Sources.Add(source);
        test.TestState.Sources.Add(RespireApiStub.Source);
        test.FixedState.Sources.Add(fixedSource);
        test.FixedState.Sources.Add(RespireApiStub.Source);

        return test.RunAsync(CancellationToken.None);
    }
}

internal static class TestReferences
{
    /// <summary>
    /// net8.0 reference assemblies: the analysed snippets only need ValueTask and IAsyncDisposable,
    /// and this set is the one the testing harness ships support for.
    /// </summary>
    public static ReferenceAssemblies Assemblies { get; } = ReferenceAssemblies.Net.Net80;
}
