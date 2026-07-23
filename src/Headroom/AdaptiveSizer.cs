namespace Headroom;

/// <summary>
/// Chooses how many items to keep (<c>K</c>) from a redundant collection, adapting to how diverse
/// the items are: highly repetitive input keeps fewer, highly diverse input keeps more.
/// <para>
/// This is a deliberately simplified port of the reference core's <c>compute_optimal_k</c>. The
/// original combines SimHash near-duplicate clustering, a Kneedle knee-point on a unique-bigram
/// coverage curve, and a zlib-ratio validation pass. Here we use the same <i>shape</i> — a
/// diversity ratio driving a keep-fraction, floored by <c>minK</c> and capped by <c>maxK</c> —
/// with distinct-item counting standing in for SimHash clustering. See <c>CLAUDE.md</c> §Fidelity.
/// </para>
/// </summary>
public static class AdaptiveSizer
{
    /// <summary>
    /// Compute the number of items to keep.
    /// </summary>
    /// <param name="items">The candidate item strings (one per element).</param>
    /// <param name="bias">
    /// A multiplier on the chosen budget (&gt;1 keeps more, &lt;1 keeps less). Defaults to 1.
    /// </param>
    /// <param name="minK">A hard floor on the result (applied even if it exceeds <paramref name="maxK"/>).</param>
    /// <param name="maxK">An upper bound; null means "no cap beyond the item count".</param>
    public static int ComputeOptimalK(
        IReadOnlyList<string> items,
        double bias = 1.0,
        int minK = 3,
        int? maxK = null)
    {
        int n = items.Count;
        int effectiveMax = maxK ?? n;

        // Small collections: keep everything (nothing worth sampling).
        if (n <= 8)
        {
            return n;
        }

        int uniqueCount = CountDistinct(items);

        // Near-total redundancy: a handful of distinct shapes — keep just those.
        if (uniqueCount <= 3)
        {
            return Math.Min(effectiveMax, Math.Max(minK, uniqueCount));
        }

        double diversity = (double)uniqueCount / n;          // 0..1
        double keepFraction = 0.3 + (0.7 * diversity);        // 0.3 (repetitive) .. 1.0 (all unique)
        int knee = Math.Max(minK, (int)Math.Floor(n * keepFraction));

        int k = Math.Max(minK, (int)Math.Floor(knee * bias));
        k = Math.Min(k, effectiveMax);

        // minK is a hard floor even when maxK is smaller (matches the reference behaviour).
        return Math.Max(minK, Math.Min(k, effectiveMax));
    }

    private static int CountDistinct(IReadOnlyList<string> items)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string item in items)
        {
            seen.Add(item.Trim());
        }

        return seen.Count;
    }
}
