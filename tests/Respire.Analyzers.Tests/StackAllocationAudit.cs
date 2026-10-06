using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Respire.Analyzers.Tests;

/// <summary>Matches reviewed source; this is not a data-flow or memory-safety proof.</summary>
internal static class StackAllocationAudit
{
    internal sealed record Site(string Framework, string Path, int Ordinal, string Fingerprint, string Description)
    {
        internal string Key => $"{Framework}|{Path}|{Ordinal}|{Fingerprint}";
    }

    internal sealed record Review(string Key, string Initialization, string ReadBoundary, string FailurePath);

    internal static Site[] FindSites(string path, string source, TestInspectionSource.SourceConfiguration configuration)
    {
        var root = TestInspectionSource.Parse(source, configuration);
        var allocations = root.DescendantNodes()
            .Where(node => node.IsKind(SyntaxKind.StackAllocArrayCreationExpression)
                || node.IsKind(SyntaxKind.ImplicitStackAllocArrayCreationExpression)).ToArray();
        // New language syntax must fail closed if this Roslyn version recovers a keyword
        // as skipped tokens instead of recognizing an allocation expression.
        if (root.DescendantTokens(descendIntoTrivia: true).Count(token => token.IsKind(SyntaxKind.StackAllocKeyword)) != allocations.Length)
            throw new InvalidOperationException("Unrecognized stackalloc syntax: " + path);
        return allocations
            .Select((node, index) =>
            {
                // Fingerprint the whole enclosing member, including the consumers and branches,
                // so replacing a site or changing its read boundary invalidates the review.
                var member = node.Ancestors().OfType<MemberDeclarationSyntax>().FirstOrDefault() ?? root;
                var tokens = string.Join("\n", member.DescendantTokens().Select(token => $"{token.RawKind}:{token.Text}"));
                var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(tokens)));
                var variable = node.Ancestors().OfType<VariableDeclaratorSyntax>().FirstOrDefault()?.Identifier.ValueText ?? "expression";
                var method = member is MethodDeclarationSyntax declaration ? declaration.Identifier.ValueText : member.Kind().ToString();
                return new Site(configuration.Framework, path, index + 1, fingerprint, $"{method}/{variable}: {node}");
            }).ToArray();
    }

    internal static Review[] ReadReviews(string markdown)
    {
        var reviews = new List<Review>();
        foreach (var line in markdown.Split('\n'))
        {
            // Deliberately narrow, documented table schema. Malformed/deleted rows cannot match a site.
            var cells = line.Trim().Split('|').Select(cell => cell.Trim().Trim('`')).ToArray();
            if (cells.Length != 10 || !(cells[2].StartsWith("src/", StringComparison.Ordinal)
                || cells[2].StartsWith("nuget/", StringComparison.Ordinal))) continue;
            foreach (var framework in cells[1].Split(','))
                reviews.Add(new Review($"{framework.Trim()}|{cells[2]}|{cells[3]}|{cells[4]}", cells[6], cells[7], cells[8]));
        }
        return reviews.ToArray();
    }

    internal static string[] Validate(IEnumerable<Site> sites, IEnumerable<Review> reviews)
    {
        var actual = sites.ToDictionary(site => site.Key);
        var documented = reviews.ToArray();
        var errors = documented.GroupBy(review => review.Key).Where(group => group.Count() != 1)
            .Select(group => "Duplicate audit: " + group.Key).ToList();
        foreach (var review in documented)
        {
            if (!actual.ContainsKey(review.Key)) errors.Add("Stale audit: " + review.Key);
            if (string.IsNullOrWhiteSpace(review.Initialization) || string.IsNullOrWhiteSpace(review.ReadBoundary)
                || string.IsNullOrWhiteSpace(review.FailurePath)) errors.Add("Missing justification: " + review.Key);
        }
        var keys = documented.Select(review => review.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var site in actual.Values.Where(site => !keys.Contains(site.Key)))
            errors.Add($"Unaudited: {site.Key} ({site.Description})");
        return errors.ToArray();
    }
}
