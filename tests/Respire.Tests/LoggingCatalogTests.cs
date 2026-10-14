using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Respire.Tests;

public class LoggingCatalogTests
{
    private sealed record LogEvent(string Package, int Id, string Name, string Level);

    [Test]
    public async Task ShippingLogsHaveUniqueExplicitIdsAndMatchPublishedCatalog()
    {
        var root = CommandRouteOwnership.FindRepository();
        var packages = XDocument.Load(Path.Combine(root, "Directory.Build.props"))
            .Descendants("RespirePackageProjects").Single().Value
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var events = new List<LogEvent>();
        var violations = new List<string>();
        foreach (var package in packages)
        {
            foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "src", package), "*.cs", SearchOption.AllDirectories)
                .Where(file => !Path.GetRelativePath(root, file).Split(Path.DirectorySeparatorChar)
                    .Any(part => part is "obj" or "bin")))
            {
                var syntax = CSharpSyntaxTree.ParseText(File.ReadAllText(file), new CSharpParseOptions(LanguageVersion.Preview)).GetRoot();
                events.AddRange(ReadEvents(package, syntax));
                violations.AddRange(FindNonGeneratedLogging(syntax).Select(call => $"{file}: {call}"));
            }
        }

        await Assert.That(violations).IsEmpty();
        await Assert.That(Duplicates(events)).IsEmpty();
        await Assert.That(events.Count).IsGreaterThan(0);
        var published = File.ReadLines(Path.Combine(root, "website", "docs", "integrations", "log-events.md"))
            .Where(line => line.StartsWith("| `", StringComparison.Ordinal))
            .Select(line => line.Split('|', StringSplitOptions.TrimEntries))
            .Select(parts => new LogEvent(parts[1].Trim('`'), int.Parse(parts[2]), parts[3].Trim('`'), parts[4])).ToArray();
        await Assert.That(events.ToArray()).IsEquivalentTo(published);
    }

    [Test]
    public async Task CatalogComparisonRejectsLevelMismatch()
    {
        var syntax = CSharpSyntaxTree.ParseText("""
            partial class Example {
                [LoggerMessage(1, LogLevel.Warning, "Positional")]
                static partial void Positional(ILogger logger);
                [LoggerMessage(EventId = 2, Level = LogLevel.Error, Message = "Named")]
                static partial void Named(ILogger logger);
                [LoggerMessage(EventId = 3, Message = "Dynamic")]
                static partial void Dynamic(ILogger logger, LogLevel level);
            }
            """).GetRoot();
        var events = ReadEvents("Package", syntax).ToArray();
        LogEvent[] published = [
            new("Package", 1, "Positional", "Warning"),
            new("Package", 2, "Named", "Error"),
            new("Package", 3, "Dynamic", "Dynamic"),
        ];
        await Assert.That(events).IsEquivalentTo(published);
        foreach (var index in Enumerable.Range(0, published.Length))
        {
            var mismatched = published.ToArray();
            mismatched[index] = mismatched[index] with { Level = "Information" };
            await Assert.That(async () => await Assert.That(events).IsEquivalentTo(mismatched))
                .Throws<TUnit.Assertions.Exceptions.AssertionException>();
        }
    }

    [Test]
    public async Task DuplicateGuardCoversDifferentClassesButAllowsDifferentPackages()
    {
        var syntax = CSharpSyntaxTree.ParseText("""
            partial class First { [LoggerMessage(1, LogLevel.Warning, "First")] static partial void One(ILogger logger); }
            partial class Second { [LoggerMessage(1, LogLevel.Warning, "Second")] static partial void Two(ILogger logger); }
            """).GetRoot();
        var events = ReadEvents("Package", syntax).ToArray();
        await Assert.That(Duplicates(events)).IsEquivalentTo(new[] { "Package:1" });
        await Assert.That(Duplicates([events[0], events[1] with { Package = "Other" }])).IsEmpty();
    }

    [Test]
    public async Task DirectLoggingGuardDetectsExtensionAndCachedDelegateCalls()
    {
        var syntax = CSharpSyntaxTree.ParseText("""
            class Example {
                void Log(ILogger logger) {
                    logger.LogWarning("warning");
                    logger?.LogError("error");
                    logger.Log(LogLevel.Debug, "debug");
                    LoggerExtensions.LogInformation(logger, "information");
                    LoggerMessage.Define<int>(LogLevel.Warning, default, "{Value}");
                    logger.GeneratedEvent();
                }
            }
            """).GetRoot();
        await Assert.That(FindNonGeneratedLogging(syntax).Count()).IsEqualTo(5);
    }

    private static IEnumerable<LogEvent> ReadEvents(string package, SyntaxNode root)
    {
        foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            foreach (var attribute in method.AttributeLists.SelectMany(list => list.Attributes)
                .Where(attribute => attribute.Name.ToString().Split('.').Last() is "LoggerMessage" or "LoggerMessageAttribute"))
            {
                var arguments = attribute.ArgumentList!.Arguments;
                var id = arguments.FirstOrDefault(argument => argument.NameEquals?.Name.Identifier.ValueText == "EventId")
                    ?? arguments.FirstOrDefault(argument => argument.NameEquals is null && argument.NameColon is null);
                if (id?.Expression is not LiteralExpressionSyntax { Token.Value: int value } || value <= 0)
                    throw new InvalidOperationException($"{package}.{method.Identifier}: logging requires an explicit positive integer event ID.");
                var name = arguments.FirstOrDefault(argument => argument.NameEquals?.Name.Identifier.ValueText == "EventName");
                var eventName = name is null ? method.Identifier.ValueText
                    : name.Expression is LiteralExpressionSyntax { Token.Value: string text } ? text
                    : throw new InvalidOperationException("Event names must be stable string literals.");
                var level = arguments.FirstOrDefault(argument => argument.NameEquals?.Name.Identifier.ValueText == "Level"
                        || argument.NameColon?.Name.Identifier.ValueText == "level")
                    ?? arguments.Where(argument => argument.NameEquals is null && argument.NameColon is null).Skip(1).FirstOrDefault();
                var eventLevel = level is null ? "Dynamic"
                    : level.Expression is MemberAccessExpressionSyntax member ? member.Name.Identifier.ValueText
                    : throw new InvalidOperationException("Event levels must be explicit LogLevel members.");
                yield return new LogEvent(package, value, eventName, eventLevel);
            }
        }
    }

    private static string[] Duplicates(IEnumerable<LogEvent> events) => events
        .GroupBy(entry => (entry.Package, entry.Id)).Where(group => group.Count() > 1)
        .Select(group => $"{group.Key.Package}:{group.Key.Id}").ToArray();

    private static IEnumerable<string> FindNonGeneratedLogging(SyntaxNode root)
    {
        foreach (var call in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            var name = call.Expression switch
            {
                MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
                MemberBindingExpressionSyntax member => member.Name.Identifier.ValueText,
                SimpleNameSyntax simple => simple.Identifier.ValueText,
                _ => "",
            };
            if (name is "LogTrace" or "LogDebug" or "LogInformation" or "LogWarning" or "LogError" or "LogCritical"
                || (name == "Define" && call.Expression.ToString().Contains("LoggerMessage", StringComparison.Ordinal))
                // ACL LOG parses a Redis command reply; it does not emit a diagnostic.
                || (name == "Log" && call.Expression.ToString() != "AclParser.Log"))
                yield return call.ToString();
        }
    }
}
