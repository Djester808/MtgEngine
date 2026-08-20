using System.Collections.Immutable;

namespace MtgEngine.Rules.Engine;

/// <summary>
/// Every random choice the game makes, in one place, seeded.
/// </summary>
/// <remarks>
/// This runs on the server and nowhere else. A client that could shuffle could read the result.
/// <para>
/// The seed makes a test reproducible; it is <em>not</em> what makes a game replayable. Seeded
/// <see cref="Random"/> is not contractually stable across .NET versions, so the shuffle's
/// resulting order is written into <see cref="Events.LibraryShuffled"/> and the log replays from
/// that. Recording the outcome rather than the input also means the algorithm here can be
/// changed later without invalidating a single stored game.
/// </para>
/// </remarks>
public sealed class GameRandom(int seed)
{
    private readonly Random _random = new(seed);

    /// <summary>The seed this instance was created with, for a test that wants to repeat a run.</summary>
    public int Seed { get; } = seed;

    /// <summary>
    /// A uniformly random permutation, by Fisher-Yates (CR 701.24a asks only that the result be
    /// randomised; the algorithm is written out rather than delegated so the bias is inspectable).
    /// </summary>
    public ImmutableList<T> Shuffle<T>(IReadOnlyList<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        var working = items.ToArray();
        for (var i = working.Length - 1; i > 0; i--)
        {
            var j = _random.Next(i + 1);
            (working[i], working[j]) = (working[j], working[i]);
        }

        return [.. working];
    }

    /// <summary>Picks one of the seated players, for deciding who goes first (CR 103.1).</summary>
    public T Choose<T>(IReadOnlyList<T> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return options.Count == 0
            ? throw new ArgumentException("Nothing to choose from.", nameof(options))
            : options[_random.Next(options.Count)];
    }
}
