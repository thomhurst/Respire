# Deferred raw commands

:::warning Experimental API: RSP0005
This feature requires an explicit compiler opt-in and is outside the 1.0 compatibility promise.
See [RSP0005](../diagnostics/RSP0005.md) for the affected APIs and the exact `NoWarn` or `#pragma` opt-in.
:::

Batches, transactions, watched transactions, and `IRespireCommandQueue` expose `Execute(command, args)` for queuing a raw command. It returns a `RespirePending<RespireResult>` at once; `batch.ExecuteAsync()` or `transaction.CommitAsync()` runs it and owns cancellation. Prefer a typed facet when one has the operation.

```csharp
await using var client = await RespireClient.ConnectAsync("localhost:6379");
var view = client.WithKeyPrefix("tenant:");
using var batch = view.CreateBatch();
var write = batch.Execute(RespireCommands.String.SET, "key", "value");
var read = batch.Execute("GET", "key");
await batch.ExecuteAsync();
using var writeResult = write.Result;
using var readResult = read.Result;
Console.WriteLine(readResult.AsString());
```

- Arguments, including binary keys and values, are copied when queued. Later edits to the array or its buffers do not change the command.
- Keys get the view's prefix. Values, fields, script source, and script arguments do not.
- Queuing never opens a connection or sends anything.
- Queuing parses the name, allocates a token array, and copies arguments, and replies are copied too. This path is not allocation-free.

## Results and failures

- Results copy replies into GC-managed storage and stay valid after the queue is disposed. Disposal is optional. Disposing a root invalidates its struct copies and nested views; repeated disposal is safe.
- Immediate `ExecuteAsync` results are pooled leases and must be disposed.
- Both kinds have the same `RespireResult` conversions and RESP2/RESP3 aggregate shapes, including nulls and nested errors.
- A top-level server error faults its pending. A batch completes all pendings before `ExecuteAsync` throws the first error; `TryExecuteAsync` returns the failure summary instead.
- In transactions, errors inside EXEC fault individual pendings, and transaction-level failures fault the commit.
- Successful neighboring results stay readable. Nested aggregate errors are data: check `IsError` and `ErrorMessage`.
- Reading a pending before execution throws `RespirePendingNotReadyException`. Discarding an unsent queue faults its pendings.

## Names and supported layouts

Use a catalog descriptor or a case-insensitive verb such as `"get"`. A known subcommand uses one space, for example `"OBJECT ENCODING"`. Put every argument in the argument array.

Rejected names: inline arguments (`"GET key"`), interpolated commands, leading, trailing, or repeated spaces, control characters, and non-ASCII. Null arguments cannot be serialized. The client validates the key layout; Redis validates arity, options, types, and versions.

Only the layouts below are supported. Unknown, module, and immediate-only layouts throw `NotSupportedException` before queuing, even on standalone unprefixed clients. The client never infers key positions, and nothing can override this check. For other forms, use a typed facet or immediate raw execution, subject to their own prefix and routing rules.

| Key layout | Commands |
| --- | --- |
| No keys | PING, ECHO, TIME, PUBLISH |
| First argument | GET, SET, GETSET, SETNX, SETEX, PSETEX, GETDEL, GETEX, APPEND, STRLEN, GETRANGE, SETRANGE, INCR, INCRBY, INCRBYFLOAT, INCREX, DECR, DECRBY, DELEX, DELIFEQ |
| First argument | TYPE, TTL, PTTL, EXPIRE, PEXPIRE, EXPIREAT, PEXPIREAT, EXPIRETIME, PEXPIRETIME, PERSIST, DUMP, RESTORE |
| First argument | HGET, HSET, HSETNX, HMGET, HMSET, HGETALL, HDEL, HEXISTS, HLEN, HKEYS, HVALS, HSTRLEN, HINCRBY, HINCRBYFLOAT, HRANDFIELD |
| First argument | LPUSH, RPUSH, LPUSHX, RPUSHX, LPOP, RPOP, LLEN, LRANGE, LINDEX, LSET, LINSERT, LREM, LTRIM, LPOS |
| First argument | SADD, SREM, SCARD, SMEMBERS, SISMEMBER, SMISMEMBER, SPOP, SRANDMEMBER |
| First argument | ZADD, ZREM, ZCARD, ZSCORE, ZMSCORE, ZINCRBY, ZCOUNT, ZLEXCOUNT, ZSCAN (one cursor page), ZRANGE, ZREVRANGE, ZRANGEBYSCORE, ZREVRANGEBYSCORE, ZRANGEBYLEX, ZREVRANGEBYLEX, ZRANK, ZREVRANK, ZREMRANGEBYRANK, ZREMRANGEBYSCORE, ZREMRANGEBYLEX, ZPOPMIN, ZPOPMAX, ZRANDMEMBER |
| First argument | GETBIT, SETBIT, BITCOUNT, BITPOS, BITFIELD, BITFIELD_RO, PFADD, GEOADD, GEODIST, GEOHASH, GEOPOS, GEOSEARCH |
| First argument | XADD, XACK, XDEL, XTRIM, XLEN, XRANGE, XREVRANGE, XPENDING, XCLAIM, XAUTOCLAIM, XCFGSET, XSETID |
| First argument after the named subcommand | OBJECT ENCODING/FREQ/IDLETIME/REFCOUNT; MEMORY USAGE; XINFO STREAM/GROUPS/CONSUMERS; XGROUP CREATE/SETID/DESTROY/CREATECONSUMER/DELCONSUMER |
| First two arguments | RENAME, RENAMENX, COPY, LCS, SMOVE, LMOVE, LMOVEM, RPOPLPUSH, ZRANGESTORE, GEOSEARCHSTORE |
| Every argument | DEL, UNLINK, EXISTS, TOUCH, MGET, SDIFF, SINTER, SUNION, SDIFFSTORE, SINTERSTORE, SUNIONSTORE, PFCOUNT, PFMERGE |
| Alternating keys and values | MSET, MSETNX |
| All arguments after the bit operation | BITOP |
| Source/name, key count, keys, other arguments | EVAL, EVALSHA, EVAL_RO, EVALSHA_RO, FCALL, FCALL_RO |
| Key count, keys, other arguments | LMPOP, ZMPOP, SINTERCARD, SDIFFCARD, SUNIONCARD, ZDIFF, ZINTER, ZUNION, ZINTERCARD |
| Destination, key count, source keys, other arguments | ZDIFFSTORE, ZINTERSTORE, ZUNIONSTORE |

- Script and function key counts may be zero. Other counts must be positive. All counts must fit the supplied arguments.
- Scripts and functions are never loaded, reloaded, or replayed after NOSCRIPT or a missing-function error.
- Scripts and functions must access keys through the declared key arguments; their source is not rewritten.

## Cluster and queue restrictions

- All keys in one raw command must share a slot after prefixing. A mismatch throws `RespireServerException` with code `CROSSSLOT` locally, before queuing, as typed multi-key commands do. No server round trip happens.
- Rejected commands do not pin a transaction to a slot. A Cluster transaction requires all its commands to share one slot.
- Cluster batches group commands by slot. Order is kept within a group; different groups may run independently.
- Keyless commands run on one execution node and never fan out across primaries.

These are rejected:

- Blocking and connection-state commands: blocking list and sorted-set pops, subscriptions, WATCH, transaction control, connection setup, CLIENT commands, WAIT/WAITAOF, and similar session operations.
- Raw XREAD/XREADGROUP, even without BLOCK. Use typed `Streams.Read` and `Streams.ReadGroup` instead.
- Administrative, unknown, and module forms.
- SORT and the GEORADIUS family, whose optional external keys need their own parsing. Use the typed sorting and geospatial APIs.

There are no per-command flags, fire-and-forget mode, or implicit cluster-wide administration. Deferred commands keep the queue's cancellation, redirect, and conservative client-cache invalidation behavior. A deferred raw command joins a batch or transaction retry only with a category declared through `WithRetryCategory`; see [command retries](command-retries.md#batches).
