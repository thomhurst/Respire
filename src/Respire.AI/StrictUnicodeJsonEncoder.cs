using System.Text;
using System.Text.Encodings.Web;

namespace Respire.AI;

// Validate strings at the writer boundary, including nested option values and dictionary keys.
// An object-graph check would miss strings written by the upstream AI JSON converters.
// Delegate escaping unchanged so well-formed scopes retain their existing hashes.
internal sealed class StrictUnicodeJsonEncoder : JavaScriptEncoder
{
    internal static readonly StrictUnicodeJsonEncoder Instance = new();
    internal static readonly Encoding Utf8 = new UTF8Encoding(false, true);

    private StrictUnicodeJsonEncoder() { }

    public override int MaxOutputCharactersPerInputCharacter => Default.MaxOutputCharactersPerInputCharacter;

    public override bool WillEncode(int unicodeScalar) => Default.WillEncode(unicodeScalar);

    public override unsafe int FindFirstCharacterToEncode(char* text, int textLength)
    {
        _ = Utf8.GetByteCount(new ReadOnlySpan<char>(text, textLength));
        return Default.FindFirstCharacterToEncode(text, textLength);
    }

    public override unsafe bool TryEncodeUnicodeScalar(int unicodeScalar, char* buffer, int bufferLength, out int numberOfCharactersWritten)
        => Default.TryEncodeUnicodeScalar(unicodeScalar, buffer, bufferLength, out numberOfCharactersWritten);
}
