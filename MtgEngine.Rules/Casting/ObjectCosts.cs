using MtgEngine.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;
using CardZone = MtgEngine.Cards.Zone;

namespace MtgEngine.Rules.Casting;

/// <summary>
/// The costs that name objects other than their own source — "sacrifice a creature", "discard a
/// card", "tap an untapped creature you control", "exile four other cards from your graveyard" —
/// paid as CR 601.2c and 601.2h describe: the objects are chosen as the cost is announced and
/// consumed in full before anything else happens, so no payment is ever partial.
/// </summary>
/// <remarks>
/// CR 601.2c says the player chooses. The command carries no list of chosen objects yet
/// (seam-requests/E3c.md asks for <c>CostObjects</c> on <see cref="Commands.CastSpell"/>), so a
/// cost whose objects are forced — exactly as many legal objects as it asks for — is paid, and a
/// cost with a real choice is refused naming CR 601.2c rather than choosing for the player.
/// </remarks>
[RuleRef("601.2c")]
internal static class ObjectCosts
{
    /// <summary>Pay one cost that consumes objects, or null when this is not one of them.</summary>
    public static Outcome? Pay(GameState state, CostContext context, Cost cost)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(context);
        return cost switch
        {
            SacrificeCost sacrifice => Sacrifice(state, context, sacrifice),
            DiscardCost discard => Discard(state, context, discard),
            TapOther tap => Tap(state, context, tap),
            ExileFromZoneCost exile => Exile(state, context, exile),
            ReturnToHandCost hand => ReturnToHand(state, context, hand),
            RemoveCountersCost counters => RemoveCounters(state, context, counters),
            ExileSelfCost => MoveSelf(state, context, CardZone.Exile, "exiled"),
            _ => null,
        };
    }

    /// <summary>CR 701.17a: a player may sacrifice only a permanent they control, and it goes to its owner's graveyard.</summary>
    [RuleRef("701.17a")]
    private static Outcome Sacrifice(GameState state, CostContext context, SacrificeCost cost)
    {
        var chosen = Choose(state, context, CardZone.Battlefield, byControl: true, cost.What, cost.Count, "permanents to sacrifice");
        return MoveEach(state, context, chosen, CardZone.Graveyard, "sacrificed");
    }

    /// <summary>CR 701.9a: a discarded card moves from its owner's hand to their graveyard.</summary>
    [RuleRef("701.9a")]
    private static Outcome Discard(GameState state, CostContext context, DiscardCost cost)
    {
        // CR 701.9b: a random discard is a choice the game makes, and the seeded generator that
        // would make it is the zone system's, not a cost's.
        if (cost.Random)
            throw new RefusedException("A random discard cost cannot be paid yet.", "701.9b");
        var chosen = Choose(state, context, CardZone.Hand, byControl: false, cost.What, cost.Count, "cards to discard");
        return MoveEach(state, context, chosen, CardZone.Graveyard, "discarded");
    }

    /// <summary>
    /// CR 118.12 and 702.51a: tapping other untapped permanents you control. A tapped permanent
    /// cannot pay it (CR 118.4) and, as a cost, an untapped creature may be tapped even while it
    /// is summoning sick — only the tap symbol on its own permanent carries CR 302.6.
    /// </summary>
    [RuleRef("118.12")]
    private static Outcome Tap(GameState state, CostContext context, TapOther cost)
    {
        var chosen = Choose(state, context, CardZone.Battlefield, byControl: true, cost.What, cost.Count, "untapped permanents", untappedOnly: true);
        var events = ImmutableList.CreateBuilder<GameEvent>();
        foreach (var id in chosen)
        {
            var tapped = new ObjectChanged(id, Tapped: true);
            state = tapped.Apply(state);
            events.Add(tapped);
        }

        return new Outcome(state, events.ToImmutable());
    }

    /// <summary>CR 701.5a and 702.138a: exiling cards from a zone, which is how escape is paid.</summary>
    [RuleRef("701.5a")]
    private static Outcome Exile(GameState state, CostContext context, ExileFromZoneCost cost)
    {
        var byControl = cost.From == CardZone.Battlefield;
        var chosen = Choose(state, context, cost.From, byControl, cost.What, cost.Count, "cards to exile", excludeSource: true);
        return MoveEach(state, context, chosen, CardZone.Exile, "exiled");
    }

    /// <summary>CR 701.18: returning a permanent you control to its owner's hand as a cost.</summary>
    [RuleRef("701.18")]
    private static Outcome ReturnToHand(GameState state, CostContext context, ReturnToHandCost cost)
    {
        var chosen = Choose(state, context, CardZone.Battlefield, byControl: true, cost.What, cost.Count, "permanents to return");
        return MoveEach(state, context, chosen, CardZone.Hand, "returned");
    }

    /// <summary>
    /// CR 701.24 and 118.1: removing counters as a cost. Only counters on the cost's own source
    /// are removable here; a cost that removes them from elsewhere names a selector nobody has
    /// chosen from, so it is refused rather than taken off the wrong permanent.
    /// </summary>
    [RuleRef("701.24")]
    private static Outcome RemoveCounters(GameState state, CostContext context, RemoveCountersCost cost)
    {
        if (cost.From is not null)
            throw new RefusedException("Which permanent to remove counters from cannot be chosen yet.", "601.2c");
        var source = context.Source ?? throw new RefusedException("There is nothing to remove counters from.", "601.2h");
        var count = Count(state, context, cost.Count);
        if (state.Objects[source.Id].Counters.GetValueOrDefault(cost.Counter) < count)
            throw new RefusedException("There are not enough counters to remove.", "601.2h");

        var changed = new CountersChanged(source.Id, cost.Counter, -count);
        return new Outcome(changed.Apply(state), [changed]);
    }

    private static Outcome MoveSelf(GameState state, CostContext context, CardZone to, string how)
    {
        var source = context.Source ?? throw new RefusedException("There is no source to pay that cost.", "601.2h");
        return MoveEach(state, context, [source.Id], to, how);
    }

    /// <summary>Each chosen object leaves in turn: a zone change makes a new object, CR 400.7, so the state is threaded.</summary>
    [RuleRef("400.7")]
    private static Outcome MoveEach(GameState state, CostContext context, ImmutableList<ObjectId> chosen, CardZone to, string how)
    {
        var events = ImmutableList.CreateBuilder<GameEvent>();
        foreach (var id in chosen)
        {
            var owner = state.Objects[id].Owner;
            var left = new ObjectLeftBy(id, how, null);
            var moved = context.Zones.Move(left.Apply(state), id, ZoneKey.Of(to, owner));
            events.Add(left);
            events.AddRange(moved.Events);
            state = moved.State;
        }

        return new Outcome(state, events.ToImmutable());
    }

    /// <summary>
    /// The objects that pay the cost. CR 601.2c makes this the player's choice; until the command
    /// can carry it, only a forced payment goes through — every legal object is taken when there
    /// are exactly as many as the cost asks for.
    /// </summary>
    [RuleRef("601.2c")]
    private static ImmutableList<ObjectId> Choose(
        GameState state,
        CostContext context,
        CardZone zone,
        bool byControl,
        ObjectFilter? filter,
        Amount? count,
        string what,
        bool untappedOnly = false,
        bool excludeSource = false)
    {
        var wanted = Count(state, context, count);
        var legal = Candidates(state, context, zone, byControl, filter, untappedOnly, excludeSource);
        if (legal.Count < wanted)
            throw new RefusedException($"There are not enough {what}.", "601.2h");
        if (legal.Count > wanted)
            throw new RefusedException($"Which of the {what} pay the cost cannot be chosen yet.", "601.2c");
        return legal;
    }

    /// <summary>Ordered by object id so a forced payment is the same on every replay.</summary>
    private static ImmutableList<ObjectId> Candidates(
        GameState state,
        CostContext context,
        CardZone zone,
        bool byControl,
        ObjectFilter? filter,
        bool untappedOnly,
        bool excludeSource)
    {
        var effect = context.Effect;
        var found = ImmutableList.CreateBuilder<ObjectId>();
        foreach (var obj in state.Objects.Values.OrderBy(o => o.Id.Value))
        {
            if (obj.Zone.Zone != zone)
                continue;

            // A permanent is sacrificed or tapped by the player who controls it, CR 701.17a; a
            // card in a hidden or an owned zone is used by the player who owns it, CR 108.3.
            if (byControl ? obj.InitialController != context.Player : obj.Owner != context.Player)
                continue;
            if (untappedOnly && obj.Tapped)
                continue;
            if (excludeSource && context.Source is { } self && obj.Id == self.Id)
                continue;
            if (filter is not null && !context.Query.Matches(state, filter, obj.Id, effect))
                continue;
            found.Add(obj.Id);
        }

        return found.ToImmutable();
    }

    /// <summary>CR 601.2h: an amount on a cost is counted as the cost is announced; no amount means one.</summary>
    [RuleRef("601.2h")]
    private static int Count(GameState state, CostContext context, Amount? amount) =>
        amount is null ? 1 : Math.Max(0, context.Query.Evaluate(state, amount, context.Effect));
}
