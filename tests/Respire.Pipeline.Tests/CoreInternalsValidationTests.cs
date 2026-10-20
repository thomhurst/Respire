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
    [Arguments(0)]
    [Arguments(23)]
    public async Task ConcurrentValidatorsUsePrivateUnixStartupCachesAndCleanUp(int exitCode)
    {
        if (OperatingSystem.IsWindows()) return; // PowerShell uses XDG_CACHE_HOME only on Unix.

        var inheritedCache = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
        var root = Directory.CreateTempSubdirectory("Respire validation cache test ");
        try
        {
            root.CreateSubdirectory("scripts");
            await File.WriteAllTextAsync(Path.Combine(root.FullName, "scripts", "Verify-CoreInternalsDependencies.ps1"), """
                param($PackagesPath, $Version)
                $ErrorActionPreference = 'Stop'
                # Query PowerShell's actual startup profile directory, not just the environment.
                $platform = [psobject].Assembly.GetType('System.Management.Automation.Platform')
                $cache = $platform.GetField('CacheDirectory', [Reflection.BindingFlags]'NonPublic,Static').GetValue($null)
                [IO.File]::WriteAllText($PackagesPath, $cache)
                [Console]::Error.WriteLine('validation fixture failure')
                exit ([int]$Version)
                """);
            var reports = Enumerable.Range(0, 3).Select(i => Path.Combine(root.FullName, $"cache-{i}.txt")).ToArray();
            await Task.WhenAll(reports.Select(async report =>
            {
                var validation = VerifyCoreInternalsDependenciesModule.VerifyAsync(
                    root.FullName, report, exitCode.ToString(), CancellationToken.None);
                if (exitCode == 0)
                    await validation;
                else
                {
                    var error = await Assert.That(async () => await validation).Throws<InvalidOperationException>();
                    await Assert.That(error!.Message).Contains("exit 23");
                    await Assert.That(error.Message).Contains("validation fixture failure");
                }
            }));

            var caches = reports.Select(File.ReadAllText).ToArray();
            await Assert.That(caches.Distinct().Count()).IsEqualTo(reports.Length);
            foreach (var cache in caches)
            {
                await Assert.That(Path.GetFileName(cache)).IsEqualTo("powershell");
                var privateRoot = Path.GetDirectoryName(cache)!;
                await Assert.That(Path.GetFileName(privateRoot).StartsWith("Respire-pwsh-", StringComparison.Ordinal)).IsTrue();
                await Assert.That(privateRoot).IsNotEqualTo(inheritedCache);
                await Assert.That(Directory.Exists(privateRoot)).IsFalse();
            }
            await Assert.That(Environment.GetEnvironmentVariable("XDG_CACHE_HOME")).IsEqualTo(inheritedCache);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Test]
    public async Task CancellationStopsValidatorBeforeDeletingItsUnixCache()
    {
        if (OperatingSystem.IsWindows()) return;

        var root = Directory.CreateTempSubdirectory("Respire validation cancellation ");
        using var cancellation = new CancellationTokenSource();
        Task<string>? validation = null;
        try
        {
            root.CreateSubdirectory("scripts");
            await File.WriteAllTextAsync(Path.Combine(root.FullName, "scripts", "Verify-CoreInternalsDependencies.ps1"), """
                param($PackagesPath, $Version)
                [IO.File]::WriteAllText($PackagesPath, $env:XDG_CACHE_HOME)
                [IO.File]::WriteAllText("$PackagesPath.signal", 'ready')
                while ($true) { Start-Sleep -Seconds 1 }
                """);
            var report = Path.Combine(root.FullName, "ready.txt");
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var watcher = new FileSystemWatcher(root.FullName, "ready.txt.signal");
            watcher.Created += (_, _) => ready.TrySetResult();
            watcher.EnableRaisingEvents = true;
            validation = VerifyCoreInternalsDependenciesModule.VerifyAsync(root.FullName, report, "unused", cancellation.Token);
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(30));
            cancellation.Cancel();
            await Assert.That(async () => await validation).Throws<OperationCanceledException>();
            var cache = await File.ReadAllTextAsync(report);
            await Assert.That(Path.GetFileName(cache).StartsWith("Respire-pwsh-", StringComparison.Ordinal)).IsTrue();
            await Assert.That(Directory.Exists(cache)).IsFalse();
        }
        finally
        {
            cancellation.Cancel();
            if (validation is not null)
            {
                try { await validation; }
                catch (OperationCanceledException) { }
            }
            root.Delete(recursive: true);
        }
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task ValidationPropagatesInvalidPackageFailure(bool invalid, bool serializer)
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
                .Where(shipping.Contains)
                .Concat(shipping.Where(id => !string.IsNullOrWhiteSpace(id) &&
                    XDocument.Load(Path.Combine(root.FullName, "src", id, id + ".csproj"))
                        .Descendants("RespirePinCoreDependency").Any(x => x.Value == "true")))
                .Distinct().ToArray();
            var invalidPackage = serializer ? "Respire.Serialization.MessagePack" : friends[0];
            foreach (var id in friends.Prepend("Respire"))
            {
                using var archive = ZipFile.Open(Path.Combine(packages.FullName, $"{id}.{version}.nupkg"), ZipArchiveMode.Create);
                using var writer = new StreamWriter(archive.CreateEntry($"{id}.nuspec").Open());
                var dependencyVersion = invalid && id == invalidPackage ? version : $"[{version}]";
                var metadata = new XElement("metadata", new XElement("id", id), new XElement("version", version),
                    new XElement("dependencies", frameworks.Select(f => new XElement("group", new XAttribute("targetFramework", f),
                        new XElement("dependency", new XAttribute("id", "Respire"), new XAttribute("version", dependencyVersion))))));
                writer.Write(new XElement("package", metadata).ToString());
            }

            if (invalid)
            {
                var error = await Assert.That(async () => await VerifyCoreInternalsDependenciesModule.VerifyAsync(
                    root.FullName, packages.FullName, version, CancellationToken.None))
                    .Throws<InvalidOperationException>();
                await Assert.That(error!.Message).Contains(invalidPackage);
                // PowerShell can wrap its error rendering between the diagnostic and version.
                await Assert.That(error.Message).Contains("must pin Respire");
                await Assert.That(error.Message).Contains($"[{version}]");
            }
            else
            {
                var output = await VerifyCoreInternalsDependenciesModule.VerifyAsync(
                    root.FullName, packages.FullName, version, CancellationToken.None);
                await Assert.That(output).Contains($"pins Respire [{version}]");
                await Assert.That(output).Contains("Core dependency validation: PowerShell ");
                await Assert.That(output).Contains("; runtime .NET ");
            }
        }
        finally
        {
            packages.Delete(recursive: true);
        }
    }
}
