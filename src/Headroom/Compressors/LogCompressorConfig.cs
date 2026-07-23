namespace Headroom.Compressors;

/// <summary>Configuration for <see cref="LogCompressor"/>. Defaults match the reference project.</summary>
public sealed record LogCompressorConfig
{
    /// <summary>Maximum error lines to keep (first/last always preferred).</summary>
    public int MaxErrors { get; init; } = 10;

    /// <summary>Lines of context to keep on each side of a kept line.</summary>
    public int ErrorContextLines { get; init; } = 3;

    /// <summary>Maximum warning lines to keep.</summary>
    public int MaxWarnings { get; init; } = 5;

    /// <summary>Collapse near-identical warnings to their first occurrence.</summary>
    public bool DedupeWarnings { get; init; } = true;

    /// <summary>Maximum stack traces to retain.</summary>
    public int MaxStackTraces { get; init; } = 3;

    /// <summary>Maximum lines to keep per stack trace.</summary>
    public int StackTraceMaxLines { get; init; } = 20;

    /// <summary>Keep test/build summary lines (e.g. <c>=== 3 passed ===</c>).</summary>
    public bool KeepSummaryLines { get; init; } = true;

    /// <summary>Global cap on total kept lines.</summary>
    public int MaxTotalLines { get; init; } = 100;

    /// <summary>Emit a CCR retrieval marker when content is dropped.</summary>
    public bool EnableCcr { get; init; } = true;

    /// <summary>Below this many lines the log is passed through unchanged.</summary>
    public int MinLinesForCcr { get; init; } = 50;

    /// <summary>Skip the CCR marker if the output is already ≥ this fraction of the input.</summary>
    public double MinCompressionRatioForCcr { get; init; } = 0.5;
}
