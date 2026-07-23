using System.Text.RegularExpressions;
using Headroom.Abstractions;

namespace Headroom.Compressors;

/// <summary>Configuration for <see cref="TextCrusher"/>. Defaults match the reference project.</summary>
public sealed record TextCrusherConfig
{
    /// <summary>Fraction of characters to keep (0.5 = keep about half).</summary>
    public double TargetRatio { get; init; } = 0.5;

    /// <summary>Weight of a segment's recency (position toward the end).</summary>
    public double RecencyWeight { get; init; } = 1.0;

    /// <summary>Weight of a segment's relevance to the query.</summary>
    public double RelevanceWeight { get; init; } = 2.0;

    /// <summary>Weight of a segment's salience (errors, identifiers, numbers).</summary>
    public double SalienceWeight { get; init; } = 1.5;

    /// <summary>Segments shorter than this are down-weighted.</summary>
    public int MinSegmentChars { get; init; } = 12;

    /// <summary>Skip a candidate if this fraction of its shingles is already covered.</summary>
    public double NearDupThreshold { get; init; } = 0.85;

    /// <summary>Below this many segments the text is passed through unchanged.</summary>
    public int MinSegmentsForCrush { get; init; } = 6;
}

/// <summary>
/// Extractive prose compressor: splits text into sentence-ish segments, scores each by
/// recency + BM25 relevance to the query + salience, and keeps the highest-scoring segments (in
/// original order) up to a target character budget, skipping near-duplicates. This is the
/// dependency-free fallback for generic text — the reference's request-path alternative to the
/// ML <c>Kompress</c> model.
/// </summary>
public sealed partial class TextCrusher : IContentCompressor
{
    private static readonly string[] SalienceKeywords =
        ["error", "exception", "failed", "failure", "fail", "warning", "traceback", "assert", "todo", "fixme"];

    private readonly TextCrusherConfig _config;

    /// <summary>Create a text crusher.</summary>
    public TextCrusher(TextCrusherConfig? config = null) => _config = config ?? new TextCrusherConfig();

    /// <inheritdoc />
    public ContentType Handles => ContentType.PlainText;

    /// <inheritdoc />
    public CompressionOutcome Compress(string content, string? context = null)
    {
        double ratio = Math.Clamp(_config.TargetRatio, 0.05, 1.0);
        List<string> segments = SplitSegments(content);
        if (segments.Count < _config.MinSegmentsForCrush)
        {
            return CompressionOutcome.PassThrough(content, "text:passthrough");
        }

        int n = segments.Count;
        int totalChars = segments.Sum(s => s.Length);
        int targetChars = Math.Max(1, (int)Math.Floor(totalChars * ratio));

        var segTokens = segments.ConvertAll(Tokenize);
        double[] relevance = Bm25(segTokens, context);

        var scores = new double[n];
        for (int i = 0; i < n; i++)
        {
            double recency = (double)(i + 1) / n;
            int salient = segTokens[i].Count(IsSalient);
            double salience = (double)salient / (segTokens[i].Count + 1);
            double score = (_config.RecencyWeight * recency)
                + (_config.RelevanceWeight * relevance[i])
                + (_config.SalienceWeight * salience);
            if (segments[i].Length < _config.MinSegmentChars)
            {
                score *= 0.25;
            }

            scores[i] = score;
        }

        var order = Enumerable.Range(0, n).OrderByDescending(i => scores[i]).ThenBy(i => i).ToList();
        var kept = new bool[n];
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int keptChars = 0;
        int keptCount = 0;
        foreach (int i in order)
        {
            if (keptChars >= targetChars)
            {
                break;
            }

            List<string> shingles = Shingles(segTokens[i], 3);
            if (shingles.Count > 0)
            {
                int covered = shingles.Count(s => seen.Contains(s));
                if ((double)covered / shingles.Count >= _config.NearDupThreshold)
                {
                    continue;
                }
            }

            kept[i] = true;
            keptCount++;
            foreach (string s in shingles)
            {
                seen.Add(s);
            }

            keptChars += segments[i].Length;
        }

        if (keptCount == 0 || keptCount == n)
        {
            return CompressionOutcome.PassThrough(content, "text:passthrough");
        }

        var outSegments = new List<string>();
        for (int i = 0; i < n; i++)
        {
            if (kept[i])
            {
                outSegments.Add(segments[i]);
            }
        }

        string compressed = string.Join('\n', outSegments);
        return new CompressionOutcome(compressed, content, WasModified: true, $"text:{n}->{keptCount}");
    }

    private static List<string> SplitSegments(string content)
    {
        var segments = new List<string>();
        foreach (string line in content.Split('\n'))
        {
            string trimmedLine = line.Trim();
            if (trimmedLine.Length == 0)
            {
                continue;
            }

            foreach (string piece in SentenceRegex().Split(trimmedLine))
            {
                string p = piece.Trim();
                if (p.Length > 0)
                {
                    segments.Add(p);
                }
            }
        }

        return segments;
    }

    private static List<string> Tokenize(string text)
    {
        var tokens = new List<string>();
        foreach (System.Text.RegularExpressions.Match m in TokenRegex().Matches(text))
        {
            tokens.Add(m.Value.ToLowerInvariant());
        }

        return tokens;
    }

    private static double[] Bm25(List<List<string>> docs, string? query)
    {
        int n = docs.Count;
        var scores = new double[n];
        if (string.IsNullOrWhiteSpace(query))
        {
            return scores;
        }

        var queryTokens = Tokenize(query).ToHashSet(StringComparer.Ordinal);
        if (queryTokens.Count == 0)
        {
            return scores;
        }

        double avgLen = docs.Average(d => d.Count);
        if (avgLen <= 0)
        {
            return scores;
        }

        const double K1 = 1.2;
        const double B = 0.75;

        var df = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (string term in queryTokens)
        {
            int count = docs.Count(d => d.Contains(term));
            df[term] = count;
        }

        double max = 0.0;
        for (int i = 0; i < n; i++)
        {
            var freq = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (string tok in docs[i])
            {
                if (queryTokens.Contains(tok))
                {
                    freq[tok] = freq.GetValueOrDefault(tok) + 1;
                }
            }

            double score = 0.0;
            foreach ((string term, int f) in freq)
            {
                int d = df[term];
                double idf = Math.Log(((n - d + 0.5) / (d + 0.5)) + 1.0);
                double denom = f + (K1 * (1 - B + (B * docs[i].Count / avgLen)));
                score += idf * (f * (K1 + 1)) / denom;
            }

            scores[i] = score;
            max = Math.Max(max, score);
        }

        if (max > 0)
        {
            for (int i = 0; i < n; i++)
            {
                scores[i] /= max;
            }
        }

        return scores;
    }

    private static bool IsSalient(string word)
    {
        if (word.Any(char.IsDigit))
        {
            return true;
        }

        string trimmed = word.Trim('.', ',', ';', ':', '!', '?', '(', ')', '[', ']');
        if (SalienceKeywords.Contains(trimmed.ToLowerInvariant()))
        {
            return true;
        }

        if (word.Length >= 2 && word.All(c => !char.IsLetter(c) || char.IsUpper(c)) && word.Any(char.IsLetter))
        {
            return true;
        }

        return word.Contains('.') && DottedIdentifierRegex().IsMatch(word);
    }

    private static List<string> Shingles(List<string> words, int k)
    {
        var result = new List<string>();
        if (words.Count >= k)
        {
            for (int i = 0; i + k <= words.Count; i++)
            {
                result.Add(string.Join('\u0001', words.Skip(i).Take(k)));
            }
        }
        else
        {
            for (int size = 1; size <= words.Count; size++)
            {
                for (int i = 0; i + size <= words.Count; i++)
                {
                    result.Add(string.Join('\u0001', words.Skip(i).Take(size)));
                }
            }
        }

        return result;
    }

    [GeneratedRegex(@"(?<=[.!?])\s+")]
    private static partial Regex SentenceRegex();

    [GeneratedRegex(@"[\p{L}\p{N}_]+")]
    private static partial Regex TokenRegex();

    [GeneratedRegex(@"^[A-Za-z_][\w]*(\.[A-Za-z_][\w]*)+$")]
    private static partial Regex DottedIdentifierRegex();
}
