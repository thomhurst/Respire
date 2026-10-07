# Completion buffer retention

Each connection recycles delivered completion arrays under the scheduler's handoff
gate. The spare list starts with four references and grows only when completed
drains demonstrate greater demand. It retains at most 16 arrays and 1,024 entry
slots in total. Full 256-entry batches therefore still retain at most four arrays;
64-entry batches can retain 16. Array-reference and array-header overhead is
additional to the entry storage. The retained-buffer tests report actual entry
bytes using the runtime's Entry size, with headers explicitly excluded.
On the validated Windows X64 net8.0/net10.0 runtimes, each Entry is 48 bytes;
the maximum retained entry storage is therefore 49,152 bytes per connection.

Pending batches, the producer's filling array and active runners own live work;
these are separate from the spare-cache bound. Entries are cleared only after
delivery releases the receive reference. Rescued tails are copied and cleared under
the existing generation/claim protocol, and the old runner cannot return its array
until its current continuation returns. The cache does not alter this ownership
protocol. Reused rescue tails grow to at most 256 entries, including tails whose
length is not a power of two.

The allocation regression queues 12 separately flushed replies per drain while
the test owns the runner. After warming, 32 drains must allocate exactly zero
bytes; the same loop with an escaping allocation is a positive control. Deep small
and full-capacity bursts verify cleared slots and bounded retention. Cancellation,
failure and blocked-runner rescue controls exercise the return boundaries.

CI compares the same fixtures against immutable candidate and baseline revisions
on each supported framework. Connection contention covers public pipelined GET
and 50 concurrent producers. Completion buffer comparisons cover binary GET at
1 KiB and 1 MiB. Bracketing baseline controls, allocation and latency intervals
remain part of acceptance; the local allocation control alone does not establish
a public-path performance improvement.
