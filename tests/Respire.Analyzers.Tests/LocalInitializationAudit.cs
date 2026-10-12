using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Respire.Analyzers.Tests;

/// <summary>
/// Per-site audit of stack allocations for the local-initialization policy (#1065).
/// Source matching checks that each site carries a reviewed justification for its current
/// enclosing member; it does not prove data-flow or memory safety. The compiled inventory
/// adds what source cannot show: NuGet content sources and source-generator output.
/// </summary>
internal static partial class LocalInitializationAudit
{
    internal const string Marker = "stackalloc-audit";
    internal const int MinimumFieldLength = 12;

    /// <summary>A stack allocation found in source, identified by the offset of its keyword.</summary>
    internal sealed record Site(string Path, int Position, int Line, string[] Configurations, string Fingerprint, string Statement)
    {
        internal string Location => $"{Path}:{Line}";
    }

    /// <summary>One reviewed justification parsed from the comment block above a site's statement.</summary>
    internal sealed record Justification(string Fingerprint, string Initialization, string ReadBoundary, string FailurePath);

    /// <summary>A compiled method that executes <c>localloc</c>.</summary>
    internal sealed record CompiledSite(string Assembly, string Framework, string Method, int Count, bool InitLocals);

    [GeneratedRegex(@"stackalloc-audit\[(?<fingerprint>[0-9a-f]{16})\]:\s*init:(?<init>.*?);\s*read:(?<read>.*?);\s*failure:(?<failure>.*?)(?=stackalloc-audit\[|\z)",
        RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex JustificationPattern();

    /// <summary>
    /// Derives one configuration per framework and build flavor, so DEBUG-only and RELEASE-only
    /// branches are inspected whichever configuration built the test assembly.
    /// </summary>
    internal static TestInspectionSource.SourceConfiguration[] Configurations(IEnumerable<TestInspectionSource.SourceConfiguration> evaluated)
        => evaluated.SelectMany(configuration =>
        {
            var common = configuration.Symbols.Where(symbol => symbol is not ("DEBUG" or "RELEASE")).ToArray();
            return new[] { "DEBUG", "RELEASE" }.Select(flavor => new TestInspectionSource.SourceConfiguration(
                configuration.Framework + "/" + flavor, common.Append(flavor).ToArray()));
        }).OrderBy(configuration => configuration.Framework, StringComparer.Ordinal).ToArray();

    /// <summary>Finds every stack allocation in one file under every configuration and fails closed on unreachable text.</summary>
    internal static Site[] FindSites(string path, string source, IReadOnlyList<TestInspectionSource.SourceConfiguration> configurations)
    {
        var roots = configurations.Select(configuration => (configuration.Framework, Root: TestInspectionSource.Parse(source, configuration))).ToArray();
        var lexical = LexicalStackAllocPositions(roots[0].Root).ToHashSet();
        var active = new Dictionary<int, (SyntaxNode Node, List<string> Configurations)>();
        foreach (var (name, root) in roots)
        {
            var allocations = root.DescendantNodes().Where(node => node.IsKind(SyntaxKind.StackAllocArrayCreationExpression)
                || node.IsKind(SyntaxKind.ImplicitStackAllocArrayCreationExpression)).ToArray();
            // New syntax must fail closed if Roslyn recovers the keyword without an allocation node.
            if (root.DescendantTokens().Count(token => token.IsKind(SyntaxKind.StackAllocKeyword)) != allocations.Length)
                throw new InvalidOperationException("Unrecognized stackalloc syntax: " + path);
            foreach (var allocation in allocations)
            {
                var position = allocation.GetFirstToken().SpanStart;
                if (!active.TryGetValue(position, out var entry)) active[position] = entry = (allocation, []);
                entry.Configurations.Add(name);
            }
        }
        var unreachable = lexical.Except(active.Keys).ToArray();
        if (unreachable.Length != 0)
            throw new InvalidOperationException($"stackalloc in a branch no audited configuration compiles: {path} at offsets {string.Join(", ", unreachable)}");
        return active.OrderBy(pair => pair.Key).Select(pair =>
        {
            var node = pair.Value.Node;
            var anchor = Anchor(node);
            return new Site(path, pair.Key, node.GetLocation().GetLineSpan().StartLinePosition.Line + 1, pair.Value.Configurations.ToArray(),
                Fingerprint(roots.Select(root => root.Root), pair.Key), anchor.ToString().Split('\n')[0].Trim());
        }).ToArray();
    }

    /// <summary>Finds the keyword in active tokens and in disabled preprocessor text.</summary>
    private static IEnumerable<int> LexicalStackAllocPositions(SyntaxNode root)
    {
        foreach (var token in root.DescendantTokens(descendIntoTrivia: true))
            if (token.IsKind(SyntaxKind.StackAllocKeyword)) yield return token.SpanStart;
        foreach (var trivia in root.DescendantTrivia(descendIntoTrivia: true).Where(trivia => trivia.IsKind(SyntaxKind.DisabledTextTrivia)))
            foreach (var token in SyntaxFactory.ParseTokens(trivia.ToString()))
                if (token.IsKind(SyntaxKind.StackAllocKeyword)) yield return trivia.SpanStart + token.SpanStart;
    }

    /// <summary>The statement whose leading comments carry the site's justification, or the member for expression bodies.</summary>
    internal static SyntaxNode Anchor(SyntaxNode allocation)
        => allocation.Ancestors().FirstOrDefault(node => node is StatementSyntax and not BlockSyntax || node is MemberDeclarationSyntax)
            ?? throw new InvalidOperationException("stackalloc outside a statement or member: " + allocation);

    /// <summary>
    /// Hashes the enclosing member's tokens under every configuration. Any change to the member,
    /// including its consumers, read boundaries or a framework-specific branch, invalidates the
    /// review. Comments are trivia and do not participate, so the justification can cite the hash.
    /// </summary>
    private static string Fingerprint(IEnumerable<SyntaxNode> roots, int position)
    {
        var text = new StringBuilder();
        foreach (var root in roots)
        {
            var token = root.FindToken(position);
            SyntaxNode member = token.Parent?.AncestorsAndSelf().OfType<MemberDeclarationSyntax>()
                .FirstOrDefault(node => node is not BaseTypeDeclarationSyntax and not BaseNamespaceDeclarationSyntax) ?? root;
            foreach (var memberToken in member.DescendantTokens()) text.Append(memberToken.RawKind).Append(':').Append(memberToken.Text).Append('\n');
            text.Append("\u0001\n");
        }
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())))[..16];
    }

    /// <summary>Reads the justifications in the contiguous line-comment block directly above an anchor.</summary>
    internal static Justification[] ReadJustifications(SyntaxNode anchor)
    {
        var comments = new List<string>();
        var lineBreaks = 0;
        foreach (var trivia in anchor.GetLeadingTrivia().Reverse())
        {
            // A comment above a framework branch covers the sites inside it.
            if (trivia.IsKind(SyntaxKind.WhitespaceTrivia) || trivia.IsDirective || trivia.IsKind(SyntaxKind.DisabledTextTrivia)) continue;
            if (trivia.IsKind(SyntaxKind.EndOfLineTrivia))
            {
                // A blank line ends the block: the comment must sit directly above the statement.
                if (++lineBreaks > 1) break;
                continue;
            }
            if (!trivia.IsKind(SyntaxKind.SingleLineCommentTrivia)) break;
            lineBreaks = 0;
            comments.Insert(0, trivia.ToString()[2..].Trim());
        }
        var block = string.Join(' ', comments);
        return JustificationPattern().Matches(block).Select(match => new Justification(match.Groups["fingerprint"].Value,
            match.Groups["init"].Value.Trim(), match.Groups["read"].Value.Trim(), match.Groups["failure"].Value.Trim())).ToArray();
    }

    /// <summary>Validates every site in one file against the justifications above its statement.</summary>
    internal static string[] Validate(string path, string source, IReadOnlyList<TestInspectionSource.SourceConfiguration> configurations)
    {
        var sites = FindSites(path, source, configurations);
        var errors = new List<string>();
        var parsed = configurations.ToDictionary(configuration => configuration.Framework, configuration => TestInspectionSource.Parse(source, configuration));
        SyntaxNode AnchorOf(Site site) => Anchor(parsed[site.Configurations[0]].FindToken(site.Position).Parent!);
        // Positions index the same source text in every configuration, so one statement groups its sites.
        foreach (var group in sites.GroupBy(site => AnchorOf(site).SpanStart))
        {
            var first = group.First();
            var justifications = ReadJustifications(AnchorOf(first));
            var expected = group.Count();
            if (justifications.Length != expected)
            {
                errors.Add($"{first.Location}: {expected} stackalloc site(s) in `{first.Statement}` need {expected} "
                    + $"`// {Marker}[{first.Fingerprint}]: init: ...; read: ...; failure: ...` comment(s) directly above the statement; found {justifications.Length}.");
                continue;
            }
            foreach (var justification in justifications)
            {
                if (justification.Fingerprint != first.Fingerprint)
                    errors.Add($"{first.Location}: the enclosing member changed since review; re-audit the site and update its {Marker} fingerprint "
                        + $"{justification.Fingerprint} -> {first.Fingerprint}.");
                foreach (var (field, value) in new[] { ("init", justification.Initialization), ("read", justification.ReadBoundary), ("failure", justification.FailurePath) })
                    if (value.Length < MinimumFieldLength)
                        errors.Add($"{first.Location}: {Marker} `{field}` justification is missing or too short.");
            }
        }
        return errors.ToArray();
    }

    /// <summary>Inventories <c>localloc</c> and local-initialization policy in one compiled assembly.</summary>
    internal static (CompiledSite[] Sites, string[] SkipLocalsInitTargets) InspectAssembly(string path, string framework)
    {
        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        var reader = pe.GetMetadataReader();
        var assembly = reader.GetString(reader.GetAssemblyDefinition().Name);
        var sites = new List<CompiledSite>();
        foreach (var typeHandle in reader.TypeDefinitions)
        {
            var type = reader.GetTypeDefinition(typeHandle);
            foreach (var methodHandle in type.GetMethods())
            {
                var method = reader.GetMethodDefinition(methodHandle);
                if (method.RelativeVirtualAddress == 0) continue;
                var body = pe.GetMethodBody(method.RelativeVirtualAddress);
                var count = CountLocalloc(body.GetILBytes()!);
                if (count != 0)
                    sites.Add(new CompiledSite(assembly, framework, TypeName(reader, type) + "::" + reader.GetString(method.Name), count, body.LocalVariablesInitialized));
            }
        }
        var targets = new List<string>();
        foreach (var attributeHandle in reader.CustomAttributes)
        {
            var attribute = reader.GetCustomAttribute(attributeHandle);
            if (AttributeTypeName(reader, attribute) != "System.Runtime.CompilerServices.SkipLocalsInitAttribute") continue;
            targets.Add(attribute.Parent.Kind switch
            {
                HandleKind.ModuleDefinition => "module",
                HandleKind.TypeDefinition => "type " + TypeName(reader, reader.GetTypeDefinition((TypeDefinitionHandle)attribute.Parent)),
                HandleKind.MethodDefinition => "method " + MethodName(reader, (MethodDefinitionHandle)attribute.Parent),
                _ => attribute.Parent.Kind.ToString(),
            });
        }
        return (sites.OrderBy(site => site.Method, StringComparer.Ordinal).ToArray(), targets.Order(StringComparer.Ordinal).ToArray());
    }

    private static string MethodName(MetadataReader reader, MethodDefinitionHandle handle)
    {
        var method = reader.GetMethodDefinition(handle);
        return TypeName(reader, reader.GetTypeDefinition(method.GetDeclaringType())) + "::" + reader.GetString(method.Name);
    }

    private static string TypeName(MetadataReader reader, TypeDefinition type)
    {
        var name = reader.GetString(type.Name);
        var declaring = type.GetDeclaringType();
        if (!declaring.IsNil) return TypeName(reader, reader.GetTypeDefinition(declaring)) + "+" + name;
        var space = reader.GetString(type.Namespace);
        return space.Length == 0 ? name : space + "." + name;
    }

    private static string AttributeTypeName(MetadataReader reader, CustomAttribute attribute)
    {
        EntityHandle type = attribute.Constructor.Kind switch
        {
            HandleKind.MemberReference => reader.GetMemberReference((MemberReferenceHandle)attribute.Constructor).Parent,
            HandleKind.MethodDefinition => reader.GetMethodDefinition((MethodDefinitionHandle)attribute.Constructor).GetDeclaringType(),
            _ => default,
        };
        return type.Kind switch
        {
            HandleKind.TypeReference => reader.GetTypeReference((TypeReferenceHandle)type) is var reference
                ? reader.GetString(reference.Namespace) + "." + reader.GetString(reference.Name) : "",
            HandleKind.TypeDefinition => TypeName(reader, reader.GetTypeDefinition((TypeDefinitionHandle)type)),
            _ => "",
        };
    }

    /// <summary>Counts <c>localloc</c> instructions by decoding every opcode and skipping its operand.</summary>
    internal static int CountLocalloc(byte[] il)
    {
        var count = 0;
        for (var offset = 0; offset < il.Length;)
        {
            int code = il[offset++];
            if (code == 0xFE)
            {
                code = 0xFE00 | il[offset++];
                if (code == (int)ILOpCode.Localloc) count++;
            }
            offset += OperandSize((ILOpCode)code, il, offset);
        }
        return count;
    }

    // ECMA-335 "no." prefix (0xFE 0x19); ILOpCode has no member for it.
    private const ILOpCode NoPrefix = (ILOpCode)0xFE19;

    private static int OperandSize(ILOpCode code, byte[] il, int offset) => code switch
    {
        ILOpCode.Switch => 4 + 4 * BitConverter.ToInt32(il, offset),
        ILOpCode.Ldc_i8 or ILOpCode.Ldc_r8 => 8,
        ILOpCode.Ldarg_s or ILOpCode.Ldarga_s or ILOpCode.Starg_s or ILOpCode.Ldloc_s or ILOpCode.Ldloca_s or ILOpCode.Stloc_s
            or ILOpCode.Ldc_i4_s or ILOpCode.Unaligned or NoPrefix => 1,
        ILOpCode.Ldarg or ILOpCode.Ldarga or ILOpCode.Starg or ILOpCode.Ldloc or ILOpCode.Ldloca or ILOpCode.Stloc => 2,
        _ when code.IsBranch() => code.GetBranchOperandSize(),
        ILOpCode.Ldc_i4 or ILOpCode.Ldc_r4 or ILOpCode.Jmp or ILOpCode.Call or ILOpCode.Calli or ILOpCode.Callvirt or ILOpCode.Ldstr
            or ILOpCode.Cpobj or ILOpCode.Ldobj or ILOpCode.Newobj or ILOpCode.Castclass or ILOpCode.Isinst or ILOpCode.Unbox
            or ILOpCode.Ldfld or ILOpCode.Ldflda or ILOpCode.Stfld or ILOpCode.Ldsfld or ILOpCode.Ldsflda or ILOpCode.Stsfld
            or ILOpCode.Stobj or ILOpCode.Box or ILOpCode.Newarr or ILOpCode.Ldelema or ILOpCode.Ldelem or ILOpCode.Stelem
            or ILOpCode.Unbox_any or ILOpCode.Refanyval or ILOpCode.Mkrefany or ILOpCode.Ldtoken or ILOpCode.Ldftn
            or ILOpCode.Ldvirtftn or ILOpCode.Initobj or ILOpCode.Constrained or ILOpCode.Sizeof => 4,
        _ => 0,
    };

    /// <summary>Finds string content in generator source that would emit a stack allocation into consumer code.</summary>
    internal static string[] FindEmittedStackAllocations(string path, string source)
        => CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview)).GetRoot().DescendantTokens()
            .Where(token => token.IsKind(SyntaxKind.StringLiteralToken) || token.IsKind(SyntaxKind.InterpolatedStringTextToken)
                || token.IsKind(SyntaxKind.SingleLineRawStringLiteralToken) || token.IsKind(SyntaxKind.MultiLineRawStringLiteralToken)
                || token.IsKind(SyntaxKind.Utf8StringLiteralToken))
            .Where(token => token.ValueText.Contains("stackalloc", StringComparison.Ordinal))
            .Select(token => $"{path}:{token.GetLocation().GetLineSpan().StartLinePosition.Line + 1}").ToArray();
}
