using System.Text.RegularExpressions;
using Headroom.Compressors;
using LlmTornado.Chat;
using LlmTornado.Code;
using Xunit;

namespace Headroom.Tests;

public partial class HeadroomCompressorTests
{
    private static string BigJsonArray(int n = 60) =>
        "[" + string.Join(",", Enumerable.Range(0, n).Select(i => $$"""{"id":{{i}},"status":"ok"}""")) + "]";

    [Fact]
    public void EmptyListReturnsEmpty()
    {
        var compressor = new HeadroomCompressor();
        CompressionResult result = compressor.Compress([]);
        Assert.Empty(result.Messages);
        Assert.Equal(0, result.TokensBefore);
    }

    [Fact]
    public void CompressesToolMessageAndReducesTokens()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatMessageRoles.System, "You are a helpful assistant."),
            new(ChatMessageRoles.User, "Summarise the results."),
            new(ChatMessageRoles.Tool, BigJsonArray()),
        };

        var compressor = new HeadroomCompressor(new HeadroomOptions { ProtectRecent = 0 });
        CompressionResult result = compressor.Compress(messages);

        Assert.True(result.TokensSaved > 0);
        Assert.True(result.TokensAfter <= result.TokensBefore);
        Assert.NotEmpty(result.TransformsApplied);
        Assert.True(result.CompressionRatio > 0);
    }

    [Fact]
    public void DoesNotMutateInput()
    {
        string original = BigJsonArray();
        var input = new List<ChatMessage> { new(ChatMessageRoles.Tool, original) };
        var compressor = new HeadroomCompressor(new HeadroomOptions { ProtectRecent = 0 });
        compressor.Compress(input);
        Assert.Equal(original, input[0].Content);
    }

    [Fact]
    public void ProtectsRecentMessages()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatMessageRoles.Tool, BigJsonArray()),
        };

        // ProtectRecent=4 (default) with a single message => it is protected.
        var compressor = new HeadroomCompressor();
        CompressionResult result = compressor.Compress(messages);
        Assert.Same(messages[0], result.Messages[0]);
        Assert.Equal(0, result.TokensSaved);
    }

    [Fact]
    public void SkipsUserMessagesByDefault()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatMessageRoles.User, BigJsonArray()),
            new(ChatMessageRoles.Assistant, "ack"),
            new(ChatMessageRoles.Assistant, "ack"),
            new(ChatMessageRoles.Assistant, "ack"),
            new(ChatMessageRoles.Assistant, "ack"),
        };

        var compressor = new HeadroomCompressor(new HeadroomOptions { ProtectRecent = 0 });
        CompressionResult result = compressor.Compress(messages);
        // User message untouched by default.
        Assert.Same(messages[0], result.Messages[0]);
    }

    [Fact]
    public void CompressesUserMessagesWhenEnabled()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatMessageRoles.User, BigJsonArray()),
            new(ChatMessageRoles.Assistant, "ack"),
            new(ChatMessageRoles.Assistant, "ack"),
            new(ChatMessageRoles.Assistant, "ack"),
            new(ChatMessageRoles.Assistant, "ack"),
        };

        var options = new HeadroomOptions
        {
            ProtectRecent = 0,
            CompressUserMessages = true,
            SmartCrusher = new SmartCrusherConfig { EnableLosslessCompaction = false },
        };
        var compressor = new HeadroomCompressor(options);
        CompressionResult result = compressor.Compress(messages);
        Assert.NotSame(messages[0], result.Messages[0]);
    }

    [Fact]
    public void HandlesTextParts()
    {
        var message = new ChatMessage(ChatMessageRoles.Tool, new[]
        {
            new ChatMessagePart("Here is the data:"),
            new ChatMessagePart(BigJsonArray()),
        });

        var messages = new List<ChatMessage> { message };
        var compressor = new HeadroomCompressor(new HeadroomOptions { ProtectRecent = 0 });
        CompressionResult result = compressor.Compress(messages);

        Assert.True(result.TokensSaved > 0);
        ChatMessage outMsg = result.Messages[0];
        Assert.NotNull(outMsg.Parts);
        Assert.Equal(2, outMsg.Parts!.Count);
    }

    [Fact]
    public void PreservesRoleAndToolCallId()
    {
        var message = new ChatMessage(ChatMessageRoles.Tool, BigJsonArray())
        {
            ToolCallId = "call_123",
            Name = "search",
        };
        var compressor = new HeadroomCompressor(new HeadroomOptions { ProtectRecent = 0 });
        CompressionResult result = compressor.Compress([message]);

        ChatMessage outMsg = result.Messages[0];
        Assert.Equal(ChatMessageRoles.Tool, outMsg.Role);
        Assert.Equal("call_123", outMsg.ToolCallId);
        Assert.Equal("search", outMsg.Name);
    }

    [Fact]
    public void CcrRoundTripFromSentinel()
    {
        var options = new HeadroomOptions
        {
            ProtectRecent = 0,
            SmartCrusher = new SmartCrusherConfig { EnableLosslessCompaction = false, MaxItemsAfterCrush = 15 },
        };
        var compressor = new HeadroomCompressor(options);
        CompressionResult result = compressor.Compress([new ChatMessage(ChatMessageRoles.Tool, BigJsonArray())]);

        string compressed = result.Messages[0].Content!;
        Match m = SentinelRegex().Match(compressed);
        Assert.True(m.Success, $"no sentinel in output: {compressed}");

        string? original = compressor.Retrieve(m.Groups["hash"].Value);
        Assert.NotNull(original);
    }

    [Fact]
    public void TinyMessagesAreLeftAlone()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatMessageRoles.Tool, "small"),
            new(ChatMessageRoles.Assistant, "a"),
            new(ChatMessageRoles.Assistant, "b"),
            new(ChatMessageRoles.Assistant, "c"),
            new(ChatMessageRoles.Assistant, "d"),
        };
        var compressor = new HeadroomCompressor(new HeadroomOptions { ProtectRecent = 0 });
        CompressionResult result = compressor.Compress(messages);
        Assert.Equal(0, result.TokensSaved);
    }

    [GeneratedRegex(@"<<ccr:(?<hash>[0-9a-f]+) (?<n>\d+)_rows_offloaded>>")]
    private static partial Regex SentinelRegex();
}
