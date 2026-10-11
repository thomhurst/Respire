using Microsoft.CodeAnalysis;
using Respire.Json;
using Respire.Search;

namespace Respire.Analyzers.Tests;

/// <summary>
/// Runtime and Respire metadata references shared by every generator compilation. Reusing the same
/// immutable reference objects lets Roslyn reuse their loaded assembly symbols across compilations.
/// </summary>
internal static class GeneratorTestReferences
{
    public static IReadOnlyList<MetadataReference> All { get; } = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")
        ?? throw new InvalidOperationException("Missing runtime assembly references."))
        .Split(Path.PathSeparator)
        .Append(typeof(RespireCommand).Assembly.Location)
        .Append(typeof(RespireJsonAttribute).Assembly.Location)
        .Append(typeof(RespireSearchAttribute).Assembly.Location)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
        .ToArray();
}
