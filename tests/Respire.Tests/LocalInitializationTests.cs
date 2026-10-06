using System.Reflection;
using System.Runtime.CompilerServices;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

public class LocalInitializationTests
{
    [Test]
    public async Task PackageModuleAndMethodBodiesSkipImplicitInitialization()
    {
        await Assert.That(typeof(RespireValue).Module.IsDefined(typeof(SkipLocalsInitAttribute), false)).IsTrue();
        var method = typeof(RespireValue).GetMethod(nameof(GetHashCode), Type.EmptyTypes)!;
        await Assert.That(method.GetMethodBody()!.InitLocals).IsFalse();
    }

    [Test]
    public async Task TestModuleRetainsImplicitInitialization()
    {
        await Assert.That(typeof(LocalInitializationTests).Module.IsDefined(typeof(SkipLocalsInitAttribute), false)).IsFalse();
        var control = typeof(LocalInitializationTests).GetMethod(nameof(ReadUntouchedTestStorage), BindingFlags.NonPublic | BindingFlags.Static)!;
        await Assert.That(control.GetMethodBody()!.InitLocals).IsTrue();
        await Assert.That(ReadUntouchedTestStorage(42)).IsEqualTo(0);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int ReadUntouchedTestStorage(int value)
    {
        Span<int> storage = stackalloc int[2];
        storage[0] = value;
        return storage[1];
    }
}
