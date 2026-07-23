using Headroom.Abstractions;
using Headroom.Ccr;
using Headroom.Routing;
using Headroom.Tokenization;
using LlmTornado.Chat;
using LlmTornado.Code;

namespace Headroom;

/// <summary>
/// Top-level entry point. Compresses a list of LlmTornado <see cref="ChatMessage"/> objects —
/// detecting the content of each eligible message, routing it to the right content-aware
/// compressor, and writing the result back into the same message shape. Drops into any
/// LlmTornado-based call site: compress the messages, then send them.
/// <para>
/// Carries over the reference project's guardrails: recent messages are protected, user messages
/// are skipped by default, tiny messages are left alone, and an inflation guard reverts to the
/// originals if "compression" ever grows the token count.
/// </para>
/// </summary>
/// <example>
/// <code>
/// var compressor = new HeadroomCompressor();
/// CompressionResult result = compressor.Compress(conversation.Messages);
/// // send result.Messages to the model; retrieve dropped originals via compressor.Ccr
/// </code>
/// </example>
public sealed class HeadroomCompressor
{
    private readonly HeadroomOptions _options;
    private readonly ITokenCounter _tokenCounter;
    private readonly ContentRouter _router;

    /// <summary>The CCR store holding originals of anything dropped, for reversible retrieval.</summary>
    public ICcrStore Ccr { get; }

    /// <summary>Create a compressor with the given options, token counter and CCR store.</summary>
    public HeadroomCompressor(
        HeadroomOptions? options = null,
        ITokenCounter? tokenCounter = null,
        ICcrStore? ccr = null)
    {
        _options = options ?? new HeadroomOptions();
        _tokenCounter = tokenCounter ?? CharTokenCounter.Instance;
        Ccr = ccr ?? new InMemoryCcrStore();
        _router = new ContentRouter(
            Ccr,
            _options.SmartCrusher,
            _options.Log,
            _options.Search,
            _options.Diff,
            _options.Text,
            _options.CompressPlainText,
            _options.CompressSourceCode);
    }

    /// <summary>Retrieve the original content offloaded under <paramref name="hash"/>, or null.</summary>
    public string? Retrieve(string hash) => Ccr.Retrieve(hash);

    /// <summary>Compress <paramref name="messages"/>. The input list is never mutated.</summary>
    public CompressionResult Compress(IReadOnlyList<ChatMessage> messages)
    {
        if (messages.Count == 0)
        {
            return new CompressionResult(messages, 0, 0, []);
        }

        int protectFrom = Math.Max(0, messages.Count - _options.ProtectRecent);
        string? context = ExtractContext(messages);

        int tokensBefore = 0;
        var output = new List<ChatMessage>(messages.Count);
        var transforms = new List<string>();

        for (int i = 0; i < messages.Count; i++)
        {
            ChatMessage message = messages[i];
            tokensBefore += CountMessageTokens(message);

            bool eligible = i < protectFrom && IsRoleEligible(message.Role);
            if (!eligible)
            {
                output.Add(message);
                continue;
            }

            output.Add(CompressMessage(message, context, transforms));
        }

        int tokensAfter = output.Sum(CountMessageTokens);

        // Inflation guard: never ship a result larger than the input.
        if (tokensAfter > tokensBefore)
        {
            return new CompressionResult(messages, tokensBefore, tokensBefore, ["inflation_guard:reverted"]);
        }

        return new CompressionResult(output, tokensBefore, tokensAfter, transforms);
    }

    private ChatMessage CompressMessage(ChatMessage message, string? context, List<string> transforms)
    {
        // Prefer explicit string content; fall back to text parts.
        if (!string.IsNullOrEmpty(message.Content))
        {
            if (!ShouldCompress(message.Content))
            {
                return message;
            }

            CompressionOutcome outcome = _router.Route(message.Content, context);
            if (!outcome.WasModified)
            {
                return message;
            }

            transforms.Add(outcome.Strategy);
            return CloneWithContent(message, outcome.Compressed);
        }

        if (message.Parts is { Count: > 0 })
        {
            var newParts = new List<ChatMessagePart>(message.Parts.Count);
            bool anyChanged = false;
            foreach (ChatMessagePart part in message.Parts)
            {
                if (part.Type == ChatMessageTypes.Text
                    && !string.IsNullOrEmpty(part.Text)
                    && ShouldCompress(part.Text))
                {
                    CompressionOutcome outcome = _router.Route(part.Text, context);
                    if (outcome.WasModified)
                    {
                        transforms.Add(outcome.Strategy);
                        newParts.Add(new ChatMessagePart(outcome.Compressed));
                        anyChanged = true;
                        continue;
                    }
                }

                newParts.Add(part);
            }

            if (anyChanged)
            {
                return CloneWithParts(message, newParts);
            }
        }

        return message;
    }

    private bool ShouldCompress(string text) => _tokenCounter.Count(text) >= _options.MinTokensToCompress;

    private bool IsRoleEligible(ChatMessageRoles? role) => role switch
    {
        ChatMessageRoles.Tool => true,
        ChatMessageRoles.Assistant => true,
        ChatMessageRoles.System => _options.CompressSystemMessages,
        ChatMessageRoles.User => _options.CompressUserMessages,
        _ => _options.CompressUserMessages,
    };

    private int CountMessageTokens(ChatMessage message)
    {
        int total = _tokenCounter.Count(message.Content);
        if (message.Parts is not null)
        {
            foreach (ChatMessagePart part in message.Parts)
            {
                if (part.Type == ChatMessageTypes.Text)
                {
                    total += _tokenCounter.Count(part.Text);
                }
            }
        }

        return total;
    }

    private string? ExtractContext(IReadOnlyList<ChatMessage> messages)
    {
        // Bias relevance toward the most recent user turn.
        for (int i = messages.Count - 1; i >= 0; i--)
        {
            if (messages[i].Role == ChatMessageRoles.User)
            {
                string? text = messages[i].Content;
                if (!string.IsNullOrEmpty(text))
                {
                    return text;
                }

                if (messages[i].Parts is { } parts)
                {
                    ChatMessagePart? textPart = parts.FirstOrDefault(p => p.Type == ChatMessageTypes.Text);
                    if (textPart?.Text is { Length: > 0 } t)
                    {
                        return t;
                    }
                }
            }
        }

        return null;
    }

    private static ChatMessage CloneWithContent(ChatMessage source, string content)
    {
        var clone = new ChatMessage(source.Role ?? ChatMessageRoles.Unknown, content, source.Id)
        {
            Name = source.Name,
            ToolCallId = source.ToolCallId,
            ToolCalls = source.ToolCalls,
            Reasoning = source.Reasoning,
            ReasoningContent = source.ReasoningContent,
        };
        return clone;
    }

    private static ChatMessage CloneWithParts(ChatMessage source, IEnumerable<ChatMessagePart> parts)
    {
        var clone = new ChatMessage(source.Role ?? ChatMessageRoles.Unknown, parts, source.Id)
        {
            Name = source.Name,
            ToolCallId = source.ToolCallId,
            ToolCalls = source.ToolCalls,
            Reasoning = source.Reasoning,
            ReasoningContent = source.ReasoningContent,
        };
        return clone;
    }
}
