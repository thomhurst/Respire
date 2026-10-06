"""Reject comparisons that change build inputs beyond the decoder and shared fixture."""

DECODER = "src/Respire/Internal/Utf8String.cs"
FIXTURE = "benchmarks/Respire.Benchmarks/ProtocolBenchmarks.cs"

# These files do not enter either benchmark build. Keep this list explicit so a
# new import, generator, SDK setting, or benchmark configuration fails closed.
MAINTENANCE = {
    ".github/workflows/benchmark-utf8-decoder.yml",
    ".github/workflows/test-full-net8.yml",
    "scripts/assert_decoder_benchmarks.py",
    "scripts/decoder_benchmark_inputs.py",
    "scripts/tests/test_assert_decoder_benchmarks.py",
    "scripts/tests/test_decoder_benchmark_inputs.py",
    "tests/Respire.Tests/Networking/MaintenanceNotificationTests.cs",
    "tests/Respire.Tests/Utf8StringTests.cs",
}


def validate_changes(changed):
    unexpected = set(changed) - MAINTENANCE - {DECODER, FIXTURE}
    if unexpected:
        raise ValueError(f"Benchmark inputs must match controls: {sorted(unexpected)}")
    return [DECODER] if DECODER in changed else []
