using Seeing.Agent.Abstractions.Files;
using System.Security.Cryptography;

namespace Seeing.Agent.Files;

/// <summary>基于全文内容的修订号计算。</summary>
internal static class RevisionCalculator
{
    /// <summary>对字节内容计算 SHA256 修订号。</summary>
    public static FileRevision FromBytes(byte[] bytes, DateTime lastWriteUtc)
    {
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return new FileRevision(hash, bytes.LongLength, lastWriteUtc);
    }
}
