namespace Respire.Analyzers.Tests;

/// <summary>
/// A stand-in for the public Respire surface the rules key off. Compiling against a stub rather
/// than the real assembly keeps the harness on one reference-assembly set and makes each test's
/// inputs obvious; the rules match types by fully qualified name, so the stub is indistinguishable.
/// </summary>
internal static class RespireApiStub
{
    public const string Source = """
        using System;
        using System.Runtime.CompilerServices;
        using System.Threading.Tasks;

        namespace Respire
        {
            public readonly struct RespireResult : IDisposable
            {
                public int Count => 0;
                public RespireResult this[int index] => default;
                public string AsString() => string.Empty;
                public long AsInteger() => 0;
                public void Dispose() { }
            }

            public readonly struct RespireLease : IDisposable
            {
                public bool IsNull => false;
                public int Length => 0;
                public void Dispose() { }
            }

            public sealed class RespirePending<T>
            {
                public bool IsCompleted => true;
                public T Result => default(T);
                public RespirePendingAwaiter<T> GetAwaiter() => new RespirePendingAwaiter<T>(this);
            }

            public readonly struct RespirePendingAwaiter<T> : ICriticalNotifyCompletion
            {
                private readonly RespirePending<T> _pending;
                public RespirePendingAwaiter(RespirePending<T> pending) { _pending = pending; }
                public bool IsCompleted => true;
                public T GetResult() => _pending.Result;
                public void OnCompleted(Action continuation) { continuation(); }
                public void UnsafeOnCompleted(Action continuation) { continuation(); }
            }

            public sealed class RespireBatch
            {
                public RespirePending<string> GetStringAsync(string key) => new RespirePending<string>();
                public RespirePending<long> IncrementAsync(string key) => new RespirePending<long>();
                public ValueTask SendAsync() => default;
            }

            public sealed class RespireTransaction : IAsyncDisposable
            {
                public RespirePending<string> GetStringAsync(string key) => new RespirePending<string>();
                public ValueTask<bool> CommitAsync() => default;
                public ValueTask DisposeAsync() => default;
            }

            public sealed class RespireClient
            {
                public ValueTask<RespireResult> ExecuteAsync(string command, params string[] args) => default;
                public ValueTask<RespireLease> GetLeaseAsync(string key) => default;
                public RespireBatch CreateBatch() => new RespireBatch();
                public RespireTransaction CreateTransaction() => new RespireTransaction();
            }
        }
        """;
}
