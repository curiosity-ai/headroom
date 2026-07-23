namespace Headroom.Abstractions;

/// <summary>
/// Estimates the token cost of a string. Compression decisions and savings metrics are computed
/// against this estimate.
/// </summary>
public interface ITokenCounter
{
    /// <summary>Estimate the number of tokens in <paramref name="text"/>.</summary>
    int Count(string? text);
}
