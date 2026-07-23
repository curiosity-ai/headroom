using Headroom.Compressors;

namespace Headroom;

/// <summary>
/// User-facing options controlling what gets compressed and how aggressively. Mirrors the
/// reference project's <c>CompressConfig</c> guardrails.
/// </summary>
public sealed record HeadroomOptions
{
    /// <summary>
    /// Compress user messages too. Off by default: for coding-agent workloads user turns are
    /// short and important; the bulk lives in tool/assistant outputs.
    /// </summary>
    public bool CompressUserMessages { get; init; }

    /// <summary>Compress system messages. On by default.</summary>
    public bool CompressSystemMessages { get; init; } = true;

    /// <summary>Compress plain prose (via the extractive text crusher). Off by default.</summary>
    public bool CompressPlainText { get; init; }

    /// <summary>Compress source code. Off by default — agents usually need exact syntax.</summary>
    public bool CompressSourceCode { get; init; }

    /// <summary>
    /// Number of trailing messages to leave untouched (the active conversation). The provider
    /// KV-cache keeps hitting on this tail. Default 4; set 0 to compress everything.
    /// </summary>
    public int ProtectRecent { get; init; } = 4;

    /// <summary>
    /// Messages whose estimated token count is below this floor are left unchanged. Default 60
    /// (~250 characters), matching the reference's ~250-token guidance scaled to the estimator.
    /// </summary>
    public int MinTokensToCompress { get; init; } = 60;

    /// <summary>Per-compressor configuration.</summary>
    public SmartCrusherConfig? SmartCrusher { get; init; }

    /// <summary>Per-compressor configuration.</summary>
    public LogCompressorConfig? Log { get; init; }

    /// <summary>Per-compressor configuration.</summary>
    public SearchCompressorConfig? Search { get; init; }

    /// <summary>Per-compressor configuration.</summary>
    public DiffCompressorConfig? Diff { get; init; }

    /// <summary>Per-compressor configuration.</summary>
    public TextCrusherConfig? Text { get; init; }
}
