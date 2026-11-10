using System.Collections.Immutable;
using BenchmarkDotNet.Characteristics;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Running;
using BenchmarkDotNet.Toolchains;
using BenchmarkDotNet.Toolchains.Parameters;
using BenchmarkDotNet.Toolchains.Results;
using BenchmarkDotNet.Validators;

namespace Respire.Benchmarks;

/// <summary>BenchmarkDotNet uses NoOverhead diagnosers only on the last launch; wrap every execution instead.</summary>
internal sealed class SelectionEvidenceToolchain(IToolchain inner, string directory) : IToolchain
{
    public BenchmarkDotNet.Environments.Runtime Runtime => inner.Runtime;
    public IBuilder Builder => inner.Builder;
    public bool IsInProcess => inner.IsInProcess;
    public IExecutor Executor { get; } = new SelectionExecutor(inner.Executor, directory);
    public IAsyncEnumerable<ValidationError> ValidateAsync(BenchmarkCase benchmarkCase, IResolver resolver)
        => inner.ValidateAsync(benchmarkCase, resolver);

    private sealed class SelectionExecutor(IExecutor inner, string directory) : IExecutor
    {
        public ValueTask<ExecuteResult> ExecuteAsync(ExecuteParameters parameters, CancellationToken cancellationToken)
        {
            if (parameters.BenchmarkCase.Descriptor.WorkloadMethod.Name != "SelectConnection")
                return inner.ExecuteAsync(parameters, cancellationToken);

            var launches = parameters.BenchmarkCase.Job.ResolveValue(
                BenchmarkDotNet.Jobs.RunMode.LaunchCountCharacteristic, parameters.Resolver);
            var extraRun = parameters.LaunchIndex > launches;
            var collector = new SelectionLaunchDiagnoser(extraRun ? Path.Combine(directory, "disassembly") : directory,
                parameters.LaunchIndex, extraRun);
            var diagnosers = ImmutableHashSet.Create<IDiagnoser>(collector);
            if (parameters.Diagnoser is { } existing) diagnosers = diagnosers.Add(existing);
            return inner.ExecuteAsync(new ExecuteParameters(parameters.BuildResult, parameters.BenchmarkCase,
                parameters.BenchmarkId, parameters.Logger, parameters.Resolver, parameters.LaunchIndex,
                parameters.CompositeInProcessDiagnoser, new CompositeDiagnoser(diagnosers), parameters.DiagnoserRunMode),
                cancellationToken);
        }
    }
}
