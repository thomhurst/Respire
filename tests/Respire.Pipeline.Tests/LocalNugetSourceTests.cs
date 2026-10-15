using System.Xml.Linq;
using PipelineAttributes = ModularPipelines.Attributes;
using Respire.Pipeline.Modules.LocalMachine;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Pipeline.Tests;

public class LocalNugetSourceTests
{
    [Test]
    public async Task FreshRegistrationIsEnabledAndRepeatedRegistrationDoesNotWrite()
    {
        using var config = new TestConfig();
        var name = await config.RegisterAsync();
        var first = File.ReadAllText(config.Path);
        await Assert.That(await config.RegisterAsync()).IsEqualTo(name);
        await Assert.That(File.ReadAllText(config.Path)).IsEqualTo(first);
        var sources = XDocument.Load(config.Path).Descendants("packageSources").Elements("add").ToArray();
        await Assert.That(sources.Length).IsEqualTo(1);
        await Assert.That(sources[0].Attribute("value")!.Value).IsEqualTo(config.Packages);
        await Assert.That(XDocument.Load(config.Path).Descendants("disabledPackageSources").Elements("add").Any()).IsFalse();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ReusesMatchingSourceWithAnotherNameAndEnablesIt(bool disabled)
    {
        using var config = new TestConfig();
        config.Write(new XElement("packageSources",
                Source("my local feed", config.Packages + System.IO.Path.DirectorySeparatorChar),
                Source("untouched", "https://api.nuget.org/v3/index.json")),
            new XElement("disabledPackageSources", Source("my local feed", disabled.ToString().ToLowerInvariant()), Source("untouched", "true")));

        await Assert.That(await config.RegisterAsync()).IsEqualTo("my local feed");
        var document = XDocument.Load(config.Path);
        await Assert.That(document.Descendants("packageSources").Elements("add").Count()).IsEqualTo(2);
        await Assert.That(document.Descendants("disabledPackageSources").Elements("add")
            .Any(x => (string?)x.Attribute("key") == "my local feed" && (string?)x.Attribute("value") == "true")).IsFalse();
        await Assert.That(document.Descendants("disabledPackageSources").Elements("add")
            .Single(x => (string?)x.Attribute("key") == "untouched").Attribute("value")!.Value).IsEqualTo("true");
    }

    [Test]
    public async Task RelativeSourceIsResolvedAgainstItsConfigurationDirectory()
    {
        using var config = new TestConfig();
        config.Write(new XElement("packageSources", Source("relative feed", "local packages")));

        await Assert.That(await config.RegisterAsync()).IsEqualTo("relative feed");
        await Assert.That(XDocument.Load(config.Path).Descendants("packageSources").Elements("add").Count()).IsEqualTo(1);
    }

    [Test]
    public async Task NameCollisionPreservesUnrelatedSourceAndSettings()
    {
        using var config = new TestConfig();
        config.Write(new XElement("packageSources", Source("RespireLocal", "https://api.nuget.org/v3/index.json")),
            new XElement("config", Source("globalPackagesFolder", "custom-cache")));

        await Assert.That(await config.RegisterAsync()).IsEqualTo("RespireLocal-2");
        await Assert.That(await config.RegisterAsync()).IsEqualTo("RespireLocal-2");
        var document = XDocument.Load(config.Path);
        await Assert.That(document.Descendants("packageSources").Elements("add").Count()).IsEqualTo(2);
        await Assert.That(document.Descendants("packageSources").Elements("add")
            .Single(x => (string?)x.Attribute("key") == "RespireLocal").Attribute("value")!.Value)
            .IsEqualTo("https://api.nuget.org/v3/index.json");
        await Assert.That(document.Descendants("config").Elements("add").Single().Attribute("value")!.Value).IsEqualTo("custom-cache");
    }

    [Test]
    [Arguments("list")]
    [Arguments("add")]
    [Arguments("enable")]
    public async Task CommandFailuresPropagate(string failingCommand)
    {
        using var config = new TestConfig();
        if (failingCommand == "enable")
        {
            config.Write(new XElement("packageSources", Source("existing", config.Packages)),
                new XElement("disabledPackageSources", Source("existing", "true")));
        }

        // Corrupt only the isolated config immediately before the selected real CLI invocation.
        // This exercises failure after discovery as well as failure of discovery itself.
        await Assert.That(async () => await LocalNugetSource.EnsureRegisteredAsync(config.Packages,
            CancellationToken.None, config.Path, async (arguments, token) =>
            {
                if (arguments[1] == failingCommand) File.WriteAllText(config.Path, "<invalid");
                return await LocalNugetSource.RunCommandAsync(arguments, token);
            })).Throws<InvalidOperationException>().WithMessageContaining($"dotnet nuget {failingCommand} failed with exit code");

        await Assert.That(typeof(UploadPackagesToLocalNuGetModule)
            .IsDefined(typeof(PipelineAttributes.DependsOnAttribute<AddLocalNugetSourceModule>), inherit: true)).IsTrue();
    }

    private static XElement Source(string name, string value)
        => new("add", new XAttribute("key", name), new XAttribute("value", value));

    private sealed class TestConfig : IDisposable
    {
        private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("Respire NuGet tests ");
        public string Path { get; }
        public string Packages { get; }

        public TestConfig()
        {
            Path = System.IO.Path.Combine(_root.FullName, "NuGet.Config");
            Packages = Directory.CreateDirectory(System.IO.Path.Combine(_root.FullName, "local packages")).FullName;
            Write(new XElement("packageSources", new XElement("clear")));
        }

        public void Write(params XElement[] sections) => new XDocument(new XElement("configuration", sections)).Save(Path);
        public Task<string> RegisterAsync() => LocalNugetSource.EnsureRegisteredAsync(Packages, CancellationToken.None, Path);
        public void Dispose() => _root.Delete(recursive: true);
    }
}
