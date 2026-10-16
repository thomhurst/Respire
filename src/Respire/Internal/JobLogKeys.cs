using System.Security.Cryptography;
using System.Text;

namespace Respire.Internal;

// Shared with the streaming worker and dashboard so inspection cannot address arbitrary keys.
internal static class JobLogKeys
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static string ForJob(string stream, string jobId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        return stream + ":logs:" + Convert.ToHexString(SHA256.HashData(StrictUtf8.GetBytes(jobId)));
    }
}
