using System.Diagnostics.CodeAnalysis;

namespace Respire;

/// <summary>
/// The result of a typed read that reports presence separately from the value.
/// <para>
/// <c>GetAsync&lt;int&gt;</c> yields <c>0</c> for both an absent key and a stored <c>0</c>.
/// Callers can use <c>GetAsync&lt;int?&gt;</c> to preserve that distinction, or use
/// <c>TryGetAsync&lt;int&gt;</c> to keep a non-nullable value while <see cref="Found"/> reports
/// presence explicitly. Neither form requires a second existence round trip.
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

    /// <summary>Gets the value and reports whether Redis returned one.</summary>
    /// <param name="value">The deserialized value, or <c>default</c> when not found.</param>
    /// <returns>True when the key or field existed; otherwise false.</returns>
    public bool TryGetValue([MaybeNullWhen(false)] out T value)
    {
        value = Found ? Value : default;
        return Found;
    }
}
