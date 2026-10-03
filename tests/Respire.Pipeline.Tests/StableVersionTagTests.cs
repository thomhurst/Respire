using System.Diagnostics;
using Respire.Pipeline.Modules;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Pipeline.Tests;

public class StableVersionTagTests
{
    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task SkipsSuffixedAndIncompleteTags(bool hasStableTag)
    {
        var directory = Directory.CreateTempSubdirectory("respire-version-tags-");
        try
        {
            await Git("init");
            await Git("-c", "user.name=Test", "-c", "user.email=test@example.com", "commit", "--allow-empty", "-m", "base");
            if (hasStableTag) await Git("tag", "V1.2.3");
            foreach (var tag in new[] { "v1.3.0-beta.1", "v1.4.0+build", "v1.5", "v999999999999999.0.0" })
            {
                await Git("-c", "user.name=Test", "-c", "user.email=test@example.com", "commit", "--allow-empty", "-m", tag);
                await Git("tag", tag);
            }
            var selected = await GitVersionDetails.GetLatestStableVersionTagAsync(directory.FullName, CancellationToken.None);
            await Assert.That(selected).IsEqualTo(hasStableTag ? "V1.2.3" : null);
            await Git("-c", "user.name=Test", "-c", "user.email=test@example.com", "commit", "--allow-empty", "-m", "+semver:minor");
            await Git("-c", "user.name=Test", "-c", "user.email=test@example.com", "commit", "--allow-empty", "--allow-empty-message", "-m", "");
            var increment = await GitVersionDetails.GetVersionIncrementAsync(directory.FullName, "HEAD~2..HEAD", 2, CancellationToken.None);
            await Assert.That(increment.Increment).IsEqualTo(VersionIncrement.Minor);
            await Assert.That(increment.PatchHeight).IsEqualTo(1);
        }
        finally
        {
            // Git marks loose objects read-only on Windows.
            foreach (var file in directory.EnumerateFiles("*", SearchOption.AllDirectories))
                file.Attributes &= ~FileAttributes.ReadOnly;
            directory.Delete(recursive: true);
        }

        async Task Git(params string[] arguments)
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo("git")
                {
                    WorkingDirectory = directory.FullName,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
            };
            foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
            process.Start();
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            await output;
            var errorText = await error;
            if (process.ExitCode != 0) throw new InvalidOperationException(errorText);
        }
    }
}
