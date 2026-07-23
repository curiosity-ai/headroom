namespace Headroom.Abstractions;

/// <summary>
/// The result of running a single content-aware compressor over one block of text.
/// </summary>
/// <param name="Compressed">The compressed text.</param>
/// <param name="Original">The original text, verbatim.</param>
/// <param name="WasModified"><c>true</c> if <see cref="Compressed"/> differs from the input.</param>
/// <param name="Strategy">
/// A short strategy tag describing what happened (e.g. <c>"smart_crush:12-&gt;15"</c>,
/// <c>"log_compress"</c>, <c>"passthrough"</c>). Used to build the transforms-applied list.
/// </param>
public sealed record CompressionOutcome(
    string Compressed,
    string Original,
    bool WasModified,
    string Strategy)
{
    /// <summary>A no-op outcome: the content passed through unchanged.</summary>
    public static CompressionOutcome PassThrough(string content, string strategy = "passthrough") =>
        new(content, content, WasModified: false, strategy);
}
