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
