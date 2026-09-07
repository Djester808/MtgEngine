using MtgEngine.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.Mana;
using MtgEngine.Rules.State;
using CardZone = MtgEngine.Cards.Zone;

namespace MtgEngine.Rules.Casting;

/// <summary>
/// Paying an activation cost, CR 601.2h: every cost is paid in full or the activation is
/// refused, because partial payments are not allowed. The costs this slice pays are the tap
/// symbol, mana, sacrificing the source and paying life; any other cost is refused naming
/// itself, so nothing is ever half paid.
/// </summary>
[RuleRef("601.2h")]
internal static class CostPayment
{
    /// <summary>Pay all of the costs, in the order written. Throws <see cref="RefusedException"/> naming the rule that stops it.</summary>
    public static Outcome Pay(
        GameState state,
        GameObject source,
        Characteristics characteristics,
        IReadOnlyList<Cost> costs,
        PlayerId player,
        int? x,
        IZoneSystem zones,
        IQuery query)
    {
        var events = ImmutableList.CreateBuilder<GameEvent>();
        foreach (var cost in Flatten(costs))
        {
            var step = One(state, source, characteristics, cost, player, x, zones, query);
            events.AddRange(step.Events);
            state = step.State;
        }

        return new Outcome(state, events.ToImmutable());
    }

    /// <summary>A mana cost, parsed and spent out of the player's pool, CR 601.2h.</summary>
    public static Outcome PayMana(GameState state, PlayerId player, string? printed, int? x, string reason)
    {
        var symbols = ManaSymbols.Parse(printed);
        var payment = ManaSymbols.Pay(state.Player(player).Mana, symbols, x);
        if (payment.Refused is { } refusal)
            throw new RefusedException(refusal.Reason, refusal.Rule);
        if (payment.Delta.Total == 0)
            return Outcome.Unchanged(state);

        var changed = new ManaChanged(player, payment.Delta, reason);
        return new Outcome(changed.Apply(state), [changed]);
    }

    /// <summary>Several costs paid together are one list, CR 601.2f; a choice of costs is not in this slice.</summary>
    private static IEnumerable<Cost> Flatten(IReadOnlyList<Cost> costs)
    {
        foreach (var cost in costs)
        {
            if (cost is Costs all)
            {
                foreach (var inner in Flatten([.. all.All]))
                    yield return inner;
            }
            else
            {
                yield return cost;
            }
        }
    }

    private static Outcome One(
        GameState state,
        GameObject source,
        Characteristics characteristics,
        Cost cost,
        PlayerId player,
        int? x,
        IZoneSystem zones,
        IQuery query)
    {
        switch (cost)
        {
            case ManaCost mana:
                return PayMana(state, player, mana.Mana, x, "activation cost");

            case TapSelf:
                return TapTheSource(state, source, characteristics);

            case SacrificeSelf:
                return Sacrifice(state, source, zones);

            case DiscardSelfCost:
                return Discard(state, source, zones);

            case PayLife life:
                return LoseLife(state, player, query.Evaluate(state, life.Amount, Context(source.Id, player, x)));

            default:
                // CR 601.2h: an unpayable cost can't be paid. Naming the cost is how the backlog
                // gets it: the primitive is missing, the activation is not silently allowed.
                throw new RefusedException($"A {cost.GetType().Name} cost cannot be paid yet.", "601.2h");
        }
    }

    /// <summary>
    /// The tap symbol, CR 118.1. An already-tapped permanent cannot pay it, CR 118.4, and a
    /// creature cannot pay it unless it has been under its controller's control since their most
    /// recent turn began, CR 302.6 — the summoning sickness rule, which haste turns off, CR 702.10b.
    /// </summary>
    [RuleRef("302.6")]
    private static Outcome TapTheSource(GameState state, GameObject source, Characteristics characteristics)
    {
        if (source.Tapped)
            throw new RefusedException("It is already tapped.", "118.4");
        if (characteristics.Types.HasFlag(CardType.Creature) && source.SummoningSick && !characteristics.Has(KeywordKind.Haste))
            throw new RefusedException("It has not been under your control since your most recent turn began.", "302.6");

        var tapped = new ObjectChanged(source.Id, Tapped: true);
        return new Outcome(tapped.Apply(state), [tapped]);
    }

    /// <summary>Sacrificing the source, CR 701.17: its controller moves it to its owner's graveyard.</summary>
    [RuleRef("701.17")]
    private static Outcome Sacrifice(GameState state, GameObject source, IZoneSystem zones)
    {
        if (source.Zone.Zone != CardZone.Battlefield)
            throw new RefusedException("Only a permanent on the battlefield can be sacrificed.", "701.17a");

        var left = new ObjectLeftBy(source.Id, "sacrificed", null);
        var moved = zones.Move(left.Apply(state), source.Id, ZoneKey.Of(CardZone.Graveyard, source.Owner));
        return new Outcome(moved.State, moved.Events.Insert(0, left));
    }

    /// <summary>
    /// "Discard this card" as a cost, CR 701.9a: from its owner's hand to their graveyard. It is
    /// the cost of cycling (CR 702.29a), so the source is in a hand, not on the battlefield.
    /// </summary>
    [RuleRef("701.9a")]
    private static Outcome Discard(GameState state, GameObject source, IZoneSystem zones)
    {
        if (source.Zone.Zone != CardZone.Hand)
            throw new RefusedException("Only a card in a hand can be discarded.", "701.9a");

        var left = new ObjectLeftBy(source.Id, "discarded", null);
        var moved = zones.Move(left.Apply(state), source.Id, ZoneKey.Of(CardZone.Graveyard, source.Owner));
        return new Outcome(moved.State, moved.Events.Insert(0, left));
    }

    /// <summary>Paying life, CR 119.4: a player can pay only life they have.</summary>
    [RuleRef("119.4")]
    private static Outcome LoseLife(GameState state, PlayerId player, int amount)
    {
        if (amount < 0)
            throw new RefusedException("A life payment cannot be negative.", "119.4");
        if (state.Player(player).Life < amount)
            throw new RefusedException("You cannot pay more life than you have.", "119.4");
        if (amount == 0)
            return Outcome.Unchanged(state);

        var paid = new LifeChanged(player, -amount, "cost");
        return new Outcome(paid.Apply(state), [paid]);
    }

    internal static EffectContext Context(ObjectId? source, PlayerId controller, int? x) => new(
        source,
        controller,
        [],
        [],
        x,
        ImmutableDictionary<string, ImmutableList<ObjectId>>.Empty,
        ImmutableDictionary<string, string>.Empty,
        ImmutableDictionary<string, int>.Empty,
        [],
        null);
}
