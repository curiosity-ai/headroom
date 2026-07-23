using System.Text;
using System.Text.RegularExpressions;
using Headroom.Abstractions;
using Headroom.Ccr;

namespace Headroom.Compressors;

/// <summary>Configuration for <see cref="SearchCompressor"/>. Defaults match the reference project.</summary>
public sealed record SearchCompressorConfig
{
    /// <summary>Maximum matches to keep per file.</summary>
    public int MaxMatchesPerFile { get; init; } = 5;

    /// <summary>Always keep the first match in each file.</summary>
    public bool AlwaysKeepFirst { get; init; } = true;

    /// <summary>Always keep the last match in each file.</summary>
    public bool AlwaysKeepLast { get; init; } = true;

    /// <summary>Global cap on total matches kept.</summary>
    public int MaxTotalMatches { get; init; } = 30;

    /// <summary>Maximum number of files to keep (least relevant dropped).</summary>
    public int MaxFiles { get; init; } = 15;

    /// <summary>Boost matches whose content looks like an error/warning.</summary>
    public bool BoostErrors { get; init; } = true;

    /// <summary>Emit a CCR retrieval marker when matches are dropped.</summary>
    public bool EnableCcr { get; init; } = true;

    /// <summary>Below this many matches the input is passed through.</summary>
    public int MinMatchesForCcr { get; init; } = 10;

    /// <summary>Skip the CCR marker if the output is already ≥ this fraction of the input.</summary>
    public double MinCompressionRatioForCcr { get; init; } = 0.8;
}

/// <summary>
/// Compresses grep/ripgrep-style <c>file:line:content</c> output: parses matches, groups them by
/// file, scores each by error/keyword/query relevance, keeps the top few per file (plus first and
/// last), and summarises the rest as <c>[... and N more matches in FILE]</c>.
/// <para>Concept-faithful port of the reference <c>search_compressor</c>.</para>
/// </summary>
public sealed partial class SearchCompressor : IContentCompressor
{
    private sealed class Match
    {
        public required int Line { get; init; }
        public required string Content { get; init; }
        public double Score { get; set; }
        public required string Raw { get; init; }
    }

    private static readonly string[] ErrorKeywords =
        ["error", "fail", "failed", "exception", "warning", "critical", "fatal", "panic"];

    private readonly SearchCompressorConfig _config;
    private readonly ICcrStore? _ccr;

    /// <summary>Create a search compressor.</summary>
    public SearchCompressor(SearchCompressorConfig? config = null, ICcrStore? ccr = null)
    {
        _config = config ?? new SearchCompressorConfig();
        _ccr = ccr;
    }

    /// <inheritdoc />
    public ContentType Handles => ContentType.SearchResults;

    /// <inheritdoc />
    public CompressionOutcome Compress(string content, string? context = null)
    {
        SortedDictionary<string, List<Match>> parsed = Parse(content);
        if (parsed.Count == 0)
        {
            return CompressionOutcome.PassThrough(content, "search:passthrough");
        }

        int originalCount = parsed.Values.Sum(m => m.Count);
        Score(parsed, context);
        SortedDictionary<string, List<Match>> selected = Select(parsed);
        int keptCount = selected.Values.Sum(m => m.Count);

        string body = FormatOutput(selected, parsed);
        string compressed = body;
        double ratio = (double)body.Length / Math.Max(1, content.Length);

        if (_config.EnableCcr
            && originalCount >= _config.MinMatchesForCcr
            && ratio < _config.MinCompressionRatioForCcr
            && _ccr is not null)
        {
            string key = Hashing.Md5Hex24(content);
            _ccr.Put(key, content);
            compressed +=
                $"\n[{originalCount} matches compressed to {keptCount}. Retrieve more: hash={key}]";
        }

        bool modified = !compressed.Equals(content, StringComparison.Ordinal);
        return new CompressionOutcome(compressed, content, modified, $"search:{originalCount}->{keptCount}");
    }

    private static SortedDictionary<string, List<Match>> Parse(string content)
    {
        var files = new SortedDictionary<string, List<Match>>(StringComparer.Ordinal);
        foreach (string rawLine in content.Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            System.Text.RegularExpressions.Match m = MatchLineRegex().Match(line);
            if (!m.Success)
            {
                continue;
            }

            string file = m.Groups["file"].Value;
            if (!int.TryParse(m.Groups["line"].Value, out int lineNo))
            {
                continue;
            }

            string body = m.Groups["content"].Value;
            if (!files.TryGetValue(file, out List<Match>? list))
            {
                list = [];
                files[file] = list;
            }

            list.Add(new Match { Line = lineNo, Content = body, Raw = line });
        }

        return files;
    }

    private void Score(SortedDictionary<string, List<Match>> files, string? context)
    {
        HashSet<string> contextWords = ExtractContextWords(context);
        foreach (List<Match> matches in files.Values)
        {
            foreach (Match match in matches)
            {
                string lower = match.Content.ToLowerInvariant();
                double score = 0.0;
                foreach (string word in contextWords)
                {
                    if (lower.Contains(word, StringComparison.Ordinal))
                    {
                        score += 0.3;
                    }
                }

                if (_config.BoostErrors)
                {
                    foreach (string kw in ErrorKeywords)
                    {
                        if (lower.Contains(kw, StringComparison.Ordinal))
                        {
                            score += 0.5;
                            break;
                        }
                    }
                }

                match.Score = Math.Min(1.0, score);
            }
        }
    }

    private SortedDictionary<string, List<Match>> Select(SortedDictionary<string, List<Match>> files)
    {
        // Rank files by total relevance; drop the least relevant beyond MaxFiles.
        var ranked = files
            .OrderByDescending(kv => kv.Value.Sum(m => m.Score))
            .ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .Take(_config.MaxFiles)
            .ToList();

        var allMatchStrings = files.Values.SelectMany(l => l).Select(m => m.Raw).ToList();
        int adaptiveTotal = AdaptiveSizer.ComputeOptimalK(
            allMatchStrings, 1.0, minK: 5, maxK: _config.MaxTotalMatches);

        var selected = new SortedDictionary<string, List<Match>>(StringComparer.Ordinal);
        int totalSelected = 0;
        foreach ((string file, List<Match> matches) in ranked)
        {
            if (totalSelected >= adaptiveTotal)
            {
                break;
            }

            int cap = Math.Min(_config.MaxMatchesPerFile, adaptiveTotal - totalSelected);
            var picked = new List<Match>();
            var seen = new HashSet<(int, string)>();

            void TryAdd(Match m)
            {
                if (picked.Count < cap && seen.Add((m.Line, m.Content)))
                {
                    picked.Add(m);
                }
            }

            if (_config.AlwaysKeepFirst && matches.Count > 0)
            {
                TryAdd(matches[0]);
            }

            if (_config.AlwaysKeepLast && matches.Count > 1)
            {
                TryAdd(matches[^1]);
            }

            foreach (Match m in matches.OrderByDescending(m => m.Score).ThenBy(m => m.Line))
            {
                if (picked.Count >= cap)
                {
                    break;
                }

                TryAdd(m);
            }

            picked.Sort((a, b) => a.Line.CompareTo(b.Line));
            selected[file] = picked;
            totalSelected += picked.Count;
        }

        return selected;
    }

    private static string FormatOutput(
        SortedDictionary<string, List<Match>> selected,
        SortedDictionary<string, List<Match>> original)
    {
        var sb = new StringBuilder();
        bool first = true;
        foreach ((string file, List<Match> matches) in selected)
        {
            foreach (Match m in matches)
            {
                if (!first)
                {
                    sb.Append('\n');
                }

                sb.Append(file).Append(':').Append(m.Line).Append(':').Append(m.Content);
                first = false;
            }

            int omitted = original[file].Count - matches.Count;
            if (omitted > 0)
            {
                if (!first)
                {
                    sb.Append('\n');
                }

                sb.Append($"[... and {omitted} more matches in {file}]");
                first = false;
            }
        }

        return sb.ToString();
    }

    private static HashSet<string> ExtractContextWords(string? context)
    {
        var words = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(context))
        {
            return words;
        }

        foreach (string token in context.ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.Length > 2)
            {
                words.Add(token);
            }
        }

        return words;
    }

    // file:line:content — file may include a Windows drive letter (C:\...).
    [GeneratedRegex(@"^(?<file>(?:[A-Za-z]:)?[^:]+):(?<line>\d+):(?<content>.*)$")]
    private static partial Regex MatchLineRegex();
}
