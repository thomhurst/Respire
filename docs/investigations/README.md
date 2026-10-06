# Investigation evidence

These directories preserve inputs and outputs from specific investigations.
They are frozen snapshots, not maintained performance baselines or additions to
the permanent benchmark suite. Read the associated report in `docs/` for the
decision, measured revision, environment, commands, and limitations.

Do not refresh historical measurements when production code changes. A new
comparison should record its own revision and environment in a separate archive,
with a report that distinguishes it from earlier evidence. Keep only artifacts
needed to reproduce or assess the decision; exclude build output, dependencies,
credentials, and unrelated machine data.

Archived source files use `.txt` so repository builds do not compile them.
Reconstruct a harness only in a disposable checkout at its recorded revision.
Rejected prototypes are evidence, not code to apply to the current product.

Where a report records hashes, preserve the archived bytes, including intentional
line endings and report formatting. Use directory-scoped attributes when needed.
Correct an explanatory error transparently without replacing the original
measurements or making their recorded hashes describe different input files.
