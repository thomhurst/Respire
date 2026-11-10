namespace System.Runtime.CompilerServices;

/// <summary>
/// Opts a method out of runtime async code generation on .NET 11. Runtime async ignores
/// <see cref="AsyncMethodBuilderAttribute"/>, so a method that relies on
/// <see cref="PoolingAsyncValueTaskMethodBuilder"/> to reuse its state machine would allocate a
/// continuation every time it suspends. The compiler recognizes this attribute by name; put
/// <c>[RuntimeAsyncMethodGeneration(false)]</c> beside every pooling builder attribute.
/// It has no effect on .NET 10.
/// </summary>
[AttributeUsage(AttributeTargets.Method, Inherited = false)]
internal sealed class RuntimeAsyncMethodGenerationAttribute(bool runtimeAsync) : Attribute
{
    public bool RuntimeAsync { get; } = runtimeAsync;
}
