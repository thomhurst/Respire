using System.Runtime.InteropServices;
using Respire.Infrastructure;

namespace Respire.Commands;

/// <summary>
/// Zero-allocation queued command that combines command data with response handling
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public readonly struct QueuedCommandData
{
    public readonly CommandData Command;
    public readonly ValueTaskCompletionSource? ResponseHandler;
    public readonly bool ExpectsResponse;
    
    public QueuedCommandData(CommandData command, ValueTaskCompletionSource? responseHandler)
    {
        Command = command;
        ResponseHandler = responseHandler;
        ExpectsResponse = responseHandler != null;
    }
    
    public static QueuedCommandData CreateWithoutResponse(CommandData command)
    {
        return new QueuedCommandData(command, null);
    }
    
    public static QueuedCommandData CreateWithResponse(CommandData command, ValueTaskCompletionSource responseHandler)
    {
        return new QueuedCommandData(command, responseHandler);
    }
}