using System.ComponentModel;

namespace Respire;

/// <summary>Owns a hash key and encoded baseline, advancing only after a successful update.</summary>
/// <remarks>Create through a generated hash mapper. Keep model properties stable during encoding.
/// Overlapping updates on one tracker throw; separate trackers and external writers require coordination.</remarks>
public sealed class RespireHashChangeTracker<T> where T : class
{
    private readonly IRespireClient _client;
    private readonly RespireKey _key;
    private readonly Func<T, Dictionary<string, string>> _encode;
    private readonly string[] _mappedFields;
    private readonly Dictionary<string, long> _fieldTtls;
    private readonly RespireHashExpiryMode _expiryMode;
    private readonly HashSet<string> _retryFields = new(StringComparer.Ordinal);
    private Dictionary<string, string>? _baseline;
    private int _updating;
    private bool _writeTimedOut;

    /// <summary>Infrastructure constructor used by generated mappers.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public RespireHashChangeTracker(IRespireClient client, RespireKey key,
        Func<T, Dictionary<string, string>> encode, string[] mappedFields,
        IReadOnlyDictionary<string, long> fieldTtls, T? baseline = null,
        RespireHashExpiryMode expiryMode = RespireHashExpiryMode.HSetEx)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(encode);
        ArgumentNullException.ThrowIfNull(mappedFields);
        ArgumentNullException.ThrowIfNull(fieldTtls);
        if (!Enum.IsDefined(expiryMode)) throw new ArgumentOutOfRangeException(nameof(expiryMode));
        _client = client;
        _key = key.Snapshot();
        _encode = encode;
        _mappedFields = (string[])mappedFields.Clone();
        _fieldTtls = new Dictionary<string, long>(fieldTtls, StringComparer.Ordinal);
        _expiryMode = expiryMode;
        _baseline = baseline is null ? null : new Dictionary<string, string>(encode(baseline), StringComparer.Ordinal);
    }

    /// <summary>Writes changed fields and removes fields changed to null. An unchanged model sends no commands.</summary>
    /// <remarks>No server comparison or rollback occurs. On failure the entire previous baseline remains available for retry.
    /// Cancellation after encoding waits for the write group to finish before releasing the tracker.
    /// A timeout of a possibly submitted write invalidates the tracker: later updates throw.</remarks>
    public async ValueTask UpdateAsync(T value, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(value);
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.CompareExchange(ref _updating, 1, 0) != 0)
            throw new InvalidOperationException("A hash tracker cannot run overlapping updates.");
        try
        {
            if (_writeTimedOut)
                throw new InvalidOperationException("A timed-out hash write may still execute. Wait for prior commands to settle and recreate the tracker from persisted values.");
            var next = new Dictionary<string, string>(_encode(value), StringComparer.Ordinal);
            var changed = new List<string>();
            var writes = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var field in _mappedFields)
            {
                var present = next.TryGetValue(field, out var text);
                if (!_retryFields.Contains(field) && _baseline is not null && _baseline.TryGetValue(field, out var previous) == present
                    && (!present || StringComparer.Ordinal.Equals(previous, text))) continue;
                changed.Add(field);
                if (present) writes.Add(field, text!);
            }
            if (changed.Count != 0)
            {
                var writeStarted = false;
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    // Accepted writes must settle before a retry can use another connection.
                    // Cancelling the response wait would release this tracker too early.
                    await RespireHashModelIO.WriteAsync(_client, _key, writes, changed.ToArray(),
                        _fieldTtls, _expiryMode, CancellationToken.None, () => writeStarted = true).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                }
                catch (Exception error)
                {
                    // A command timeout abandons only the response wait. No retry through this
                    // client can prove that an accepted write on another connection has settled.
                    if (writeStarted && error is RespireTimeoutException { IsCommandNotSubmitted: false }) _writeTimedOut = true;
                    // Some commands may have applied. Resend these fields even if the caller
                    // reverts to the old baseline, so partially applied values can be corrected.
                    foreach (var field in changed) _retryFields.Add(field);
                    throw;
                }
            }
            _baseline = next;
            _retryFields.Clear();
        }
        finally
        {
            Volatile.Write(ref _updating, 0);
        }
    }
}
