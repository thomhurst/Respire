# GET Operation Flow Comparison: Respire vs StackExchange.Redis

## Allocation Targets
- **StackExchange.Redis**: ~232 bytes per GET
- **Respire (current)**: ~616 bytes per GET  
- **Gap**: 384 bytes

## StackExchange.Redis GET Flow

### Key Design Principles
1. **Message Reuse**: Static reusable message instances for common commands
2. **Lease API**: IMemoryOwner<T> pattern for buffer management (v2.0+)
3. **Multiplexing**: Single connection shared across all operations
4. **Direct Buffers**: ReadOnlySequence<byte> instead of byte arrays

### Flow Steps
```csharp
// Simplified StackExchange.Redis GET flow
1. StringGetAsync(key) called
2. Message.Create(database, flags, RedisCommand.GET, key)
   - Messages are lightweight structs
   - Reuses static instances where possible
3. ExecuteAsync(message, ResultProcessor.RedisValue)
   - Enqueues to PhysicalBridge
   - No lambda allocations
4. PhysicalBridge processing
   - Backlog processed on dedicated thread
   - Commands batched automatically
5. Response handling
   - Direct buffer access via ReadOnlySequence
   - StringGetLease API returns IMemoryOwner<byte>
   - No intermediate byte[] allocation
```

### Key Optimizations
- **Static Message Instances**: Reusable message objects for common commands
- **Struct Messages**: Messages are value types, not reference types
- **No Delegates/Lambdas**: Direct method calls without closure allocations
- **Buffer Leasing**: Memory returned from pool, not allocated
- **Synchronous Completion**: Many operations complete synchronously

## Respire GET Flow (Current)

### Flow Steps
```csharp
// Current Respire GET flow
1. GetAsync(key) called
2. Fast path check (if enabled)
3. ExecuteGetFastPathAsync:
   a. Check pre-encoded cache (ConcurrentDictionary lookup)
   b. GetConnectionHandleAsync (async state machine)
   c. Create PooledBufferWriter (allocation)
   d. Build command into buffer
   e. WritePreCompiledCommandAsync (async state machine)
   f. ReadAsync (async state machine)
   g. ParseRespResponse with RespPipelineReader
   h. Return RespireValue
```

### Current Allocations
1. **Async State Machines**: ~3 per GET (GetConnectionHandleAsync, WritePreCompiledCommandAsync, ReadAsync)
2. **ConnectionHandle**: Disposable struct boxing
3. **PooledBufferWriter**: Even with pooling, the wrapper allocates
4. **Cache Operations**: ConcurrentDictionary operations
5. **ValueTask Boxing**: When not completing synchronously

## Key Differences

### 1. Message Pattern
- **StackExchange**: Lightweight struct messages, reused instances
- **Respire**: Direct command building, but with allocations

### 2. Async Handling
- **StackExchange**: Synchronous fast paths, minimal async state machines
- **Respire**: Multiple async methods in hot path

### 3. Connection Management
- **StackExchange**: Single multiplexed connection, no per-operation overhead
- **Respire**: ConnectionHandle allocation per operation

### 4. Command Building
- **StackExchange**: Pre-built message structures
- **Respire**: Dynamic buffer building (even with pooling)

### 5. Response Handling
- **StackExchange**: Direct buffer access, lease API
- **Respire**: Good (ref struct reader), but still allocations in flow

## Recommendations for Respire

### High Priority (Quick Wins)
1. **Eliminate ConnectionHandle allocations**
   - Make it a readonly struct, not IDisposable
   - Or cache and reuse handles

2. **Synchronous Fast Paths**
   - Make GetConnectionHandleAsync complete synchronously when possible
   - Already partially done, needs more optimization

3. **Pre-built Commands**
   - Cache entire Message objects, not just byte arrays
   - Use struct messages like StackExchange

### Medium Priority
4. **Reduce Async State Machines**
   - Combine multiple async operations
   - Use synchronous APIs where possible

5. **Lease API for Responses**
   - Return IMemoryOwner<byte> instead of copying
   - Allow zero-copy deserialization

### Low Priority (Architecture Changes)
6. **True Multiplexing**
   - Single connection for all operations
   - Eliminate per-operation connection overhead

7. **Struct-based Command Queue**
   - Use value types for queue items
   - Avoid boxing and reference allocations

## Specific Allocation Sources to Address

### Per-GET Allocations (Estimated)
- Async state machine (GetConnectionHandleAsync): ~80 bytes
- Async state machine (WritePreCompiledCommandAsync): ~80 bytes  
- Async state machine (ReadAsync): ~80 bytes
- ConnectionHandle (if boxing): ~24 bytes
- PooledBufferWriter wrapper: ~40 bytes
- ConcurrentDictionary operations: ~40 bytes
- ValueTask boxing (when not synchronous): ~40 bytes
**Total**: ~384 bytes (matches our gap!)

## Implementation Priority

1. **Make ConnectionHandle a readonly struct** (save ~24 bytes)
2. **Cache and reuse ConnectionHandles** (save ~80 bytes async state)
3. **Ensure synchronous completion paths** (save ~240 bytes from 3 async states)
4. **Pre-build and cache Message structs** (save ~40 bytes)
5. **Implement lease API for responses** (future optimization)

These optimizations should bring Respire's GET allocations down from 616 bytes to approximately 232 bytes, matching StackExchange.Redis.