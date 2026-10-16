using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace Respire.Idempotency;

internal sealed record StoredResponse(int Status, Dictionary<string, string[]> Headers, byte[] Body)
{
    private const int HeaderLimit = 16 * 1024;
    private static readonly string[] SafeHeaders =
        ["Content-Type", "Content-Language", "Content-Encoding", "Location", "ETag", "Last-Modified"];

    internal static byte[]? Encode(HttpResponse response, BoundedResponseStream body)
    {
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true);
        writer.Write(1);
        writer.Write(response.StatusCode);
        // Connection can nominate even otherwise safe headers as hop-by-hop.
        var connectionHeaders = response.Headers.Connection.ToString().Split(',', StringSplitOptions.TrimEntries);
        var headers = SafeHeaders.Where(h => response.Headers.ContainsKey(h)
            && !connectionHeaders.Contains(h, StringComparer.OrdinalIgnoreCase)).ToArray();
        writer.Write(headers.Length);
        foreach (var name in headers)
        {
            writer.Write(name);
            var values = response.Headers[name];
            writer.Write(values.Count);
            foreach (var value in values)
            {
                if (Encoding.UTF8.GetByteCount(value ?? "") > HeaderLimit) return null;
                writer.Write(value ?? "");
                if (output.Length > HeaderLimit) return null;
            }
        }
        if (output.Length > HeaderLimit) return null;
        writer.Write((int)body.Length);
        writer.Write(body.GetBuffer(), 0, (int)body.Length);
        return output.ToArray();
    }

    internal static StoredResponse Decode(ReadOnlyMemory<byte> bytes, int bodyLimit)
    {
        if (bytes.Length > bodyLimit + HeaderLimit + 4) throw new InvalidDataException("Stored response exceeds configured bounds.");
        using var input = new MemoryStream(bytes.ToArray(), writable: false);
        using var reader = new BinaryReader(input, Encoding.UTF8);
        if (reader.ReadInt32() != 1) throw new InvalidDataException("Unknown stored response format.");
        var status = reader.ReadInt32();
        var count = reader.ReadInt32();
        if (status is < 200 or > 599 || count < 0 || count > SafeHeaders.Length) throw new InvalidDataException("Invalid stored response.");
        var headers = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < count; i++)
        {
            var name = reader.ReadString();
            var valueCount = reader.ReadInt32();
            if (!SafeHeaders.Contains(name, StringComparer.OrdinalIgnoreCase) || valueCount is < 0 or > HeaderLimit)
                throw new InvalidDataException("Invalid stored headers.");
            var values = new string[valueCount];
            for (var j = 0; j < valueCount; j++) values[j] = reader.ReadString();
            if (input.Position > HeaderLimit) throw new InvalidDataException("Stored headers exceed configured bounds.");
            headers.Add(name, values);
        }
        var length = reader.ReadInt32();
        if (length < 0 || length > bodyLimit || input.Length - input.Position != length) throw new InvalidDataException("Invalid stored body.");
        return new(status, headers, reader.ReadBytes(length));
    }

    internal async Task ReplayAsync(HttpContext context)
    {
        context.Response.StatusCode = Status;
        foreach (var (name, values) in Headers) context.Response.Headers[name] = new StringValues(values);
        if (Status is StatusCodes.Status204NoContent or StatusCodes.Status304NotModified) return;
        context.Response.ContentLength = Body.Length;
        if (HttpMethods.IsHead(context.Request.Method)) return;
        await context.Response.Body.WriteAsync(Body, context.RequestAborted).ConfigureAwait(false);
    }
}
