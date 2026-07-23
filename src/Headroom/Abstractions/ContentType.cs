namespace Headroom.Abstractions;

/// <summary>
/// The kind of content a block of text holds, used to route it to the right compressor.
/// Mirrors the reference project's content taxonomy.
/// </summary>
public enum ContentType
{
    /// <summary>A JSON array (of objects) or object — handled by <c>SmartCrusher</c>.</summary>
    JsonArray,

    /// <summary>Source code (Python, JS, TS, Go, Rust, Java, C# …).</summary>
    SourceCode,

    /// <summary>grep / ripgrep style <c>file:line:content</c> search output.</summary>
    SearchResults,

    /// <summary>Compiler / test / lint build output.</summary>
    BuildOutput,

    /// <summary>Unified <c>git diff</c> output.</summary>
    GitDiff,

    /// <summary>HTML markup.</summary>
    Html,

    /// <summary>Generic prose — the fallback.</summary>
    PlainText,
}
