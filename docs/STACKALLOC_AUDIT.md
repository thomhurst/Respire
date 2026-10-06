# Stack initialization audit

The package projects listed in `RespirePackageProjects` compile the shared
`src/Shared/SkipLocalsInit.cs` module attribute. Tests, benchmarks, samples, tooling,
and the netstandard analyzer assembly retain their existing initialization policy.
The attribute omits the compiler's local-initialization flag; it does not remove
managed heap initialization or explicit `default`, `Clear`, and assignment operations.

Every stack allocation must initialize each element before reading it. An API that
reports a written length may expose only that prefix; exception paths must not consume
unfinished output. Audit new stack allocations and generated code before extending
this policy. A passing test alone does not prove that an uninitialized read is safe.

## Active source inventory

The architecture test compares this inventory with explicit and implicit
`stackalloc` expressions in package source under both supported target-framework
branches. Adding a site or source file requires updating this inventory and the
initialization reasoning below. Removing a site also requires reconciling the
audit. Counts detect inventory drift, not reads before writes or replacements
that retain the same count; those still require source review. Generated compiler
output is not scanned by this source guard.

| Source | Active sites |
| --- | ---: |
| `src/Respire/Compression/RespireValueCodec.cs` | 2 |
| `src/Respire/Facets/BitmapCommands.cs` | 2 |
| `src/Respire/Facets/ScriptCommands.cs` | 3 |
| `src/Respire/Internal/ClusterHash.cs` | 2 |
| `src/Respire/Protocol/RespWriter.cs` | 1 |
| `src/Respire/Serialization/PrimitiveCodec.cs` | 1 |
| `src/Respire/RespireValue.cs` | 15 |
| `src/Respire/RespireKey.cs` | 1 |
| `src/Respire.Json/RespireJsonClient.cs` | 1 |

## Core sites

The core audit covers all 28 stack allocation expressions at base commit `f69d7ce9`.
The UTF-8 decoder's char scratch buffer is removed by the change, leaving 27 sites.

| Source and buffer | Sites | Initialization and read boundary |
| --- | ---: | --- |
| `Compression/RespireValueCodec.cs`: checksum and SHA256 hash | 2 | Version 2 writes all eight checksum bytes. Version 1 fills the 32-byte hash, then copies its first eight bytes. Validation reads exactly eight checksum bytes. |
| `Facets/BitmapCommands.cs`: encoding and field-index offset | 2 | The first byte is assigned explicitly. Formatting writes the remaining prefix; serialization uses only the returned length plus that first byte. Valid widths fit two decimal digits and signed Int64 offsets fit the 20-byte formatting region. |
| `Facets/ScriptCommands.cs`: source encoding, SHA1 hash, and hexadecimal text | 3 | UTF-8 byte count determines the initialized source prefix. ASCII conversion is selected only when that count equals the UTF-16 length. SHA1 fills all 20 hash bytes. The loop assigns both hex chars for every byte before constructing the string. |
| `Internal/ClusterHash.cs`: UTF-8 key and removal-lease candidate | 2 | Encoding fills the exact byte-count prefix before CRC calculation. The candidate is exactly two bytes; both are assigned before the containment check and CRC calculation on every iteration. |
| `Protocol/RespWriter.cs`: integer digits | 1 | Formatting reports the initialized prefix; `WriteBulkString` receives only that slice. The buffer accommodates signed Int64 decimal output. |
| `Serialization/PrimitiveCodec.cs`: decoded char | 1 | Strict UTF-8 char count must equal one. `GetChars` fills that char before it is read; conversion exceptions leave without reading it. |
| `RespireValue.cs`: numeric wire writes and scalar slots | 7 | Each formatter reports its initialized prefix. Writers and slot hashing receive only that prefix. Int64, UInt64, Single, and Double buffers accommodate their default wire representations. |
| `RespireValue.cs`: scalar equality, byte/string equality, and wire-length scratch | 5 | `WriteWirePayload` initializes the returned prefix for every kind. Comparisons slice to that length; wire-length calculation reads only the returned integer. The null/default case returns zero and reads no buffer element. |
| `RespireValue.cs`: payload hashing and UTF-8 equality buffers | 3 | `GetWireLength` or UTF-8 byte count determines the consumed prefix. `WriteWirePayload`/`GetBytes` initializes that prefix first. Hashing and comparisons ignore any excess capacity in rented arrays. |
| `RespireKey.cs`: cache-prefix encoding | 1 | UTF-8 encoding reports its initialized prefix; prefix matching receives only that slice. |
| `Internal/Utf8String.cs`: former 256-char scratch | 1 | Removed. On net8, ASCII spans up to 256 bytes write directly into the final string; longer spans use the runtime decoder. The memory overload and net10 span overload retain direct ASCII conversion without that cutoff. Unicode and invalid UTF-8 use the runtime decoder. |

### Decoder scope and measurements

The net8 span optimization removes the scratch copy for small ASCII payloads.
It retains the original 256-byte cutoff, named `Net8DirectAsciiMaxByteLength` in
the implementation; this is a preserved boundary, not a newly tuned optimum.
In [CI run 37399372813](https://github.com/thomhurst/Respire/actions/runs/37399372813),
removing the cutoff made large Unicode spans cost 465.69 ns against 441.76/428.10 ns
controls, with non-overlapping reported confidence intervals. A long ASCII
preflight before the runtime decoder added work on that path.

[CI run 37401915652](https://github.com/thomhurst/Respire/actions/runs/37401915652)
measured the restored cutoff: small net8 ASCII spans cost 16.39 ns against
24.93/25.05 ns controls. Large Unicode spans cost 562.23 ns against 552.90/585.79 ns
controls; its interval overlaps the first control and lies below the second.
No large-input decoder speedup is claimed.
Both frameworks retained the same allocation counts in all ten measured cases.
Acceptance requires the small-span gain without a repeatable regression in the
other operations; compare each candidate with both controls and their dispersion.
These measurements describe the tested build and runner, not a universal latency
guarantee. Reassess current-head reports after decoder or module-policy changes.

## Satellite packages

The satellite package source contains one explicit stack allocation at the audited
base. `Respire.Json/RespireJsonClient.cs` stores JSON.MSET end offsets in `int[count]`
for at most 128 entries. The first loop assigns every index before the second loop
reads any index. A serialization failure leaves through `finally` without consuming
unfinished offsets. The rented-array branch follows the same boundary.

The same module policy applies to the other satellites, which contain no explicit
stack allocations. Generated command wrappers use initialized command structs and
arrays. Audit compiler-generated source again when changing generators or adding
stack-based helpers. Buffers written by compression and platform APIs must still
honor their existing written-length contracts; skipping initialization does not
relax them.
