using System.Reflection;
using ModularPipelines.Context;
using ModularPipelines.Logging;
using ModularPipelines.Models;
using ModularPipelines.Modules;
using Moq;
using Respire.Pipeline.Modules;
using Respire.Pipeline.Modules.LocalMachine;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Pipeline.Tests;

public class LocalPackageUploadTests
{
    [Test]
    public async Task CopiesEveryPackageAndOverwritesExistingPackage()
    {
        var root = Directory.CreateTempSubdirectory("Respire local upload ");
        try
        {
            var source = root.CreateSubdirectory("source");
            var destination = root.CreateSubdirectory("feed");
            var first = WritePackage(source, "First.1.0.0.nupkg", "first package");
            var second = WritePackage(source, "Second.1.0.0.nupkg", "second package");
            WritePackage(destination, first.Name, "old package");

            var count = await ExecuteUploadAsync([first, second], destination);

            await Assert.That(count).IsEqualTo(2);
            await Assert.That(File.ReadAllText(Path.Combine(destination.FullName, first.Name))).IsEqualTo("first package");
            await Assert.That(File.ReadAllText(Path.Combine(destination.FullName, second.Name))).IsEqualTo("second package");
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task CopyFailureFaultsModuleEvenAfterAnotherPackageWasCopied(bool destinationConflict)
    {
        var root = Directory.CreateTempSubdirectory("Respire local upload ");
        try
        {
            var source = root.CreateSubdirectory("source");
            var destination = root.CreateSubdirectory("feed");
            var first = WritePackage(source, "First.1.0.0.nupkg", "first package");
            var failed = new FileInfo(Path.Combine(source.FullName, "Failed.1.0.0.nupkg"));
            var failedDestination = Path.Combine(destination.FullName, failed.Name);
            if (destinationConflict)
            {
                File.WriteAllText(failed.FullName, "failed package");
                Directory.CreateDirectory(failedDestination);
            }

            var execution = ExecuteUploadAsync([first, failed], destination);
            var error = await Assert.That(async () => await execution).Throws<IOException>();

            await Assert.That(execution.IsFaulted).IsTrue();
            await Assert.That(error!.Message).Contains(failed.FullName);
            await Assert.That(error.Message).Contains(failedDestination);
            await Assert.That(error.InnerException).IsNotNull();
            await Assert.That(File.ReadAllText(Path.Combine(destination.FullName, first.Name))).IsEqualTo("first package");
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Test]
    public async Task NoPackagesReturnsZero()
    {
        var destination = Directory.CreateTempSubdirectory("Respire local upload ");
        try
        {
            await Assert.That(await ExecuteUploadAsync([], destination)).IsEqualTo(0);
            await Assert.That(destination.GetFileSystemInfos().Length).IsEqualTo(0);
        }
        finally
        {
            destination.Delete(recursive: true);
        }
    }

    private static FileInfo WritePackage(DirectoryInfo directory, string name, string content)
    {
        var path = Path.Combine(directory.FullName, name);
        File.WriteAllText(path, content);
        return new FileInfo(path);
    }

    private static Task<int> ExecuteUploadAsync(FileInfo[] packages, DirectoryInfo destination)
    {
        var parser = new PackagePathsParserModule();
        var folder = new CreateLocalNugetFolderModule();
        CompleteDependency(parser, packages);
        CompleteDependency(folder, destination);
        var context = new Mock<IModuleContext>(MockBehavior.Strict);
        context.Setup(x => x.GetModule<PackagePathsParserModule>()).Returns(parser);
        context.Setup(x => x.GetModule<CreateLocalNugetFolderModule>()).Returns(folder);
        context.SetupGet(x => x.Logger).Returns(Mock.Of<IModuleLogger>());
        return new TestLocalPackageUploadModule().RunAsync(context.Object);
    }

    private static void CompleteDependency<T>(Module<T> module, T value)
    {
        // The framework exposes awaitable modules but keeps their completion source internal.
        // Supply dependency results without running packing or touching the user's NuGet configuration.
        var completion = (TaskCompletionSource<ModuleResult<T?>>)typeof(Module<T>)
            .GetProperty("CompletionSource", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(module)!;
        completion.SetResult(new ModuleResult<T?>.Success(value)
        {
            ModuleName = module.GetType().Name,
            ModuleDuration = TimeSpan.Zero,
            ModuleStart = DateTimeOffset.UnixEpoch,
            ModuleEnd = DateTimeOffset.UnixEpoch,
            ModuleStatus = ModularPipelines.Enums.Status.Successful,
        });
    }
}

public sealed class TestLocalPackageUploadModule : UploadPackagesToLocalNuGetModule
{
    public Task<int> RunAsync(IModuleContext context) => base.ExecuteAsync(context, CancellationToken.None);
}
