namespace Respire.Search;

/// <summary>Shared vector representation validation for generated schemas and independent connectors.</summary>
public static class RespireSearchVectorValidation
{
    /// <summary>Validates the byte length of a hash vector against its schema element type and dimensions.</summary>
    public static void ValidateHash(byte[] value, RespireSearchVectorOptions options)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(options);
        if (options.Dimensions <= 0) throw new ArgumentOutOfRangeException(nameof(options));
        var size = options.Type switch
        {
            RespireSearchVectorType.Float32 => 4,
            RespireSearchVectorType.Float64 => 8,
            RespireSearchVectorType.BFloat16 or RespireSearchVectorType.Float16 => 2,
            RespireSearchVectorType.Int8 or RespireSearchVectorType.UInt8 => 1,
            _ => throw new ArgumentOutOfRangeException(nameof(options)),
        };
        if (value.LongLength != (long)options.Dimensions * size)
            throw new ArgumentException("Hash vector byte length must match dimensions times element size.", nameof(value));
    }

    /// <summary>Validates a FLOAT32 JSON vector, including finite values.</summary>
    public static void ValidateJson(float[] value, RespireSearchVectorOptions options)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(options);
        if (options.Type != RespireSearchVectorType.Float32 || options.Dimensions <= 0 || value.Length != options.Dimensions)
            throw new ArgumentException("JSON float vector must match FLOAT32 schema dimensions.", nameof(value));
        foreach (var element in value)
            if (!float.IsFinite(element)) throw new ArgumentException("JSON vector values must be finite.", nameof(value));
    }

    /// <summary>Validates a FLOAT64 JSON vector, including finite values.</summary>
    public static void ValidateJson(double[] value, RespireSearchVectorOptions options)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(options);
        if (options.Type != RespireSearchVectorType.Float64 || options.Dimensions <= 0 || value.Length != options.Dimensions)
            throw new ArgumentException("JSON double vector must match FLOAT64 schema dimensions.", nameof(value));
        foreach (var element in value)
            if (!double.IsFinite(element)) throw new ArgumentException("JSON vector values must be finite.", nameof(value));
    }
}
