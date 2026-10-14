using System.IO.Compression;
using System.Reflection;
using System.Xml.Linq;
using PipelineAttributes = ModularPipelines.Attributes;
using Respire.Pipeline.Modules;
using Respire.Pipeline.Modules.LocalMachine;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Pipeline.Tests;

public class CoreInternalsValidationTests
{
    [Test]
    public async Task UploadsRequireSuccessfulValidationAfterPacking()
    {
        await Assert.That(typeof(UploadPackagesToNugetModule)
            .IsDefined(typeof(PipelineAttributes.DependsOnAttribute<VerifyCoreInternalsDependenciesModule>))).IsTrue();
        await Assert.That(typeof(UploadPackagesToLocalNuGetModule)
            .IsDefined(typeof(PipelineAttributes.DependsOnAttribute<VerifyCoreInternalsDependenciesModule>))).IsTrue();
        await Assert.That(typeof(VerifyCoreInternalsDependenciesModule)
            .IsDefined(typeof(PipelineAttributes.DependsOnAttribute<PackProjectsModule>))).IsTrue();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ValidationPropagatesInvalidPackageFailure(bool invalid)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Directory.Build.props")))
            root = root.Parent;
        if (root is null) throw new InvalidOperationException("Repository root not found.");

        var packages = Directory.CreateTempSubdirectory("Respire package validation ");
        try
        {
            const string version = "1.2.3-preview.1570";
            var props = XDocument.Load(Path.Combine(root.FullName, "Directory.Build.props"));
            var shipping = props.Descendants("RespirePackageProjects").Single().Value.Split(';', StringSplitOptions.TrimEntries);
            var frameworks = props.Descendants("TargetFrameworks").First().Value.Split(';');
            var core = XDocument.Load(Path.Combine(root.FullName, "src", "Respire", "Respire.csproj"));
            var friends = core.Descendants("InternalsVisibleTo").Select(x => x.Attribute("Include")!.Value)
                .Where(shipping.Contains).ToArray();
            foreach (var id in friends.Prepend("Respire"))
            {
                using var archive = ZipFile.Open(Path.Combine(packages.FullName, $"{id}.{version}.nupkg"), ZipArchiveMode.Create);
                using var writer = new StreamWriter(archive.CreateEntry($"{id}.nuspec").Open());
                var dependencyVersion = invalid && id == friends[0] ? version : $"[{version}]";
                var metadata = new XElement("metadata", new XElement("id", id), new XElement("version", version),
                    new XElement("dependencies", frameworks.Select(f => new XElement("group", new XAttribute("targetFramework", f),
                        new XElement("dependency", new XAttribute("id", "Respire"), new XAttribute("version", dependencyVersion))))));
                writer.Write(new XElement("package", metadata).ToString());
            }

            if (invalid)
            {
                await Assert.That(async () => await VerifyCoreInternalsDependenciesModule.VerifyAsync(
                    root.FullName, packages.FullName, version, CancellationToken.None))
                    .Throws<InvalidOperationException>();
            }
            else
            {
                var output = await VerifyCoreInternalsDependenciesModule.VerifyAsync(
                    root.FullName, packages.FullName, version, CancellationToken.None);
                await Assert.That(output).Contains($"pins Respire [{version}]");
            }
        }
        finally
        {
            packages.Delete(recursive: true);
        }
    }
}
