using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Headroom.Abstractions;
using Headroom.Ccr;
using Headroom.Compressors;
using Xunit;

namespace Headroom.Tests;

public partial class SmartCrusherTests
{
    private static string BuildArray(int n, Func<int, string> item)
    {
        var parts = Enumerable.Range(0, n).Select(item);
        return "[" + string.Join(",", parts) + "]";
    }

    [Fact]
    public void SmallArrayPassesThrough()
    {
        string json = BuildArray(4, i => $$"""{"id":{{i}},"v":"row{{i}}"}""");
        var crusher = new SmartCrusher();
        CompressionOutcome outcome = crusher.Crush(json);
        Assert.False(outcome.WasModified);
    }

    [Fact]
    public void NonJsonPassesThrough()
    {
        var crusher = new SmartCrusher();
        CompressionOutcome outcome = crusher.Crush("this is not json at all");
        Assert.False(outcome.WasModified);
        Assert.Contains("not_json", outcome.Strategy);
    }

    [Fact]
    public void LosslessCsvSchemaForHomogeneousTable()
    {
        // 40 homogeneous scalar rows — the lossless path should win.
        string json = BuildArray(40, i => $$"""{"id":{{i}},"name":"user{{i}}","score":{{i * 3}}}""");
        var crusher = new SmartCrusher(new SmartCrusherConfig());
        CompressionOutcome outcome = crusher.Crush(json);

        Assert.True(outcome.WasModified);
        Assert.Contains("lossless:csv-schema", outcome.Strategy);
        Assert.True(outcome.Compressed.Length < json.Length);

        // The compacted output is a JSON string whose value starts with the schema header.
        JsonNode? node = JsonNode.Parse(outcome.Compressed);
        string rendered = node!.GetValue<string>();
        Assert.StartsWith("[40]{", rendered);
        Assert.Contains("id:int", rendered);
        Assert.Contains("name:string", rendered);
    }

    [Fact]
    public void LossyDropEmitsCcrSentinelAndOffloadsOriginal()
    {
        // Repetitive content with unique ids, lossless disabled => lossy row-drop path.
        string json = BuildArray(60, i => $$"""{"id":{{i}},"status":"ok"}""");
        var store = new InMemoryCcrStore();
        var crusher = new SmartCrusher(
            new SmartCrusherConfig { EnableLosslessCompaction = false, MaxItemsAfterCrush = 15 },
            store);

        CompressionOutcome outcome = crusher.Crush(json);
        Assert.True(outcome.WasModified);

        var array = JsonNode.Parse(outcome.Compressed)!.AsArray();
        Assert.True(array.Count < 60);

        // Last element is the CCR sentinel.
        JsonObject sentinel = array[^1]!.AsObject();
        Assert.True(sentinel.ContainsKey("_ccr_dropped"));
        string marker = sentinel["_ccr_dropped"]!.GetValue<string>();

        Match m = SentinelRegex().Match(marker);
        Assert.True(m.Success, $"marker did not match: {marker}");
        string hash = m.Groups["hash"].Value;

        // Original is retrievable from the store, and round-trips to the same rows.
        string? original = store.Retrieve(hash);
        Assert.NotNull(original);
        Assert.Equal(60, JsonNode.Parse(original!)!.AsArray().Count);
    }

    [Fact]
    public void UniqueEntitiesNoSignalAreNotCrushed()
    {
        // 20 fully-unique entities, no errors/outliers, lossless disabled => crushability skip.
        string json = BuildArray(20, i =>
            $$"""{"uuid":"{{Guid.NewGuid()}}","label":"distinct-subject-{{i}}-{{i * 31}}"}""");
        var crusher = new SmartCrusher(new SmartCrusherConfig { EnableLosslessCompaction = false });
        CompressionOutcome outcome = crusher.Crush(json);

        Assert.Contains("skip:", outcome.Strategy);
        Assert.Equal(20, JsonNode.Parse(outcome.Compressed)!.AsArray().Count);
    }

    [Fact]
    public void IdenticalItemsAreDeduplicated()
    {
        string json = BuildArray(50, _ => """{"status":"ok","code":200}""");
        var crusher = new SmartCrusher(new SmartCrusherConfig { EnableLosslessCompaction = false });
        CompressionOutcome outcome = crusher.Crush(json);

        Assert.True(outcome.WasModified);
        // All rows identical => keep a tiny sample (+ maybe a sentinel), far fewer than 50.
        int count = JsonNode.Parse(outcome.Compressed)!.AsArray().Count;
        Assert.True(count < 10, $"expected heavy dedup, kept {count}");
    }

    [Fact]
    public void ErrorRowsArePreserved()
    {
        // 59 ok rows + 1 error row; lossless disabled so lossy selection runs.
        string json = BuildArray(60, i => i == 30
            ? """{"id":30,"status":"error","detail":"disk failure"}"""
            : $$"""{"id":{{i}},"status":"ok"}""");
        var crusher = new SmartCrusher(new SmartCrusherConfig { EnableLosslessCompaction = false, MaxItemsAfterCrush = 15 });
        CompressionOutcome outcome = crusher.Crush(json);

        Assert.Contains("\"error\"", outcome.Compressed);
        Assert.Contains("disk failure", outcome.Compressed);
    }

    [Fact]
    public void OutputStaysValidJson()
    {
        string json = BuildArray(80, i => $$"""{"id":{{i}},"name":"n{{i}}","v":{{i % 7}}}""");
        var crusher = new SmartCrusher();
        CompressionOutcome outcome = crusher.Crush(json);
        // Must always be parseable JSON.
        Exception? ex = Record.Exception(() => JsonDocument.Parse(outcome.Compressed));
        Assert.Null(ex);
    }

    [GeneratedRegex(@"<<ccr:(?<hash>[0-9a-f]+) (?<n>\d+)_rows_offloaded>>")]
    private static partial Regex SentinelRegex();
}
