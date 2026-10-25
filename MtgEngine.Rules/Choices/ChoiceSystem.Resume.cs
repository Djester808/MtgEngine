using System.Globalization;
using MtgEngine.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Choices;

/// <summary>
/// Resumption: what happens once the answer is recorded. One branch per kind, so a game that
/// is reloaded from its log resumes the same way it would have the first time. The kinds a
/// rule raises are finished here; the kinds a card raises go back to the interpreter through
/// <see cref="IResumable"/>, which is the only subsystem that knows where in an effect tree
/// the question came from.
/// </summary>
public sealed partial class ChoiceSystem
{
    private readonly OpeningHands.OpeningHandCoordinator openingHands = new(zones);

    private Outcome Resume(GameState state, PendingChoice choice, ImmutableList<ChoiceOption> chosen) => choice.Kind switch
    {
        ChoiceKinds.Discard => Discard(state, chosen),
        ChoiceKinds.LegendRule => LegendRule(state, choice, chosen),
        ChoiceKinds.CommanderReturn => ReturnCommander(state, choice, chosen),
        ChoiceKinds.Mulligan => openingHands.Decide(state, choice, chosen).Then(s => Next(s, choice.Player)),
        ChoiceKinds.MulliganBottom => openingHands.Bottom(state, choice, chosen).Then(s => Next(s, choice.Player)),

        // CR 117.3b with 608.2: every other kind is a card's question, asked in the middle of
        // something the game was already doing — a resolution, a combat damage batch, a cast.
        // Whatever the answer finished, the turn system is asked to open the step; it opens
        // nothing while another question stands or while a player still holds priority, so an
        // answer that finished nothing changes nothing here. A mulligan keep is not among them:
        // it opens the game rather than a step, through Next and CR 103.5.
        _ => ResumeOperation(state, choice, chosen)
            .Then(turn.OpenStep),
    };

    /// <summary>Finish the interrupted operation before evaluating its enclosing payment.</summary>
    private Outcome ResumeOperation(GameState state, PendingChoice choice, ImmutableList<ChoiceOption> chosen)
    {
        var resumed = resumable.Resume(state, new Resumption(choice.Kind, choice.Player, chosen, choice.Context));
        var resolved = Resumed(resumed);
        var drawn = Library.LibraryDrawCoordinator.Resume(resolved, choice.Context.DeferredDraws, zones);
        var batch = FinishZoneBatch(AndTheRest(drawn, choice), choice);
        return Library.LibraryShuffleContinuation.Complete(batch, choice.Context.DeferredShuffles, zones);
    }


    /// <summary>
    /// CR 510.2 with 702.15b: the rest of a halted combat damage batch, told what the halted hit
    /// finished with. The events are the ones the answer produced, so a hit whose amount the
    /// replacement system decided can still gain its lifelink for the damage actually dealt.
    /// </summary>
    [RuleRef("702.15b")]
    private Outcome AndTheRest(Outcome resumed, PendingChoice choice) =>
        resumed.Then(after => RestOfTheBatch(after, choice, resumed.Events));

    /// <summary>
    /// CR 510.2 with 616.1: a replacement-order question raised inside the combat damage step
    /// stopped the batch. The replacement system has just finished the one event it was asked
    /// about; the assignments after it are still owed, and the marks the halt left on the question
    /// say which step and which hit, so the combat system re-runs the step from there. A question
    /// carrying no marks came from somewhere else and this is not its business; a second question
    /// already open means the game is waiting again and nothing else happens (CR 101.4).
    /// </summary>
    [RuleRef("510.2")]
    private Outcome RestOfTheBatch(GameState state, PendingChoice choice, ImmutableList<GameEvent> dealt)
    {
        if (combat is null
            || !choice.Context.Extra.TryGetValue(ChoiceKinds.CombatDamageIndex, out var raw)
            || !int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var from))
            return Outcome.Unchanged(state);

        var firstStrike = choice.Context.Extra.TryGetValue(ChoiceKinds.CombatDamageStep, out var step)
            && string.Equals(step, ChoiceKinds.FirstStrikeDamage, StringComparison.Ordinal);

        // CR 101.4: the answer left another question open — a third effect still applies to the
        // same hit — so the batch is waiting again rather than owed. The marks travel onto that
        // question because they are the only record that this halt is a combat damage batch's.
        return state.Pending is { } again
            ? StillTheBatch(state, again, firstStrike, from)
            : combat.ResumeCombatDamage(state, firstStrike, from, dealt);
    }

    /// <summary>
    /// CR 616.1 with 510.2: a question raised by answering a question keeps the halt's marks, so
    /// however many times one hit stops the game, the answer that finally finishes it deals the
    /// rest of the batch and opens the step. The marks are written the way
    /// <c>CombatSystem.Halted</c> writes them — the same two keys, from the same step and index —
    /// and re-raising the question is how they are recorded, so a game rebuilt from its log
    /// stops on the marked question and resumes the same way.
    /// </summary>
    [RuleRef("616.1")]
    private static Outcome StillTheBatch(GameState state, PendingChoice pending, bool firstStrike, int from)
    {
        if (pending.Context.Extra.ContainsKey(ChoiceKinds.CombatDamageIndex))
            return Outcome.Unchanged(state);

        var marked = pending with
        {
            Context = pending.Context with
            {
                Extra = pending.Context.Extra
                    .SetItem(ChoiceKinds.CombatDamageStep, firstStrike ? ChoiceKinds.FirstStrikeDamage : ChoiceKinds.RegularDamage)
                    .SetItem(ChoiceKinds.CombatDamageIndex, from.ToString(CultureInfo.InvariantCulture)),
            },
        };

        var requested = new ChoiceRequested(marked);
        return new Outcome(requested.Apply(state), [requested]);
    }

    /// <summary>
    /// CR 608.2m: an answer that finished a resolution (the interpreter says so with
    /// <see cref="StackObjectResolved"/>) leaves the object on the stack until the casting system
    /// takes it where it goes; without a casting system the object stays, which is what a test of
    /// the choice system alone expects.
    /// </summary>
    [RuleRef("608.2m")]
    private Outcome Resumed(Outcome resumed)
    {
        if (casting is null)
            return resumed;
        var outcome = resumed;
        foreach (var finished in resumed.Events.OfType<StackObjectResolved>().Select(e => e.StackObject).Distinct())
            outcome = outcome.Then(s => casting.FinishResolving(s, finished));
        return outcome;
    }

    /// <summary>
    /// CR 103.5: a finished declaration hands back to the turn system, which asks the next
    /// player or begins turn 1. A question still open — the next declaration after a mulligan,
    /// or the bottoming after a keep that cost cards — means this player is not finished yet.
    /// </summary>
    [RuleRef("103.5")]
    private Outcome Next(GameState state, PlayerId finished) =>
        state.Pending is null ? turn.AfterMulligans(state, finished) : Outcome.Unchanged(state);

    /// <summary>
    /// CR 514.1: the active player discards down to their maximum hand size, and CR 701.9a:
    /// the cards chosen are discarded at once, so one board for all of them. The turn system
    /// stopped mid-cleanup to ask, so finishing the answer finishes the step, CR 514.3.
    /// </summary>
    [RuleRef("514.1")]
    private Outcome Discard(GameState state, ImmutableList<ChoiceOption> chosen) =>
        AdvanceAfterBatch(zones.MoveAll(state, ToGraveyard(state, chosen)));

    /// <summary>
    /// CR 704.5j: the controller of two or more legendary permanents with the same name keeps
    /// one and puts the rest into their owners' graveyards. The answer names the one kept, so
    /// what moves is every option that was not chosen.
    /// </summary>
    [RuleRef("704.5j")]
    private Outcome LegendRule(GameState state, PendingChoice choice, ImmutableList<ChoiceOption> chosen)
    {
        var kept = chosen.Select(c => c.Id).ToImmutableHashSet(StringComparer.Ordinal);
        var rest = choice.Options.Where(o => !kept.Contains(o.Id)).ToImmutableList();
        return zones.MoveAll(state, ToGraveyard(state, rest));
    }

    private static ImmutableList<MoveRequest> ToGraveyard(GameState state, IEnumerable<ChoiceOption> options)
    {
        var moves = ImmutableList.CreateBuilder<MoveRequest>();
        foreach (var (id, obj) in Objects(state, options))
            moves.Add(new MoveRequest(id, ZoneKey.Of(Zone.Graveyard, obj.Owner)));
        return moves.ToImmutable();
    }

    /// <summary>The object behind each chosen option. An option that names no object, or one
    /// that has since ceased to exist, is a wrong answer and not a silent skip.</summary>
    private static IEnumerable<(ObjectId Id, GameObject Object)> Objects(GameState state, IEnumerable<ChoiceOption> options)
    {
        foreach (var option in options)
        {
            var id = option.Object
                ?? throw new RefusedException("That option does not name a card.", "101.4");
            if (!state.Objects.TryGetValue(id, out var obj))
                throw new RefusedException("That card is no longer where it was.", "101.4");
            yield return (id, obj);
        }
    }

}
