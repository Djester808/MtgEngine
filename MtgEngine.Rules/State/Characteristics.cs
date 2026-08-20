using MtgEngine.Domain.Enums;

namespace MtgEngine.Rules.State;

/// <summary>
/// What an object's characteristics currently are, as opposed to what was printed on it.
/// </summary>
/// <remarks>
/// Nothing asks a permanent for its power. It is asked for here, computed, every time — because
/// power is the printed value with every applicable continuous effect layered over it (CR 613),
/// and the moment it is cached somewhere it starts disagreeing with the effects that produce it.
/// That disagreement is exactly what broke the previous engine: static abilities were applied by
/// writing into state, so a buff outlived the lord that granted it.
/// <para>
/// Today this knows about printed values and counters (CR 613.4d puts counters in layer 7d).
/// The layer system in slice 4 goes <em>inside</em> these methods, so every caller — including
/// state-based actions, which are the fussiest consumer — is already asking the right question.
/// </para>
/// </remarks>
public static class Characteristics
{
    /// <summary>Current power (CR 208, 613.4d).</summary>
    public static int? PowerOf(GameState state, GameObject obj)
    {
        ArgumentNullException.ThrowIfNull(obj);

        return obj.Card.Power is null ? null : obj.Card.Power + CounterDelta(obj);
    }

    /// <summary>Current toughness (CR 208, 613.4d). Null for anything that is not a creature.</summary>
    public static int? ToughnessOf(GameState state, GameObject obj)
    {
        ArgumentNullException.ThrowIfNull(obj);

        return obj.Card.Toughness is null ? null : obj.Card.Toughness + CounterDelta(obj);
    }

    /// <summary>Whether the object is currently a creature (CR 302.1).</summary>
    public static bool IsCreature(GameState state, GameObject obj)
    {
        ArgumentNullException.ThrowIfNull(obj);

        return obj.Card.CardTypes.HasFlag(CardType.Creature);
    }

    /// <summary>Whether the object currently has a keyword ability (CR 702).</summary>
    public static bool HasKeyword(GameState state, GameObject obj, KeywordAbility keyword)
    {
        ArgumentNullException.ThrowIfNull(obj);

        return obj.Card.Keywords.HasFlag(keyword);
    }

    /// <summary>
    /// The +1/+1 and -1/-1 counters on a permanent, netted (CR 122.1c, 613.4d).
    /// </summary>
    /// <remarks>
    /// The two kinds also annihilate as a state-based action (CR 704.5q), so in a settled game
    /// only one kind is present. This nets them anyway, because characteristics are asked for
    /// mid-resolution too, when state-based actions have not run yet (CR 704.4).
    /// </remarks>
    private static int CounterDelta(GameObject obj)
    {
        if (obj.Permanent is null)
            return 0;

        var counters = obj.Permanent.Counters;
        return counters.GetValueOrDefault(CounterKinds.PlusOnePlusOne)
            - counters.GetValueOrDefault(CounterKinds.MinusOneMinusOne);
    }
}

/// <summary>The counter kinds the rules name directly (CR 122.1).</summary>
public static class CounterKinds
{
    public const string PlusOnePlusOne = "+1/+1";
    public const string MinusOneMinusOne = "-1/-1";
    public const string Loyalty = "loyalty";
}
