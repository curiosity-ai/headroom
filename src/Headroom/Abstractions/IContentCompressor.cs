namespace Headroom.Abstractions;

/// <summary>
/// A content-aware compressor. Each implementation targets one content shape (JSON, logs,
/// search output, diffs, prose) and knows how to shrink it while preserving what the model needs.
/// </summary>
public interface IContentCompressor
{
    /// <summary>The content type this compressor handles.</summary>
    ContentType Handles { get; }

    /// <summary>
    /// Compress <paramref name="content"/>. Implementations must never throw for malformed
    /// input — they return a pass-through <see cref="CompressionOutcome"/> instead.
    /// </summary>
    /// <param name="content">The text to compress.</param>
    /// <param name="context">
    /// Optional query/context text used to bias relevance-aware selection. May be null or empty.
    /// </param>
    CompressionOutcome Compress(string content, string? context = null);
}
