namespace Respire.Idempotency;

internal sealed class ResponseLimitException : IOException;

internal sealed class BoundedResponseStream(int limit) : MemoryStream
{
    internal bool Exceeded { get; private set; }

    private void Check(long end)
    {
        if (end > limit)
        {
            Exceeded = true;
            throw new ResponseLimitException();
        }
        // Prevent MemoryStream's geometric growth from exceeding the configured limit.
        if (end > Capacity) Capacity = (int)Math.Min(limit, Math.Max(end, Math.Max(256L, Capacity * 2L)));
    }

    public override void SetLength(long value) { Check(value); base.SetLength(value); }
    public override void Write(byte[] buffer, int offset, int count) { Check(Position + count); base.Write(buffer, offset, count); }
    public override void Write(ReadOnlySpan<byte> buffer) { Check(Position + buffer.Length); base.Write(buffer); }
    public override void WriteByte(byte value) { Check(Position + 1); base.WriteByte(value); }
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Write(buffer, offset, count);
        return Task.CompletedTask;
    }
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Write(buffer.Span);
        return ValueTask.CompletedTask;
    }
}
