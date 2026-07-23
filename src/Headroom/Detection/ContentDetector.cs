using System.Text.Json;
using System.Text.RegularExpressions;
using Headroom.Abstractions;

namespace Headroom.Detection;

/// <summary>
/// Detects the <see cref="ContentType"/> of a block of text so the router can pick a compressor.
/// <para>
/// Ported from the reference project's content detector. Detection runs in a fixed priority order,
/// each stage gated by a confidence threshold; the first stage to clear its threshold wins:
/// JSON → diff (≥0.7) → HTML (≥0.7) → search (≥0.6) → build/log (≥0.5) → source code (≥0.5) →
/// plain text (fallback).
/// </para>
/// </summary>
public static partial class ContentDetector
{
    /// <summary>Detect the content type of <paramref name="content"/>.</summary>
    public static DetectionResult Detect(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return DetectionResult.Of(ContentType.PlainText, 0.0);
        }

        // 1. JSON (highest priority — SmartCrusher's domain). Returned unconditionally.
        DetectionResult? json = TryDetectJson(content);
        if (json is not null)
        {
            return json;
        }

        // 2. Diff.
        DetectionResult? diff = TryDetectDiff(content);
        if (diff is { Confidence: >= 0.7 })
        {
            return diff;
        }

        // 3. HTML.
        DetectionResult? html = TryDetectHtml(content);
        if (html is { Confidence: >= 0.7 })
        {
            return html;
        }

        // 4. Search results.
        DetectionResult? search = TryDetectSearch(content);
        if (search is { Confidence: >= 0.6 })
        {
            return search;
        }

        // 5. Build / log output.
        DetectionResult? log = TryDetectLog(content);
        if (log is { Confidence: >= 0.5 })
        {
            return log;
        }

        // 6. Source code.
        DetectionResult? code = TryDetectCode(content);
        if (code is { Confidence: >= 0.5 })
        {
            return code;
        }

        // 7. Fallback.
        return DetectionResult.Of(ContentType.PlainText, 0.5);
    }

    /// <summary>True if the content is a JSON array whose every element is an object.</summary>
    public static bool IsJsonArrayOfObjects(string content)
    {
        DetectionResult result = Detect(content);
        return result.ContentType == ContentType.JsonArray
            && result.Metadata.TryGetValue("is_dict_array", out object? v)
            && v is true;
    }

    // ── JSON ────────────────────────────────────────────────────────────────────────────────

    private const double JsonMinBulkFraction = 0.6;

    private static DetectionResult? TryDetectJson(string content)
    {
        string stripped = content.Trim();
        if (stripped.Length == 0)
        {
            return null;
        }

        if (TryParseJson(stripped, out JsonElement value))
        {
            return ClassifyJsonValue(value);
        }

        // Not pure JSON. Handle the whitespace-separated web_search shape ({...} {...}) first.
        if (stripped.StartsWith('{'))
        {
            List<JsonElement>? items = TryDecodeConcatenated(stripped);
            if (items is { Count: >= 2 } && items.TrueForAll(i => i.ValueKind == JsonValueKind.Object))
            {
                return new DetectionResult(
                    ContentType.JsonArray,
                    1.0,
                    new Dictionary<string, object>
                    {
                        ["item_count"] = items.Count,
                        ["is_dict_array"] = true,
                        ["concatenated"] = true,
                    });
            }
        }

        // A JSON value wrapped in a little surrounding text: accept when the decoded value is the
        // bulk of the content (a harness shell / "Exit code:" prefix), not a prose fragment.
        int braceIdx = stripped.IndexOf('{');
        int bracketIdx = stripped.IndexOf('[');
        int start = MinNonNegative(braceIdx, bracketIdx);
        if (start < 0)
        {
            return null;
        }

        if (TryRawDecode(stripped, start, out JsonElement wrapped, out int consumed)
            && consumed >= stripped.Length * JsonMinBulkFraction)
        {
            return ClassifyJsonValue(wrapped);
        }

        return null;
    }

    private static DetectionResult? ClassifyJsonValue(JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Array:
            {
                int count = value.GetArrayLength();
                bool isDictArray = count > 0
                    && value.EnumerateArray().All(e => e.ValueKind == JsonValueKind.Object);
                return new DetectionResult(
                    ContentType.JsonArray,
                    isDictArray ? 1.0 : 0.8,
                    new Dictionary<string, object>
                    {
                        ["item_count"] = count,
                        ["is_dict_array"] = isDictArray,
                    });
            }

            case JsonValueKind.Object:
                return new DetectionResult(
                    ContentType.JsonArray,
                    0.9,
                    new Dictionary<string, object>
                    {
                        ["is_dict_array"] = false,
                        ["is_object"] = true,
                    });

            default:
                // Bare scalars (42, "s", true) are not structured data worth routing as JSON.
                return null;
        }
    }

    private static bool TryParseJson(string text, out JsonElement value)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            value = doc.RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            value = default;
            return false;
        }
    }

    private static bool TryRawDecode(string text, int start, out JsonElement value, out int endExclusive)
    {
        value = default;
        endExclusive = 0;
        try
        {
            var reader = new Utf8JsonReader(
                System.Text.Encoding.UTF8.GetBytes(text[start..]),
                new JsonReaderOptions { AllowTrailingCommas = false });
            using var doc = JsonDocument.ParseValue(ref reader);
            value = doc.RootElement.Clone();
            // BytesConsumed counts UTF-8 bytes from `start`; approximate char end for ASCII-heavy
            // wrappers. For the bulk-fraction gate this is close enough.
            endExclusive = start + (int)reader.BytesConsumed;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static List<JsonElement>? TryDecodeConcatenated(string content)
    {
        // A single Utf8JsonReader over the whole buffer enforces single-value semantics, so we
        // parse one value at a time and re-slice past the bytes consumed.
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(content);
        var items = new List<JsonElement>();
        int offset = 0;
        while (offset < bytes.Length)
        {
            while (offset < bytes.Length && char.IsWhiteSpace((char)bytes[offset]))
            {
                offset++;
            }

            if (offset >= bytes.Length)
            {
                break;
            }

            var reader = new Utf8JsonReader(bytes.AsSpan(offset));
            try
            {
                if (!reader.Read())
                {
                    break;
                }

                using var doc = JsonDocument.ParseValue(ref reader);
                items.Add(doc.RootElement.Clone());
                offset += (int)reader.BytesConsumed;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        return items.Count > 0 ? items : null;
    }

    private static int MinNonNegative(int a, int b)
    {
        if (a < 0)
        {
            return b;
        }

        if (b < 0)
        {
            return a;
        }

        return Math.Min(a, b);
    }

    // ── Diff ────────────────────────────────────────────────────────────────────────────────

    private static DetectionResult? TryDetectDiff(string content)
    {
        string[] lines = content.Split('\n');
        int headerMatches = 0;
        int changeMatches = 0;
        int scan = Math.Min(lines.Length, 500);
        for (int i = 0; i < scan; i++)
        {
            if (DiffHeaderRegex().IsMatch(lines[i]))
            {
                headerMatches++;
            }

            if (DiffChangeRegex().IsMatch(lines[i]))
            {
                changeMatches++;
            }
        }

        if (headerMatches == 0)
        {
            return null;
        }

        double confidence = Math.Min(1.0, 0.5 + (headerMatches * 0.2) + (changeMatches * 0.05));
        return new DetectionResult(
            ContentType.GitDiff,
            confidence,
            new Dictionary<string, object>
            {
                ["header_matches"] = headerMatches,
                ["change_lines"] = changeMatches,
            });
    }

    // ── HTML ────────────────────────────────────────────────────────────────────────────────

    private static DetectionResult? TryDetectHtml(string content)
    {
        string sample = content.Length > 3000 ? content[..3000] : content;

        bool hasDoctype = HtmlDoctypeRegex().IsMatch(sample);
        bool hasHtmlTag = HtmlTagRegex().IsMatch(sample);
        bool hasHead = HtmlHeadRegex().IsMatch(sample);
        bool hasBody = HtmlBodyRegex().IsMatch(sample);
        int structural = HtmlStructuralRegex().Matches(sample).Count;

        if (!hasDoctype && !hasHtmlTag && structural < 3)
        {
            return null;
        }

        double confidence = 0.0;
        if (hasDoctype)
        {
            confidence += 0.5;
        }

        if (hasHtmlTag)
        {
            confidence += 0.3;
        }

        if (hasHead)
        {
            confidence += 0.1;
        }

        if (hasBody)
        {
            confidence += 0.1;
        }

        confidence += Math.Min(0.3, structural * 0.03);
        confidence = Math.Min(1.0, confidence);
        if (confidence < 0.5)
        {
            return null;
        }

        return new DetectionResult(
            ContentType.Html,
            confidence,
            new Dictionary<string, object>
            {
                ["has_doctype"] = hasDoctype,
                ["has_html_tag"] = hasHtmlTag,
                ["structural_tags"] = structural,
            });
    }

    // ── Search ──────────────────────────────────────────────────────────────────────────────

    private static DetectionResult? TryDetectSearch(string content)
    {
        string[] lines = content.Split('\n');
        int scan = Math.Min(lines.Length, 100);
        int matching = 0;
        int nonEmpty = 0;
        for (int i = 0; i < scan; i++)
        {
            string line = lines[i];
            if (line.Trim().Length == 0)
            {
                continue;
            }

            nonEmpty++;
            if (SearchResultRegex().IsMatch(line))
            {
                matching++;
            }
        }

        if (matching == 0 || nonEmpty == 0)
        {
            return null;
        }

        double ratio = (double)matching / nonEmpty;
        if (ratio < 0.3)
        {
            return null;
        }

        double confidence = Math.Min(1.0, 0.4 + (ratio * 0.6));
        return new DetectionResult(
            ContentType.SearchResults,
            confidence,
            new Dictionary<string, object>
            {
                ["matching_lines"] = matching,
                ["total_lines"] = nonEmpty,
            });
    }

    // ── Log / build ─────────────────────────────────────────────────────────────────────────

    private static readonly Regex[] LogPatterns =
    [
        new(@"\b(ERROR|FAIL|FAILED|FATAL|CRITICAL)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\b(WARN|WARNING)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\b(INFO|DEBUG|TRACE)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"^\s*\d{4}-\d{2}-\d{2}", RegexOptions.Compiled),
        new(@"^\s*\[\d{2}:\d{2}:\d{2}\]", RegexOptions.Compiled),
        new(@"^={3,}|^-{3,}", RegexOptions.Compiled),
        new(@"^\s*PASSED|^\s*FAILED|^\s*SKIPPED", RegexOptions.Compiled),
        new(@"^npm ERR!|^yarn error|^cargo error", RegexOptions.Compiled),
        new(@"Traceback \(most recent call last\)", RegexOptions.Compiled),
        new(@"^\w*(Error|Exception):", RegexOptions.Compiled),
        new(@"^\s*at\s+[\w.$/]+\(", RegexOptions.Compiled),
        new(@"^(panic|fatal error): ", RegexOptions.Compiled),
        new(@"^thread '[^']*' panicked at", RegexOptions.Compiled),
        new(@"^Unhandled exception\.", RegexOptions.Compiled),
        new(@"^\s*at .+\) in .+:line \d+", RegexOptions.Compiled),
        new(@"^Caused by: ", RegexOptions.Compiled),
    ];

    private static DetectionResult? TryDetectLog(string content)
    {
        string[] lines = content.Split('\n');
        int scan = Math.Min(lines.Length, 200);
        int patternMatches = 0;
        int errorMatches = 0;
        int nonEmpty = 0;
        for (int i = 0; i < scan; i++)
        {
            string line = lines[i];
            if (line.Trim().Length > 0)
            {
                nonEmpty++;
            }

            for (int p = 0; p < LogPatterns.Length; p++)
            {
                if (LogPatterns[p].IsMatch(line))
                {
                    patternMatches++;
                    if (p < 2)
                    {
                        errorMatches++;
                    }

                    break;
                }
            }
        }

        if (patternMatches == 0 || nonEmpty == 0)
        {
            return null;
        }

        double ratio = (double)patternMatches / nonEmpty;
        if (ratio < 0.1)
        {
            return null;
        }

        double confidence = Math.Min(1.0, 0.3 + (ratio * 0.5) + (errorMatches * 0.05));
        return new DetectionResult(
            ContentType.BuildOutput,
            confidence,
            new Dictionary<string, object>
            {
                ["pattern_matches"] = patternMatches,
                ["error_matches"] = errorMatches,
                ["total_lines"] = nonEmpty,
            });
    }

    // ── Source code ─────────────────────────────────────────────────────────────────────────

    private static readonly (string Language, Regex[] Patterns)[] CodePatterns =
    [
        ("python",
        [
            new(@"^\s*(def|class|import|from|async def)\s+\w+", RegexOptions.Compiled),
            new(@"^\s*@\w+", RegexOptions.Compiled),
            new(@"^\s*""""""", RegexOptions.Compiled),
            new(@"^\s*if __name__\s*==", RegexOptions.Compiled),
        ]),
        ("javascript",
        [
            new(@"^\s*(function|const|let|var|class|import|export)\s+", RegexOptions.Compiled),
            new(@"^\s*(async\s+function|=>\s*\{)", RegexOptions.Compiled),
            new(@"^\s*module\.exports", RegexOptions.Compiled),
        ]),
        ("typescript",
        [
            new(@"^\s*(interface|type|enum|namespace)\s+\w+", RegexOptions.Compiled),
            new(@"^:\s*(string|number|boolean|any|void)\b", RegexOptions.Compiled),
        ]),
        ("go",
        [
            new(@"^\s*(func|type|package|import)\s+", RegexOptions.Compiled),
            new(@"^\s*func\s+\([^)]+\)\s+\w+", RegexOptions.Compiled),
        ]),
        ("rust",
        [
            new(@"^\s*(fn|struct|enum|impl|mod|use|pub)\s+", RegexOptions.Compiled),
            new(@"^\s*#\[", RegexOptions.Compiled),
        ]),
        ("java",
        [
            new(@"^\s*(public|private|protected)\s+(class|interface|enum)", RegexOptions.Compiled),
            new(@"^\s*@\w+", RegexOptions.Compiled),
            new(@"^\s*package\s+[\w.]+;", RegexOptions.Compiled),
        ]),
        ("csharp",
        [
            new(@"^\s*using\s+[\w.]+\s*;", RegexOptions.Compiled),
            new(@"^\s*namespace\s+[\w.]+", RegexOptions.Compiled),
            new(@"^\s*(public|private|protected|internal|sealed|static|abstract|partial)\s+(class|struct|record|interface|enum)\b", RegexOptions.Compiled),
            new(@"\b(get|set|init);", RegexOptions.Compiled),
        ]),
    ];

    private static DetectionResult? TryDetectCode(string content)
    {
        string[] lines = content.Split('\n');
        int scan = Math.Min(lines.Length, 100);

        // Preserve first-appearance order so ties resolve like the reference (first wins).
        var scores = new Dictionary<string, int>(StringComparer.Ordinal);
        var order = new List<string>();
        int nonEmpty = 0;
        for (int i = 0; i < scan; i++)
        {
            string line = lines[i];
            if (line.Trim().Length > 0)
            {
                nonEmpty++;
            }

            foreach ((string lang, Regex[] patterns) in CodePatterns)
            {
                foreach (Regex pattern in patterns)
                {
                    if (pattern.IsMatch(line))
                    {
                        if (!scores.TryAdd(lang, 1))
                        {
                            scores[lang]++;
                        }
                        else
                        {
                            order.Add(lang);
                        }

                        break;
                    }
                }
            }
        }

        if (scores.Count == 0)
        {
            return null;
        }

        string bestLang = order[0];
        int bestScore = scores[bestLang];
        foreach (string lang in order)
        {
            if (scores[lang] > bestScore)
            {
                bestLang = lang;
                bestScore = scores[lang];
            }
        }

        if (bestScore < 3)
        {
            return null;
        }

        double ratio = (double)bestScore / Math.Max(nonEmpty, 1);
        double confidence = Math.Min(1.0, 0.4 + (ratio * 0.4) + (bestScore * 0.02));
        return new DetectionResult(
            ContentType.SourceCode,
            confidence,
            new Dictionary<string, object>
            {
                ["language"] = bestLang,
                ["pattern_matches"] = bestScore,
            });
    }

    // ── Compiled regexes ────────────────────────────────────────────────────────────────────

    [GeneratedRegex(@"^(diff --git|diff --combined |diff --cc |--- a/|@@\s+-\d+,\d+\s+\+\d+,\d+\s+@@|@@@+\s+-\d+(?:,\d+)?\s+(?:-\d+(?:,\d+)?\s+)+\+\d+(?:,\d+)?\s+@@@+)")]
    private static partial Regex DiffHeaderRegex();

    [GeneratedRegex(@"^[+-][^+-]")]
    private static partial Regex DiffChangeRegex();

    [GeneratedRegex(@"^[^\s:]+:\d+:")]
    private static partial Regex SearchResultRegex();

    [GeneratedRegex(@"^\s*<!doctype\s+html", RegexOptions.IgnoreCase)]
    private static partial Regex HtmlDoctypeRegex();

    [GeneratedRegex(@"<html[\s>]", RegexOptions.IgnoreCase)]
    private static partial Regex HtmlTagRegex();

    [GeneratedRegex(@"<head[\s>]", RegexOptions.IgnoreCase)]
    private static partial Regex HtmlHeadRegex();

    [GeneratedRegex(@"<body[\s>]", RegexOptions.IgnoreCase)]
    private static partial Regex HtmlBodyRegex();

    [GeneratedRegex(@"<(div|span|script|style|link|meta|nav|header|footer|aside|article|section|main)[\s>]", RegexOptions.IgnoreCase)]
    private static partial Regex HtmlStructuralRegex();
}
