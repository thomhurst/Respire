using Microsoft.Extensions.VectorData;

namespace Respire.VectorData;

internal static class RespireVectorDataOperations
{
    internal static async ValueTask<T> ExecuteAsync<T>(ValueTask<T> operation, string operationName, string? collectionName = null)
    {
        try
        {
            return await operation.ConfigureAwait(false);
        }
        catch (RespireException error)
        {
            throw CreateException(error, operationName, collectionName);
        }
    }

    internal static async ValueTask ExecuteAsync(ValueTask operation, string operationName, string collectionName, string ignoredServerMessage)
    {
        try
        {
            await operation.ConfigureAwait(false);
        }
        catch (RespireServerException error) when (error.Message.Contains(ignoredServerMessage, StringComparison.Ordinal))
        {
            // Another caller completed the same idempotent lifecycle operation.
        }
        catch (RespireException error)
        {
            throw CreateException(error, operationName, collectionName);
        }
    }

    private static VectorStoreException CreateException(RespireException error, string operationName, string? collectionName)
        => new(error.Message, error)
        {
            VectorStoreSystemName = "redis",
            CollectionName = collectionName,
            OperationName = operationName,
        };
}
