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
        if (Casting is null)
            throw new RefusedException(
                $"{nameof(MayCast)} needs ICastingSystem on the interpreter's seam; asked for in seam-requests/E9e.md",
                "601");

        var cards = Objects(run, may.What);
        if (cards.Count == 0)
            return run;

        var who = FirstPlayer(run, may.Who);
        if (Answered(run) is { } answered)
            return answered.Any(o => string.Equals(o.Id, "yes", StringComparison.Ordinal))
                ? Cast(run, who, cards[0], may)
                : run;

        return Ask(run, who, "may-cast", "You may cast that card. Do you?", YesNo, 1, 1);
    }

    /// <summary>
    /// Announce the spell, CR 601.2. The alternative cost is recorded on the stack entry as
    /// <c>OptionalCostsPaid</c> so "if its cost was paid that way" can be asked when it resolves —
    /// which is exactly what the casting system does with the channel it is given. It has no
    /// channel for "without paying its mana cost" yet (seam-requests/E9e.md), and CR 601.2h means
    /// a spell is either cast for the cost that was announced or not cast at all, so that reading
    /// is refused by name rather than cast for a price nobody offered.
    /// </summary>
    [RuleRef("601.2")]
    private EffectRun Cast(EffectRun run, PlayerId who, ObjectId card, MayCast may)
    {
        if (may.WithoutPayingManaCost || may.AlternativeCost is not null)
            throw new RefusedException(
                "Casting a card without paying its mana cost needs a free cost on CastSpell; asked for in seam-requests/E9e.md",
                "601.2h");

        // CR 706: a copy of a card is not this card being cast, and nothing copies a card in the
        // zone it is in yet; CR 601.2 casts it now, not later, so a duration is a permission the
        // continuous-effect layer would have to hold.
        if (may.AsCopy || may.Until is not null)
            throw new RefusedException(
                "A copy, or a permission to cast later, is not a cast now; asked for in seam-requests/E9e.md",
                "601.2");

        var command = new CastSpell(who, card);
        if (Casting!.Check(run.State, command) is { } refusal)
            throw new RefusedException(refusal.Reason, refusal.Rule);
        return Merge(run, Casting.Cast(run.State, command));
    }
}
