using System.Collections.Concurrent;

namespace Headroom.Ccr;

/// <summary>
/// Thread-safe in-memory <see cref="ICcrStore"/>. Suitable for a single agent process; swap in a
/// persistent implementation (SQLite, Redis, …) for cross-session retrieval.
/// </summary>
public sealed class InMemoryCcrStore : ICcrStore
{
    private readonly ConcurrentDictionary<string, string> _store = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public void Put(string key, string original) => _store[key] = original;

    /// <inheritdoc />
    public string? Retrieve(string key) => _store.TryGetValue(key, out string? v) ? v : null;

    /// <inheritdoc />
    public bool Contains(string key) => _store.ContainsKey(key);

    /// <summary>Number of stored originals.</summary>
    public int Count => _store.Count;
}
