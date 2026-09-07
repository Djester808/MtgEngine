using MtgEngine.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Query;

/// <summary>
/// The yes-or-no questions: an intervening "if" (CR 603.4), the "as long as" of a static
/// ability (CR 604.1), the "if" of an effect. The ability words are conditions here, each
/// spelled out as the count it stands for.
/// </summary>
public sealed partial class Query
{
    public bool Holds(GameState state, Condition condition, EffectContext context)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(condition);
        return condition switch
        {
            NotCondition not => !Holds(state, not.Of, context),
            And and => and.Of.All(c => Holds(state, c, context)),
            Or or => or.Of.Any(c => Holds(state, c, context)),

            Controls controls => Meets(CountControlled(state, controls, context), controls.Count, state, context),
            Cards.Compare compare => Satisfies(
                Evaluate(state, compare.Left, context), compare.Op, Evaluate(state, compare.Right, context)),

            IsYourTurn turn => turn.Value == (state.Turn.ActivePlayer == context.Controller), // CR 500.1
            IsStep step => state.Turn.Step == step.Step, // CR 500.1

            // The ability words, each the count its name stands for. CR 207.2c: an ability word
            // has no rules meaning of its own, so each is written out here.
            Threshold t => CardsIn(state, t.Who, Cards.Zone.Graveyard, null, context) >= 7,
            Delirium d => CardTypesIn(state, d.Who, context) >= 4,
            Metalcraft m => CountMatching(state, new ObjectFilter(Types: CardType.Artifact, ControlledBy: m.Who), context) >= 3,
            Hellbent h => CardsIn(state, h.Who, Cards.Zone.Hand, null, context) == 0,
            Ferocious f => CountMatching(
                state,
                new ObjectFilter(Types: CardType.Creature, ControlledBy: f.Who, Power: new Comparison(ComparisonOp.Ge, new Fixed(4))),
                context) >= 1,
            Formidable f => TotalPower(state, new ObjectFilter(Types: CardType.Creature, ControlledBy: f.Who), context) >= 8,
            Descend descend => PermanentCardsIn(state, descend.Who, context) >= descend.N,
            SpellMastery s => CardsIn(state, s.Who, Cards.Zone.Graveyard, new ObjectFilter(In: Cards.Zone.Graveyard, AnyTypes: CardType.Instant | CardType.Sorcery), context) >= 2,
            Coven c => DistinctPowers(state, c.Who, context) >= 3,
            Corrupted => Players(state, new EachOpponent(), context)
                .Any(p => state.Player(p).Counters.GetValueOrDefault(Counters.Poison) >= 3), // CR 122.1
            FatefulHour f => Players(state, f.Who, context).Any(p => state.Player(p).Life <= 5),

            HasKeyword keyword => Every(state, keyword.Of, context, id => _layers.Of(state, id).Has(keyword.Keyword)), // CR 702.1
            Cards.IsAttacking attacking => Every(state, attacking.Of, context, id => IsAttacking(state, id)),
            Cards.IsBlocking blocking => Every(state, blocking.Of, context, id => IsBlocking(state, id)),
            Cards.IsTapped tapped => Every(state, tapped.Of, context, id => state.Objects[id].Tapped),
            IsOnBattlefield battlefield => Every(state, battlefield.Of, context, id => state.Objects[id].Zone.Zone == Cards.Zone.Battlefield),
            IsInZone zone => Every(state, zone.Of, context, id => state.Objects[id].Zone.Zone == zone.Zone),
            Cards.Matches matches => Every(state, matches.Of, context, id => Matches(state, matches.Filter, id, context)),

            ChoiceIs choice => context.Chosen.TryGetValue(choice.Key, out var value)
                && string.Equals(value, choice.Value, StringComparison.OrdinalIgnoreCase),
            StoredIs stored => context.Stored.TryGetValue(stored.Key, out var n)
                && Satisfies(n, stored.Compare.Op, Evaluate(state, stored.Compare.Value, context)),
            TargetsAre targets => context.Targets.Count > 0
                && context.Targets.All(id => Matches(state, targets.Filter, id, context)), // CR 608.2b

            Lieutenant => throw Unanswerable(nameof(Lieutenant), NoCommander),
            Morbid => throw Unanswerable(nameof(Morbid), NoHistory),
            Revolt => throw Unanswerable(nameof(Revolt), NoHistory),
            Raid => throw Unanswerable(nameof(Raid), NoHistory),
            Paradox => throw Unanswerable(nameof(Paradox), NoHistory),
            FirstTimeThisTurn => throw Unanswerable(nameof(FirstTimeThisTurn), NoHistory),
            Adamant => throw Unanswerable(nameof(Adamant), NoManaSpent),
            WasKickedCondition => throw Unanswerable(nameof(WasKickedCondition), NoManaSpent),
            PaidAlternativeCost => throw Unanswerable(nameof(PaidAlternativeCost), NoManaSpent),
            CastFromZone => throw Unanswerable(nameof(CastFromZone), NoHistory),
            CityBlessing => throw Unanswerable(nameof(CityBlessing), NoDesignation),
            YouAreMonarch => throw Unanswerable(nameof(YouAreMonarch), NoDesignation),
            YouHaveInitiative => throw Unanswerable(nameof(YouHaveInitiative), NoDesignation),
            _ => throw Unanswerable(condition.GetType().Name, "no arm in the query layer"),
        };
    }

    private const string NoCommander = "no state records which object is a player's commander (CR 903.3); see seam-requests/E0.md";

    private const string NoManaSpent = "no state records what was paid to cast a spell (CR 601.2h); see seam-requests/E0.md";

    /// <summary>A condition about a selection holds when there is a selection and every object in it holds, CR 608.2.</summary>
    private bool Every(GameState state, Selector selector, EffectContext ctx, Func<ObjectId, bool> test)
    {
        var objects = Resolve(state, selector, ctx);
        return objects.Count > 0 && objects.All(id => state.Objects.ContainsKey(id) && test(id));
    }

    /// <summary>"you control three or more artifacts": the filter takes the controller the condition names.</summary>
    private int CountControlled(GameState state, Controls controls, EffectContext ctx)
    {
        var filter = controls.What.ControlledBy is null ? controls.What with { ControlledBy = controls.Who } : controls.What;
        return CountMatching(state, filter, ctx);
    }

    private int CountMatching(GameState state, ObjectFilter filter, EffectContext ctx) =>
        Candidates(state, filter, ctx).Count();

    private int CardsIn(GameState state, PlayerRef who, Cards.Zone zone, ObjectFilter? matching, EffectContext ctx)
    {
        var owners = Players(state, who, ctx);
        var filter = matching ?? new ObjectFilter(In: zone);
        return Candidates(state, filter with { In = zone }, ctx)
            .Count(id => owners.Contains(state.Objects[id].Owner));
    }

    /// <summary>Delirium counts card types among the cards in the graveyard, CR 205.2a.</summary>
    private int CardTypesIn(GameState state, PlayerRef who, EffectContext ctx)
    {
        var owners = Players(state, who, ctx);
        var types = CardType.None;
        foreach (var obj in state.Objects.Values.Where(o => o.Zone.Zone == Cards.Zone.Graveyard && owners.Contains(o.Owner)))
            types |= _layers.Of(state, obj.Id).Types;
        return System.Numerics.BitOperations.PopCount((uint)types);
    }

    /// <summary>Permanent cards, CR 110.4a: artifact, battle, creature, enchantment, land or planeswalker.</summary>
    private int PermanentCardsIn(GameState state, PlayerRef who, EffectContext ctx)
    {
        const CardType permanentTypes = CardType.Artifact | CardType.Battle | CardType.Creature
            | CardType.Enchantment | CardType.Land | CardType.Planeswalker;
        return CardsIn(state, who, Cards.Zone.Graveyard, new ObjectFilter(In: Cards.Zone.Graveyard, AnyTypes: permanentTypes), ctx);
    }

    private int TotalPower(GameState state, ObjectFilter filter, EffectContext ctx) =>
        Candidates(state, filter, ctx).Sum(id => _layers.Of(state, id).Power ?? 0); // CR 208.1

    /// <summary>Coven: three or more creatures you control with different powers, CR 702 ability word.</summary>
    private int DistinctPowers(GameState state, PlayerRef who, EffectContext ctx) =>
        Candidates(state, new ObjectFilter(Types: CardType.Creature, ControlledBy: who), ctx)
            .Select(id => _layers.Of(state, id).Power)
            .Where(p => p is not null)
            .Distinct()
            .Count();
}
