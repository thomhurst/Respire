using Respire.Benchmarks;

// Issue #2065: exercise the identical public fixture without BDN timing or profiling.
// Each process owns one policy, so JIT files cannot overwrite another policy's output.
if (args is not ["Default" or "Disabled" or "Enabled"])
    throw new ArgumentException("Specify Default, Disabled or Enabled.");

var fixture = new CommandRetryBenchmarks { Policy = args[0] };
try
{
    await fixture.Setup();
    // Enough invocations for normal background tier promotion; FullOpts is collected
    // separately. Neither mode reports latency or changes the acceptance benchmarks.
    for (var i = 0; i < 16384; i++)
    {
        if (await fixture.ScriptLength() != 5)
            throw new InvalidOperationException("Unexpected script result.");
    }
    Console.WriteLine($"JIT_EXERCISE_COMPLETE policy={args[0]} operations=16384");
}
finally
{
    await fixture.Cleanup();
}
