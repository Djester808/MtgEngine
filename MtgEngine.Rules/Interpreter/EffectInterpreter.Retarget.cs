using MtgEngine.Cards;
using MtgEngine.Rules.Commands;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Interpreter;

/// <summary>
/// Two things a resolving effect does to other spells: point one somewhere else (CR 115.7 and
/// 114.6) and cast one from inside the resolution (CR 601). Both reach outside the walk — the
/// first rewrites a stack entry, the second is the casting system's whole announcement — so both
/// are here rather than beside the verbs that only change numbers.
/// </summary>
public sealed partial class EffectInterpreter
{
    /// <summary>
    /// The casting system, set after construction the way <c>ZoneSystem.Replacements</c> is,
    /// because casting resolves through the interpreter and the interpreter casts through it
    /// (CR 601). Null until the composition sets it, and the <see cref="MayCast"/> arm refuses
    /// rather than pretending a card was cast.
    /// </summary>
    [RuleRef("601")]
    public ICastingSystem? Casting { get; set; }

    /// <summary>
    /// The second question of a may-cast pair, CR 601.2c: which objects pay the additional cost the
    /// card prints. The kind is what tells the resumed walk which of the two answers it is holding.
    /// </summary>
    [RuleRef("601.2c")]
    private const string CastCost = "cast-cost";

    /* ---------------------------------------------------------------------------------------- *
     * CR 115.7 — a spell on the stack points somewhere else.
     * ---------------------------------------------------------------------------------------- */

    /// <summary>
    /// Change the target of a spell or ability on the stack, CR 115.7. The new target is checked
    /// against the filter of the <see cref="Target"/> selector whose slot it fills, read out of the
    /// spell's own tree, so "change its target to another creature" cannot point a creature spell
    /// at a land. CR 115.7b: a change has to alter at least one target, and an unchanged reading
    /// emits nothing; CR 114.6's "choose new targets" may leave them exactly as they were, which is
    /// the same silence. Legality is rechecked again on resolution (CR 608.2b), so nothing here has
    /// to guess what the board will look like then.
    /// </summary>
    [RuleRef("115.7")]
    private EffectRun Retargeted(EffectRun run, ChangeTarget change)
    {
        var spells = Objects(run, change.What);
        if (spells.Count == 0
            || !run.State.Objects.TryGetValue(spells[0], out var spell)
            || spell.Stack is not { } entry)
            return run;

        var effect = EffectOf(spell, entry);
        var filters = effect is null ? [] : EffectWalk.TargetsIn(effect);
        var context = ContextFor(run.State, spell, entry, characteristics.ControllerOf(run.State, spell.Id));

        if (Answered(run) is { } answered)
            return Retarget(run, spell.Id, entry, Named(run.State, answered));

        // The card names the new target: "change its target to this creature".
        if (change.To is { } to)
        {
            ImmutableList<ObjectId> named =
            [
                .. Objects(run, to).Where((id, slot) => Legal(run.State, filters, slot, id, context)),
            ];
            return Retarget(run, spell.Id, entry, named);
        }

        // The card lets a player choose, CR 114.6 and 115.7a: as many targets as the spell has,
        // from among everything the spell could have targeted.
        var candidates = Candidates(run.State, filters, context);
        var count = entry.Targets.Count;
        if (count == 0 || candidates.Count == 0)
            return run;

        return Ask(
            run,
            FirstPlayer(run, change.Who),
            "target",
            "Choose new targets for that spell or ability.",
            OptionsFor(run.State, candidates),
            count,
            count);
    }

    /// <summary>
    /// Write the new targets on the stack entry, CR 115.7. Nothing is emitted when the reading is
    /// the one the entry already holds: CR 115.7b needs at least one target changed, and CR 114.6
    /// allows all of them to stay, so "no change" is a legal outcome with no event either way.
    /// </summary>
    [RuleRef("115.7")]
    private static EffectRun Retarget(EffectRun run, ObjectId spell, StackEntry entry, ImmutableList<ObjectId> targets) =>
        targets.Count == 0 || targets.SequenceEqual(entry.Targets)
            ? run
            : Emit(run, new StackTargetsChanged(spell, targets, entry.TargetPlayers));

    /// <summary>
    /// Does this object fit the target slot it is being put in, CR 115.7a? An
    /// <see cref="AnyTarget"/> slot has no filter of its own (CR 115.4 names the four kinds of
    /// target and the query layer answers them), so it admits any object still in the game.
    /// </summary>
    [RuleRef("115.7a")]
    private bool Legal(GameState state, ImmutableList<Selector> filters, int slot, ObjectId id, EffectContext context) =>
        state.Objects.ContainsKey(id)
        && (FilterAt(filters, slot) is not { } filter || query.Matches(state, filter, id, context));

    /// <summary>Everything the spell could target, in id order, CR 115.7a.</summary>
    [RuleRef("115.7a")]
    private ImmutableList<ObjectId> Candidates(GameState state, ImmutableList<Selector> filters, EffectContext context) =>
        FilterAt(filters, 0) is { } filter
            ? query.Resolve(state, new Each(filter), context)
            : [];

    /// <summary>The filter of the target selector filling that slot, or null when the slot is an "any target".</summary>
    private static ObjectFilter? FilterAt(ImmutableList<Selector> filters, int slot) =>
        filters.Count == 0 ? null
            : filters[Math.Min(slot, filters.Count - 1)] is Target target ? target.Filter
            : null;

    /* ---------------------------------------------------------------------------------------- *
     * CR 601 — casting a card from inside a resolution.
     * ---------------------------------------------------------------------------------------- */

    /// <summary>
    /// "You may cast that card", CR 601. The permission is an offer, so the walk stops and asks
    /// (CR 101.4); answered yes, the card is cast through the casting system, which is the only
    /// thing that knows how to announce a spell. The question is asked before anything is checked
    /// because a player may decline a cast they could not have made anyway.
    /// </summary>
    [RuleRef("601")]
    private EffectRun MayCastCard(EffectRun run, MayCast may)
    {
        var cards = Objects(run, may.What);
        if (cards.Count == 0)
            return run;

        // CR 601.3a: a permission that lasts is not a cast, so nothing is offered and no question
        // is asked — it is a continuous effect, and it needs no casting system.
        if (may.Until is { } until)
            return Permission(run, may, cards, until);

        if (Casting is null)
            throw new RefusedException(
                $"{nameof(MayCast)} needs ICastingSystem on the interpreter's seam; asked for in seam-requests/E9e.md",
                "601");

        var who = FirstPlayer(run, may.Who);
        if (Answered(run) is { } answered)
        {
            // CR 601.2c: the second question of the pair names the objects an additional cost
            // consumes; the first is the yes/no of the permission itself.
            if (run.Answer is { Kind: CastCost })
                return Cast(run, who, cards[0], may, Named(run.State, answered));
            return answered.Any(o => string.Equals(o.Id, "yes", StringComparison.Ordinal))
                ? Cast(run, who, cards[0], may, [])
                : run;
        }

        return Ask(run, who, "may-cast", "You may cast that card. Do you?", YesNo, 1, 1);
    }

    /// <summary>
    /// Announce the spell, CR 601.2. "Without paying its mana cost" is CR 601.2h and CR 118.9: the
    /// mana cost is not paid, and the same permission is also the permission to cast the card from
    /// the zone it is in (CR 601.3a). Both halves ride on one flag, which the casting system
    /// records on the stack entry as <c>"free"</c>, so "if it was cast that way" can be asked when
    /// it resolves. Every additional cost is still paid whatever the mana cost does (CR 601.2b),
    /// and whether this command pays them is the casting system's word, not a guess made from the
    /// printed face here: the command is handed to <see cref="ICastingSystem.Check"/> and its
    /// refusal is relayed with its own rule.
    /// </summary>
    [RuleRef("601.2h")]
    private EffectRun Cast(EffectRun run, PlayerId who, ObjectId card, MayCast may, ImmutableList<ObjectId> costObjects)
    {
        // CR 707.12: casting a copy of an object creates the copy in the zone the object is in and
        // casts that, so the copy has to exist before the casting system is handed anything. No
        // seam makes a copy of a card in a zone — CreateToken makes a permanent — so this is
        // refused by name rather than casting the original in the copy's place.
        if (may.AsCopy)
            throw new RefusedException(
                "A copy of that card is not this card being cast; asked for in seam-requests/E9g.md",
                "707.12");

        // CR 118.9: the command names an alternative cost by the printed keyword whose cost the
        // plan parses; a cost the effect states in its own words has no channel.
        if (may.AlternativeCost is not null)
            throw new RefusedException(
                "An alternative cost stated by the effect has no channel on CastSpell; asked for in seam-requests/E9g.md",
                "118.9");

        var command = new CastSpell(
            who,
            card,
            CostObjects: costObjects.IsEmpty ? null : costObjects,
            WithoutPayingManaCost: may.WithoutPayingManaCost);
        if (Casting!.Check(run.State, command) is { } refusal)
            return CostQuestion(run, who, card, refusal, costObjects);
        return Merge(run, Casting.Cast(run.State, command));
    }

    /// <summary>
    /// CR 601.2c: the objects an additional cost consumes are chosen as the spell is announced, and
    /// a yes/no cannot carry them. A refusal naming CR 601.2c is the casting system saying nobody
    /// chose, so the walk asks a second question of the objects that could pay and announces the
    /// spell again with the answer. Any other refusal is the casting system's word about this
    /// command and is relayed with its rule — a cost that needs no object (pay life, sacrifice this
    /// card) never gets here, because <c>Check</c> accepts it.
    /// </summary>
    [RuleRef("601.2c")]
    private EffectRun CostQuestion(EffectRun run, PlayerId who, ObjectId card, Refusal refusal, ImmutableList<ObjectId> already)
    {
        if (!already.IsEmpty || !string.Equals(refusal.Rule, "601.2c", StringComparison.Ordinal))
            throw new RefusedException(refusal.Reason, refusal.Rule);

        var wants = ImmutableList.CreateBuilder<CostObjectsWanted>();
        foreach (var cost in PrintedFace(run.State, card)?.AdditionalCosts ?? [])
        {
            if (Wants(cost) is { } want)
                wants.Add(want);
        }

        // One question fills CostObjects in the order the costs are written, so two costs that each
        // name objects cannot be told apart inside a single answer.
        if (wants.Count != 1)
            throw new RefusedException(
                "Which objects that spell's additional costs want cannot be asked for; asked for in seam-requests/E9g.md",
                "601.2c");

        var wanted = wants[0];
        var count = Number(run, wanted.Count);
        var legal = query.Resolve(run.State, new Each(Where(wanted)), run.Context);
        if (count <= 0 || legal.Count < count)
            throw new RefusedException(refusal.Reason, refusal.Rule);

        return Ask(run, who, CastCost, "Choose what pays that spell's additional cost.", OptionsFor(run.State, legal), count, count);
    }

    /// <summary>
    /// Where an additional cost looks for what pays it, CR 601.2c: the zone, whether the objects are
    /// the player's by control or by ownership, and how many. Null for a cost that consumes no
    /// object of the player's choosing — pay life, sacrifice this card, a random discard — which is
    /// paid without anything on the command.
    /// </summary>
    [RuleRef("601.2c")]
    private static CostObjectsWanted? Wants(Cost cost) => cost switch
    {
        // CR 701.17a: a player sacrifices only a permanent they control.
        SacrificeCost sacrifice => new(sacrifice.What, sacrifice.Count, Cards.Zone.Battlefield, true, false),
        ReturnToHandCost hand => new(hand.What, hand.Count, Cards.Zone.Battlefield, true, false),
        // CR 118.12: a tapped permanent cannot pay a cost that taps one.
        TapOther tap => new(tap.What, tap.Count, Cards.Zone.Battlefield, true, true),
        // CR 701.9b: a random discard is the game's choice, not a question.
        DiscardCost { Random: false } discard => new(discard.What, discard.Count, Cards.Zone.Hand, false, false),
        ExileFromZoneCost exile => new(exile.What, exile.Count, exile.From, exile.From == Cards.Zone.Battlefield, false),
        _ => null,
    };

    /// <summary>The filter that finds what could pay the cost: its own, narrowed to the zone and the player.</summary>
    [RuleRef("601.2c")]
    private static ObjectFilter Where(CostObjectsWanted wanted)
    {
        var filter = (wanted.What ?? new ObjectFilter()) with { In = wanted.In };
        filter = wanted.ByControl
            ? filter with { ControlledBy = filter.ControlledBy ?? new You() }
            : filter with { OwnedBy = filter.OwnedBy ?? new You() };
        return wanted.UntappedOnly ? filter with { Tapped = false } : filter;
    }

    /// <summary>
    /// "You may cast it this turn", CR 601.3a: a permission that outlives the resolution is not a
    /// cast but a continuous effect (CR 611.1), so it is created rather than offered — the player
    /// takes it later, at a time they could cast the card, or not at all. <see cref="MayPlayFromZone"/>
    /// is the vocabulary's spelling; the effect applies to the cards the resolution named and to
    /// the zone they are in now. CR 118.9: "you may cast it this turn without paying its mana
    /// cost" is one permission with two halves, and the record carries both — the flag rides on the
    /// effect and the casting system hands it to the <c>CastSpell</c> the player later announces.
    /// A copy, or a cost the effect states in its own words, still has no half to say it with.
    /// </summary>
    [RuleRef("601.3a")]
    private EffectRun Permission(EffectRun run, MayCast may, ImmutableList<ObjectId> cards, Duration until)
    {
        if (may.AsCopy || may.AlternativeCost is not null)
            throw new RefusedException(
                "A lasting permission has no cost half to say that with; asked for in seam-requests/E9g.md",
                "118.9");

        var who = FirstPlayer(run, may.Who);
        var from = run.State.Objects.TryGetValue(cards[0], out var card) ? card.Zone.Zone : Cards.Zone.Hand;
        var permission = new MayPlayFromZone(
            may.Who ?? new You(),
            from,
            WithoutPayingManaCost: may.WithoutPayingManaCost);
        return Create(run, permission, cards, [who], until);
    }

    /// <summary>The printed face a card would be cast as, CR 601.3b: the front one until a face is chosen.</summary>
    [RuleRef("601.3b")]
    private static Face? PrintedFace(GameState state, ObjectId card) =>
        state.Objects.TryGetValue(card, out var obj) && obj.Definition.Faces.Count > 0
            ? obj.Definition.Faces[0]
            : null;
}

/// <summary>
/// What an additional cost is looking for, CR 601.2c: the objects that could pay it, said in the
/// terms a question needs — a filter, how many, the zone they are in, whether they are the
/// player's by control (a permanent they sacrifice or tap) or by ownership (a card in their hand),
/// and whether a tapped one is disqualified (CR 118.4).
/// </summary>
[RuleRef("601.2c")]
internal sealed record CostObjectsWanted(
    ObjectFilter? What,
    Amount? Count,
    Cards.Zone In,
    bool ByControl,
    bool UntappedOnly);
