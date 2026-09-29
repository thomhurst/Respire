namespace Respire.Internal;

/// <summary>Channel and pattern names must survive a UTF-8 round trip; a lone surrogate cannot.</summary>
internal static class Utf8RouteName
{
    public static void Validate(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        for (var i = 0; i < name.Length; i++)
        {
            if (char.IsHighSurrogate(name[i]))
            {
                if (++i < name.Length && char.IsLowSurrogate(name[i]))
                {
                    continue;
                }

                throw new ArgumentException("Route names must contain valid UTF-16.", nameof(name));
            }

            if (char.IsLowSurrogate(name[i]))
            {
                throw new ArgumentException("Route names must contain valid UTF-16.", nameof(name));
            }
        }
    }
}
