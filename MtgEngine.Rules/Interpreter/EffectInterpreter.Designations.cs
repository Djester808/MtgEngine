using MtgEngine.Cards;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.Query;
using MtgEngine.Rules.State;
using CardZone = MtgEngine.Cards.Zone;

namespace MtgEngine.Rules.Interpreter;

/// <summary>
/// The designations the rules put on an object: prepared (CR 702.180), suspected (701.60),
/// goaded (701.15a), monstrous (701.37), harnessed (701.64) and the Ring-bearer (701.54). A
/// designation is not a counter and not an ability, so every one of them is one
/// <see cref="ObjectDesignated"/> event over the same names the query layer reads out of
/// <see cref="Designations"/>; nothing here invents a string.
///
/// None of these is offered to the replacement system: CR 614.1 replaces events, and no
/// <c>ReplacedEvent</c> in the vocabulary matches a designation, so proposing one would be
/// asking a question nothing can answer.
/// </summary>
public sealed partial class EffectInterpreter
{
    /// <summary>
    /// Give or take a designation, once per object. An object that already carries it is skipped
    /// and one that never had it is skipped when the designation is being taken away, so nothing
    /// fires twice and "unprepare a creature that was not prepared" is silence, not an event.
    /// </summary>
    [RuleRef("702.180")]
    private static EffectRun Designated(EffectRun run, IEnumerable<ObjectId> ids, string designation, bool given)
    {
        foreach (var id in ids)
        {
            if (!run.State.Objects.TryGetValue(id, out var obj) || obj.Designations.Contains(designation) == given)
                continue;
            run = Emit(run, new ObjectDesignated(id, designation, given));
        }

        return run;
    }

    /// <summary>
    /// Goad, CR 701.15a: the designation names the player who goaded, because the creature has to
    /// attack someone other than that player. So one goader is one designation and a creature
    /// goaded by two players carries two, rather than a single flag that forgets which.
    /// </summary>
    [RuleRef("701.15")]
    private EffectRun Goaded(EffectRun run, Cards.Goad goad) =>
        Designated(run, Objects(run, goad.What), Designations.GoadedBy(FirstPlayer(run, goad.By)), true);

    /// <summary>
    /// Monstrosity N, CR 701.37a: if this permanent is not already monstrous, put N +1/+1 counters
    /// on it and it becomes monstrous. A permanent that is already monstrous does nothing at all —
    /// not the counters either — and the counters are proposed, because "enters with an additional
    /// counter" and the rest of CR 614.1 apply to them like any other counter event.
    /// </summary>
    [RuleRef("701.37")]
    private EffectRun Monstrous(EffectRun run, Cards.Monstrosity monstrosity)
    {
        if (run.Context.Source is not { } id
            || !run.State.Objects.TryGetValue(id, out var obj)
            || obj.Designations.Contains(Designations.Monstrous))
            return run;

        var count = Math.Max(0, Number(run, monstrosity.N, 0));
        if (count > 0)
        {
            run = Propose(run, new CountersChanged(id, "+1/+1", count), characteristics.ControllerOf(run.State, id));
            if (run.Halted)
                return run;
        }

        return Emit(run, new ObjectDesignated(id, Designations.Monstrous, true));
    }

    /// <summary>
    /// The Ring tempts you, CR 701.54: the tempted player chooses a creature they control as their
    /// Ring-bearer, and the creature that was their Ring-bearer stops being one (CR 701.54c) — a
    /// player has one Ring-bearer, so the previous one loses the designation as the new one gains
    /// it, and re-choosing the same creature changes nothing and emits nothing. A player who
    /// controls no creatures does not choose (CR 701.54d).
    /// </summary>
    [RuleRef("701.54")]
    private EffectRun Tempted(EffectRun run, Cards.RingTemptsYou ring)
    {
        var who = FirstPlayer(run, ring.Who);
        if (Answered(run) is { } answered)
            return Bearer(run, who, Named(run.State, answered));

        var creatures = Creatures(run.State, who);
        if (creatures.Count == 0)
            return run;

        return Ask(
            run,
            who,
            "ring-bearer",
            "Choose a creature you control to be your Ring-bearer.",
            OptionsFor(run.State, creatures),
            1,
            1);
    }

    /// <summary>CR 701.54c: one Ring-bearer per player, so the seat's previous bearer loses it.</summary>
    [RuleRef("701.54")]
    private EffectRun Bearer(EffectRun run, PlayerId who, ImmutableList<ObjectId> chosen)
    {
        if (chosen.Count == 0)
            return run;

        var bearer = chosen[0];
        var previous = run.State.Objects.Values
            .Where(o => o.Id != bearer
                && o.Designations.Contains(Designations.RingBearer)
                && characteristics.ControllerOf(run.State, o.Id) == who)
            .Select(o => o.Id)
            .ToList();
        run = Designated(run, previous, Designations.RingBearer, false);
        return Designated(run, [bearer], Designations.RingBearer, true);
    }

    /// <summary>The creatures that player controls on the battlefield, read through the layers, CR 701.54a.</summary>
    private ImmutableList<ObjectId> Creatures(GameState state, PlayerId who) =>
    [
        .. state.Objects.Values
            .Where(o => o.Zone.Zone == CardZone.Battlefield
                && characteristics.ControllerOf(state, o.Id) == who
                && characteristics.Of(state, o.Id).Types.HasFlag(CardType.Creature))
            .OrderBy(o => o.Id.Value)
            .Select(o => o.Id),
    ];
}
