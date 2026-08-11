namespace MtgEngine.Api.Services;

/// <summary>
/// Reproducible random sampling for prompt construction.
/// </summary>
/// <remarks>
/// Prompts that inject a random subset of candidate cards are uncacheable: the
/// prefix changes on every request, so Anthropic's prompt cache can never hit.
/// Sampling with a seed derived from the request makes the prompt a pure function of
/// its inputs, while different requests still get a spread subset rather than an
/// alphabetically biased one.
/// <para>
/// This buys cacheability, not reproducibility. The API does not guarantee identical
/// completions for identical input even at <c>temperature = 0</c>, so an identical
/// prompt can still yield a different deck.
/// </para>
/// </remarks>
internal static class DeterministicSample
{
    /// <summary>
    /// Returns up to <paramref name="count"/> items sampled from <paramref name="source"/>,
    /// spread across the whole list but identical for a given <paramref name="seedKey"/>.
    /// </summary>
    public static string[] Take(IReadOnlyList<string> source, int count, string seedKey)
    {
        if (source is null || source.Count == 0 || count <= 0)
            return [];

        if (source.Count <= count)
            return [.. source];

        // Seeded Fisher-Yates over a copy; partial shuffle is enough for `count` items.
        var pool = source.ToArray();
        var rng = new Random(StableSeed(seedKey));

        for (int i = 0; i < count; i++)
        {
            int j = rng.Next(i, pool.Length);
            (pool[i], pool[j]) = (pool[j], pool[i]);
        }

        return pool[..count];
    }

    /// <summary>
    /// 32-bit FNV-1a hash, used as an RNG seed.
    /// </summary>
    /// <remarks>
    /// <c>string.GetHashCode()</c> is deliberately randomised per process on .NET Core,
    /// so it would reintroduce the very nondeterminism this class exists to remove.
    /// </remarks>
    private static int StableSeed(string value)
    {
        const uint FnvOffsetBasis = 2166136261;
        const uint FnvPrime = 16777619;

        uint hash = FnvOffsetBasis;
        foreach (char c in value)
        {
            hash ^= c;
            hash *= FnvPrime;
        }

        // Random(int) accepts the full int range; reinterpret rather than truncate.
        return unchecked((int)hash);
    }
}
