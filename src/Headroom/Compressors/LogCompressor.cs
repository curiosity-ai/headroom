using System.Text;
using System.Text.RegularExpressions;
using Headroom.Abstractions;
using Headroom.Ccr;

namespace Headroom.Compressors;

/// <summary>
/// Compresses build/test log output. Keeps errors and failures (with a little context), a bounded
/// set of de-duplicated warnings, collapsed stack traces and summary lines; drops the rest and
/// appends an omitted-count line. When enough is dropped, the original is offloaded to the CCR
/// store and a retrieval marker is appended.
/// <para>Concept-faithful port of the reference <c>log_compressor</c>.</para>
/// </summary>
public sealed partial class LogCompressor : IContentCompressor
{
    private enum Level
    {
        Error,
        Fail,
        Warn,
        Info,
        Debug,
        Trace,
        Unknown,
    }

    private sealed class LogLine
    {
        public required int Number { get; init; }
        public required string Content { get; init; }
        public Level Level { get; set; }
        public bool IsStackTrace { get; set; }
        public bool IsSummary { get; set; }
        public double Score { get; set; }
    }

    private readonly LogCompressorConfig _config;
    private readonly ICcrStore? _ccr;

    /// <summary>Create a log compressor.</summary>
    public LogCompressor(LogCompressorConfig? config = null, ICcrStore? ccr = null)
    {
        _config = config ?? new LogCompressorConfig();
        _ccr = ccr;
    }

    /// <inheritdoc />
    public ContentType Handles => ContentType.BuildOutput;

    /// <inheritdoc />
    public CompressionOutcome Compress(string content, string? context = null)
    {
        string[] lines = content.Split('\n');
        if (lines.Length < _config.MinLinesForCcr)
        {
            return CompressionOutcome.PassThrough(content, "log:passthrough");
        }

        List<LogLine> parsed = ParseLines(lines);
        SortedSet<int> selected = SelectLines(parsed);
        string body = FormatOutput(selected, parsed);

        string compressed = body;
        double ratio = (double)compressed.Length / Math.Max(1, content.Length);

        if (_config.EnableCcr
            && ratio < _config.MinCompressionRatioForCcr
            && _ccr is not null)
        {
            string key = Hashing.Md5Hex24(content);
            _ccr.Put(key, content);
            compressed +=
                $"\n[{lines.Length} lines compressed to {selected.Count}. Retrieve more: hash={key}]";
        }

        bool modified = compressed.Length != content.Length || !compressed.Equals(content, StringComparison.Ordinal);
        return new CompressionOutcome(compressed, content, modified, $"log:{lines.Length}->{selected.Count}");
    }

    private List<LogLine> ParseLines(string[] lines)
    {
        var result = new List<LogLine>(lines.Length);
        int traceRemaining = 0;
        for (int i = 0; i < lines.Length; i++)
        {
            string content = lines[i];
            var line = new LogLine
            {
                Number = i,
                Content = content,
                Level = ClassifyLevel(content),
                IsSummary = IsSummaryLine(content),
            };

            if (traceRemaining > 0 && IsFrameContinuation(content))
            {
                line.IsStackTrace = true;
                traceRemaining--;
            }
            else if (IsTraceOpener(content))
            {
                line.IsStackTrace = true;
                traceRemaining = _config.StackTraceMaxLines - 1;
            }
            else
            {
                traceRemaining = 0;
            }

            line.Score = ScoreLine(line);
            result.Add(line);
        }

        return result;
    }

    private SortedSet<int> SelectLines(List<LogLine> lines)
    {
        var allStrings = lines.ConvertAll(l => l.Content);
        int adaptiveMax = AdaptiveSizer.ComputeOptimalK(allStrings, 1.0, minK: 10, maxK: _config.MaxTotalLines);

        var errors = lines.FindAll(l => l.Level == Level.Error);
        var fails = lines.FindAll(l => l.Level == Level.Fail);
        var warnings = lines.FindAll(l => l.Level == Level.Warn);
        var summaries = lines.FindAll(l => l.IsSummary);

        var selected = new SortedSet<int>();
        foreach (int idx in SelectWithFirstLast(errors, _config.MaxErrors))
        {
            selected.Add(idx);
        }

        foreach (int idx in SelectWithFirstLast(fails, _config.MaxErrors))
        {
            selected.Add(idx);
        }

        List<LogLine> warns = _config.DedupeWarnings ? DedupeSimilar(warnings) : warnings;
        foreach (LogLine w in warns.Take(_config.MaxWarnings))
        {
            selected.Add(w.Number);
        }

        foreach (List<LogLine> trace in StackTraceRuns(lines).Take(_config.MaxStackTraces))
        {
            foreach (LogLine t in trace.Take(_config.StackTraceMaxLines))
            {
                selected.Add(t.Number);
            }
        }

        if (_config.KeepSummaryLines)
        {
            foreach (LogLine s in summaries)
            {
                selected.Add(s.Number);
            }
        }

        // Context window around every kept line.
        var withContext = new SortedSet<int>(selected);
        foreach (int idx in selected)
        {
            for (int d = -_config.ErrorContextLines; d <= _config.ErrorContextLines; d++)
            {
                int j = idx + d;
                if (j >= 0 && j < lines.Count)
                {
                    withContext.Add(j);
                }
            }
        }

        // Global cap: keep the highest-scoring lines.
        if (withContext.Count > adaptiveMax)
        {
            var top = withContext
                .OrderByDescending(i => lines[i].Score)
                .ThenBy(i => i)
                .Take(adaptiveMax);
            return new SortedSet<int>(top);
        }

        return withContext;
    }

    private IEnumerable<int> SelectWithFirstLast(List<LogLine> lines, int max)
    {
        if (lines.Count <= max)
        {
            return lines.ConvertAll(l => l.Number);
        }

        var picked = new SortedSet<int> { lines[0].Number, lines[^1].Number };
        foreach (LogLine l in lines.OrderByDescending(l => l.Score).ThenBy(l => l.Number))
        {
            if (picked.Count >= max)
            {
                break;
            }

            picked.Add(l.Number);
        }

        return picked;
    }

    private static List<LogLine> DedupeSimilar(List<LogLine> warnings)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<LogLine>();
        foreach (LogLine w in warnings)
        {
            if (seen.Add(NormalizeForDedupe(w.Content)))
            {
                result.Add(w);
            }
        }

        return result;
    }

    private static string NormalizeForDedupe(string content)
    {
        int split = content.IndexOfAny([':', '=']);
        string prefix = split >= 0 ? content[..split] : content;
        string suffix = split >= 0 ? content[split..] : string.Empty;
        suffix = DigitsRegex().Replace(suffix, "N");
        suffix = HexRegex().Replace(suffix, "ADDR");
        return prefix + suffix;
    }

    private static IEnumerable<List<LogLine>> StackTraceRuns(List<LogLine> lines)
    {
        List<LogLine>? run = null;
        foreach (LogLine l in lines)
        {
            if (l.IsStackTrace)
            {
                run ??= [];
                run.Add(l);
            }
            else if (run is not null)
            {
                yield return run;
                run = null;
            }
        }

        if (run is not null)
        {
            yield return run;
        }
    }

    private string FormatOutput(SortedSet<int> selected, List<LogLine> lines)
    {
        var sb = new StringBuilder();
        bool first = true;
        foreach (int idx in selected)
        {
            if (!first)
            {
                sb.Append('\n');
            }

            sb.Append(lines[idx].Content);
            first = false;
        }

        int omitted = lines.Count - selected.Count;
        if (omitted > 0)
        {
            int errs = lines.Count(l => l.Level == Level.Error);
            int fails = lines.Count(l => l.Level == Level.Fail);
            int warns = lines.Count(l => l.Level == Level.Warn);
            int infos = lines.Count(l => l.Level == Level.Info);
            var parts = new List<string>();
            if (errs > 0)
            {
                parts.Add($"{errs} ERROR");
            }

            if (fails > 0)
            {
                parts.Add($"{fails} FAIL");
            }

            if (warns > 0)
            {
                parts.Add($"{warns} WARN");
            }

            if (infos > 0)
            {
                parts.Add($"{infos} INFO");
            }

            sb.Append('\n').Append($"[{omitted} lines omitted: {string.Join(", ", parts)}]");
        }

        return sb.ToString();
    }

    private static double ScoreLine(LogLine line)
    {
        double levelScore = line.Level switch
        {
            Level.Error or Level.Fail => 1.0,
            Level.Warn => 0.5,
            Level.Info or Level.Unknown => 0.1,
            Level.Debug => 0.05,
            _ => 0.02,
        };
        double stack = line.IsStackTrace ? 0.3 : 0.0;
        double summary = line.IsSummary ? 0.4 : 0.0;
        return Math.Min(1.0, levelScore + stack + summary);
    }

    private static Level ClassifyLevel(string content)
    {
        if (ErrorWordRegex().IsMatch(content))
        {
            return Level.Error;
        }

        if (FailWordRegex().IsMatch(content))
        {
            return Level.Fail;
        }

        if (WarnWordRegex().IsMatch(content))
        {
            return Level.Warn;
        }

        if (InfoWordRegex().IsMatch(content))
        {
            return Level.Info;
        }

        if (DebugWordRegex().IsMatch(content))
        {
            return Level.Debug;
        }

        if (TraceWordRegex().IsMatch(content))
        {
            return Level.Trace;
        }

        return Level.Unknown;
    }

    private static bool IsSummaryLine(string content)
    {
        string t = content.TrimStart();
        if (t.StartsWith("===", StringComparison.Ordinal) || t.StartsWith("---", StringComparison.Ordinal))
        {
            return true;
        }

        if (SummaryCountRegex().IsMatch(t))
        {
            return true;
        }

        return t.StartsWith("TOTAL", StringComparison.Ordinal)
            || t.StartsWith("Total", StringComparison.Ordinal)
            || t.StartsWith("Summary", StringComparison.Ordinal);
    }

    private static bool IsTraceOpener(string content)
    {
        string t = content.TrimStart();
        return t.StartsWith("Traceback (most recent call last)", StringComparison.Ordinal)
            || t.StartsWith("File \"", StringComparison.Ordinal)
            || t.StartsWith("at ", StringComparison.Ordinal)
            || t.StartsWith("thread '", StringComparison.Ordinal)
            || t.StartsWith("stack backtrace:", StringComparison.Ordinal)
            || t.StartsWith("panic:", StringComparison.Ordinal)
            || t.StartsWith("goroutine ", StringComparison.Ordinal)
            || t.StartsWith("Caused by:", StringComparison.Ordinal)
            || RustFrameRegex().IsMatch(t);
    }

    private static bool IsFrameContinuation(string content)
    {
        if (content.Trim().Length == 0)
        {
            return false;
        }

        string t = content.TrimStart();
        return content.StartsWith(' ') || content.StartsWith('\t')
            || t.StartsWith("at ", StringComparison.Ordinal)
            || t.StartsWith("File \"", StringComparison.Ordinal)
            || t.StartsWith("Caused by:", StringComparison.Ordinal)
            || t.StartsWith("... ", StringComparison.Ordinal)
            || RustFrameRegex().IsMatch(t);
    }

    [GeneratedRegex(@"\b(ERROR|error|Error|FATAL|fatal|Fatal|CRITICAL|critical)\b")]
    private static partial Regex ErrorWordRegex();

    [GeneratedRegex(@"\b(FAIL|FAILED|fail|failed|Fail|Failed)\b")]
    private static partial Regex FailWordRegex();

    [GeneratedRegex(@"\b(WARN|WARNING|warn|warning|Warn|Warning)\b")]
    private static partial Regex WarnWordRegex();

    [GeneratedRegex(@"\b(INFO|info|Info)\b")]
    private static partial Regex InfoWordRegex();

    [GeneratedRegex(@"\b(DEBUG|debug|Debug)\b")]
    private static partial Regex DebugWordRegex();

    [GeneratedRegex(@"\b(TRACE|trace|Trace)\b")]
    private static partial Regex TraceWordRegex();

    [GeneratedRegex(@"^\d+ (passed|failed|skipped|error|warning)")]
    private static partial Regex SummaryCountRegex();

    [GeneratedRegex(@"^\d+:\s+0x[0-9a-fA-F]+")]
    private static partial Regex RustFrameRegex();

    [GeneratedRegex(@"\d+")]
    private static partial Regex DigitsRegex();

    [GeneratedRegex(@"0x[0-9a-fA-F]+")]
    private static partial Regex HexRegex();
}
