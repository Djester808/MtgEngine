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
        var context = ContextFor(run.State, spell, entry, characteristics.ControllerOf(run.State, spell.Id), effect);

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
            // consumes; the first is the yes/no of the permission itself. A face whose costs need
            // two answers asks twice, and what the earlier answers named is carried on the mark
            // rather than re-derived, because the board they were chosen from has moved on.
            if (run.Answer is { Kind: CastCost })
            {
                ImmutableList<ObjectId> earlier = Marked(run) is { } already
                    ? [.. already.Select(id => new ObjectId(id))]
                    : [];
                return Cast(Unmark(run), who, cards[0], may, earlier.AddRange(Named(run.State, answered)));
            }

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
        // CR 118.9: the command names an alternative cost by the printed keyword whose cost the
        // plan parses; a cost the effect states in its own words has no channel.
        if (may.AlternativeCost is not null)
            throw new RefusedException(
                "An alternative cost stated by the effect has no channel on CastSpell; asked for in seam-requests/E9g.md",
                "118.9");

        var announced = may.AsCopy ? Copy(ref run, card) : card;
        var command = new CastSpell(
            who,
            announced,
            CostObjects: costObjects.IsEmpty ? null : costObjects,
            WithoutPayingManaCost: may.WithoutPayingManaCost);
        if (Casting!.CheckInstructed(run.State, command) is { } refusal)
            return CostQuestion(run, who, command, refusal, costObjects);

        // CR 608.2g: this is a cast a resolving effect instructed, so it happens during that
        // resolution and CR 117.1a's timing does not apply to it — a player who could not cast the
        // card of their own accord still casts it here. A command a player announced goes through
        // the ordinary door; only the interpreter uses this one.
        return Merge(run, Casting.CastInstructed(run.State, command));
    }

    /// <summary>
    /// The copy that is cast, CR 707.12. "You may cast a copy of that card" casts a copy created in
    /// the zone the card is in, not the card, so the copy has to be an object before the casting
    /// system is handed anything — and only the zone system makes objects. A zone system that
    /// cannot make one leaves the refusal standing rather than casting the original in the copy's
    /// place, which is a different game action.
    /// </summary>
    [RuleRef("707.12")]
    private ObjectId Copy(ref EffectRun run, ObjectId card)
    {
        var owner = run.State.Objects.TryGetValue(card, out var original)
            ? original.Owner
            : run.Context.Controller;
        Outcome copied;
        try
        {
            copied = zones.CopyInPlace(run.State, card, owner);
        }
        // The refusal a zone system gives for a copy it cannot make, and the NotImplementedException
        // the unbuilt seam default still throws (Seams.cs, not this box's — asked for in
        // seam-requests/E9q.md). Either way the walk refuses by name rather than crashing.
        catch (Exception thrown) when (thrown is RefusedException or NotImplementedException)
        {
            throw new RefusedException(
                "A copy of that card is not this card being cast; asked for in seam-requests/E9g.md",
                "707.12");
        }

        // The copy is the last object the outcome created: CR 707.12 makes one object, and anything
        // the zone system created before it is the making of that one.
        var made = copied.Events.OfType<ObjectCreated>().LastOrDefault()
            ?? throw new RefusedException(
                "The zone system made no copy of that card to cast; asked for in seam-requests/E9g.md",
                "707.12");
        run = Merge(run, copied);
        return made.Object.Id;
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
    private EffectRun CostQuestion(EffectRun run, PlayerId who, CastSpell command, Refusal refusal, ImmutableList<ObjectId> already)
    {
        // CR 118.4: a question nobody can answer is not asked. The accumulated answer grows by at
        // least one object per question, so the cap is what stops a casting system that refuses
        // the same command however it is answered.
        if (!string.Equals(refusal.Rule, "601.2c", StringComparison.Ordinal) || already.Count >= MaxCostObjects)
            throw new RefusedException(refusal.Reason, refusal.Rule);

        // The casting system is the only thing that knows what a cost consumes, where it looks and
        // whose objects it may take; the interpreter's job is to turn one of its asks into one
        // question. A system that cannot say answers with nothing, and its refusal stands.
        var choices = Casting!.CostChoices(run.State, command);
        if (choices.Count == 0)
            throw new RefusedException(refusal.Reason, refusal.Rule);

        var choice = choices[0];
        if (choice.Count <= 0 || choice.Legal.Count < choice.Count)
            throw new RefusedException(refusal.Reason, refusal.Rule);

        // CR 601.2c: two costs that each name objects are asked for one at a time, in the order the
        // casting system writes them, and what the earlier ones took rides on the mark.
        return Ask(
            Mark(run, already.Select(id => id.Value)),
            who,
            CastCost,
            "Choose what pays that spell's " + choice.Cost + ".",
            OptionsFor(run.State, choice.Legal),
            choice.Count,
            choice.Count);
    }

    /// <summary>
    /// How many objects one announcement may be asked to name, CR 601.2c. Every question adds at
    /// least one, so this is the bound that makes the ask-and-announce-again loop finite.
    /// </summary>
    private const int MaxCostObjects = 16;

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
    /// <summary>
    /// "You may play it" — with or without paying its mana cost — CR 601.3a with 118.9 and 305.1: a
    /// play permission is a lasting continuous effect (CR 611.1) and never a play on the spot, because
    /// a land is played through the land-play door (CR 305.1, using the turn's land play, 305.2) and
    /// a spell through a cast the player announces when they choose to. It lasts until the effect's
    /// own duration, or the end of the turn when the card names none (CR 611.2a). The permission
    /// applies to the cards the resolution named, in the zone they are in now.
    /// </summary>
    [RuleRef("601.3a")]
    private EffectRun MayPlayCards(EffectRun run, MayPlay play)
    {
        var cards = Objects(run, play.What);
        if (cards.Count == 0)
            return run;

        var who = FirstPlayer(run, play.Who);
        var from = run.State.Objects.TryGetValue(cards[0], out var card) ? card.Zone.Zone : Cards.Zone.Hand;
        var permission = new MayPlayFromZone(
            play.Who ?? new You(),
            from,
            WithoutPayingManaCost: play.WithoutPayingManaCost);
        return Create(run, permission, cards, [who], play.Until ?? new UntilEndOfTurn());
    }

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
}
