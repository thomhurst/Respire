using Respire.Pipeline.Modules;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Pipeline.Tests;

public class PackagePolicyPublicationTests
{
    [Test]
    [Arguments(typeof(PackProjectsModule))]
    [Arguments(typeof(UploadPackagesToNugetModule))]
    [Arguments(typeof(CreateGitHubReleaseModule))]
    public async Task VerificationIsARequiredPredecessor(Type publication)
    {
        await Assert.That(Dependencies(publication).Any(type => type.Name == "VerifyPackageLocalInitializationModule")).IsTrue();
    }

    [Test]
    public async Task VerificationWaitsForTheSolutionBuild()
    {
        await Assert.That(Dependencies(typeof(VerifyPackageLocalInitializationModule)).Contains(typeof(BuildProjectsModule))).IsTrue();
    }

    [Test]
    public async Task VerifierReceivesSeparateArgumentsForPathsWithSpaces()
    {
        var root = Path.GetFullPath("repository with spaces");
        var options = VerifyPackageLocalInitializationModule.CreateOptions(root);
        await Assert.That(options.Tool).IsEqualTo("pwsh");
        await Assert.That(options.Arguments!.SequenceEqual(new[]
        {
            "-NoLogo", "-NoProfile", "-NonInteractive", "-File",
            Path.Combine(root, "scripts", "Verify-PackageLocalInitialization.ps1"),
            "-RepositoryRoot", root, "-ReportPath", Path.Combine(root, "artifacts", "local-initialization-policy.json")
        })).IsTrue();
    }

    private static IEnumerable<Type> Dependencies(Type module)
    {
        foreach (var attribute in module.GetCustomAttributes(inherit: false))
        {
            var type = attribute.GetType();
            if (!type.IsGenericType || type.GetGenericTypeDefinition().Name != "DependsOnAttribute`1") continue;
            var dependency = type.GenericTypeArguments[0];
            yield return dependency;
            foreach (var ancestor in Dependencies(dependency)) yield return ancestor;
        }
    }
}
