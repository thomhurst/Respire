using System.Buffers;
using System.IO.Compression;
using Keva.Core.Pipeline;
using Keva.Core.Protocol;

namespace Keva.Compression;

public class CompressionInterceptor : IKevaInterceptor
{
    private readonly CompressionOptions _options;
    private readonly ArrayPool<byte> _arrayPool;

    public CompressionInterceptor(CompressionOptions? options = null)
    {
        _options = options ?? new CompressionOptions();
        _arrayPool = ArrayPool<byte>.Shared;
    }

    public async ValueTask<RespValue> InterceptAsync(
        KevaInterceptorContext context,
        InterceptorDelegate next,
        CancellationToken cancellationToken = default)
    {
        // Check if compression should be applied
        if (!ShouldCompress(context))
        {
            return await next(context, cancellationToken);
        }

        // Compress the command if it's large enough
        var originalCommand = context.Command;
        if (originalCommand.Length >= _options.MinSizeForCompression)
        {
            var compressedCommand = await CompressAsync(originalCommand, cancellationToken);
            
            // Create new context with compressed command
            var compressedContext = new KevaInterceptorContext(compressedCommand);
            
            // Copy existing items
            foreach (var item in context.Items)
            {
                compressedContext.Items[item.Key] = item.Value;
            }
            
            // Add compression metadata
            compressedContext.Items["Compressed"] = true;
            compressedContext.Items["CompressionAlgorithm"] = _options.Algorithm.ToString();
            compressedContext.Items["OriginalSize"] = originalCommand.Length;
            compressedContext.Items["CompressedSize"] = compressedCommand.Length;
            
            // Execute with compressed command
            var response = await next(compressedContext, cancellationToken);
            
            // Check if response needs decompression
            if (IsCompressedResponse(response))
            {
                return await DecompressResponseAsync(response, cancellationToken);
            }
            
            return response;
        }

        return await next(context, cancellationToken);
    }

    private bool ShouldCompress(KevaInterceptorContext context)
    {
        // Check if already compressed
        if (context.Items.ContainsKey("Compressed"))
        {
            return false;
        }

        // Check if command type should be compressed
        if (_options.ExcludedCommands != null && _options.ExcludedCommands.Count > 0)
        {
            // Parse command name from the RESP command
            var commandName = context.Command.GetCommandName();
            if (!string.IsNullOrEmpty(commandName) && _options.ExcludedCommands.Contains(commandName))
            {
                return false;
            }
        }

        return _options.Enabled;
    }

    private async ValueTask<ReadOnlyMemory<byte>> CompressAsync(
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken)
    {
        using var output = new MemoryStream();
        
        Stream compressionStream = _options.Algorithm switch
        {
            CompressionAlgorithm.GZip => new GZipStream(output, _options.Level),
            CompressionAlgorithm.Deflate => new DeflateStream(output, _options.Level),
            CompressionAlgorithm.Brotli => new BrotliStream(output, _options.Level),
            _ => throw new NotSupportedException($"Compression algorithm {_options.Algorithm} is not supported")
        };

        using (compressionStream)
        {
            await compressionStream.WriteAsync(data, cancellationToken);
            await compressionStream.FlushAsync(cancellationToken);
        }

        return output.ToArray();
    }

    private bool IsCompressedResponse(RespValue response)
    {
        // Check if response has compression marker
        // This would need to be coordinated with server-side compression
        // For now, return false as standard Redis doesn't compress responses
        return false;
    }

    private async ValueTask<RespValue> DecompressResponseAsync(
        RespValue response,
        CancellationToken cancellationToken)
    {
        // Decompress response if needed
        // This would need implementation based on how the server marks compressed responses
        return response;
    }
}

public class CompressionOptions
{
    public bool Enabled { get; set; } = true;
    public CompressionAlgorithm Algorithm { get; set; } = CompressionAlgorithm.GZip;
    public CompressionLevel Level { get; set; } = CompressionLevel.Fastest;
    public int MinSizeForCompression { get; set; } = 1024; // 1KB minimum
    public HashSet<string>? ExcludedCommands { get; set; }
}

public enum CompressionAlgorithm
{
    GZip,
    Deflate,
    Brotli
}