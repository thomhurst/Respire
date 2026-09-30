using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

public class ScriptInterfaceTests
{
    [Test]
    public async Task ArrayConvenience_ForwardsOriginalStorageAndCancellationToSpanCore()
    {
        RespireKey[] keys = ["key"];
        RespireValue[] args = ["argument"];
        var script = RespireScript.Create("return ARGV[1]");
        using var cancellation = new CancellationTokenSource();
        var implementation = new SpanOnlyScripts(keys, args);
        IScriptCommands commands = implementation;

        using var result = await commands.ExecuteAsync(script, keys, args, cancellation.Token);

        await Assert.That(implementation.Calls).IsEqualTo(1);
        await Assert.That(implementation.OriginalStorage).IsTrue();
        await Assert.That(ReferenceEquals(implementation.Script, script)).IsTrue();
        await Assert.That(implementation.CancellationToken).IsEqualTo(cancellation.Token);
    }

    [Test]
    public async Task NullArrayInputs_ReachSpanCoreAsEmptySpans()
    {
        var implementation = new SpanOnlyScripts([], []);
        IScriptCommands commands = implementation;

        using var result = await commands.ExecuteAsync(RespireScript.Create("return 1"));

        await Assert.That(implementation.Calls).IsEqualTo(1);
        await Assert.That(implementation.OriginalStorage).IsTrue();
    }

    [Test]
    public async Task ExistingImplementationsHaveExplicitUnsupportedCacheDefaults()
    {
        IScriptCommands commands = new SpanOnlyScripts([], []);
        await Assert.That(async () => await commands.ExistsAsync("digest")).ThrowsExactly<NotSupportedException>();
        await Assert.That(async () => await commands.FlushAsync()).ThrowsExactly<NotSupportedException>();
    }

    private sealed class SpanOnlyScripts(RespireKey[] expectedKeys, RespireValue[] expectedArgs) : IScriptCommands
    {
        public int Calls { get; private set; }
        public bool OriginalStorage { get; private set; }
        public RespireScript? Script { get; private set; }
        public CancellationToken CancellationToken { get; private set; }

        public ValueTask<RespireResult> ExecuteSpanAsync(
            RespireScript script, ReadOnlySpan<RespireKey> keys, ReadOnlySpan<RespireValue> args,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            Script = script;
            CancellationToken = cancellationToken;
            OriginalStorage = SameStorage(keys, expectedKeys) && SameStorage(args, expectedArgs);
            return ValueTask.FromResult(default(RespireResult));
        }

        public ValueTask<string> LoadAsync(RespireScript script, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        private static bool SameStorage<T>(ReadOnlySpan<T> actual, T[] expected)
            => actual.Length == expected.Length && (actual.IsEmpty || Unsafe.AreSame(
                ref MemoryMarshal.GetReference(actual), ref MemoryMarshal.GetArrayDataReference(expected)));
    }
}
