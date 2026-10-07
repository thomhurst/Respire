# Receive tail compaction

The receive loop reclaims a consumed prefix before starting another receive when
the free tail is smaller than `min(4096, max(1, capacity / 8))` bytes and the
consumed prefix is at least as large as the unread suffix. At the exact threshold,
the loop uses the existing tail. A full buffer still uses the previous compaction
or growth behavior, regardless of the relative prefix and suffix sizes.

The size check avoids another tiny receive after a pipeline has consumed most of
the buffer. The prefix check limits the optional copy to bytes no larger than the
space it reclaims. These are candidate thresholds; benchmark comparisons must
establish their performance before the optimization is accepted.

## Ownership and framing

Compaction runs after parsing the completed receive and before starting the next
receive. Direct-fill has also completed at this point. No outstanding receive can
reference bytes being moved. The existing deferred delivery and receive paths
receive the resulting free-tail memory only after this step.

Only the unread suffix moves. Resumable aggregate state retains parsed children
and lengths independently of the receive array. The accumulated response-byte
count is not reset by compaction. Receive storage growth and return retain the
connection's pinning and pool ownership policy.

`BufferedBulkReplyTests` observes actual scripted read destination lengths at the
4 KiB and one-eighth thresholds, including one-byte tails. Controls cover nested
attributes and aggregates, following pipeline replies, malformed trailing bulk
framing, large unread suffixes, full-buffer compaction, and unconsumed-frame
growth. The related bytes, cancellation, parser, and receive-storage tests cover
direct-fill, response limits, and receive lifetime.

## Performance acceptance

The connection-contention workflow compares 200-command pipelined GET and
concurrency-50 pipelines. The receive-compaction workflow additionally compares
raw and typed byte GET with 1 KiB and 1 MiB values. Both use the reusable pinned
comparison workflow on net10.0, as required by repository benchmark policy.
Correctness controls continue to run on net8.0 and net10.0.

Each comparison checks immutable merge parents and copies the candidate fixture
unchanged into the baseline. Bracketing baseline measurements surround the
candidate on the same runner. Reports include revision identities, allocations,
latency intervals, and uncertainty. Review both the large-value results and any
small-reply regression before accepting or revising the threshold. Local tests
establish framing and ownership behavior; they do not establish a speedup.
