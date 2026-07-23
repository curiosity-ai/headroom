namespace Headroom.Ccr;

/// <summary>
/// CCR (Cache-and-Retrieve) store. When a lossy compression step drops content, the original is
/// stored here keyed by a short content hash and a sentinel marker is left in the prompt. A
/// retrieval tool can later hand the original bytes back to the model — this is what makes
/// Headroom compression <b>reversible</b>.
/// </summary>
public interface ICcrStore
{
    /// <summary>Store <paramref name="original"/> under <paramref name="key"/> (idempotent).</summary>
    void Put(string key, string original);

    /// <summary>Retrieve the original for <paramref name="key"/>, or null if unknown.</summary>
    string? Retrieve(string key);

    /// <summary>True if <paramref name="key"/> is present.</summary>
    bool Contains(string key);
}
