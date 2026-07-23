using Headroom;
using Headroom.Ccr;
using Headroom.Tokenization;
using Xunit;

namespace Headroom.Tests;

public class CharTokenCounterTests
{
    [Fact]
    public void EmptyIsZero()
    {
        Assert.Equal(0, CharTokenCounter.Instance.Count(null));
        Assert.Equal(0, CharTokenCounter.Instance.Count(string.Empty));
    }

    [Fact]
    public void RoughlyFourCharsPerToken()
    {
        Assert.Equal(1, CharTokenCounter.Instance.Count("abc"));   // ceil(3/4)
        Assert.Equal(1, CharTokenCounter.Instance.Count("abcd"));  // ceil(4/4)
        Assert.Equal(2, CharTokenCounter.Instance.Count("abcde")); // ceil(5/4)
    }

    [Fact]
    public void CustomRatio()
    {
        var counter = new CharTokenCounter(charsPerToken: 2);
        Assert.Equal(3, counter.Count("abcde")); // ceil(5/2)
    }

    [Fact]
    public void RejectsNonPositiveRatio()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new CharTokenCounter(0));
    }
}

public class AdaptiveSizerTests
{
    [Fact]
    public void SmallCollectionsKeepEverything()
    {
        var items = Enumerable.Range(0, 5).Select(i => $"item{i}").ToList();
        Assert.Equal(5, AdaptiveSizer.ComputeOptimalK(items, minK: 3, maxK: 15));
    }

    [Fact]
    public void HighlyRepetitiveKeepsFew()
    {
        var items = Enumerable.Repeat("same", 100).ToList();
        int k = AdaptiveSizer.ComputeOptimalK(items, minK: 3, maxK: 50);
        Assert.True(k <= 3, $"expected <= 3 for near-total redundancy, got {k}");
    }

    [Fact]
    public void DiverseKeepsMoreThanRepetitive()
    {
        var diverse = Enumerable.Range(0, 100).Select(i => $"unique-{i}-{i * 7}").ToList();
        var repetitive = Enumerable.Range(0, 100).Select(i => i % 4 == 0 ? $"x{i}" : "same").ToList();
        int kDiverse = AdaptiveSizer.ComputeOptimalK(diverse, minK: 3, maxK: 100);
        int kRepetitive = AdaptiveSizer.ComputeOptimalK(repetitive, minK: 3, maxK: 100);
        Assert.True(kDiverse > kRepetitive);
    }

    [Fact]
    public void RespectsMaxCap()
    {
        var items = Enumerable.Range(0, 100).Select(i => $"unique-{i}").ToList();
        Assert.True(AdaptiveSizer.ComputeOptimalK(items, minK: 3, maxK: 15) <= 15);
    }

    [Fact]
    public void MinKIsHardFloor()
    {
        var items = Enumerable.Range(0, 100).Select(i => $"unique-{i}").ToList();
        // maxK smaller than minK: minK wins (documented behaviour).
        Assert.Equal(10, AdaptiveSizer.ComputeOptimalK(items, minK: 10, maxK: 5));
    }
}

public class CcrStoreTests
{
    [Fact]
    public void RoundTrips()
    {
        var store = new InMemoryCcrStore();
        store.Put("abc", "original content");
        Assert.True(store.Contains("abc"));
        Assert.Equal("original content", store.Retrieve("abc"));
        Assert.Null(store.Retrieve("missing"));
        Assert.Equal(1, store.Count);
    }

    [Fact]
    public void Md5Hex24IsStableAndLength24()
    {
        string h1 = Hashing.Md5Hex24("hello");
        string h2 = Hashing.Md5Hex24("hello");
        Assert.Equal(24, h1.Length);
        Assert.Equal(h1, h2);
        Assert.NotEqual(h1, Hashing.Md5Hex24("world"));
    }
}
