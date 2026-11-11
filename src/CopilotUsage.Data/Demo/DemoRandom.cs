namespace CopilotUsage.Data.Demo;

/// <summary>Small deterministic PRNG (SplitMix64) so demo data never changes between runs or runtimes.</summary>
internal sealed class DemoRandom
{
    private ulong _state;

    public DemoRandom(params object[] seedParts) => _state = Hash(seedParts);

    public static ulong Hash(params object[] parts)
    {
        // FNV-1a over the textual form (string.GetHashCode is randomized per process, so it cannot be used).
        var h = 14695981039346656037UL;
        foreach (var p in parts)
            foreach (var ch in (p.ToString() ?? "") + "\u001f")
            {
                h ^= ch;
                h *= 1099511628211UL;
            }
        return h;
    }

    public ulong NextULong()
    {
        _state += 0x9E3779B97F4A7C15UL;
        var z = _state;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    /// <summary>Uniform in [0, 1).</summary>
    public double NextDouble() => (NextULong() >> 11) * (1.0 / (1UL << 53));

    public double Between(double lo, double hi) => lo + (hi - lo) * NextDouble();

    public int Between(int loInclusive, int hiInclusive) => loInclusive + (int)(NextDouble() * (hiInclusive - loInclusive + 1));

    public T Pick<T>(IReadOnlyList<T> items) => items[Between(0, items.Count - 1)];
}
