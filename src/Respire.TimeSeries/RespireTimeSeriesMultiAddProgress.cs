namespace Respire.TimeSeries;

/// <summary>Confirmed replies and remaining work when a chunked TS.MADD operation is interrupted.</summary>
/// <remarks>
/// Completed samples form the request prefix and include server rejections. The next
/// <see cref="UncertainSampleCount"/> samples may have executed; the remaining samples were not attempted.
/// Never automatically replay the uncertain chunk: a lost reply does not imply a lost write.
/// </remarks>
public sealed class RespireTimeSeriesMultiAddProgress
{
    internal RespireTimeSeriesMultiAddProgress(int totalSampleCount, int completedChunkCount,
        int completedSampleCount, int uncertainSampleCount, long[] timestamps, string?[]? errors)
    {
        TotalSampleCount = totalSampleCount;
        CompletedChunkCount = completedChunkCount;
        UncertainSampleCount = uncertainSampleCount;
        var confirmedTimestamps = new long?[completedSampleCount];
        var confirmedErrors = new string?[completedSampleCount];
        for (var index = 0; index < completedSampleCount; index++)
        {
            confirmedErrors[index] = errors?[index];
            if (confirmedErrors[index] is null) confirmedTimestamps[index] = timestamps[index];
        }
        Timestamps = Array.AsReadOnly(confirmedTimestamps);
        Errors = Array.AsReadOnly(confirmedErrors);
    }

    /// <summary>The number of samples in the original request.</summary>
    public int TotalSampleCount { get; }
    /// <summary>The number of chunks with fully decoded replies.</summary>
    public int CompletedChunkCount { get; }
    /// <summary>The number of samples with confirmed outcomes, including server rejections.</summary>
    public int CompletedSampleCount => Timestamps.Count;
    /// <summary>The size of the attempted chunk without a fully decoded reply, or zero if cancellation preceded it.</summary>
    /// <remarks>Conservative: the transport may have rejected the chunk before sending any bytes.</remarks>
    public int UncertainSampleCount { get; }
    /// <summary>The number of trailing samples for which no command was attempted.</summary>
    public int UnattemptedSampleCount => TotalSampleCount - CompletedSampleCount - UncertainSampleCount;
    /// <summary>Timestamps for the completed request prefix, or null for rejected samples.</summary>
    public IReadOnlyList<long?> Timestamps { get; }
    /// <summary>Server errors for the completed request prefix, or null for accepted samples.</summary>
    public IReadOnlyList<string?> Errors { get; }

    /// <summary>Returns progress from an interrupted chunked write, or null for an unrelated exception.</summary>
    public static RespireTimeSeriesMultiAddProgress? FromException(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception switch
        {
            RespireTimeSeriesMultiAddInterruptedException interrupted => interrupted.Progress,
            RespireTimeSeriesMultiAddCanceledException canceled => canceled.Progress,
            _ => null,
        };
    }
}

/// <summary>A chunked TS.MADD operation failed after at least one chunk's reply was confirmed.</summary>
/// <remarks>The original failure is available through <see cref="Exception.InnerException"/>.</remarks>
public sealed class RespireTimeSeriesMultiAddInterruptedException : RespireException
{
    internal RespireTimeSeriesMultiAddInterruptedException(RespireTimeSeriesMultiAddProgress progress, Exception cause)
        : base($"TS.MADD was interrupted after {progress.CompletedSampleCount} confirmed sample outcomes.", cause)
        => Progress = progress;

    /// <summary>Confirmed outcomes, the uncertain chunk, and unattempted samples.</summary>
    public RespireTimeSeriesMultiAddProgress Progress { get; }
}

/// <summary>A chunked TS.MADD operation was canceled after at least one chunk's reply was confirmed.</summary>
public sealed class RespireTimeSeriesMultiAddCanceledException : OperationCanceledException
{
    internal RespireTimeSeriesMultiAddCanceledException(RespireTimeSeriesMultiAddProgress progress, OperationCanceledException cause)
        : base($"TS.MADD was canceled after {progress.CompletedSampleCount} confirmed sample outcomes.", cause, cause.CancellationToken)
        => Progress = progress;

    /// <summary>Confirmed outcomes, the uncertain chunk, and unattempted samples.</summary>
    public RespireTimeSeriesMultiAddProgress Progress { get; }
}
