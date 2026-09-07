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
            return answered.Any(o => string.Equals(o.Id, "yes", StringComparison.Ordinal))
                ? Cast(run, who, cards[0], may)
                : run;

        return Ask(run, who, "may-cast", "You may cast that card. Do you?", YesNo, 1, 1);
    }

    /// <summary>
    /// Announce the spell, CR 601.2. "Without paying its mana cost" is CR 601.2h and CR 118.9: the
    /// mana cost is not paid, and the same permission is also the permission to cast the card from
    /// the zone it is in (CR 601.3a). Both halves ride on one flag, which the casting system
    /// records on the stack entry as <c>"free"</c>, so "if it was cast that way" can be asked when
    /// it resolves. Every additional cost is still paid (CR 601.2b) and the objects one needs are
    /// chosen as the spell is announced (CR 601.2c), which a yes/no question cannot carry — a card
    /// that prints an additional cost is refused by name rather than cast for a cost nobody chose.
    /// </summary>
    [RuleRef("601.2h")]
    private EffectRun Cast(EffectRun run, PlayerId who, ObjectId card, MayCast may)
    {
        // CR 706.2: a copy of a card in a zone is a new object, not this card being cast.
        if (may.AsCopy)
            throw new RefusedException(
                "A copy of that card is not this card being cast; asked for in seam-requests/E9f.md",
                "706.2");

        // CR 118.9: the command names an alternative cost by the printed keyword whose cost the
        // plan parses; a cost the effect states in its own words has no channel.
        if (may.AlternativeCost is not null)
            throw new RefusedException(
                "An alternative cost stated by the effect has no channel on CastSpell; asked for in seam-requests/E9f.md",
                "118.9");

        if (PrintedFace(run.State, card) is { AdditionalCosts.Count: > 0 })
            throw new RefusedException(
                "That card's additional cost cannot be chosen from a may-cast question; asked for in seam-requests/E9f.md",
                "601.2b");

        var command = new CastSpell(who, card, WithoutPayingManaCost: may.WithoutPayingManaCost);
        if (Casting!.Check(run.State, command) is { } refusal)
            throw new RefusedException(refusal.Reason, refusal.Rule);
        return Merge(run, Casting.Cast(run.State, command));
    }

    /// <summary>
    /// "You may cast it this turn", CR 601.3a: a permission that outlives the resolution is not a
    /// cast but a continuous effect (CR 611.1), so it is created rather than offered — the player
    /// takes it later, at a time they could cast the card, or not at all. <see cref="MayPlayFromZone"/>
    /// is the vocabulary's spelling; the effect applies to the cards the resolution named and to
    /// the zone they are in now, and it carries no cost half, so a permission that also changes
    /// what the card costs is refused by name (seam-requests/E9f.md, which also asks the casting
    /// system to read this effect for CR 601.3's permission).
    /// </summary>
    [RuleRef("601.3a")]
    private EffectRun Permission(EffectRun run, MayCast may, ImmutableList<ObjectId> cards, Duration until)
    {
        if (may.WithoutPayingManaCost || may.AsCopy || may.AlternativeCost is not null)
            throw new RefusedException(
                "A lasting permission has no cost half to say that with; asked for in seam-requests/E9f.md",
                "118.9");

        var who = FirstPlayer(run, may.Who);
        var from = run.State.Objects.TryGetValue(cards[0], out var card) ? card.Zone.Zone : Cards.Zone.Hand;
        return Create(run, new MayPlayFromZone(may.Who ?? new You(), from), cards, [who], until);
    }

    /// <summary>The printed face a card would be cast as, CR 601.3b: the front one until a face is chosen.</summary>
    [RuleRef("601.3b")]
    private static Face? PrintedFace(GameState state, ObjectId card) =>
        state.Objects.TryGetValue(card, out var obj) && obj.Definition.Faces.Count > 0
            ? obj.Definition.Faces[0]
            : null;
}
