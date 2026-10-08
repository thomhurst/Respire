using System.Reflection;
using Respire.Networking;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

public class TestInspectionArchitectureTests
{
    private const string InspectionFactoryName = "InspectForTests";
    private const BindingFlags OwnerMembers = BindingFlags.Instance | BindingFlags.Public
        | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

    [Test]
    [Arguments(typeof(RespireConnection), typeof(RespireConnection.TestInspection), nameof(RespireConnection.TestInspection.Inflight))]
    [Arguments(typeof(PendingResponse), typeof(PendingResponse.TestInspection), nameof(PendingResponse.TestInspection.RegisteredCancellationToken))]
    [Arguments(typeof(ClientSideCacheCoordinator), typeof(ClientSideCacheCoordinator.TestInspection), nameof(ClientSideCacheCoordinator.TestInspection.SharedReadGate))]
    [Arguments(typeof(ClientSideCacheCoordinator), typeof(ClientSideCacheCoordinator.TestInspection), nameof(ClientSideCacheCoordinator.TestInspection.PendingQueryDependencyCount))]
    [Arguments(typeof(ClientSideCacheCoordinator), typeof(ClientSideCacheCoordinator.TestInspection), nameof(ClientSideCacheCoordinator.TestInspection.IdleQueryStorage))]
    [Arguments(typeof(ClientSideCacheCoordinator), typeof(ClientSideCacheCoordinator.TestInspection), nameof(ClientSideCacheCoordinator.TestInspection.ActiveMutationCount))]
    [Arguments(typeof(ClientSideCacheCoordinator), typeof(ClientSideCacheCoordinator.TestInspection), nameof(ClientSideCacheCoordinator.TestInspection.MutationWriterStorage))]
    [Arguments(typeof(RespireTransactionBase), typeof(RespireTransactionBase.TestInspection), nameof(RespireTransactionBase.TestInspection.WatchConnection))]
    public async Task InspectionStateRemainsBehindInternalBorrowedViews(Type owner, Type view, string member)
    {
        await Assert.That(HasDirectInspectionMember(owner, member)).IsFalse();
        await Assert.That(view.IsByRefLike).IsTrue();
        await Assert.That(view.IsNestedAssembly).IsTrue();
        var factory = owner.GetMethod(InspectionFactoryName, OwnerMembers);
        await Assert.That(factory).IsNotNull();
        await Assert.That(factory!.IsAssembly).IsTrue();
        await Assert.That(factory.ReturnType).IsEqualTo(view);
    }

    [Test]
    public async Task DirectInspectionGuardDetectsLegacyAccessor()
        => await Assert.That(HasDirectInspectionMember(typeof(LegacyOwner), nameof(LegacyOwner.Inflight))).IsTrue();

    private static bool HasDirectInspectionMember(Type owner, string member)
        => owner.GetMember(member, OwnerMembers).Length != 0;

    private sealed class LegacyOwner
    {
        internal int Inflight => 42;
    }
}
