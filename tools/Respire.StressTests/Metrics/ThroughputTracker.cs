using System.Diagnostics;

namespace Respire.StressTests.Metrics;

/// <summary>
/// Thread-safe throughput tracker for one stress pass. Operations and errors are
/// counted lock-free; the runner calls <see cref="SampleInterval"/> once per second
/// to build the per-interval rate series used for drift analysis.
/// </summary>
internal sealed class ThroughputTracker
{
    private const int MaxErrorSamples = 5;

    private long _operationCount;
    private long _errorCount;
    private readonly Stopwatch _stopwatch = new();
    private readonly List<double> _operationsPerSecondSamples = [];
    private readonly object _samplesLock = new();
    private readonly List<string> _errorSamples = [];
    private readonly object _errorsLock = new();
    // Lock-free fast path once the sample list is full, so error storms don't contend
    // the worker loops just to discard samples.
    private volatile bool _errorSamplesFull;
    private long _lastSampleOperationCount;
    private long _lastSampleTimestamp;
    private TimeSpan _cpuTimeStart;
    private double _cpuTimeSeconds;

    public long OperationCount => Interlocked.Read(ref _operationCount);
    public long ErrorCount => Interlocked.Read(ref _errorCount);
    public TimeSpan Elapsed => _stopwatch.Elapsed;

    /// <summary>
    /// Process CPU time consumed over the Start/Stop window (valid after Stop).
    /// CPU cost per operation differentiates client efficiency even when both
    /// clients are capped by the same Redis server.
    /// </summary>
    public double CpuTimeSeconds => _cpuTimeSeconds;

    public void Start()
    {
        _stopwatch.Start();
        _cpuTimeStart = Process.GetCurrentProcess().TotalProcessorTime;
        _lastSampleTimestamp = Stopwatch.GetTimestamp();
        _lastSampleOperationCount = 0;
    }

    public void Stop()
    {
        _stopwatch.Stop();
        _cpuTimeSeconds = (Process.GetCurrentProcess().TotalProcessorTime - _cpuTimeStart).TotalSeconds;
    }

    public void RecordOperation() => Interlocked.Increment(ref _operationCount);

    public void RecordError(Exception exception)
    {
        _ = Interlocked.Increment(ref _errorCount);

        if (_errorSamplesFull)
            return;

        lock (_errorsLock)
        {
            if (_errorSamples.Count >= MaxErrorSamples)
            {
                _errorSamplesFull = true;
                return;
            }

            _errorSamples.Add($"{exception.GetType().Name}: {exception.Message}");
        }
    }

    /// <summary>
    /// Records the operation rate since the previous sample. Called by the runner's
    /// sampling loop; rate is computed from actual elapsed time between calls, so
    /// timer jitter does not skew the series.
    /// </summary>
    public void SampleInterval()
    {
        var now = Stopwatch.GetTimestamp();
        var operations = OperationCount;
        var elapsedSeconds = (now - _lastSampleTimestamp) / (double)Stopwatch.Frequency;
        if (elapsedSeconds <= 0)
            return;

        var rate = (operations - _lastSampleOperationCount) / elapsedSeconds;
        lock (_samplesLock)
        {
            _operationsPerSecondSamples.Add(rate);
        }

        _lastSampleTimestamp = now;
        _lastSampleOperationCount = operations;
    }

    public IReadOnlyList<double> GetSamples()
    {
        lock (_samplesLock)
        {
            return _operationsPerSecondSamples.ToArray();
        }
    }

    public IReadOnlyList<string> GetErrorSamples()
    {
        lock (_errorsLock)
        {
            return _errorSamples.ToArray();
        }
    }
}
