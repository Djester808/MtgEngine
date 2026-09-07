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
    private const int DefaultHandSize = 7;

    private Outcome Resume(GameState state, PendingChoice choice, ImmutableList<ChoiceOption> chosen) => choice.Kind switch
    {
        ChoiceKinds.Discard => Discard(state, chosen),
        ChoiceKinds.LegendRule => LegendRule(state, choice, chosen),
        ChoiceKinds.Mulligan => Mulligan(state, choice, chosen).Then(s => Next(s, choice.Player)),
        ChoiceKinds.MulliganBottom => Bottom(state, chosen).Then(s => Next(s, choice.Player)),
        _ => AndTheRest(Resumed(resumable.Resume(state, new Resumption(choice.Kind, choice.Player, chosen, choice.Context))), choice),
    };

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
            : combat.ResumeCombatDamage(state, firstStrike, from, dealt).Then(OpenTheDamageStep);
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
    /// CR 510.3 with 117.3b: combat damage has been dealt, so the active player receives priority
    /// and the step can be walked on. An unhalted batch is dealt inside the turn system's advance,
    /// which grants priority itself once its turn-based action is done (CR 117.3a); a batch a
    /// CR 616.1 question halted comes back here through the answer instead, and until this nobody
    /// had opened the step — the damage was right and the game could not go on. A second question
    /// means the batch is waiting again and the step stays shut until that answer too (CR 101.4).
    /// <para>
    /// The grant is written out rather than asked of <see cref="ITurnSystem"/> because that
    /// interface has no member for "a turn-based action is finished, open the step":
    /// <c>seam-requests/E4e.md</c> asks for one, and this <b>duplicates</b>
    /// <c>TurnSystem.GrantPriority</c> until it lands.
    /// </para>
    /// </summary>
    [RuleRef("510.3")]
    private static Outcome OpenTheDamageStep(GameState state)
    {
        if (state.Pending is not null || state.Priority.Holder is not null)
            return Outcome.Unchanged(state);

        var granted = new PriorityGranted(state.Turn.ActivePlayer);
        return new Outcome(granted.Apply(state), [granted]);
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
        zones.MoveAll(state, ToGraveyard(state, chosen)).Then(turn.Advance);

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

    /// <summary>
    /// CR 103.5: a player dissatisfied with their hand takes a mulligan — the hand is shuffled
    /// back, a new one of the starting size is drawn, and then, once they keep, they put one
    /// card per mulligan taken on the bottom of their library. Each declaration is its own
    /// question, so the count so far travels in the context rather than in a loop variable.
    /// </summary>
    [RuleRef("103.5")]
    private Outcome Mulligan(GameState state, PendingChoice choice, ImmutableList<ChoiceOption> chosen)
    {
        var player = choice.Player;
        var taken = Number(choice.Context, "mulligans");
        var handSize = Number(choice.Context, "handSize", DefaultHandSize);

        if (!chosen.Any(c => string.Equals(c.Id, ChoiceKinds.Mulligan, StringComparison.Ordinal)))
            return AskToBottom(state, player, taken, handSize);

        var hand = Hand(state, player);
        var back = hand.Select(id => new MoveRequest(id, ZoneKey.Of(Zone.Library, player))).ToImmutableList();
        var next = taken + 1;
        return zones.MoveAll(state, back)
            .Then(s => zones.Shuffle(s, player))
            .Then(s => zones.Draw(s, player, handSize))

            // CR 103.5: mulligans may be taken until the opening hand would be zero cards.
            .Then(s => next < handSize
                ? Ask(s, Declaration(s, player, next, handSize))
                : AskToBottom(s, player, next, handSize));
    }

    /// <summary>
    /// CR 103.5: the kept cards that go on the bottom of the library, in the order answered —
    /// the rule says any order, and the answer is the order the player chose.
    /// </summary>
    [RuleRef("103.5")]
    private Outcome Bottom(GameState state, ImmutableList<ChoiceOption> chosen)
    {
        var moves = ImmutableList.CreateBuilder<MoveRequest>();
        foreach (var (id, obj) in Objects(state, chosen))
            moves.Add(new MoveRequest(id, ZoneKey.Of(Zone.Library, obj.Owner), Placement.Bottom));
        return zones.MoveAll(state, moves.ToImmutable());
    }

    /// <summary>
    /// The declaration a player makes each round of mulligans, CR 103.5. Public because the
    /// turn system raises the first one when the game begins; every later one is raised here.
    /// </summary>
    public static PendingChoice Declaration(GameState state, PlayerId player, int taken, int handSize) =>
        new(
            new ChoiceId(state.NextChoiceId),
            player,
            ChoiceKinds.Mulligan,
            "Keep this hand, or take a mulligan?",
            [new ChoiceOption(ChoiceKinds.Keep, "Keep"), new ChoiceOption(ChoiceKinds.Mulligan, "Mulligan")],
            1,
            1,
            MulliganContext(taken, handSize));

    /// <summary>Where a mulligan declaration keeps its count and the starting hand size, CR 103.5.</summary>
    public static ChoiceContext MulliganContext(int taken, int handSize) =>
        new(
            null,
            null,
            ChoiceKinds.Mulligan,
            ImmutableDictionary<string, string>.Empty
                .Add("mulligans", taken.ToString(CultureInfo.InvariantCulture))
                .Add("handSize", handSize.ToString(CultureInfo.InvariantCulture)));

    /// <summary>
    /// One card per mulligan taken goes on the bottom, CR 103.5. A player who kept their first
    /// hand puts none back, and there is nothing to ask.
    /// </summary>
    private static Outcome AskToBottom(GameState state, PlayerId player, int taken, int handSize)
    {
        var hand = Hand(state, player);
        var count = Math.Min(taken, hand.Count);
        if (count <= 0)
            return Outcome.Unchanged(state);

        var options = hand
            .Select(id => new ChoiceOption(id.Value.ToString(CultureInfo.InvariantCulture), state.Objects[id].Face.Name, id))
            .ToImmutableList();
        return Ask(state, new PendingChoice(
            new ChoiceId(state.NextChoiceId),
            player,
            ChoiceKinds.MulliganBottom,
            $"Put {count} card(s) on the bottom of your library.",
            options,
            count,
            count,
            MulliganContext(taken, handSize)));
    }

    private static Outcome Ask(GameState state, PendingChoice choice)
    {
        var requested = new ChoiceRequested(choice);
        return new Outcome(requested.Apply(state), [requested]);
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

    private static ImmutableList<ObjectId> Hand(GameState state, PlayerId player) =>
        state.In(ZoneKey.Of(Zone.Hand, player));

    private static int Number(ChoiceContext context, string key, int fallback = 0) =>
        context.Extra.TryGetValue(key, out var raw)
        && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;
}
