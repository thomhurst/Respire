using System.Runtime.CompilerServices;
using Respire.Commands;
using Respire.Protocol;
using TUnit.Core;

namespace Respire.Tests;

[NotInParallel]
public class RawCommandCancellationAllocationTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public void ResponseCancellationPreservesTokenWithoutBoxing(bool dynamic)
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var token = cancellation.Token;
        if (dynamic)
        {
            var command = new DynamicCommand(["STRLEN", "key"], 1);
            Verify(in command, token);
        }
        else
        {
            var command = new CatalogCommand(RespireCommands.String.STRLEN, ["key"]);
            Verify(in command, token);
        }
    }

    private static void Verify<TCommand>(in TCommand command, CancellationToken token)
        where TCommand : struct, IRespCommand
    {
        Measure(in command, token, false);
        Measure(in command, token, true);
        var copy = command;
        var measured = AllocationMeasurement.WithoutConcurrentGc(() =>
            (Bytes: Measure(in copy, token, false), Control: Measure(in copy, token, true)));
        if (measured.Bytes != 0 || measured.Control < 37_000)
            throw new InvalidOperationException($"Expected no response cancellation allocation and a positive control; observed {measured}.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long Measure<TCommand>(in TCommand command, CancellationToken token, bool control)
        where TCommand : struct, IRespCommand
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 1_000; index++)
        {
            if (command.GetResponseCancellationToken(token) != token ||
                command.GetResponseCancellationToken(default) != default)
                throw new InvalidOperationException("The raw command changed its response cancellation token.");
            if (control) GC.KeepAlive(new byte[37]);
        }
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }
}
