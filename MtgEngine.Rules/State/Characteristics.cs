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

        // CR 613.1: start with the printed values, then apply the effects layer by layer. Within
        // a layer the order is by timestamp (CR 613.7) unless one effect depends on another, in
        // which case dependency wins (CR 613.8).
        foreach (var layer in Candidates(state, abilities, obj)
            .GroupBy(c => c.Effect.Layer)
            .OrderBy(g => (int)g.Key))
        {
            foreach (var candidate in InDependencyOrder(state, [.. layer], builder))
            {
                // Applicability is asked again here rather than reused: an effect earlier in the
                // same layer may have just brought this one into range.
                if (candidate.Effect.Applies(state, candidate.Source, builder))
                    candidate.Effect.Apply(builder);
            }
        }

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

    /// <summary>One continuous effect that might apply to the object being computed.</summary>
    private readonly record struct Candidate(
        ContinuousEffectDefinition Effect, GameObject? Source, long Timestamp);

    /// <summary>
    /// Every continuous effect that could apply to this object.
    /// </summary>
    /// <remarks>
    /// Gathered without asking whether each one applies: that question is answered as its layer
    /// is reached, because an effect in an earlier layer can bring a later one into range —
    /// turning a creature white makes an anthem that pumps white creatures start applying to it.
    /// </remarks>
    private static IEnumerable<Candidate> Candidates(
        GameState state, IAbilitySource abilities, GameObject target)
    {
        // Static abilities of permanents on the battlefield (CR 604.2): their effects exist for
        // exactly as long as the permanent does.
        foreach (var id in state.Battlefield)
        {
            var source = state.GetObject(id);
            foreach (var effect in abilities.StaticsOf(source.Card))
                yield return new Candidate(effect, source, source.Timestamp);
        }

        // Counters modify power and toughness in layer 7c (CR 613.4c, 122.1c). They are not a
        // static ability of anything, so they are added here rather than found on a permanent.
        if (target.Permanent is not null && CounterDelta(target) != 0)
            yield return new Candidate(CounterEffect(CounterDelta(target)), null, target.Timestamp);

        // Effects created by a resolved spell or ability, which outlive their source (CR 613.7b).
        foreach (var floating in state.FloatingEffects)
        {
            if (!floating.AffectedIds.Contains(target.Id))
                continue;

            var definition = abilities.FloatingEffect(floating.DefinitionId);
            if (definition is not null)
                yield return new Candidate(definition, null, floating.Timestamp);
        }
    }

    /// <summary>
    /// Orders one layer's effects, letting dependency override timestamp (CR 613.8).
    /// </summary>
    /// <remarks>
    /// CR 613.8a: an effect depends on another when applying that other would change what the
    /// first applies to, or what it does. That is answered by asking — applying the other to a
    /// throwaway copy and seeing whether the first's answer changes — rather than by a table of
    /// special cases, which would only ever cover the cards somebody thought of.
    /// <para>
    /// CR 613.8b: dependents wait until everything they depend on has been applied, and a
    /// dependency loop falls back to timestamp order. The loop case is why this is written as
    /// "take whatever is ready, and if nothing is, take the earliest" rather than as a topological
    /// sort that can fail.
    /// </para>
    /// </remarks>
    private static List<Candidate> InDependencyOrder(
        GameState state, List<Candidate> layer, CharacteristicsBuilder builder)
    {
        var remaining = layer.OrderBy(c => c.Timestamp).ToList();
        if (remaining.Count < 2)
            return remaining;

        var ordered = new List<Candidate>(remaining.Count);

        while (remaining.Count > 0)
        {
            // The earliest effect that nothing else still to come would change.
            var next = remaining.FirstOrDefault(
                c => !remaining.Any(other => !Equals(other, c) && DependsOn(state, c, other, builder)));

            // A dependency loop: CR 613.8b says ignore the rule and use timestamp order.
            if (next == default)
                next = remaining[0];

            ordered.Add(next);
            remaining.Remove(next);
        }

        return ordered;
    }

    /// <summary>Whether applying <paramref name="other"/> would change what this one does.</summary>
    private static bool DependsOn(
        GameState state, Candidate effect, Candidate other, CharacteristicsBuilder builder)
    {
        var before = effect.Effect.Applies(state, effect.Source, builder);

        var probe = builder.Copy();
        if (!other.Effect.Applies(state, other.Source, probe))
            return false;

        other.Effect.Apply(probe);
        return effect.Effect.Applies(state, effect.Source, probe) != before;
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
