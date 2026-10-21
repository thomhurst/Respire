using Respire.Benchmarks;

// Issue #2065: exercise the identical public fixture without BDN timing or profiling.
// Each process owns one policy, so JIT files cannot overwrite another policy's output.
if (args is not ["Default" or "Disabled" or "Enabled", _]
    || !int.TryParse(args[1], out var operations) || operations <= 0)
    throw new ArgumentException("Specify Default, Disabled or Enabled and a positive operation count.");

var fixture = new CommandRetryBenchmarks { Policy = args[0] };
try
{
    await fixture.Setup();
    // The collector starts call counting immediately for background tier promotion;
    // FullOpts is collected separately. Neither mode reports latency or changes the
    // acceptance benchmarks.
    for (var i = 0; i < operations; i++)
    {
        if (await fixture.ScriptLength() != 5)
            throw new InvalidOperationException("Unexpected script result.");
    }
    Console.WriteLine($"JIT_EXERCISE_COMPLETE policy={args[0]} operations={operations}");
}
finally
{
    await fixture.Cleanup();
}
