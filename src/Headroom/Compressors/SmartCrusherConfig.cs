namespace Headroom.Compressors;

/// <summary>
/// Configuration for <see cref="SmartCrusher"/>. Field defaults match the reference project's
/// <c>SmartCrusherConfig</c>.
/// </summary>
public sealed record SmartCrusherConfig
{
    /// <summary>Arrays smaller than this are not analysed/crushed.</summary>
    public int MinItemsToAnalyze { get; init; } = 5;

    /// <summary>σ multiplier for change-point and numeric-anomaly detection.</summary>
    public double VarianceThreshold { get; init; } = 2.0;

    /// <summary>Target cap on kept items (also the adaptive-K upper bound).</summary>
    public int MaxItemsAfterCrush { get; init; } = 15;

    /// <summary>Keep the neighbourhoods around numeric change-points.</summary>
    public bool PreserveChangePoints { get; init; } = true;

    /// <summary>De-duplicate byte-identical items before sampling.</summary>
    public bool DedupIdenticalItems { get; init; } = true;

    /// <summary>Fraction of the budget drawn from the array head.</summary>
    public double FirstFraction { get; init; } = 0.3;

    /// <summary>Fraction of the budget drawn from the array tail.</summary>
    public double LastFraction { get; init; } = 0.15;

    /// <summary>Minimum byte-savings ratio for lossless compaction to win over the lossy path.</summary>
    public double LosslessMinSavingsRatio { get; init; } = 0.15;

    /// <summary>Emit the CCR dropped-row sentinel (and offload the original) on the lossy path.</summary>
    public bool EnableCcrMarker { get; init; } = true;

    /// <summary>
    /// Strict lossless mode: lossless compaction still applies, but the lossy row-drop path is
    /// forbidden. Output is always fully recoverable and never drops rows.
    /// </summary>
    public bool LosslessOnly { get; init; }

    /// <summary>Enable the lossless <c>csv-schema</c> compaction path.</summary>
    public bool EnableLosslessCompaction { get; init; } = true;
}
