using Headroom.Abstractions;

namespace Headroom.Tokenization;

/// <summary>
/// Default token estimator: <c>ceil(chars / charsPerToken)</c> with <c>charsPerToken = 4</c>.
/// This is the same ~4-chars-per-token heuristic the reference project uses for its token-saved
/// estimates. It is provider-agnostic and dependency-free; swap in a real tokenizer via
/// <see cref="ITokenCounter"/> when exactness matters.
/// </summary>
public sealed class CharTokenCounter : ITokenCounter
{
    /// <summary>A shared default instance.</summary>
    public static readonly CharTokenCounter Instance = new();

    private readonly double _charsPerToken;

    /// <summary>Create a counter with a custom chars-per-token ratio (default 4).</summary>
    public CharTokenCounter(double charsPerToken = 4.0)
    {
        if (charsPerToken <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(charsPerToken), "Must be positive.");
        }

        _charsPerToken = charsPerToken;
    }

    /// <inheritdoc />
    public int Count(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        return (int)Math.Ceiling(text.Length / _charsPerToken);
    }
}
