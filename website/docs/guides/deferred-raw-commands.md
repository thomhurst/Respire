# Deferred raw commands

Batches, transactions, and watched transactions expose `Execute(command, args)`,
also available through `IRespireCommandQueue`. It returns a
`RespirePending<RespireResult>` immediately. Execution and cancellation belong to
`batch.ExecuteAsync()` or `transaction.CommitAsync()`.

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

The arguments are snapshotted when queued, including binary keys and values.
Later edits to the argument array or its binary buffers do not change the command.
Keys receive the view's prefix; values, fields, script source, and script arguments
do not. Queueing never opens a connection or sends the command.

## Results and failures

Deferred raw results copy replies into GC-managed storage. Unread results do not
retain pooled receive buffers. Disposal is optional, but disposing a root invalidates
all struct copies and nested views; repeated disposal is safe. Results remain valid
after the queue is disposed. Immediate `ExecuteAsync` results remain pooled leases
and must be disposed. Both expose the same `RespireResult` conversions and RESP2/RESP3
aggregate representation, including nulls and nested errors.

Top-level server errors fault the corresponding pending. A batch finishes all
pendings before `ExecuteAsync` throws its first error; `TryExecuteAsync` returns the
failure summary instead. Transactions preserve their existing contract: errors
inside EXEC fault individual pendings, while transaction-level failures fault commit.
Successful neighboring results remain readable. Nested aggregate errors are data:
inspect `IsError` and `ErrorMessage`. Reading a pending before execution throws
`RespirePendingNotReadyException`. Discarding an unsent queue faults its pendings.

## Names and supported layouts

Use a catalog descriptor or a case-insensitive verb such as `"get"`. A known
subcommand uses one space, for example `"OBJECT ENCODING"`. All actual arguments
belong in the argument array. Inline arguments (`"GET key"`), interpolated commands,
leading/trailing/repeated spaces, control characters, and non-ASCII names are not
accepted. Null arguments cannot be serialized. The client validates the key layout;
Redis validates remaining arity, options, types, and version requirements.

Supported forms are deliberately explicit. Unknown or module layouts throw
`NotSupportedException` before enqueueing, including on standalone unprefixed clients.
The client does not infer key positions from arbitrary argument contents. There is no
metadata override that can bypass these checks. Use a typed facet or immediate raw
execution for unsupported forms, subject to that API's own prefix/routing restrictions.

| Key layout | Commands |
| --- | --- |
| No keys | PING, ECHO, TIME |
| First argument | GET, SET, GETSET, SETNX, SETEX, PSETEX, GETDEL, GETEX, APPEND, STRLEN, GETRANGE, SETRANGE, INCR, INCRBY, INCRBYFLOAT, DECR, DECRBY |
| First argument | TYPE, TTL, PTTL, EXPIRE, PEXPIRE, EXPIREAT, PEXPIREAT, EXPIRETIME, PEXPIRETIME, PERSIST, DUMP, RESTORE |
| First argument | HGET, HSET, HSETNX, HMGET, HMSET, HGETALL, HDEL, HEXISTS, HLEN, HKEYS, HVALS, HSTRLEN, HINCRBY, HINCRBYFLOAT, HRANDFIELD |
| First argument | LPUSH, RPUSH, LPUSHX, RPUSHX, LPOP, RPOP, LLEN, LRANGE, LINDEX, LSET, LINSERT, LREM, LTRIM, LPOS |
| First argument | SADD, SREM, SCARD, SMEMBERS, SISMEMBER, SMISMEMBER, SPOP, SRANDMEMBER |
| First argument | ZADD, ZREM, ZCARD, ZSCORE, ZMSCORE, ZINCRBY, ZCOUNT, ZLEXCOUNT, ZRANGE, ZREVRANGE, ZRANGEBYSCORE, ZREVRANGEBYSCORE, ZRANGEBYLEX, ZREVRANGEBYLEX, ZRANK, ZREVRANK, ZREMRANGEBYRANK, ZREMRANGEBYSCORE, ZREMRANGEBYLEX, ZPOPMIN, ZPOPMAX, ZRANDMEMBER |
| First argument | GETBIT, SETBIT, BITCOUNT, BITPOS, BITFIELD, BITFIELD_RO, PFADD, GEOADD, GEODIST, GEOHASH, GEOPOS, GEOSEARCH |
| First argument | XADD, XACK, XDEL, XTRIM, XLEN, XRANGE, XREVRANGE, XPENDING, XCLAIM, XAUTOCLAIM |
| First argument after the named subcommand | OBJECT ENCODING/FREQ/IDLETIME/REFCOUNT; MEMORY USAGE; XINFO STREAM/GROUPS/CONSUMERS; XGROUP CREATE/SETID/DESTROY/CREATECONSUMER/DELCONSUMER |
| First two arguments | RENAME, RENAMENX, COPY, LCS, SMOVE, LMOVE, RPOPLPUSH, ZRANGESTORE, GEOSEARCHSTORE |
| Every argument | DEL, UNLINK, EXISTS, TOUCH, MGET, SDIFF, SINTER, SUNION, SDIFFSTORE, SINTERSTORE, SUNIONSTORE, PFCOUNT, PFMERGE |
| Alternating keys and values | MSET, MSETNX |
| All arguments after the bit operation | BITOP |
| Source/name, key count, keys, other arguments | EVAL, EVALSHA, EVAL_RO, EVALSHA_RO, FCALL, FCALL_RO |
| Key count, keys, other arguments | LMPOP, ZMPOP, SINTERCARD, ZDIFF, ZINTER, ZUNION, ZINTERCARD |
| Destination, key count, source keys, other arguments | ZDIFFSTORE, ZINTERSTORE, ZUNIONSTORE |

Script/function key counts may be zero. Other counted layouts require a positive
count, and all counts must fit the supplied arguments. Script/function execution
never loads, reloads, or replays after NOSCRIPT or a missing-function error. Scripts
and functions must access keys through the declared key arguments; source is not rewritten.

## Cluster and queue restrictions

Every key within one raw command must share a slot after prefixing. A mismatch throws
`RespireServerException` with code `CROSSSLOT` locally, before enqueueing. This matches
the existing typed multi-key command contract; the exception type does not imply a
server round trip. Rejected
commands do not pin a transaction to a slot. A Cluster transaction also requires all
its commands to share one slot. Cluster batches group commands by slot; order is
preserved within a group, while different groups may execute independently. Keyless
commands use an execution node and never fan out across primaries.

Blocking and connection-state commands are rejected: blocking list/sorted-set pops,
subscriptions, WATCH, transaction control, connection setup, CLIENT commands,
WAIT/WAITAOF, and similar session operations. XREAD/XREADGROUP are currently rejected
even without BLOCK because their layouts are not supported here. Administrative,
unknown, and module forms are rejected rather than trusted to preserve queue state.
SORT and the GEORADIUS family are also excluded because their optional external keys need their
own parsing rules; use the typed sorting/geospatial APIs where available.

No per-command flags, fire-and-forget mode, or implicit cluster-wide administration
are available. Deferred execution keeps the queue's existing cancellation, redirect,
and conservative client-cache invalidation behavior. Custom queue adapters must
implement the added `IRespireCommandQueue.Execute` member.
