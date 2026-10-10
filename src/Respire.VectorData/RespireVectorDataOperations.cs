using System.Globalization;
using Microsoft.Extensions.VectorData;
using Respire.Search;

namespace Respire.VectorData;

internal static class RespireVectorDataOperations
{
    internal static double ReadHybridScore(RespireSearchDocument document, string collectionName)
        => document.Score is { } score && double.IsFinite(score) ? score
            : throw CreateException(new InvalidOperationException("Hybrid search returned a missing or non-finite fusion score."), "HybridSearchAsync", collectionName);

    internal static string DecodeName(string encoded, string operationName, string? collectionName = null)
    {
        try
        {
            return RespireVectorStore.DecodeName(encoded);
        }
        catch (Exception error) when (error is FormatException or ArgumentException)
        {
            throw CreateException(error, operationName, collectionName);
        }
    }

    internal static double ReadSearchScore(RespireSearchDocument document, string prefix, string collectionName)
    {
        if (!document.Id.StartsWith(prefix, StringComparison.Ordinal))
            throw CreateException(new InvalidOperationException("Search returned a document outside this collection."), "SearchAsync", collectionName);
        if (!document.Fields.TryGetValue("vector_score", out var value)
            || !double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var score)
            || !double.IsFinite(score))
            throw CreateException(new InvalidOperationException("Search returned a missing, invalid or non-finite vector distance."), "SearchAsync", collectionName);
        return score;
    }

    internal static async Task DeleteCollectionAsync(IRespireClient client, string index, string prefix, string collectionName, CancellationToken cancellationToken)
    {
        const string operationName = "EnsureCollectionDeletedAsync";
        await ExecuteAsync(client.Search.DropIndexAsync(index, deleteDocuments: true, cancellationToken),
            operationName, collectionName, "Unknown Index name", "SEARCH_INDEX_NOT_FOUND").ConfigureAwait(false);
        // FT.DROPINDEX DD only removes indexed documents. Also remove records written before index creation.
        try
        {
            await foreach (var key in client.Keys.ScanAsync(match: prefix + "*", cancellationToken: cancellationToken).ConfigureAwait(false))
                await ExecuteAsync(client.Keys.DeleteAsync([key], cancellationToken), operationName, collectionName).ConfigureAwait(false);
        }
        catch (RespireException error)
        {
            throw CreateException(error, operationName, collectionName);
        }
    }

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

    internal static async ValueTask ExecuteAsync(ValueTask operation, string operationName, string collectionName, params string[] ignoredServerMessages)
    {
        try
        {
            await operation.ConfigureAwait(false);
        }
        catch (RespireServerException error) when (ignoredServerMessages.Any(message => error.Message.Contains(message, StringComparison.Ordinal)))
        {
            // Another caller completed the same idempotent lifecycle operation.
        }
        catch (RespireException error)
        {
            throw CreateException(error, operationName, collectionName);
        }
    }

    private static VectorStoreException CreateException(Exception error, string operationName, string? collectionName)
        => new(error.Message, error)
        {
            VectorStoreSystemName = "redis",
            CollectionName = collectionName,
            OperationName = operationName,
        };
}
