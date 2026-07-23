using Headroom.Abstractions;
using Headroom.Ccr;
using Headroom.Compressors;
using Headroom.Detection;

namespace Headroom.Routing;

/// <summary>
/// Detects the content type of a block of text and routes it to the matching compressor. HTML and
/// source code fall through to the prose crusher (HTML content-extraction is out of scope; code is
/// deliberately left intact by default so agents keep exact syntax).
/// </summary>
public sealed class ContentRouter
{
    private readonly SmartCrusher _smartCrusher;
    private readonly LogCompressor _logCompressor;
    private readonly SearchCompressor _searchCompressor;
    private readonly DiffCompressor _diffCompressor;
    private readonly TextCrusher _textCrusher;
    private readonly bool _compressPlainText;
    private readonly bool _compressSourceCode;

    /// <summary>Create a router wired to the given store and configuration.</summary>
    public ContentRouter(
        ICcrStore? ccr = null,
        SmartCrusherConfig? smartCrusherConfig = null,
        LogCompressorConfig? logConfig = null,
        SearchCompressorConfig? searchConfig = null,
        DiffCompressorConfig? diffConfig = null,
        TextCrusherConfig? textConfig = null,
        bool compressPlainText = false,
        bool compressSourceCode = false)
    {
        _smartCrusher = new SmartCrusher(smartCrusherConfig, ccr);
        _logCompressor = new LogCompressor(logConfig, ccr);
        _searchCompressor = new SearchCompressor(searchConfig, ccr);
        _diffCompressor = new DiffCompressor(diffConfig, ccr);
        _textCrusher = new TextCrusher(textConfig);
        _compressPlainText = compressPlainText;
        _compressSourceCode = compressSourceCode;
    }

    /// <summary>Detect and compress <paramref name="content"/>, biasing toward <paramref name="context"/>.</summary>
    public CompressionOutcome Route(string content, string? context = null)
    {
        DetectionResult detection = ContentDetector.Detect(content);
        return Route(content, detection, context);
    }

    /// <summary>Compress using a pre-computed <paramref name="detection"/>.</summary>
    public CompressionOutcome Route(string content, DetectionResult detection, string? context = null)
    {
        return detection.ContentType switch
        {
            ContentType.JsonArray => _smartCrusher.Compress(content, context),
            ContentType.BuildOutput => _logCompressor.Compress(content, context),
            ContentType.SearchResults => _searchCompressor.Compress(content, context),
            ContentType.GitDiff => _diffCompressor.Compress(content, context),
            ContentType.PlainText when _compressPlainText => _textCrusher.Compress(content, context),
            ContentType.SourceCode when _compressSourceCode => _textCrusher.Compress(content, context),
            ContentType.Html when _compressPlainText => _textCrusher.Compress(content, context),
            _ => CompressionOutcome.PassThrough(content),
        };
    }
}
