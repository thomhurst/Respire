namespace Respire;

/// <summary>Admission policy for definitely-unsent standalone, Sentinel primary, replica-read and Cluster commands during recovery.</summary>
public enum RespireBacklogPolicy
{
    /// <summary>Wait in a bounded FIFO, using the command's original timeout and cancellation.</summary>
    Queue,
    /// <summary>Reject commands when no healthy command connection is available.</summary>
    FailFast,
}
