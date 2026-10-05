using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Respire.Internal;

/// <summary>Bounds data messages while retaining ordered, coalesced continuity markers.</summary>
internal sealed class SubscriptionBuffer(int capacity, SubscriptionOverflow overflow)
{
    private readonly Lock _gate = new();
    private readonly Entry[] _entries = CreateEntries(capacity);
    private readonly Channel<byte> _ready = Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropWrite,
        SingleReader = true,
    });
    private int _head;
    private int _count;
    private bool _completed;
    private RespireSubscriptionGap? _tailGap;

    private static Entry[] CreateEntries(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        return new Entry[capacity];
    }

    internal RespireSubscriptionGap? Write(RespireMessage message)
    {
        lock (_gate)
        {
            if (_completed) return null;
            RespireSubscriptionGap? dropped = null;
            if (_count == _entries.Length)
            {
                var now = DateTimeOffset.UtcNow;
                dropped = new(RespireSubscriptionGapReason.BufferOverflow, now, now, 1);
                if (overflow == SubscriptionOverflow.DropNewest)
                {
                    _tailGap = RespireSubscriptionGap.Merge(_tailGap, dropped);
                    _ready.Writer.TryWrite(0);
                    return dropped;
                }

                var before = RespireSubscriptionGap.Merge(_entries[_head].GapBefore, dropped);
                _entries[_head] = default;
                _head = (_head + 1) % _entries.Length;
                _count--;
                if (_count == 0)
                {
                    _tailGap = RespireSubscriptionGap.Merge(before, _tailGap);
                }
                else
                {
                    _entries[_head].GapBefore = RespireSubscriptionGap.Merge(before, _entries[_head].GapBefore);
                }
            }

            _entries[(_head + _count) % _entries.Length] = new Entry { Message = message, GapBefore = _tailGap };
            _tailGap = null;
            _count++;
            _ready.Writer.TryWrite(0);
            return dropped;
        }
    }

    internal bool WriteGap(RespireSubscriptionGap gap)
    {
        lock (_gate)
        {
            if (_completed) return false;
            _tailGap = RespireSubscriptionGap.Merge(_tailGap, gap);
            _ready.Writer.TryWrite(0);
            return true;
        }
    }

    private bool TryRead(out RespireMessage message)
    {
        lock (_gate)
        {
            if (_count > 0)
            {
                ref var entry = ref _entries[_head];
                if (entry.GapBefore is { } gap)
                {
                    entry.GapBefore = null;
                    message = new RespireMessage(gap);
                    return true;
                }
                message = entry.Message;
                entry = default;
                _head = (_head + 1) % _entries.Length;
                _count--;
                return true;
            }
            if (_tailGap is { } tail)
            {
                _tailGap = null;
                message = new RespireMessage(tail);
                return true;
            }
            message = default;
            return false;
        }
    }

    internal async IAsyncEnumerable<RespireMessage> ReadAllAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        while (true)
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!TryRead(out var message)) break;
                yield return message;
            }
            if (!await _ready.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false)) yield break;
            _ready.Reader.TryRead(out _);
        }
    }

    internal void Complete()
    {
        lock (_gate)
        {
            _completed = true;
            _ready.Writer.TryComplete();
        }
    }

    private struct Entry
    {
        internal RespireMessage Message;
        internal RespireSubscriptionGap? GapBefore;
    }
}
