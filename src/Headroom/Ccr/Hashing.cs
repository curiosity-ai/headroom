using System.Security.Cryptography;
using System.Text;

namespace Headroom.Ccr;

/// <summary>
/// Short content hashes used for CCR cache keys and de-duplication. Matches the reference
/// project's conventions: lowercase hex, truncated.
/// </summary>
public static class Hashing
{
    /// <summary>
    /// MD5 of the UTF-8 bytes, lowercase hex, truncated to the first 24 chars. Used as the CCR
    /// cache key for log / search / diff offloads.
    /// </summary>
    public static string Md5Hex24(string content) => Md5Hex(content)[..24];

    /// <summary>Full lowercase MD5 hex of the UTF-8 bytes.</summary>
    public static string Md5Hex(string content)
    {
        byte[] hash = MD5.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>
    /// SHA-256 of the UTF-8 bytes, lowercase hex, truncated to the first 12 chars (6 bytes).
    /// Used for the SmartCrusher dropped-row sentinel key.
    /// </summary>
    public static string Sha256Hex12(string content) => Sha256Hex(content)[..12];

    /// <summary>
    /// SHA-256 of the UTF-8 bytes, lowercase hex, truncated to the first 16 chars. Used for
    /// content de-duplication keys.
    /// </summary>
    public static string Sha256Hex16(string content) => Sha256Hex(content)[..16];

    /// <summary>Full lowercase SHA-256 hex of the UTF-8 bytes.</summary>
    public static string Sha256Hex(string content)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexStringLower(hash);
    }
}
