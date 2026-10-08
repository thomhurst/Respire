using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Respire.Tests;

// Syntax discovery deliberately does not inspect telemetry calls or use their count as a baseline.
internal static class CommandRouteOwnership
{
    internal sealed record Member(string Id, string Type, string Signature, string Name,
        bool PublicRoute, bool HasBody, string File, bool IsInterface = false,
        bool PublicImplementation = false, string[]? Contracts = null);
    internal sealed record Surface(string Type, string OwnerType, string[] Members,
        Dictionary<string, string>? Overrides = null, string Contract = "", string[]? AdditionalOwnerTypes = null);
    internal sealed record Boundary(string Name, string Role, string Member, string? Owner, string Contract);
    internal sealed record NonRoute(string Member, string Reason);
    internal sealed record Inventory(Surface[] Surfaces, Boundary[] Boundaries, NonRoute[]? NonRoutes = null);

    internal static Member[] Discover(IEnumerable<(string File, string Source)> files)
    {
        var members = new List<Member>();
        var symbolSets = new[] { Array.Empty<string>(),
            new[] { "NET", "NET8_0", "NET8_0_OR_GREATER" },
            new[] { "NET", "NET10_0", "NET8_0_OR_GREATER", "NET9_0_OR_GREATER", "NET10_0_OR_GREATER" } };
        var roots = files.SelectMany(file => symbolSets.Select(symbols => (file.File,
            Root: CSharpSyntaxTree.ParseText(file.Source,
                new CSharpParseOptions(LanguageVersion.Preview, preprocessorSymbols: symbols)).GetRoot()))).ToArray();
        var publicTypes = roots.SelectMany(file => file.Root.DescendantNodes().OfType<TypeDeclarationSyntax>())
            .Where(type => type.Modifiers.Any(SyntaxKind.PublicKeyword))
            .Select(TypeId).ToHashSet(StringComparer.Ordinal);
        var typesById = roots.SelectMany(file => file.Root.DescendantNodes().OfType<TypeDeclarationSyntax>())
            .GroupBy(TypeId).ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
        var contractCache = new Dictionary<string, string[]>(StringComparer.Ordinal);
        string[] Contracts(string type)
        {
            if (contractCache.TryGetValue(type, out var cached)) return cached;
            var result = new HashSet<string>(StringComparer.Ordinal);
            void Visit(string current)
            {
                if (!typesById.TryGetValue(current, out var declarations)) return;
                foreach (var declaration in declarations)
                    foreach (var parent in declaration.BaseList?.Types ?? [])
                    {
                        var name = Compact(parent.Type).Replace("global::", "", StringComparison.Ordinal);
                        var space = Namespace(declaration);
                        var qualified = name.Contains('.') || space.Length == 0 ? name : space + "." + name;
                        if (result.Add(qualified)) Visit(qualified);
                    }
            }
            Visit(type);
            return contractCache[type] = result.ToArray();
        }
        foreach (var (file, root) in roots)
        {
            // Union framework branches and public partial declarations independently of telemetry.
            foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
            {
                var types = method.Ancestors().OfType<TypeDeclarationSyntax>().Reverse().ToArray();
                if (types.Length == 0) continue;
                var type = TypeId(types[^1]);
                var explicitContract = method.ExplicitInterfaceSpecifier?.Name.ToString();
                var signature = (explicitContract is null ? "" : explicitContract + ".") + method.Identifier.ValueText + Arity(method.TypeParameterList) + "(" +
                    string.Join(",", method.ParameterList.Parameters.Select(Parameter)) + "):" + Compact(method.ReturnType);
                var visible = types.All(t => publicTypes.Contains(TypeId(t)));
                var publicMethod = method.Modifiers.Any(SyntaxKind.PublicKeyword) ||
                    (types[^1] is InterfaceDeclarationSyntax && !method.Modifiers.Any(SyntaxKind.PrivateKeyword)
                        && !method.Modifiers.Any(SyntaxKind.InternalKeyword));
                var route = visible && publicMethod;
                var contracts = Contracts(type);
                var implements = publicMethod || (explicitContract is not null && contracts.Any(c =>
                    c == explicitContract || c.EndsWith("." + explicitContract, StringComparison.Ordinal)));
                members.Add(new(type + "." + signature, type, signature, method.Identifier.ValueText,
                    route, method.Body is not null || method.ExpressionBody is not null, file,
                    types[^1] is InterfaceDeclarationSyntax, implements, contracts));
            }
            // Deferred inspection and borrowed payload state also have property boundaries.
            foreach (var property in root.DescendantNodes().OfType<PropertyDeclarationSyntax>())
            {
                var type = property.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
                if (type is null) continue;
                var signature = property.Identifier.ValueText + ":" + Compact(property.Type);
                var typeId = TypeId(type);
                members.Add(new(typeId + "." + signature, typeId, signature, property.Identifier.ValueText,
                    false, property.ExpressionBody is not null || property.AccessorList?.Accessors.Any(a =>
                        a.Body is not null || a.ExpressionBody is not null) == true, file));
            }
        }
        return members.GroupBy(m => m.Id).Select(g => g.First() with {
            PublicRoute = g.Any(m => m.PublicRoute), HasBody = g.Any(m => m.HasBody),
            PublicImplementation = g.Any(m => m.HasBody && m.PublicImplementation),
            Contracts = g.SelectMany(m => m.Contracts ?? []).Distinct(StringComparer.Ordinal).ToArray()
        }).OrderBy(m => m.Id, StringComparer.Ordinal).ToArray();
    }

    private static string TypeName(TypeDeclarationSyntax type) => type.Identifier.ValueText + Arity(type.TypeParameterList);
    private static string TypeId(TypeDeclarationSyntax type)
    {
        var space = Namespace(type);
        return (space.Length == 0 ? "" : space + ".") + string.Join(".", type.AncestorsAndSelf().OfType<TypeDeclarationSyntax>().Reverse().Select(TypeName));
    }
    private static string Namespace(TypeDeclarationSyntax type) => string.Join(".", type.Ancestors()
        .OfType<BaseNamespaceDeclarationSyntax>().Reverse().Select(n => n.Name.ToString()));
    private static string Arity(TypeParameterListSyntax? list) => list is null ? "" : "`" + list.Parameters.Count;
    private static string Compact(SyntaxNode node) => string.Concat(node.DescendantTokens().Select(t => t.Text));
    private static string Parameter(ParameterSyntax parameter) => string.Concat(parameter.Modifiers.Select(t => t.Text + " "))
        + (parameter.Type is null ? "" : Compact(parameter.Type));

    internal static string[] Validate(Member[] source, Inventory inventory)
    {
        var errors = new List<string>();
        var members = source.ToDictionary(m => m.Id, StringComparer.Ordinal);
        var declared = new HashSet<string>(StringComparer.Ordinal);
        foreach (var surface in inventory.Surfaces)
        {
            if (string.IsNullOrWhiteSpace(surface.Contract)) errors.Add("Missing surface lifetime contract: " + surface.Type);
            foreach (var signature in surface.Members)
            {
                var route = surface.Type + "." + signature;
                if (!declared.Add(route)) errors.Add("Duplicate route: " + route);
                if (!members.TryGetValue(route, out var entry) || !entry.PublicRoute)
                    errors.Add("Removed or non-public route: " + route);
                var owner = surface.Overrides?.GetValueOrDefault(signature) ?? surface.OwnerType + "." + signature;
                if (!members.TryGetValue(owner, out var final) || !final.HasBody)
                    errors.Add("Missing final owner: " + route + " => " + owner);
                else if (entry?.IsInterface == true && entry.Id != final.Id && final.Signature == entry.Signature
                    && (!final.PublicImplementation || !(final.Contracts ?? []).Contains(entry.Type, StringComparer.Ordinal)))
                    errors.Add("Owner does not implement public contract: " + route + " => " + owner);
                foreach (var additionalType in surface.AdditionalOwnerTypes ?? [])
                {
                    var additionalOwner = additionalType + "." + signature;
                    if (!members.TryGetValue(additionalOwner, out var additional) || !additional.HasBody)
                        errors.Add("Missing alternative final owner: " + route + " => " + additionalOwner);
                    else if (entry?.IsInterface == true && (!additional.PublicImplementation
                        || !(additional.Contracts ?? []).Contains(entry.Type, StringComparer.Ordinal)))
                        errors.Add("Alternative owner does not implement public contract: " + route + " => " + additionalOwner);
                }
            }
            if (surface.Overrides is not null)
                foreach (var signature in surface.Overrides.Keys.Except(surface.Members))
                    errors.Add("Orphan owner override: " + surface.Type + "." + signature);
        }
        foreach (var exclusion in inventory.NonRoutes ?? [])
        {
            if (!declared.Add(exclusion.Member)) errors.Add("Duplicate route classification: " + exclusion.Member);
            if (!members.TryGetValue(exclusion.Member, out var member) || !member.PublicRoute)
                errors.Add("Removed or non-public exclusion: " + exclusion.Member);
            if (string.IsNullOrWhiteSpace(exclusion.Reason)) errors.Add("Missing non-route reason: " + exclusion.Member);
        }
        foreach (var route in source.Where(m => m.PublicRoute && !declared.Contains(m.Id)))
            errors.Add("Undeclared public route: " + route.Id);

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var boundary in inventory.Boundaries)
        {
            if (!names.Add(boundary.Name)) errors.Add("Duplicate boundary: " + boundary.Name);
            if (!members.ContainsKey(boundary.Member)) errors.Add("Missing boundary member: " + boundary.Name);
            if (string.IsNullOrWhiteSpace(boundary.Contract)) errors.Add("Missing lifetime contract: " + boundary.Name);
            if (boundary.Role is not ("final" or "helper" or "borrower" or "internal"))
                errors.Add("Unknown ownership role: " + boundary.Name);
            if (boundary.Role == "internal")
            {
                if (boundary.Owner is not null) errors.Add("Internal observation cannot publish caller failure: " + boundary.Name);
            }
            else if (boundary.Owner is null || !members.TryGetValue(boundary.Owner, out var owner) || !owner.HasBody)
                errors.Add("Missing boundary final owner: " + boundary.Name);
            if (boundary.Role == "final" && boundary.Member != boundary.Owner)
                errors.Add("Final boundary must own itself: " + boundary.Name);
            if (boundary.Role is "helper" or "borrower" && boundary.Member == boundary.Owner)
                errors.Add("Borrowed/helper boundary cannot own itself: " + boundary.Name);
        }
        return errors.ToArray();
    }

    internal static string FindRepository()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "src", "Respire", "Respire.csproj"))) return directory.FullName;
        throw new DirectoryNotFoundException("Command route guard requires the repository source checkout.");
    }
}
