namespace Headroom.Abstractions;

/// <summary>
/// Outcome of content-type detection: the detected <see cref="ContentType"/>, a confidence in
/// <c>[0,1]</c>, and type-specific metadata (e.g. <c>language</c> for code, <c>is_dict_array</c>
/// for JSON).
/// </summary>
/// <param name="ContentType">The detected content type.</param>
/// <param name="Confidence">Detection confidence in the range <c>0.0</c>–<c>1.0</c>.</param>
/// <param name="Metadata">Type-specific detail; never null (may be empty).</param>
public sealed record DetectionResult(
    ContentType ContentType,
    double Confidence,
    IReadOnlyDictionary<string, object> Metadata)
{
    /// <summary>Convenience factory with no metadata.</summary>
    public static DetectionResult Of(ContentType type, double confidence) =>
        new(type, confidence, EmptyMetadata);

    private static readonly IReadOnlyDictionary<string, object> EmptyMetadata =
        new Dictionary<string, object>();
}
