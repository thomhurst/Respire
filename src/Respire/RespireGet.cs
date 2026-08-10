namespace Respire;

/// <summary>
/// The result of a typed read that reports presence separately from the value.
/// <para>
/// <c>GetAsync&lt;T&gt;</c> returns <c>T?</c>, which for a value type collapses "key was missing"
/// and "key held <c>default(T)</c>" into the same answer — <c>GetAsync&lt;int&gt;</c> yields
/// <c>0</c> for both an absent key and a stored <c>0</c>. <c>TryGetAsync&lt;T&gt;</c> returns this
/// struct instead, so <see cref="Found"/> tells the two apart without boxing into
/// <c>Nullable&lt;T&gt;</c> or paying for a second existence round trip.
/// </para>
/// </summary>
/// <typeparam name="T">The deserialized value type.</typeparam>
/// <param name="Found">True when the key or field existed; false when Redis replied null.</param>
/// <param name="Value">The deserialized value, or <c>default</c> when <paramref name="Found"/> is false.</param>
/// <example>
/// <code>
/// var (found, hits) = await client.TryGetAsync&lt;int&gt;("page:hits");
/// </code>
/// </example>
public readonly record struct RespireGet<T>(bool Found, T Value)
{
    /// <summary>The value when present, otherwise <paramref name="fallback"/>.</summary>
    public T GetValueOrDefault(T fallback) => Found ? Value : fallback;
}
