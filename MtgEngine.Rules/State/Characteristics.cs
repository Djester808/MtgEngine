using System.Collections.Immutable;
using MtgEngine.Domain.Enums;
using MtgEngine.Rules.Abilities;

namespace MtgEngine.Rules.State;

/// <summary>An object's characteristics after every continuous effect has been applied.</summary>
public sealed record ComputedCharacteristics
{
    public int? Power { get; init; }

    public int? Toughness { get; init; }

    public CardType CardTypes { get; init; }

    public KeywordAbility Keywords { get; init; }

    public Guid ControllerId { get; init; }

    public ImmutableList<string> Subtypes { get; init; } = [];

    public ImmutableList<ManaColor> Colors { get; init; } = [];

    public bool IsCreature => CardTypes.HasFlag(CardType.Creature);

    public bool Has(KeywordAbility keyword) => Keywords.HasFlag(keyword);
}

/// <summary>
/// What an object's characteristics currently are, as opposed to what was printed on it.
/// </summary>
/// <remarks>
/// Nothing stores a permanent's power. It is computed here, every time, by starting from the
/// printed values and applying every applicable continuous effect in layer order (CR 613.1).
/// <para>
/// This is the direct fix for the bug class that ended the previous engine. There,
/// <c>IStaticAbility.Apply(state) =&gt; state</c> wrote a lord's bonus into each creature as a
/// mutation, so when the lord left the battlefield nothing took the bonus back off. Here a lord's
/// effect exists only while the lord is on the battlefield to produce it, and it stops existing
/// the instant it is not — because the effect is never recorded anywhere in the first place.
/// </para>
/// <para>
/// Recomputed on demand rather than cached. Caching would need invalidating on every change to
/// the battlefield, every counter, and every timestamp, and a stale cache here is exactly the
/// failure being avoided. If it becomes a measured problem, memoise on a state version — do not
/// go back to writing values into permanents.
/// </para>
/// </remarks>
public static class Characteristics
{
    /// <summary>Everything about an object, after the layers (CR 613).</summary>
    public static ComputedCharacteristics Of(
        GameState state, IAbilitySource abilities, GameObject obj)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(abilities);
        ArgumentNullException.ThrowIfNull(obj);

        var builder = new CharacteristicsBuilder(obj);

        // CR 613.1: start with the printed values, then apply each applicable effect in layer
        // order, and within a layer in timestamp order (CR 613.7).
        foreach (var (effect, source) in Applicable(state, abilities, obj))
            effect.Apply(builder);

        return builder.Build();
    }

    /// <summary>Current power (CR 208, 613.4).</summary>
    public static int? PowerOf(GameState state, IAbilitySource abilities, GameObject obj) =>
        Of(state, abilities, obj).Power;

    /// <summary>Current toughness (CR 208, 613.4).</summary>
    public static int? ToughnessOf(GameState state, IAbilitySource abilities, GameObject obj) =>
        Of(state, abilities, obj).Toughness;

    /// <summary>Whether the object is currently a creature (CR 302.1).</summary>
    public static bool IsCreature(GameState state, IAbilitySource abilities, GameObject obj) =>
        Of(state, abilities, obj).IsCreature;

    /// <summary>Whether the object currently has a keyword ability (CR 702).</summary>
    public static bool HasKeyword(
        GameState state, IAbilitySource abilities, GameObject obj, KeywordAbility keyword) =>
        Of(state, abilities, obj).Has(keyword);

    /// <summary>
    /// Every continuous effect that applies to this object, in the order the rules apply them.
    /// </summary>
    /// <remarks>
    /// Ordered by layer, then timestamp (CR 613.7). Dependency (CR 613.8) can reorder effects
    /// within a layer; it is rare enough, and subtle enough, that guessing at it would be worse
    /// than not doing it — this applies straight timestamp order and will need revisiting when a
    /// card that actually depends on another is implemented.
    /// </remarks>
    private static IEnumerable<(ContinuousEffectDefinition Effect, GameObject? Source)> Applicable(
        GameState state, IAbilitySource abilities, GameObject target)
    {
        var found = new List<(ContinuousEffectDefinition Effect, GameObject? Source, long Timestamp)>();

        // Static abilities of permanents on the battlefield (CR 604.2): their effects exist for
        // exactly as long as the permanent does.
        foreach (var id in state.Battlefield)
        {
            var source = state.GetObject(id);
            foreach (var effect in abilities.StaticsOf(source.Card))
            {
                if (effect.Applies(state, source, target))
                    found.Add((effect, source, source.Timestamp));
            }
        }

        // Counters modify power and toughness in layer 7c (CR 613.4c, 122.1c). They are not a
        // static ability of anything, so they are added here rather than found on a permanent.
        if (target.Permanent is not null && CounterDelta(target) != 0)
        {
            var delta = CounterDelta(target);
            found.Add((CounterEffect(delta), null, target.Timestamp));
        }

        // Effects created by a resolved spell or ability, which outlive their source (CR 613.7b).
        foreach (var floating in state.FloatingEffects)
        {
            if (!floating.AffectedIds.Contains(target.Id))
                continue;

            var definition = abilities.FloatingEffect(floating.DefinitionId);
            if (definition is not null)
                found.Add((definition, null, floating.Timestamp));
        }

        return found
            .OrderBy(f => (int)f.Effect.Layer)
            .ThenBy(f => f.Timestamp)
            .Select(f => (f.Effect, f.Source));
    }

    /// <summary>The +1/+1 and -1/-1 counters on a permanent, netted (CR 122.1c).</summary>
    private static int CounterDelta(GameObject obj)
    {
        if (obj.Permanent is null)
            return 0;

        return obj.Permanent.Counters.GetValueOrDefault(CounterKinds.PlusOnePlusOne)
            - obj.Permanent.Counters.GetValueOrDefault(CounterKinds.MinusOneMinusOne);
    }

    private static ContinuousEffectDefinition CounterEffect(int delta) => new()
    {
        Id = "counters",
        Layer = EffectLayer.PowerToughnessModify,
        Applies = (_, _, _) => true,
        Apply = builder => builder.Modify(delta, delta),
    };
}

/// <summary>The counter kinds the rules name directly (CR 122.1).</summary>
public static class CounterKinds
{
    public const string PlusOnePlusOne = "+1/+1";
    public const string MinusOneMinusOne = "-1/-1";
    public const string Loyalty = "loyalty";
}
