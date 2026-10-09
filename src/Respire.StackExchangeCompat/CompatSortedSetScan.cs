using System.Collections;
using StackExchange.Redis;
using RedisSortedSetEntry = StackExchange.Redis.SortedSetEntry;

namespace Respire.StackExchangeCompat;

// IEnumerator.MoveNext is the synchronous scan contract.
#pragma warning disable SER308

internal sealed class CompatSortedSetScan(
    CompatDatabase database, int pageSize, long initialCursor, int initialOffset,
    Func<long, Task<CompatSortedSetScan.Page>> readPage)
    : IEnumerable<RedisSortedSetEntry>, IAsyncEnumerable<RedisSortedSetEntry>, IScanningCursor
{
    internal sealed record Page(long Cursor, RedisSortedSetEntry[] Entries);
    private readonly CompatDatabase _database = database;
    private readonly Func<long, Task<Page>> _readPage = readPage;
    private readonly long _initialCursor = initialCursor;
    private readonly int _initialOffset = initialOffset;
    private readonly int _pageSize = pageSize;
    // Retain the last enumerator's cursor metadata after disposal for callers that resume a scan.
    private Enumerator? _active;
    public long Cursor => _active?.Cursor ?? _initialCursor;
    public int PageSize => _pageSize;
    public int PageOffset => _active?.PageOffset ?? _initialOffset;
    public IEnumerator<RedisSortedSetEntry> GetEnumerator() => new Enumerator(this, default);
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    public IAsyncEnumerator<RedisSortedSetEntry> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        => new Enumerator(this, cancellationToken);

    // MoveNext calls must be sequential; a failed synchronous move stops the enumerator.
    private sealed class Enumerator(CompatSortedSetScan parent, CancellationToken cancellationToken)
        : IEnumerator<RedisSortedSetEntry>, IAsyncEnumerator<RedisSortedSetEntry>, IScanningCursor
    {
        private RedisSortedSetEntry[] _page = [];
        private long _nextCursor = parent._initialCursor;
        private int _index = -1;
        private bool _started;
        private bool _complete;
        private volatile bool _disposed;
        public long Cursor { get; private set; } = parent._initialCursor;
        public int PageSize => parent._pageSize;
        public int PageOffset => _started ? Math.Max(0, _index) : parent._initialOffset;
        public RedisSortedSetEntry Current => _index >= 0 && _index < _page.Length ? _page[_index]
            : throw new InvalidOperationException("The scan enumerator is not positioned on an entry.");
        object IEnumerator.Current => Current;
        public bool MoveNext()
        {
            var pending = MoveNextAsync().AsTask();
            try { return parent._database.Wait(pending); }
            catch
            {
                // Stop late pages from advancing this enumerator while preserving the original error.
                Dispose();
                _ = ObserveAsync(pending);
                throw;
            }
        }

        private static async Task ObserveAsync(Task pending)
        {
            try { await pending.ConfigureAwait(false); }
            catch { /* The original operation preserves the caller's exception. */ }
        }

        public async ValueTask<bool> MoveNextAsync()
        {
            if (_complete || _disposed) return false;
            cancellationToken.ThrowIfCancellationRequested();
            if (_index + 1 < _page.Length) { _index++; return true; }
            while (!_started || _nextCursor != 0)
            {
                parent._active = this;
                var cursor = _nextCursor;
                var page = await parent._readPage(cursor).WaitAsync(cancellationToken).ConfigureAwait(false);
                if (_disposed) return false;
                Cursor = cursor;
                _nextCursor = page.Cursor;
                _page = page.Entries;
                _index = _started ? 0 : parent._initialOffset;
                _started = true;
                if (_index < _page.Length) return true;
                cancellationToken.ThrowIfCancellationRequested();
            }
            _complete = true;
            _page = [];
            _index = 0;
            return false;
        }

        public void Reset()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _page = [];
            _nextCursor = Cursor = parent._initialCursor;
            _index = -1;
            _started = _complete = false;
        }

        public void Dispose() { _disposed = true; _page = []; }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
