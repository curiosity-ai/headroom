# Headroom for .NET

A C# reimplementation of the **concept** behind [Headroom](https://github.com/chopratejas/headroom):
a **content-aware, reversible context-compression layer for LLM agents**. It compresses
everything an agent reads — tool outputs, logs, search results, diffs, JSON data and prose —
*before* it reaches the model, so the same answers cost a fraction of the tokens.

This port is built to sit **on top of [LlmTornado](https://github.com/lofcz/LlmTornado)**: it
consumes and produces `LlmTornado.Chat.ChatMessage` objects, so it drops into any
LlmTornado-based call site.

> The original project is preserved verbatim under [`reference/`](reference/) (Rust core +
> Python package). It is the source of truth for algorithm behaviour and is used to derive the
> C# implementation. The reference is **not** built by this repo.

## The core idea (how it compresses tokens)

Most tokens an agent burns are not the user's words — they are **bulk machine output**: a
100-row JSON API response, a 5,000-line build log, a grep with 400 hits, a giant diff. That
content is highly **redundant and structured**, and the model rarely needs all of it. Headroom
exploits this in four stages:

```
 messages ─▶ CacheAligner ─▶ ContentRouter ─▶ [ content-aware compressor ] ─▶ CCR ─▶ model
                                              ├─ SmartCrusher    (JSON arrays)
                                              ├─ LogCompressor   (build/test logs)
                                              ├─ SearchCompressor(grep/ripgrep)
                                              ├─ DiffCompressor  (unified diffs)
                                              └─ TextCrusher     (prose fallback)
```

1. **ContentRouter / ContentDetector** — sniffs each block of text and decides *what* it is
   (JSON array, source code, search results, build log, git diff, HTML, plain text) by parsing
   and by weighted pattern heuristics with a fixed priority order.
2. **Content-aware compressors** — each format has a purpose-built strategy:
   - **SmartCrusher (JSON)** — the flagship, 60–95% savings. Given a JSON array of objects it
     (a) analyses each field's *uniqueness*, *constancy* and *numeric distribution*; (b) decides
     whether the array is even *safe* to sample (crushability gate — unique entities with no
     signal are kept whole); (c) keeps a principled subset: a fraction of the head, a fraction of
     the tail, statistical **outliers**, **change-points** in numeric series, rows containing
     **error keywords**, and query-relevant rows, after **de-duplicating** identical rows; then
     drops the rest. It also has a **lossless** path: when the rows are a clean homogeneous
     table it re-encodes them as a compact `csv-schema` block (`[N]{col:type,...}` + CSV rows)
     which is fully recoverable and needs no retrieval round-trip.
   - **LogCompressor** — detects the tool (pytest/npm/cargo/jest/make), classifies each line's
     level, keeps errors/failures (with a few lines of context), warnings (de-duplicated),
     collapsed stack traces and summary lines; drops the noise; appends an omitted-count line.
   - **SearchCompressor** — parses `file:line:content`, groups by file, scores each hit
     (error/keyword/query relevance), keeps the top few per file plus first/last, and summarises
     the rest as `[... and N more matches ...]`.
   - **DiffCompressor** — parses unified diffs, caps hunks-per-file and files, trims surrounding
     context lines to a couple each, and adds a `[N files changed, +A -D]` footer.
   - **TextCrusher** — extractive prose compression: split into segments, score by
     recency + BM25 relevance to the query + salience (errors, identifiers, numbers), keep the
     top segments up to a target character budget, skipping near-duplicates.
3. **CCR (Cache-and-Retrieve / reversible compression)** — whenever a lossy step drops content,
   the *original* is stored keyed by a short content hash, and a **sentinel marker** is left in
   the prompt (`<<ccr:HASH N_rows_offloaded>>` for dropped JSON rows,
   `[... Retrieve more: hash=HASH]` for logs/search/diffs). A retrieval tool can hand the model
   the original bytes back on demand — so compression is **reversible**, never destructive.
4. **CacheAligner** — leaves the recent/active tail of the conversation untouched so provider
   KV-caches keep hitting, and only compresses the stable "cold" zone.

### Guardrails carried over from the original

- **Inflation guard** — if "compression" ever produces *more* tokens, the original is returned.
- **Protect-recent** — the last *N* messages (active conversation) are never compressed.
- **Skip user messages by default** — for coding-agent workloads user turns are short and
  important; bulk lives in tool/assistant outputs. Configurable.
- **Minimum size threshold** — blocks below a token floor are left alone.

## Public API shape

```csharp
using Headroom;
using LlmTornado.Chat;

var compressor = new HeadroomCompressor();               // sensible defaults
CompressionResult result = compressor.Compress(messages); // messages: IReadOnlyList<ChatMessage>

result.Messages;          // List<ChatMessage> — same shape, fewer tokens
result.TokensBefore;      // estimated token count in
result.TokensAfter;      // estimated token count out
result.TokensSaved;
result.CompressionRatio;  // fraction removed (0.0 = nothing, 1.0 = everything)
result.TransformsApplied; // e.g. ["smart_crush:1", "log_compress:1"]

// Reverse a CCR marker the model asked about:
string? original = compressor.Ccr.Retrieve(hash);
```

Individual compressors are also usable directly (`new SmartCrusher().Crush(json)`), and
`ContentRouter` / `ContentDetector` are public for advanced callers.

## Fidelity: what is faithful vs. simplified

This is a faithful port of the **concept and architecture**, not a byte-for-byte clone of the
Rust core. Deliberate scope decisions:

- **Faithful**: content detection order & thresholds; the JSON analyse→gate→select→sentinel
  pipeline; log/search/diff/text strategies; CCR sentinels & reversibility; the guardrails.
- **Simplified**: the adaptive line/row budget (`AdaptiveSizer`) uses a clear diversity-based
  heuristic rather than the Rust core's SimHash + Kneedle + zlib validation combo; relevance is
  a lightweight BM25 rather than the hybrid embedding scorer.
- **Out of scope**: the ML `Kompress` transformer model (ONNX), ICU-based CJK segmentation, the
  proxy / MCP / `wrap` / `learn` tooling. Those are product surface, not the compression concept.

See [`TODO.md`](TODO.md) for the work breakdown and status.

## Layout

```
src/Headroom/            the library (net10.0)
tests/Headroom.Tests/    xUnit tests
reference/               original headroom project (Rust + Python), read-only reference
Headroom.slnx
```

## Building & testing

```bash
dotnet build
dotnet test
```

Targets **.NET 10**, C# `latest`, nullable enabled, warnings-as-errors. The only runtime
dependency is `LlmTornado`.
