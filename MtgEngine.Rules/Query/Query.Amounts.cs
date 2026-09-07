using MtgEngine.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Query;

/// <summary>
/// Numbers a card refers to. Counted when the effect asks, CR 608.2 — never cached, because a
/// count taken a step earlier is a different number.
/// </summary>
public sealed partial class Query
{
    public int Evaluate(GameState state, Amount amount, EffectContext context)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(amount);
        return amount switch
        {
            Fixed fixedAmount => fixedAmount.N,
            X => context.X ?? 0, // CR 107.3b: X is zero unless a value was chosen for it
            CountOf count => Candidates(state, count.Filter, context).Count(),
            PlayerCount players => Players(state, players.Who, context).Count,
            LifeOf life => Players(state, life.Who, context).Sum(p => state.Player(p).Life), // CR 119.1
            CardsInHandOf hand => Players(state, hand.Who, context)
                .Sum(p => state.In(ZoneKey.Of(Cards.Zone.Hand, p)).Count), // CR 402.1
            CardsInZoneOf zone => CardsIn(state, zone.Who, zone.Zone, zone.Matching, context),

            PowerOf power => Resolve(state, power.Of, context).Sum(id => _layers.Of(state, id).Power ?? 0), // CR 208.1
            ToughnessOf toughness => Resolve(state, toughness.Of, context).Sum(id => _layers.Of(state, id).Toughness ?? 0),
            ManaValueOf manaValue => Resolve(state, manaValue.Of, context)
                .Sum(id => ManaCosts.Value(_layers.Of(state, id).ManaCost, state.Objects[id])), // CR 202.3

            CountersOn counters => Resolve(state, counters.On, context).Sum(id => CountersOf(state, id, counters.Counter)), // CR 122.1
            CountersOnPlayer counters => Players(state, counters.Who, context)
                .Sum(p => state.Player(p).Counters.GetValueOrDefault(counters.Counter)),

            Cards.Devotion devotion => DevotionOf(state, devotion, context), // CR 700.5
            Domain domain => BasicLandTypes(state, domain.Who, context),

            GreatestAmong greatest => Among(state, greatest.Among, greatest.Of, context) is { Count: > 0 } values
                ? values.Max() : 0,
            TotalAmong total => Among(state, total.Among, total.Of, context).Sum(),

            ChosenNumber chosen => ChosenNumberOf(chosen.Key, context),
            Stored stored => context.Stored.GetValueOrDefault(stored.Key),
            TurnNumber => state.Turn.Number, // CR 500.1

            Sum sum => Evaluate(state, sum.Left, context) + Evaluate(state, sum.Right, context),
            Difference difference => Evaluate(state, difference.Left, context) - Evaluate(state, difference.Right, context),
            Times times => Evaluate(state, times.Value, context) * times.Factor,
            // CR 107.2: a fraction rounds down unless the effect says otherwise.
            Cards.Half half => half.RoundUp
                ? (Evaluate(state, half.Value, context) + 1) / 2
                : Evaluate(state, half.Value, context) / 2,
            Max max => Math.Max(Evaluate(state, max.Left, context), Evaluate(state, max.Right, context)),
            Min min => Math.Min(Evaluate(state, min.Left, context), Evaluate(state, min.Right, context)),

            ManaSpentOnThis => throw Unanswerable(nameof(ManaSpentOnThis), NoManaSpent),
            SpellsCastThisTurn => throw Unanswerable(nameof(SpellsCastThisTurn), NoHistory),
            DamageDealtThisTurn => throw Unanswerable(nameof(DamageDealtThisTurn), NoHistory),
            _ => throw Unanswerable(amount.GetType().Name, "no arm in the query layer"),
        };
    }

    /// <summary>Counters of one kind, or every counter when the amount names none, CR 122.1.</summary>
    private static int CountersOf(GameState state, ObjectId id, string? counter)
    {
        var obj = state.Objects[id];
        return counter is null ? obj.Counters.Values.Sum() : obj.Counters.GetValueOrDefault(counter);
    }

    /// <summary>Devotion, CR 700.5: mana symbols of the given colors among the mana costs of permanents the player controls.</summary>
    private int DevotionOf(GameState state, Cards.Devotion devotion, EffectContext ctx) =>
        Candidates(state, new ObjectFilter(ControlledBy: devotion.Who), ctx)
            .Sum(id => ManaCosts.Devotion(_layers.Of(state, id).ManaCost, devotion.Colors));

    /// <summary>Domain: the basic land types among lands the player controls, CR 205.3i.</summary>
    private int BasicLandTypes(GameState state, PlayerRef who, EffectContext ctx)
    {
        string[] basics = ["Plains", "Island", "Swamp", "Mountain", "Forest"];
        var lands = Candidates(state, new ObjectFilter(Types: CardType.Land, ControlledBy: who), ctx)
            .Select(id => _layers.Of(state, id))
            .ToList();
        return basics.Count(basic => lands.Any(land => HasSubtype(land, basic)));
    }

    /// <summary>
    /// "the greatest power among creatures you control": the inner amount is evaluated once per
    /// matching object, with that object as the one the amount speaks of, CR 608.2.
    /// </summary>
    private List<int> Among(GameState state, ObjectFilter among, Amount of, EffectContext ctx) =>
        [.. Candidates(state, among, ctx).Select(id => Evaluate(state, of, ctx with { Source = id }))];

    /// <summary>A number chosen earlier: stored as a count when it is one, else as the text of the choice.</summary>
    private static int ChosenNumberOf(string key, EffectContext ctx)
    {
        if (ctx.Stored.TryGetValue(key, out var stored))
            return stored;
        if (ctx.Chosen.TryGetValue(key, out var text)
            && int.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        throw Unanswerable(nameof(ChosenNumber), $"nothing was chosen for \"{key}\"");
    }
}
