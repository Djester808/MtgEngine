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

            // CR 903.3: the designation travels with the object, so "you control your commander"
            // is a filter over the battlefield and not a separate register.
            Lieutenant lieutenant => CountMatching(state, new ObjectFilter(IsCommander: true, ControlledBy: lieutenant.Who), context) >= 1,

            // The turn's history, folded by the events that made each fact (CR 603.10).
            Morbid => CreatureDied(state), // CR 603.10
            Revolt revolt => Left(state, revolt.Who, context),
            Raid raid => AttackersOf(state, raid.Who, context) >= 1, // CR 506.2
            AttackedThisTurn attacked => AttackersOf(state, attacked.Who, context) >= Evaluate(state, attacked.Min, context),
            CreatedTokenThisTurn token => CreatedToken(state, token, context), // CR 111.1
            Paradox paradox => state.History.SpellsCast.Any(cast =>
                cast.From != Cards.Zone.Hand && Players(state, paradox.Who, context).Contains(cast.By)), // CR 601.2a

            // What was paid as the spell was cast, CR 601.2h, kept on its stack entry.
            Adamant adamant => Spent(state, context).GetValueOrDefault(adamant.Color) >= 3,
            WasKickedCondition kicked => KicksOfSource(state, context) >= (kicked.Times ?? 1), // CR 702.32
            PaidAlternativeCost paid => Paid(state, context, paid.Which), // CR 601.2b
            CastFromZone from => CastFrom(state, context) == from.From, // CR 400.7
            GiftPromised => false, // CR 702.174b: nothing on the stack entry records the promise yet; see seam-requests/E0b.md

            CityBlessing blessing => Players(state, blessing.Who, context) is { Count: > 0 } who
                && who.All(p => state.Player(p).Designations.Contains(Designations.CityBlessing)), // CR 702.131a
            YouAreMonarch => state.Monarch == context.Controller, // CR 724.2
            YouHaveInitiative => state.Initiative == context.Controller, // CR 725.2

            FirstTimeThisTurn => FirstResolutionThisTurn(state, context), // CR 603.4
            _ => throw Unanswerable(condition.GetType().Name, "no arm in the query layer"),
        };
    }

    /// <summary>
    /// How many creatures a player declared as attackers this turn, CR 506.2 and 508.1: only the
    /// active player declares attackers, and every combat phase of the turn folds into the same
    /// list, so a creature declared twice is still one attacker. A creature that has since left
    /// combat or died still counts, which is why the history is read and not the combat state.
    /// </summary>
    [RuleRef("508.1")]
    private int AttackersOf(GameState state, PlayerRef who, EffectContext ctx) =>
        Players(state, who, ctx).Contains(state.Turn.ActivePlayer)
            ? state.History.AttackedWith.Distinct().Count()
            : 0;

    /// <summary>The mana spent to cast the spell this effect is on, CR 601.2h.</summary>
    private static ImmutableDictionary<Color, int> Spent(GameState state, EffectContext ctx) =>
        Source(state, ctx)?.Stack?.ManaSpent ?? ImmutableDictionary<Color, int>.Empty;

    private static int KicksOfSource(GameState state, EffectContext ctx) =>
        Source(state, ctx) is { } source ? Kicks(source) : 0;

    /// <summary>
    /// CR 601.2b: what was paid as the spell was cast is recorded by name on its stack entry, and
    /// this asks whether an <em>alternative</em> cost was among it (CR 118.9). With a keyword named
    /// the marker is that keyword's own name; with none, the question is answered by the alternative
    /// markers by name and not by the length of the list — a kicked spell paid an additional cost
    /// (CR 601.2b) and no alternative one, so it answers false. See seam-requests/E3n.md § 1.
    /// </summary>
    [RuleRef("118.9")]
    private static bool Paid(GameState state, EffectContext ctx, KeywordKind? which)
    {
        var paid = Source(state, ctx)?.Stack?.OptionalCostsPaid ?? [];
        return which is { } keyword
            ? paid.Contains(keyword.ToString(), StringComparer.Ordinal)
            : paid.Any(IsAlternativeCostMarker);
    }

    /// <summary>
    /// How a waived mana cost is recorded on the stack entry, CR 118.9: the marker
    /// <c>Casting.CastPlan</c> writes for a permission that lets the spell be cast without paying
    /// its mana cost. It has no constant on that side yet, which is the one ask in
    /// seam-requests/E5i.md; the string is spelled here so this reading cannot drift silently.
    /// </summary>
    [RuleRef("118.9")]
    private const string WaivedManaCost = "free";

    /// <summary>
    /// Whether one marker on <see cref="StackEntry.OptionalCostsPaid"/> names an alternative cost,
    /// CR 118.9. Three are written by name — the printed alternative, a waived mana cost and the
    /// life paid instead of one — and a keyword alternative cost records the keyword's own name, so
    /// its shape is asked of <see cref="Keywords.KeywordSemantics"/> rather than listed here: every
    /// keyword whose cost replaces the mana cost answers this, and kicker and buyback, which are
    /// paid on top of it, do not.
    /// </summary>
    [RuleRef("118.9")]
    private static bool IsAlternativeCostMarker(string marker) =>
        marker is Casting.CastPlan.PaidPrintedAlternativeCost or Casting.CastPlan.PaidLifeForManaValue or WaivedManaCost
        || (Enum.TryParse<KeywordKind>(marker, out var keyword)
            && Keywords.KeywordSemantics.Of(keyword).Cost == Keywords.CostShape.Alternative);

    /// <summary>Where this spell was cast from, CR 400.7: the stack entry, or the turn's record of the cast if it has left the stack.</summary>
    private static Cards.Zone? CastFrom(GameState state, EffectContext ctx)
    {
        if (Source(state, ctx)?.Stack?.CastFrom is { } zone)
            return zone;
        if (ctx.Source is { } id)
        {
            foreach (var cast in state.History.SpellsCast)
            {
                if (cast.Spell == id)
                    return cast.From;
            }
        }

        return null;
    }

    private static GameObject? Source(GameState state, EffectContext ctx) =>
        ctx.Source is { } id && state.Objects.TryGetValue(id, out var obj) ? obj : null;

    private const string NoCommander = "no state records which object is a player's commander (CR 903.3); see seam-requests/E0.md";

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
        return CardsIn(state, who, Cards.Zone.Graveyard, new ObjectFilter(In: Cards.Zone.Graveyard, AnyTypes: PermanentTypes), ctx);
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
