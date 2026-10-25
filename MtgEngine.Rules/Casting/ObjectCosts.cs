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
/// CR 601.2c says the player chooses, and the command carries that choice: <c>CostObjects</c> on
/// <see cref="Commands.CastSpell"/> and <see cref="Commands.ActivateAbility"/> is read in the
/// order the costs are written, each cost taking as many as it asks for. An object that is not
/// legal for the cost it was named for — wrong zone, wrong controller, or not matching the
/// filter — is refused naming CR 601.2c. A cost the command names nothing for is still paid when
/// the payment is forced (exactly as many legal objects as it asks for) and refused otherwise,
/// so no object is ever picked for the player.
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

    /// <summary>
    /// What this cost is asking for, CR 601.2c, or null when it names no objects — a mana cost, a
    /// life payment, a cost the source pays with itself. This is the same table
    /// <see cref="Pay"/> switches on, read the other way round: the zone, the control-or-ownership
    /// split, the filter and the count are stated once, so a question and the payment that
    /// follows it cannot offer and accept different objects. The name is the one a refusal uses,
    /// so a question can be worded from either.
    /// </summary>
    [RuleRef("601.2c")]
    public static CostChoice? Choice(GameState state, CostContext context, Cost cost)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(context);
        return cost switch
        {
            SacrificeCost sacrifice => Asked(state, context, CardZone.Battlefield, true, sacrifice.What, sacrifice.Count, "permanents to sacrifice"),
            DiscardCost { Random: false } discard => Asked(state, context, CardZone.Hand, false, discard.What, discard.Count, "cards to discard"),
            TapOther tap => Asked(state, context, CardZone.Battlefield, true, tap.What, tap.Count, "untapped permanents", untappedOnly: true),
            ExileFromZoneCost exile => Asked(state, context, exile.From, exile.From == CardZone.Battlefield, exile.What, exile.Count, "cards to exile", excludeSource: true),
            ReturnToHandCost hand => Asked(state, context, CardZone.Battlefield, true, hand.What, hand.Count, "permanents to return"),
            _ => null,
        };
    }

    /// <summary>One cost's ask, CR 601.2c: nothing is asked for when the cost takes no objects.</summary>
    [RuleRef("601.2c")]
    private static CostChoice? Asked(
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
        return wanted == 0
            ? null
            : new CostChoice(what, Candidates(state, context, zone, byControl, filter, untappedOnly, excludeSource), wanted);
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
        var from = cost.From is null
            ? context.Source?.Id ?? throw new RefusedException("There is nothing to remove counters from.", "601.2h")
            : NamedPermanent(state, context, cost.From);
        var count = Count(state, context, cost.Count);
        if (state.Objects[from].Counters.GetValueOrDefault(cost.Counter) < count)
            throw new RefusedException("There are not enough counters to remove.", "601.2h");

        var changed = new CountersChanged(from, cost.Counter, -count);
        return new Outcome(changed.Apply(state), [changed]);
    }

    /// <summary>
    /// CR 601.2c: the permanent a cost takes counters off is the player's choice, so it comes off
    /// the command. Nothing is resolved unless one was named — a command that names none is
    /// refused, exactly as it was before the choice could be carried.
    /// </summary>
    [RuleRef("601.2c")]
    private static ObjectId NamedPermanent(GameState state, CostContext context, Selector from)
    {
        var named = context.Named.Take(1);
        if (named.Count == 0)
            throw new RefusedException("Which permanent to remove counters from was not chosen.", "601.2c");
        return context.Query.Resolve(state, from, context.Effect).Contains(named[0])
            ? named[0]
            : throw new RefusedException("That permanent cannot pay this cost.", "601.2c");
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
    /// The objects that pay the cost, CR 601.2c: the ones the command named for it, each of which
    /// must be legal for this cost. A command that named none for this cost falls back to the
    /// forced payment — every legal object is taken when there are exactly as many as the cost
    /// asks for — so a cost with no choice in it needs nothing on the command.
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
        if (wanted == 0)
            return [];

        var legal = Candidates(state, context, zone, byControl, filter, untappedOnly, excludeSource);
        if (legal.Count < wanted)
            throw new RefusedException($"There are not enough {what}.", "601.2h");

        var named = context.Named.Take(wanted);
        if (named.Count == 0)
        {
            // CR 601.2c: nothing was named for this cost, so it is paid only when the payment is
            // forced. A real choice is refused rather than made for the player.
            return legal.Count == wanted
                ? legal
                : throw new RefusedException($"Which of the {what} pay the cost was not chosen.", "601.2c");
        }

        if (named.Count < wanted)
            throw new RefusedException($"Too few {what} were chosen to pay the cost.", "601.2c");
        if (named.Distinct().Count() != named.Count)
            throw new RefusedException("One object cannot pay a cost twice.", "601.2c");
        foreach (var id in named)
        {
            if (!legal.Contains(id))
                throw new RefusedException($"That is not one of the {what} that can pay the cost.", "601.2c");
        }

        return named;
    }

    /// <summary>Ordered by object id so a forced payment is the same on every replay.</summary>
    /// <summary>"A permanent you control", asked of the layer system (CR 613.1b).</summary>
    private static readonly ObjectFilter ControlledByCaster = new(ControlledBy: new You());

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
            //
            // Control is asked of the layer system (CR 613.1b), not of InitialController, which
            // is only where control STARTED: a creature gained from an opponent could not pay a
            // "sacrifice a creature" cost, and the opponent who no longer controlled it was
            // still offered it.
            if (byControl
                ? !context.Query.Matches(state, ControlledByCaster, obj.Id, effect)
                : obj.Owner != context.Player)
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

/// <summary>
/// The objects a command named to pay the costs that name some, CR 601.2c, read in the order the
/// costs are written: each cost takes as many as it asks for and the next one reads on from where
/// it stopped. One payment is one pass, so the position is this object's and never the state's.
/// </summary>
[RuleRef("601.2c")]
internal sealed class ChosenObjects(ImmutableList<ObjectId> chosen)
{
    private int taken;

    /// <summary>A command that named nothing: every cost falls back to its forced payment.</summary>
    public static ChosenObjects None => new([]);

    /// <summary>
    /// A copy standing where this one stands, CR 601.2c: a trial payment made only to learn which
    /// of the costs a <see cref="ChooseCost"/> offers can be paid must not move the position the
    /// real payment, and every cost after it, reads the command's objects from.
    /// </summary>
    [RuleRef("601.2c")]
    public ChosenObjects Rest() => new(chosen.GetRange(taken, chosen.Count - taken));

    /// <summary>The next <paramref name="count"/> objects, or fewer when the command named fewer.</summary>
    public ImmutableList<ObjectId> Take(int count)
    {
        if (count <= 0 || taken >= chosen.Count)
            return [];

        var slice = chosen.GetRange(taken, Math.Min(count, chosen.Count - taken));
        taken += slice.Count;
        return slice;
    }
}
