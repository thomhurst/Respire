namespace Respire.Internal;

internal static class CommandNameRules
{
    internal static bool IsValidCharacter(char character)
        => char.IsAsciiLetterOrDigit(character) || character is ' ' or '.' or '_' or '-';
}
