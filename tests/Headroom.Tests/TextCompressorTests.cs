using Headroom.Abstractions;
using Headroom.Ccr;
using Headroom.Compressors;
using Xunit;

namespace Headroom.Tests;

public class LogCompressorTests
{
    [Fact]
    public void KeepsErrorsDropsNoiseAndOffloads()
    {
        var lines = new List<string>();
        for (int i = 0; i < 200; i++)
        {
            lines.Add($"[12:00:{i:D2}] INFO processing item {i}");
        }

        lines.Insert(120, "ERROR failed to connect to database: connection refused");
        string log = string.Join('\n', lines);

        var store = new InMemoryCcrStore();
        var compressor = new LogCompressor(new LogCompressorConfig(), store);
        CompressionOutcome outcome = compressor.Compress(log);

        Assert.True(outcome.WasModified);
        Assert.True(outcome.Compressed.Length < log.Length);
        Assert.Contains("ERROR failed to connect", outcome.Compressed);
        Assert.Contains("lines omitted", outcome.Compressed);
        Assert.Contains("Retrieve more: hash=", outcome.Compressed);
        Assert.Equal(1, store.Count);
    }

    [Fact]
    public void ShortLogPassesThrough()
    {
        string log = string.Join('\n', Enumerable.Range(0, 10).Select(i => $"INFO line {i}"));
        var compressor = new LogCompressor();
        CompressionOutcome outcome = compressor.Compress(log);
        Assert.False(outcome.WasModified);
    }
}

public class SearchCompressorTests
{
    [Fact]
    public void KeepsTopMatchesPerFileAndSummarisesRest()
    {
        var lines = new List<string>();
        for (int f = 0; f < 3; f++)
        {
            for (int i = 0; i < 20; i++)
            {
                lines.Add($"src/file{f}.cs:{i + 1}:    var x = compute({i});");
            }
        }

        string search = string.Join('\n', lines);
        var store = new InMemoryCcrStore();
        var compressor = new SearchCompressor(new SearchCompressorConfig(), store);
        CompressionOutcome outcome = compressor.Compress(search);

        Assert.True(outcome.WasModified);
        Assert.Contains("more matches in", outcome.Compressed);
        Assert.Contains("Retrieve more: hash=", outcome.Compressed);
    }

    [Fact]
    public void NonSearchPassesThrough()
    {
        var compressor = new SearchCompressor();
        CompressionOutcome outcome = compressor.Compress("just some prose without file line markers");
        Assert.False(outcome.WasModified);
    }
}

public class DiffCompressorTests
{
    [Fact]
    public void TrimsContextAndAppendsFooter()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("diff --git a/big.cs b/big.cs");
        sb.AppendLine("--- a/big.cs");
        sb.AppendLine("+++ b/big.cs");
        for (int h = 0; h < 15; h++)
        {
            sb.AppendLine($"@@ -{h * 10},8 +{h * 10},8 @@ method{h}");
            for (int c = 0; c < 6; c++)
            {
                sb.AppendLine($" context line {h}-{c}");
            }

            sb.AppendLine($"-old value {h}");
            sb.AppendLine($"+new value {h}");
        }

        string diff = sb.ToString();
        var compressor = new DiffCompressor(new DiffCompressorConfig());
        CompressionOutcome outcome = compressor.Compress(diff);

        Assert.True(outcome.WasModified);
        Assert.Contains("files changed", outcome.Compressed);
        Assert.Contains("hunks omitted", outcome.Compressed);
    }

    [Fact]
    public void ShortDiffPassesThrough()
    {
        const string diff = """
            diff --git a/x b/x
            @@ -1 +1 @@
            -a
            +b
            """;
        var compressor = new DiffCompressor();
        CompressionOutcome outcome = compressor.Compress(diff);
        Assert.False(outcome.WasModified);
    }
}

public class TextCrusherTests
{
    [Fact]
    public void KeepsRelevantSegmentsWithinBudget()
    {
        var segments = new List<string>();
        for (int i = 0; i < 40; i++)
        {
            segments.Add($"This is filler sentence number {i} about unrelated topics and background.");
        }

        segments.Insert(20, "The critical error occurred in the payment gateway module.");
        string text = string.Join(' ', segments);

        var crusher = new TextCrusher(new TextCrusherConfig { TargetRatio = 0.3 });
        CompressionOutcome outcome = crusher.Compress(text, context: "payment gateway error");

        Assert.True(outcome.WasModified);
        Assert.True(outcome.Compressed.Length < text.Length);
        Assert.Contains("payment gateway", outcome.Compressed);
    }

    [Fact]
    public void ShortTextPassesThrough()
    {
        var crusher = new TextCrusher();
        CompressionOutcome outcome = crusher.Compress("One. Two. Three.");
        Assert.False(outcome.WasModified);
    }
}
