# TODO — Headroom for .NET

Work breakdown for the C# port. See [`CLAUDE.md`](CLAUDE.md) for the concept and architecture.

## Milestone 0 — repo prep ✅
- [x] Study how the original compresses tokens (SmartCrusher, log/search/diff/text, detector, CCR).
- [x] Move the original source into `reference/`.
- [x] Remove CI/CD pipeline code from the repo.
- [x] Write `CLAUDE.md` and `TODO.md`.
- [x] Scaffold the .NET 10 solution (`Headroom` library + `Headroom.Tests`, LlmTornado dependency).

## Milestone 1 — foundations
- [x] `ContentType`, `DetectionResult` and the `IContentCompressor` / `CompressionOutcome` contracts.
- [x] `ITokenCounter` + default `char/4` estimator (matches the reference's token-saved heuristic).
- [x] `ICcrStore` + `InMemoryCcrStore`; content hashing (MD5-24 / SHA-family short hashes).
- [x] `AdaptiveSizer` — diversity-based budget helper (simplified port of `compute_optimal_k`).

## Milestone 2 — content detection
- [x] `ContentDetector` — port the priority order and thresholds:
      JSON → diff(≥.7) → HTML(≥.7) → search(≥.6) → log(≥.5) → code(≥.5) → plain-text.
- [x] JSON detection by parsing (arrays, objects, concatenated web-search shape, wrapped payloads).

## Milestone 3 — compressors
- [x] **SmartCrusher** (JSON arrays): field analysis, crushability gate, anchor/outlier/
      change-point/error/dedup selection, CCR row-drop sentinel, lossless `csv-schema` compaction.
- [x] **LogCompressor**: format detect, level classify, error/warning/stack/summary selection,
      warning dedup, omitted-count line, CCR marker.
- [x] **SearchCompressor**: `file:line:content` parse, per-file grouping/scoring/selection,
      `[... and N more]` summaries, CCR marker.
- [x] **DiffCompressor**: unified-diff parse, hunk/file caps, context trimming, change footer,
      CCR marker.
- [x] **TextCrusher**: segment split, recency+relevance+salience scoring, budgeted extraction,
      near-duplicate skipping.

## Milestone 4 — routing & top-level integration
- [x] `ContentRouter` — detect → dispatch to the right compressor.
- [x] `HeadroomCompressor` over `IReadOnlyList<ChatMessage>`: text extraction from
      `Content`/`Parts`, protect-recent, skip-user/skip-system, min-token threshold,
      inflation guard, per-transform stats.
- [x] `HeadroomOptions` / `CompressionResult`.

## Milestone 5 — tests
- [x] Content detector: one case per content type + priority/threshold edges.
- [x] SmartCrusher: lossy drop + sentinel, lossless csv-schema, crushability skip, dedup, passthrough.
- [x] Log / search / diff / text compressors: golden savings + marker shape.
- [x] CCR round-trip (compress → read sentinel hash → retrieve original).
- [x] `HeadroomCompressor`: protect-recent, skip-user, inflation guard, LlmTornado `Parts` handling.
- [x] Token counter + adaptive sizer unit tests.

## Backlog / future
- [ ] Query-aware relevance via real embeddings (hybrid scorer) instead of BM25-only.
- [ ] Byte-exact `compute_optimal_k` (SimHash + Kneedle + zlib) if strict parity is needed.
- [ ] Config/YAML-TOML/tabular detectors from the Python superset.
- [ ] Streaming / incremental compression across a live conversation loop.
- [ ] NuGet packaging & sample app.
