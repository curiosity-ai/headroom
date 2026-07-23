using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Headroom.Abstractions;
using Headroom.Ccr;

namespace Headroom.Compressors;

/// <summary>
/// The flagship JSON compressor. Given a JSON array of objects it (1) analyses each field's
/// uniqueness, constancy and numeric distribution; (2) decides whether the array is even safe to
/// sample (the <i>crushability gate</i> — unique entities carrying no signal are kept whole);
/// (3) keeps a principled subset — a fraction of the head and tail, statistical outliers, numeric
/// change-points and rows containing error keywords — after de-duplicating identical rows; then
/// drops the rest and appends a reversible CCR sentinel. It also has a <b>lossless</b> path: a
/// clean homogeneous table is re-encoded as a compact <c>csv-schema</c> block that needs no
/// retrieval round-trip.
/// <para>Concept-faithful port of the reference <c>smart_crusher</c>.</para>
/// </summary>
public sealed class SmartCrusher : IContentCompressor
{
    private const int MaxDepth = 50;
    private static readonly string[] ErrorKeywords =
    [
        "error", "exception", "failed", "failure", "critical", "fatal",
        "crash", "panic", "abort", "timeout", "denied", "rejected",
    ];

    // ensure_ascii=false equivalent: don't escape <, >, & or non-ASCII — matches the reference's
    // compact, UTF-8 output (so the CCR "<<ccr:...>>" sentinel stays literal, not <-escaped).
    private static readonly JsonSerializerOptions Compact = new()
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly SmartCrusherConfig _config;
    private readonly ICcrStore? _ccr;

    /// <summary>Create a smart crusher.</summary>
    public SmartCrusher(SmartCrusherConfig? config = null, ICcrStore? ccr = null)
    {
        _config = config ?? new SmartCrusherConfig();
        _ccr = ccr;
    }

    /// <inheritdoc />
    public ContentType Handles => ContentType.JsonArray;

    /// <inheritdoc />
    public CompressionOutcome Compress(string content, string? context = null) =>
        Crush(content, context);

    /// <summary>Crush a JSON document, biasing item selection toward <paramref name="query"/>.</summary>
    public CompressionOutcome Crush(string content, string? query = null, double bias = 1.0)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(content);
        }
        catch (JsonException)
        {
            return CompressionOutcome.PassThrough(content, "smart_crush:not_json");
        }

        if (root is null)
        {
            return CompressionOutcome.PassThrough(content, "smart_crush:not_json");
        }

        var info = new List<string>();
        JsonNode processed = ProcessNode(root, 0, query, bias, info);
        string result = processed.ToJsonString(Compact);
        bool modified = !result.Equals(content.Trim(), StringComparison.Ordinal) && info.Count > 0;
        string strategy = info.Count > 0 ? string.Join(",", info) : "passthrough";
        return new CompressionOutcome(result, content, modified, $"smart_crush:{strategy}");
    }

    private JsonNode ProcessNode(JsonNode node, int depth, string? query, double bias, List<string> info)
    {
        if (depth >= MaxDepth)
        {
            return node;
        }

        switch (node)
        {
            case JsonArray array:
            {
                var objects = new List<JsonObject>();
                bool allObjects = array.Count > 0;
                foreach (JsonNode? child in array)
                {
                    if (child is JsonObject obj)
                    {
                        objects.Add(obj);
                    }
                    else
                    {
                        allObjects = false;
                    }
                }

                if (allObjects && objects.Count >= _config.MinItemsToAnalyze)
                {
                    return CrushArray(objects, query, bias, info);
                }

                // Recurse element-wise.
                var newArray = new JsonArray();
                foreach (JsonNode? child in array)
                {
                    newArray.Add(child is null ? null : ProcessNode(child.DeepClone(), depth + 1, query, bias, info));
                }

                return newArray;
            }

            case JsonObject obj:
            {
                var newObj = new JsonObject();
                foreach (KeyValuePair<string, JsonNode?> kv in obj)
                {
                    newObj[kv.Key] = kv.Value is null
                        ? null
                        : ProcessNode(kv.Value.DeepClone(), depth + 1, query, bias, info);
                }

                return newObj;
            }

            default:
                return node;
        }
    }

    private JsonNode CrushArray(List<JsonObject> items, string? query, double bias, List<string> info)
    {
        var itemStrings = items.ConvertAll(o => o.ToJsonString(Compact));
        int adaptiveK = AdaptiveSizer.ComputeOptimalK(itemStrings, bias, minK: 3, maxK: _config.MaxItemsAfterCrush);

        if (items.Count <= adaptiveK)
        {
            return Rebuild(items);
        }

        // ── Lossless-first: homogeneous CSV-schema table ──
        if (_config.EnableLosslessCompaction)
        {
            string? compacted = TryCompactCsvSchema(items, itemStrings);
            if (compacted is not null)
            {
                info.Add($"lossless:csv-schema({items.Count}->len={compacted.Length})");
                return JsonValue.Create(compacted);
            }
        }

        if (_config.LosslessOnly)
        {
            return Rebuild(items);
        }

        // ── Lossy path ──
        FieldAnalysis analysis = AnalyzeFields(items);
        if (!analysis.Crushable)
        {
            info.Add($"skip:{analysis.SkipReason}");
            return Rebuild(items);
        }

        SortedSet<int> keep = SelectIndices(items, analysis, adaptiveK, query);
        var kept = keep.Select(i => items[i]).ToList();
        int dropped = items.Count - kept.Count;

        var outArray = Rebuild(kept);
        if (dropped > 0 && _config.EnableCcrMarker)
        {
            string canonical = SerializeArray(items);
            string hash = Hashing.Sha256Hex12(canonical);
            _ccr?.Put(hash, canonical);
            outArray.Add(new JsonObject
            {
                ["_ccr_dropped"] = $"<<ccr:{hash} {dropped}_rows_offloaded>>",
            });
            info.Add($"smart_crush({items.Count}->{kept.Count})");
        }
        else if (dropped > 0)
        {
            info.Add($"smart_crush({items.Count}->{kept.Count})");
        }

        return outArray;
    }

    // ── Field analysis & crushability gate ──────────────────────────────────────────────────

    private sealed class FieldStat
    {
        public required string Name { get; init; }
        public double UniqueRatio { get; init; }
        public bool IsConstant { get; init; }
        public bool IsNumeric { get; init; }
        public bool IsString { get; init; }
        public double Mean { get; init; }
        public double StdDev { get; init; }
        public double IdConfidence { get; init; }
        public IReadOnlyList<int> ChangePoints { get; init; } = [];
        public IReadOnlyList<double?> NumericValues { get; init; } = [];
    }

    private sealed class FieldAnalysis
    {
        public required bool Crushable { get; init; }
        public required string SkipReason { get; init; }
        public required IReadOnlyList<FieldStat> Fields { get; init; }
        public required IReadOnlyList<int> StructuralOutliers { get; init; }
        public required IReadOnlyList<int> ErrorItems { get; init; }
        public string? IdField { get; init; }
    }

    private FieldAnalysis AnalyzeFields(List<JsonObject> items)
    {
        var keys = new SortedSet<string>(StringComparer.Ordinal);
        foreach (JsonObject o in items)
        {
            foreach (KeyValuePair<string, JsonNode?> kv in o)
            {
                keys.Add(kv.Key);
            }
        }

        var fields = new List<FieldStat>();
        foreach (string key in keys)
        {
            fields.Add(AnalyzeField(key, items));
        }

        // Structural outliers: items whose key set differs from the modal key set.
        List<int> structural = DetectStructuralOutliers(items);
        List<int> errorItems = DetectErrorItems(items);

        // ID field: highest-confidence field with confidence >= 0.7.
        FieldStat? idField = fields.Where(f => f.IdConfidence >= 0.7).MaxBy(f => f.IdConfidence);
        double idUniqueness = idField?.UniqueRatio ?? 0.0;

        var stringFields = fields.Where(f => f.IsString && f.Name != idField?.Name).ToList();
        var numericFields = fields.Where(f => f.IsNumeric && f.Name != idField?.Name).ToList();
        double avgStringUniqueness = stringFields.Count > 0 ? stringFields.Average(f => f.UniqueRatio) : 0.0;
        double avgNumericUniqueness = numericFields.Count > 0 ? numericFields.Average(f => f.UniqueRatio) : 0.0;

        double maxUniqueness = Math.Max(avgStringUniqueness, Math.Max(idUniqueness, 0.0));
        double nonIdContentUniqueness = Math.Max(avgStringUniqueness, avgNumericUniqueness);
        bool hasChangePoints = fields.Any(f => f.ChangePoints.Count > 0);
        bool hasAnySignal = structural.Count > 0 || errorItems.Count > 0 || hasChangePoints
            || fields.Any(f => IsScoreField(f));
        bool hasIdField = idField is not null;

        (bool crushable, string reason) = Decide(
            nonIdContentUniqueness, maxUniqueness, hasIdField, hasAnySignal);

        return new FieldAnalysis
        {
            Crushable = crushable,
            SkipReason = reason,
            Fields = fields,
            StructuralOutliers = structural,
            ErrorItems = errorItems,
            IdField = idField?.Name,
        };
    }

    private static (bool Crushable, string Reason) Decide(
        double nonIdContentUniqueness, double maxUniqueness, bool hasIdField, bool hasAnySignal)
    {
        if (nonIdContentUniqueness < 0.1 && hasIdField)
        {
            return (true, "repetitive_content_with_ids");
        }

        if (maxUniqueness < 0.3)
        {
            return (true, "low_uniqueness_safe_to_sample");
        }

        if (hasIdField && maxUniqueness > 0.8 && !hasAnySignal)
        {
            return (false, "unique_entities_no_signal");
        }

        if (maxUniqueness > 0.8 && hasAnySignal)
        {
            return (true, "unique_entities_with_signal");
        }

        if (!hasAnySignal)
        {
            return (false, "medium_uniqueness_no_signal");
        }

        return (true, "medium_uniqueness_with_signal");
    }

    private FieldStat AnalyzeField(string key, List<JsonObject> items)
    {
        var reprs = new List<string>(items.Count);
        var nonNull = new List<JsonNode>();
        foreach (JsonObject o in items)
        {
            JsonNode? v = o.TryGetPropertyValue(key, out JsonNode? node) ? node : null;
            reprs.Add(v?.ToJsonString(Compact) ?? "null");
            if (v is not null)
            {
                nonNull.Add(v);
            }
        }

        int unique = reprs.Distinct(StringComparer.Ordinal).Count();
        double uniqueRatio = (double)unique / items.Count;
        bool isConstant = unique == 1;

        bool isNumeric = nonNull.Count > 0 && nonNull.All(IsNumber);
        bool isString = nonNull.Count > 0 && nonNull.All(n => n.GetValueKind() == JsonValueKind.String);

        double mean = 0, std = 0;
        var numericValues = new List<double?>();
        IReadOnlyList<int> changePoints = [];
        if (isNumeric)
        {
            var nums = new List<double>();
            foreach (JsonObject o in items)
            {
                if (o.TryGetPropertyValue(key, out JsonNode? node) && node is not null && TryGetNumber(node, out double d))
                {
                    numericValues.Add(d);
                    nums.Add(d);
                }
                else
                {
                    numericValues.Add(null);
                }
            }

            if (nums.Count > 0)
            {
                mean = nums.Average();
                std = SampleStdDev(nums, mean);
                changePoints = DetectChangePoints(nums, std);
            }
        }

        double idConfidence = ComputeIdConfidence(uniqueRatio, isString, isNumeric, nonNull);

        return new FieldStat
        {
            Name = key,
            UniqueRatio = uniqueRatio,
            IsConstant = isConstant,
            IsNumeric = isNumeric,
            IsString = isString,
            Mean = mean,
            StdDev = std,
            IdConfidence = idConfidence,
            ChangePoints = changePoints,
            NumericValues = numericValues,
        };
    }

    private static double ComputeIdConfidence(
        double uniqueRatio, bool isString, bool isNumeric, List<JsonNode> nonNull)
    {
        if (uniqueRatio < 0.9)
        {
            return 0.0;
        }

        if (isString)
        {
            int sampleN = Math.Min(20, nonNull.Count);
            int uuidCount = 0;
            for (int i = 0; i < sampleN; i++)
            {
                if (IsUuid(nonNull[i].GetValue<string>()))
                {
                    uuidCount++;
                }
            }

            if (sampleN > 0 && (double)uuidCount / sampleN > 0.8)
            {
                return 0.95;
            }

            if (uniqueRatio > 0.95)
            {
                return 0.8;
            }
        }

        if (isNumeric && uniqueRatio > 0.95)
        {
            return 0.85;
        }

        return uniqueRatio > 0.98 ? 0.7 : 0.0;
    }

    private static bool IsScoreField(FieldStat f)
    {
        if (!f.IsNumeric || f.NumericValues.Count == 0)
        {
            return false;
        }

        var vals = f.NumericValues.Where(v => v.HasValue).Select(v => v!.Value).ToList();
        if (vals.Count == 0)
        {
            return false;
        }

        double min = vals.Min();
        double max = vals.Max();
        bool bounded = (min >= 0 && max <= 1) || (min >= 0 && max <= 100);
        bool hasFractions = vals.Any(v => v != Math.Floor(v));
        return bounded && hasFractions;
    }

    // ── Selection ───────────────────────────────────────────────────────────────────────────

    private SortedSet<int> SelectIndices(
        List<JsonObject> items, FieldAnalysis analysis, int budget, string? query)
    {
        int n = items.Count;
        int kFirst = Math.Min(Math.Max(1, (int)Math.Round(budget * _config.FirstFraction, MidpointRounding.ToEven)), budget);
        int kLast = Math.Min(Math.Max(1, (int)Math.Round(budget * _config.LastFraction, MidpointRounding.ToEven)), Math.Max(0, budget - kFirst));

        var keep = new SortedSet<int>();
        for (int i = 0; i < kFirst && i < n; i++)
        {
            keep.Add(i);
        }

        for (int i = Math.Max(0, n - kLast); i < n; i++)
        {
            keep.Add(i);
        }

        // Numeric anomalies (outliers).
        foreach (FieldStat f in analysis.Fields.Where(f => f.IsNumeric && f.StdDev > 0))
        {
            double threshold = _config.VarianceThreshold * f.StdDev;
            for (int i = 0; i < f.NumericValues.Count; i++)
            {
                double? v = f.NumericValues[i];
                if (v.HasValue && Math.Abs(v.Value - f.Mean) > threshold)
                {
                    keep.Add(i);
                }
            }
        }

        // Change-point neighbourhoods (±1).
        if (_config.PreserveChangePoints)
        {
            foreach (FieldStat f in analysis.Fields)
            {
                foreach (int cp in f.ChangePoints)
                {
                    foreach (int j in new[] { cp - 1, cp, cp + 1 })
                    {
                        if (j >= 0 && j < n)
                        {
                            keep.Add(j);
                        }
                    }
                }
            }
        }

        foreach (int i in analysis.ErrorItems)
        {
            keep.Add(i);
        }

        foreach (int i in analysis.StructuralOutliers)
        {
            keep.Add(i);
        }

        // Query anchor matching: keep items whose text contains a query token.
        if (!string.IsNullOrWhiteSpace(query))
        {
            var tokens = query.ToLowerInvariant()
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .Where(t => t.Length > 2)
                .ToHashSet(StringComparer.Ordinal);
            if (tokens.Count > 0)
            {
                for (int i = 0; i < n; i++)
                {
                    string lower = items[i].ToJsonString(Compact).ToLowerInvariant();
                    if (tokens.Any(t => lower.Contains(t, StringComparison.Ordinal)))
                    {
                        keep.Add(i);
                    }
                }
            }
        }

        // De-duplicate identical items (keep first occurrence).
        if (_config.DedupIdenticalItems)
        {
            keep = DedupIndices(keep, items);
        }

        // Fill toward the budget if under.
        if (keep.Count < budget && keep.Count < n)
        {
            FillRemaining(keep, items, n, budget);
        }

        // Prune if over budget, protecting critical items.
        if (keep.Count > budget)
        {
            keep = PruneToBudget(keep, analysis, n, budget);
        }

        return keep;
    }

    private SortedSet<int> DedupIndices(SortedSet<int> keep, List<JsonObject> items)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new SortedSet<int>();
        foreach (int i in keep)
        {
            string hash = Hashing.Sha256Hex16(items[i].ToJsonString(Compact));
            if (seen.Add(hash))
            {
                result.Add(i);
            }
        }

        return result;
    }

    private static void FillRemaining(SortedSet<int> keep, List<JsonObject> items, int n, int budget)
    {
        var seen = keep.Select(i => Hashing.Sha256Hex16(items[i].ToJsonString(Compact)))
            .ToHashSet(StringComparer.Ordinal);
        var candidates = Enumerable.Range(0, n).Where(i => !keep.Contains(i)).ToList();
        int remaining = budget - keep.Count;
        if (remaining <= 0 || candidates.Count == 0)
        {
            return;
        }

        int step = Math.Max(candidates.Count / (remaining + 1), 1);
        for (int offset = 0; offset < step && keep.Count < budget; offset++)
        {
            for (int i = offset; i < candidates.Count && keep.Count < budget; i += step)
            {
                int idx = candidates[i];
                string hash = Hashing.Sha256Hex16(items[idx].ToJsonString(Compact));
                if (seen.Add(hash))
                {
                    keep.Add(idx);
                }
            }
        }
    }

    private static SortedSet<int> PruneToBudget(SortedSet<int> keep, FieldAnalysis analysis, int n, int budget)
    {
        var critical = new SortedSet<int>();
        foreach (int i in analysis.ErrorItems)
        {
            critical.Add(i);
        }

        foreach (int i in analysis.StructuralOutliers)
        {
            critical.Add(i);
        }

        var result = new SortedSet<int>(critical);
        // Head and tail anchors.
        foreach (int i in new[] { 0, 1, 2, n - 2, n - 1 })
        {
            if (result.Count >= budget)
            {
                break;
            }

            if (i >= 0 && i < n && keep.Contains(i))
            {
                result.Add(i);
            }
        }

        foreach (int i in keep)
        {
            if (result.Count >= budget)
            {
                break;
            }

            result.Add(i);
        }

        return result;
    }

    // ── Lossless CSV-schema compaction ──────────────────────────────────────────────────────

    private string? TryCompactCsvSchema(List<JsonObject> items, List<string> itemStrings)
    {
        // Column frequency across all rows.
        var freq = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (JsonObject o in items)
        {
            foreach (KeyValuePair<string, JsonNode?> kv in o)
            {
                freq[kv.Key] = freq.GetValueOrDefault(kv.Key) + 1;
            }
        }

        if (freq.Count == 0)
        {
            return null;
        }

        // Require a mostly-homogeneous schema: >=80% of rows carry the "core" columns.
        int total = items.Count;
        int coreThreshold = (int)Math.Ceiling(total * 0.8);
        int coreCount = freq.Values.Count(v => v >= coreThreshold);
        double coreRatio = (double)coreCount / freq.Count;
        if (coreRatio < 0.6)
        {
            return null; // heterogeneous — a table would be too sparse to help
        }

        // Only handle scalar-valued cells losslessly (nested structures bail out to lossy).
        foreach (JsonObject o in items)
        {
            foreach (KeyValuePair<string, JsonNode?> kv in o)
            {
                if (kv.Value is JsonObject or JsonArray)
                {
                    return null;
                }
            }
        }

        var columns = freq
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => kv.Key)
            .ToList();

        var sb = new StringBuilder();
        sb.Append('[').Append(total).Append("]{");
        for (int c = 0; c < columns.Count; c++)
        {
            if (c > 0)
            {
                sb.Append(',');
            }

            string col = columns[c];
            string type = InferColumnType(items, col);
            bool nullable = freq[col] < total || items.Any(o =>
                o.TryGetPropertyValue(col, out JsonNode? v) && v is null);
            sb.Append(col).Append(':').Append(type);
            if (nullable)
            {
                sb.Append('?');
            }
        }

        sb.Append("}\n");

        foreach (JsonObject o in items)
        {
            for (int c = 0; c < columns.Count; c++)
            {
                if (c > 0)
                {
                    sb.Append(',');
                }

                sb.Append(CsvCell(o, columns[c]));
            }

            sb.Append('\n');
        }

        string rendered = sb.ToString();

        // Only win if it actually saves enough bytes.
        int inputBytes = itemStrings.Sum(s => s.Length) + Math.Max(0, itemStrings.Count - 1) + 2;
        double savings = inputBytes > 0 ? 1.0 - ((double)rendered.Length / inputBytes) : 0.0;
        return savings >= _config.LosslessMinSavingsRatio ? rendered : null;
    }

    private static string InferColumnType(List<JsonObject> items, string col)
    {
        bool anyInt = false, anyFloat = false, anyBool = false, anyString = false;
        foreach (JsonObject o in items)
        {
            if (!o.TryGetPropertyValue(col, out JsonNode? v) || v is null)
            {
                continue;
            }

            switch (v.GetValueKind())
            {
                case JsonValueKind.Number:
                    if (TryGetNumber(v, out double d) && d == Math.Floor(d))
                    {
                        anyInt = true;
                    }
                    else
                    {
                        anyFloat = true;
                    }

                    break;
                case JsonValueKind.True or JsonValueKind.False:
                    anyBool = true;
                    break;
                default:
                    anyString = true;
                    break;
            }
        }

        if (anyString)
        {
            return "string";
        }

        if (anyBool && !anyInt && !anyFloat)
        {
            return "bool";
        }

        if (anyFloat)
        {
            return "float";
        }

        return anyInt ? "int" : "string";
    }

    private static string CsvCell(JsonObject o, string col)
    {
        if (!o.TryGetPropertyValue(col, out JsonNode? v) || v is null)
        {
            return string.Empty;
        }

        string raw = v.GetValueKind() == JsonValueKind.String
            ? v.GetValue<string>()
            : v.ToJsonString(Compact);

        if (raw.IndexOfAny([',', '"', '\n', '\r']) >= 0)
        {
            return "\"" + raw.Replace("\"", "\"\"") + "\"";
        }

        return raw;
    }

    // ── Detectors & math ────────────────────────────────────────────────────────────────────

    private static List<int> DetectStructuralOutliers(List<JsonObject> items)
    {
        var keySetCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var itemKeySets = new List<string>(items.Count);
        foreach (JsonObject o in items)
        {
            string sig = string.Join('\u0001', o.Select(kv => kv.Key).OrderBy(k => k, StringComparer.Ordinal));
            itemKeySets.Add(sig);
            keySetCounts[sig] = keySetCounts.GetValueOrDefault(sig) + 1;
        }

        string modal = keySetCounts.MaxBy(kv => kv.Value).Key;
        var outliers = new List<int>();
        for (int i = 0; i < itemKeySets.Count; i++)
        {
            if (itemKeySets[i] != modal)
            {
                outliers.Add(i);
            }
        }

        return outliers;
    }

    private static List<int> DetectErrorItems(List<JsonObject> items)
    {
        var result = new List<int>();
        for (int i = 0; i < items.Count; i++)
        {
            string lower = items[i].ToJsonString(Compact).ToLowerInvariant();
            if (ErrorKeywords.Any(kw => lower.Contains(kw, StringComparison.Ordinal)))
            {
                result.Add(i);
            }
        }

        return result;
    }

    private static List<int> DetectChangePoints(List<double> values, double overallStd, int window = 5)
    {
        var result = new List<int>();
        if (values.Count < window * 2 || overallStd <= 0)
        {
            return result;
        }

        double threshold = 2.0 * overallStd;
        int lastKept = int.MinValue;
        for (int i = window; i < values.Count - window; i++)
        {
            double before = 0, after = 0;
            for (int j = i - window; j < i; j++)
            {
                before += values[j];
            }

            for (int j = i; j < i + window; j++)
            {
                after += values[j];
            }

            before /= window;
            after /= window;
            if (Math.Abs(after - before) > threshold && i - lastKept > window)
            {
                result.Add(i);
                lastKept = i;
            }
        }

        return result;
    }

    private static double SampleStdDev(List<double> values, double mean)
    {
        if (values.Count < 2)
        {
            return 0.0;
        }

        double sumSq = 0.0;
        foreach (double v in values)
        {
            double d = v - mean;
            sumSq += d * d;
        }

        return Math.Sqrt(sumSq / (values.Count - 1));
    }

    private static bool IsUuid(string s)
    {
        if (s.Length != 36)
        {
            return false;
        }

        string[] parts = s.Split('-');
        int[] lengths = [8, 4, 4, 4, 12];
        if (parts.Length != 5)
        {
            return false;
        }

        for (int i = 0; i < 5; i++)
        {
            if (parts[i].Length != lengths[i] || !parts[i].All(Uri.IsHexDigit))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsNumber(JsonNode node) => node.GetValueKind() == JsonValueKind.Number;

    private static bool TryGetNumber(JsonNode node, out double value)
    {
        value = 0;
        if (node.GetValueKind() != JsonValueKind.Number)
        {
            return false;
        }

        try
        {
            value = node.GetValue<double>();
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static JsonArray Rebuild(List<JsonObject> items)
    {
        var array = new JsonArray();
        foreach (JsonObject o in items)
        {
            array.Add(o.DeepClone());
        }

        return array;
    }

    private static string SerializeArray(List<JsonObject> items) => Rebuild(items).ToJsonString(Compact);
}
