namespace MtgEngine.Rules.State;

/// <summary>
/// Element-wise comparison for the collections the state is built from.
/// </summary>
/// <remarks>
/// C# records generate equality field by field using <see cref="EqualityComparer{T}.Default"/>,
/// and the immutable collections do not implement structural equality — so a record holding one
/// compares it <em>by reference</em>. Two states with identical contents come out unequal, and,
/// worse, they come out unequal silently: the generated <c>==</c> looks like it means what a
/// reader assumes.
/// <para>
/// That is not a cosmetic problem here. "The state is a fold of the log" is the property this
/// engine was rebuilt to have, and the test that asserts it compares two states. With the
/// generated equality that test passes or fails for reasons unrelated to what it claims to
/// check, which is how a suite ends up green and wrong.
/// </para>
/// </remarks>
internal static class Structural
{
    /// <summary>True when both sequences hold equal elements in the same order.</summary>
    public static bool Same<T>(IReadOnlyList<T> left, IReadOnlyList<T> right)
    {
        if (ReferenceEquals(left, right))
            return true;

        if (left.Count != right.Count)
            return false;

        for (var i = 0; i < left.Count; i++)
        {
            if (!EqualityComparer<T>.Default.Equals(left[i], right[i]))
                return false;
        }

        return true;
    }

    /// <summary>True when both sets hold the same elements, in whatever order they are held.</summary>
    /// <remarks>
    /// A set had no overload here, and the one field that is a set — the armed state triggers —
    /// was therefore left out of <see cref="GameState.Equals(GameState?)"/> rather than compared
    /// wrongly. That is the quiet half of the same defect this class exists for: the omission
    /// looked like a decision, and two states differing only in which state triggers had already
    /// fired compared equal, so <c>Replay(log) == State</c> passed straight through a divergence
    /// in them (CR 603.8) and the second firing it would cause.
    /// <para>
    /// Order is not part of what a set means, so this compares membership rather than sequence.
    /// </para>
    /// </remarks>
    public static bool Same<T>(IReadOnlySet<T> left, IReadOnlySet<T> right)
    {
        if (ReferenceEquals(left, right))
            return true;

        if (left.Count != right.Count)
            return false;

        foreach (var element in left)
        {
            if (!right.Contains(element))
                return false;
        }

        return true;
    }

    /// <summary>True when both dictionaries hold the same keys mapped to equal values.</summary>
    public static bool Same<TKey, TValue>(
        IReadOnlyDictionary<TKey, TValue> left, IReadOnlyDictionary<TKey, TValue> right)
        where TKey : notnull
    {
        if (ReferenceEquals(left, right))
            return true;

        if (left.Count != right.Count)
            return false;

        foreach (var (key, value) in left)
        {
            if (!right.TryGetValue(key, out var other) ||
                !EqualityComparer<TValue>.Default.Equals(value, other))
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>
/// A delayed triggered ability waiting for its moment (CR 603.7).
/// </summary>
/// <remarks>
/// It names what it will do by <see cref="EffectId"/> rather than holding an effect, for the same
/// reason <c>GenerativeEffects</c> names a pump: the name is what lands in the log, and a log has
/// to rebuild the whole game without holding a closure.
/// </remarks>
public sealed record DelayedTrigger
{
    public required Guid Id { get; init; }

    public required Guid ControllerId { get; init; }

    /// <summary>What it will act on. CR 603.7c: gone from its zone means it does nothing.</summary>
    public required ObjectId SubjectId { get; init; }

    /// <summary>The step it waits for.</summary>
    public required TurnStep Step { get; init; }

    /// <summary>What it does, by name: "sacrifice" or "exile".</summary>
    public required string EffectId { get; init; }

    /// <summary>
    /// The turn it was created on, so "the next end step" is not the one already in progress.
    /// </summary>
    public required int TurnCreated { get; init; }
}
