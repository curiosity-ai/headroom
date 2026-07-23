using System.Text;
using System.Text.RegularExpressions;
using Headroom.Abstractions;
using Headroom.Ccr;

namespace Headroom.Compressors;

/// <summary>Configuration for <see cref="DiffCompressor"/>. Defaults match the reference project.</summary>
public sealed record DiffCompressorConfig
{
    /// <summary>Context lines to keep on each side of a change within a hunk.</summary>
    public int MaxContextLines { get; init; } = 2;

    /// <summary>Maximum hunks to keep per file (first + last + top-scored middle).</summary>
    public int MaxHunksPerFile { get; init; } = 10;

    /// <summary>Maximum files to keep (largest change count preferred).</summary>
    public int MaxFiles { get; init; } = 20;

    /// <summary>Emit a CCR retrieval marker when content is dropped.</summary>
    public bool EnableCcr { get; init; } = true;

    /// <summary>Below this many lines the diff is passed through unchanged.</summary>
    public int MinLinesForCcr { get; init; } = 50;

    /// <summary>Skip the CCR marker if the output is already ≥ this fraction of the input.</summary>
    public double MinCompressionRatioForCcr { get; init; } = 0.8;
}

/// <summary>
/// Compresses unified <c>git diff</c> output: parses files and hunks, caps hunks-per-file and
/// total files, trims surrounding context lines, and appends a <c>[N files changed, +A -D]</c>
/// footer. Large diffs are offloaded to CCR with a retrieval marker.
/// <para>Concept-faithful port of the reference <c>diff_compressor</c>.</para>
/// </summary>
public sealed partial class DiffCompressor : IContentCompressor
{
    private sealed class Hunk
    {
        public required string Header { get; init; }
        public List<string> Lines { get; } = [];
        public int Additions { get; set; }
        public int Deletions { get; set; }
        public double Score { get; set; }
    }

    private sealed class DiffFile
    {
        public required string Header { get; init; }
        public List<string> PreambleLines { get; } = [];
        public List<Hunk> Hunks { get; } = [];
        public int TotalChanges => Hunks.Sum(h => h.Additions + h.Deletions);
    }

    private static readonly string[] PriorityWords =
    [
        "error", "exception", "fail", "fatal", "critical", "crash", "panic",
        "important", "todo", "fixme", "bug", "fix",
        "security", "auth", "password", "secret", "token",
    ];

    private readonly DiffCompressorConfig _config;
    private readonly ICcrStore? _ccr;

    /// <summary>Create a diff compressor.</summary>
    public DiffCompressor(DiffCompressorConfig? config = null, ICcrStore? ccr = null)
    {
        _config = config ?? new DiffCompressorConfig();
        _ccr = ccr;
    }

    /// <inheritdoc />
    public ContentType Handles => ContentType.GitDiff;

    /// <inheritdoc />
    public CompressionOutcome Compress(string content, string? context = null)
    {
        string[] lines = content.Split('\n');
        if (lines.Length < _config.MinLinesForCcr)
        {
            return CompressionOutcome.PassThrough(content, "diff:passthrough");
        }

        (List<string> preDiff, List<DiffFile> files) = ParseDiff(lines);
        if (files.Count == 0)
        {
            return CompressionOutcome.PassThrough(content, "diff:passthrough");
        }

        ScoreHunks(files, context);

        var droppedFiles = new List<string>();
        if (files.Count > _config.MaxFiles)
        {
            files = files.OrderByDescending(f => f.TotalChanges).ToList();
            droppedFiles = files.Skip(_config.MaxFiles).Select(f => f.Header).ToList();
            files = files.Take(_config.MaxFiles).ToList();
        }

        int hunksRemoved = 0;
        foreach (DiffFile file in files)
        {
            (List<Hunk> kept, int dropped) = SelectHunks(file.Hunks);
            hunksRemoved += dropped;
            file.Hunks.Clear();
            foreach (Hunk h in kept)
            {
                ReduceContext(h);
                file.Hunks.Add(h);
            }
        }

        int totalAdd = files.Sum(f => f.Hunks.Sum(h => h.Additions));
        int totalDel = files.Sum(f => f.Hunks.Sum(h => h.Deletions));

        string body = FormatOutput(preDiff, files, files.Count + droppedFiles.Count, totalAdd, totalDel, hunksRemoved);
        int compressedLineCount = body.Split('\n').Length;

        string compressed = body;
        if (_config.EnableCcr
            && compressedLineCount < lines.Length * _config.MinCompressionRatioForCcr
            && _ccr is not null)
        {
            string key = Hashing.Md5Hex24(content);
            _ccr.Put(key, content);
            compressed +=
                $"\n[{lines.Length} lines compressed to {compressedLineCount}. Retrieve full diff: hash={key}]";
        }

        bool modified = !compressed.Equals(content, StringComparison.Ordinal);
        return new CompressionOutcome(compressed, content, modified, $"diff:{lines.Length}->{compressedLineCount}");
    }

    private static (List<string> PreDiff, List<DiffFile> Files) ParseDiff(string[] lines)
    {
        var preDiff = new List<string>();
        var files = new List<DiffFile>();
        DiffFile? currentFile = null;
        Hunk? currentHunk = null;

        foreach (string line in lines)
        {
            if (DiffHeaderRegex().IsMatch(line))
            {
                currentHunk = null;
                currentFile = new DiffFile { Header = line };
                files.Add(currentFile);
                continue;
            }

            if (currentFile is null)
            {
                preDiff.Add(line);
                continue;
            }

            if (HunkHeaderRegex().IsMatch(line))
            {
                currentHunk = new Hunk { Header = line };
                currentFile.Hunks.Add(currentHunk);
                continue;
            }

            if (currentHunk is null)
            {
                currentFile.PreambleLines.Add(line);
                continue;
            }

            currentHunk.Lines.Add(line);
            if (line.StartsWith('+') && !line.StartsWith("+++", StringComparison.Ordinal))
            {
                currentHunk.Additions++;
            }
            else if (line.StartsWith('-') && !line.StartsWith("---", StringComparison.Ordinal))
            {
                currentHunk.Deletions++;
            }
        }

        return (preDiff, files);
    }

    private static void ScoreHunks(List<DiffFile> files, string? context)
    {
        HashSet<string> words = ExtractWords(context);
        foreach (DiffFile file in files)
        {
            foreach (Hunk hunk in file.Hunks)
            {
                double score = Math.Min(0.3, (hunk.Additions + hunk.Deletions) * 0.03);
                string lower = string.Join('\n', hunk.Lines).ToLowerInvariant();
                foreach (string w in words)
                {
                    if (lower.Contains(w, StringComparison.Ordinal))
                    {
                        score += 0.2;
                    }
                }

                foreach (string p in PriorityWords)
                {
                    if (lower.Contains(p, StringComparison.Ordinal))
                    {
                        score += 0.3;
                        break;
                    }
                }

                hunk.Score = Math.Min(1.0, score);
            }
        }
    }

    private (List<Hunk> Kept, int Dropped) SelectHunks(List<Hunk> hunks)
    {
        if (hunks.Count <= _config.MaxHunksPerFile)
        {
            return (hunks, 0);
        }

        Hunk first = hunks[0];
        Hunk last = hunks[^1];
        var middle = hunks.Skip(1).Take(hunks.Count - 2).ToList();
        int slots = _config.MaxHunksPerFile - 2;
        var keptMiddle = middle
            .OrderByDescending(h => h.Score)
            .Take(Math.Max(0, slots))
            .ToList();
        int dropped = middle.Count - keptMiddle.Count;

        var kept = new List<Hunk> { first };
        kept.AddRange(keptMiddle);
        kept.Add(last);
        kept = kept.OrderBy(ExtractStartLine).ToList();
        return (kept, dropped);
    }

    private void ReduceContext(Hunk hunk)
    {
        var changeIdx = new List<int>();
        for (int i = 0; i < hunk.Lines.Count; i++)
        {
            string l = hunk.Lines[i];
            bool isChange = (l.StartsWith('+') && !l.StartsWith("+++", StringComparison.Ordinal))
                || (l.StartsWith('-') && !l.StartsWith("---", StringComparison.Ordinal));
            if (isChange)
            {
                changeIdx.Add(i);
            }
        }

        if (changeIdx.Count == 0)
        {
            var head = hunk.Lines.Take(Math.Min(_config.MaxContextLines, hunk.Lines.Count)).ToList();
            hunk.Lines.Clear();
            hunk.Lines.AddRange(head);
            return;
        }

        var keep = new SortedSet<int>();
        foreach (int idx in changeIdx)
        {
            for (int d = -_config.MaxContextLines; d <= _config.MaxContextLines; d++)
            {
                int j = idx + d;
                if (j >= 0 && j < hunk.Lines.Count)
                {
                    keep.Add(j);
                }
            }
        }

        for (int i = 0; i < hunk.Lines.Count; i++)
        {
            if (hunk.Lines[i].StartsWith('\\'))
            {
                keep.Add(i);
            }
        }

        var reduced = keep.Select(i => hunk.Lines[i]).ToList();
        hunk.Lines.Clear();
        hunk.Lines.AddRange(reduced);
    }

    private static string FormatOutput(
        List<string> preDiff,
        List<DiffFile> files,
        int filesAffected,
        int totalAdd,
        int totalDel,
        int hunksRemoved)
    {
        var sb = new StringBuilder();
        foreach (string p in preDiff)
        {
            sb.Append(p).Append('\n');
        }

        foreach (DiffFile file in files)
        {
            sb.Append(file.Header).Append('\n');
            foreach (string pre in file.PreambleLines)
            {
                sb.Append(pre).Append('\n');
            }

            foreach (Hunk hunk in file.Hunks)
            {
                sb.Append(hunk.Header).Append('\n');
                foreach (string l in hunk.Lines)
                {
                    sb.Append(l).Append('\n');
                }
            }
        }

        var footer = new StringBuilder();
        footer.Append($"[{filesAffected} files changed, +{totalAdd} -{totalDel} lines");
        if (hunksRemoved > 0)
        {
            footer.Append($", {hunksRemoved} hunks omitted");
        }

        footer.Append(']');
        sb.Append(footer);
        return sb.ToString();
    }

    private static int ExtractStartLine(Hunk hunk)
    {
        System.Text.RegularExpressions.Match m = HunkStartRegex().Match(hunk.Header);
        return m.Success && int.TryParse(m.Groups[1].Value, out int n) ? n : 0;
    }

    private static HashSet<string> ExtractWords(string? context)
    {
        var words = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(context))
        {
            return words;
        }

        foreach (string t in context.ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (t.Length > 2)
            {
                words.Add(t);
            }
        }

        return words;
    }

    [GeneratedRegex(@"^(diff --git |diff --combined |diff --cc )")]
    private static partial Regex DiffHeaderRegex();

    [GeneratedRegex(@"^@@@?@? .*@@@?@?")]
    private static partial Regex HunkHeaderRegex();

    [GeneratedRegex(@"\+(\d+)")]
    private static partial Regex HunkStartRegex();
}
