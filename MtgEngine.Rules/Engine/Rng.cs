using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Engine;

/// <summary>
/// The only source of randomness. SplitMix64 over (seed, counter): the same log replays the
/// same shuffles, coin flips and die rolls, which is what makes a reported game reproducible.
/// </summary>
public static class Rng
{
    public static (ulong Value, RngState Next) Draw(RngState state)
    {
        var z = state.Seed + (ulong)(state.Counter + 1) * 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        z ^= z >> 31;
        return (z, state with { Counter = state.Counter + 1 });
    }

    /// <summary>A number in [0, exclusiveMax).</summary>
    public static (int Value, RngState Next) Below(RngState state, int exclusiveMax)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(exclusiveMax, 0);
        var (value, next) = Draw(state);
        return ((int)(value % (ulong)exclusiveMax), next);
    }

    /// <summary>Fisher–Yates over a list, drawing from the state.</summary>
    public static (ImmutableList<T> Shuffled, RngState Next) Shuffle<T>(RngState state, IReadOnlyList<T> items)
    {
        var array = items.ToArray();
        var rng = state;
        for (var i = array.Length - 1; i > 0; i--)
        {
            var (j, next) = Below(rng, i + 1);
            rng = next;
            (array[i], array[j]) = (array[j], array[i]);
        }

        return ([.. array], rng);
    }
}
