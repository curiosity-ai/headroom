using LlmTornado.Chat;

namespace Headroom;

/// <summary>
/// The result of compressing a message list.
/// </summary>
/// <param name="Messages">The compressed messages — same shape as the input, fewer tokens.</param>
/// <param name="TokensBefore">Estimated token count of the input.</param>
/// <param name="TokensAfter">Estimated token count of the output.</param>
/// <param name="TransformsApplied">
/// The strategy tags of every compressor that modified a message (e.g. <c>"smart_crush:1"</c>).
/// </param>
public sealed record CompressionResult(
    IReadOnlyList<ChatMessage> Messages,
    int TokensBefore,
    int TokensAfter,
    IReadOnlyList<string> TransformsApplied)
{
    /// <summary>Tokens removed by compression (never negative).</summary>
    public int TokensSaved => Math.Max(0, TokensBefore - TokensAfter);

    /// <summary>Fraction of tokens removed: 0.0 = nothing saved, 1.0 = everything removed.</summary>
    public double CompressionRatio => TokensBefore > 0 ? (double)TokensSaved / TokensBefore : 0.0;
}
