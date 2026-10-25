using System.Globalization;
using MtgEngine.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Turn;

/// <summary>
/// Turn structure and priority, CR 500 and CR 117.
/// <para>
/// The turn order is the player order: the player at turn-order index 0 takes turn 1, and each
/// turn after that goes to the next player in that order who has not lost the game, CR 103.8
/// and CR 104.3. Every step of CR 500.1 happens every turn, whether or not anything happens in
/// it, so the combat steps are stepped through even with no combat; what happens inside them is
/// the combat system's, CR 506–511.
/// </para>
/// <para>
/// The turn-based actions here are the ones of CR 703.4 that need no other subsystem: untap and
/// summoning sickness, the draw, the cleanup step, and emptying mana pools. Drawing is the zone
/// system's; resolving the stack is the casting system's; combat damage is the combat system's,
/// but the moment it happens is a turn-based action of CR 703.4m and so belongs here.
/// </para>
/// <para>
/// <paramref name="combat"/> is optional so that a test of turn structure alone need not build a
/// combat system. A game composed without one steps through the combat steps and deals no
/// damage, which is wrong at a table and honest in a unit test.
/// </para>
/// <para>
/// <paramref name="characteristics"/> is optional for the same reason, and two rules read it:
/// CR 502.3's untap, which untaps the permanents the active player controls *now* rather than
/// the ones they were dealt, and CR 510.4's first-strike combat damage step, which exists only
/// when a creature in combat has first or double strike. Without it a turn is walked from the
/// stored controller and both damage steps are walked always — the wave-1 behaviour, wrong in a
/// game and honest in a unit test.
/// </para>
/// </summary>
[RuleRef("500")]
public sealed partial class TurnSystem(IZoneSystem zones, ICastingSystem casting, ICombatSystem? combat = null, ICharacteristics? characteristics = null) : ITurnSystem
{
    /// <summary>The steps of a turn in order, CR 500.1. Main phases have no steps and stand for themselves.</summary>
    [RuleRef("500.1")]
    private static readonly ImmutableArray<Step> Order =
    [
        Step.Untap,
        Step.Upkeep,
        Step.Draw,
        Step.PrecombatMain,
        Step.BeginningOfCombat,
        Step.DeclareAttackers,
        Step.DeclareBlockers,
        Step.FirstStrikeCombatDamage,
        Step.CombatDamage,
        Step.EndOfCombat,
        Step.PostcombatMain,
        Step.End,
        Step.Cleanup,
    ];

    public string Name => "turn and priority";

    /// <summary>The starting player takes the first turn, CR 103.8.</summary>
    [RuleRef("103.8")]
    public Outcome Begin(GameState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var log = new Log(state);

        // CR 103.8: the starting player was decided when the game was created, CR 103.1, and the
        // state carries the answer.
        var startingPlayer = state.Player(state.Turn.ActivePlayer).HasLost
            ? NextInTurnOrder(state, state.Turn.ActivePlayer)
            : state.Turn.ActivePlayer;
        BeginTurn(log, startingPlayer, 1, extra: false);
        return log.Done();
    }

    /// <summary>
    /// A player passes, CR 117.3d. When every player still in the game has passed in
    /// succession the top of the stack resolves, or, with an empty stack, the step ends,
    /// CR 117.4.
    /// </summary>
    [RuleRef("117.4")]
    public Outcome Pass(GameState state, PlayerId player)
    {
        ArgumentNullException.ThrowIfNull(state);

        if (state.Outcome is not null)
            throw new RefusedException("The game is over.", "104.1");
        if (state.Pending is not null)
            throw new RefusedException("A pending choice must be answered first.", "101.4");

        // CR 117.3: priority is held by one player at a time, and only they may pass it.
        if (!new PrioritySnapshot(state).CanPass(player))
            throw new RefusedException($"{player} does not have priority.", "117.3");

        var log = new Log(state);
        log.Add(new PriorityPassed(player));

        // CR 104.3: a player who has lost the game is no longer in it, so their pass is not
        // waited for. CR 117.4 needs every remaining player to have passed in succession.
        var passed = log.State.Priority.PassedInSuccession;
        if (InGame(log.State).Any(p => !passed.Contains(p)))
        {
            log.Add(new PriorityHandedTo(NextInTurnOrder(log.State, player)));
            return log.Done();
        }

        if (log.State.In(ZoneKey.Shared(Zone.Stack)).IsEmpty)
        {
            // CR 117.4: with an empty stack the phase or step ends.
            log.Merge(Advance(log.State));
            return log.Done();
        }

        // CR 608.2: the top object of the stack resolves. CR 117.3b: the active player then
        // receives priority.
        log.Merge(casting.ResolveTop(log.State));
        if (log.State.Pending is null && log.State.Outcome is null)
            GrantPriority(log, log.State.Turn.ActivePlayer);
        return log.Done();
    }

    /// <summary>The step ends and the next begins, with the turn-based actions of CR 703.4 in between.</summary>
    [RuleRef("500.1")]
    public Outcome Advance(GameState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.Outcome is not null || state.Pending is not null)
            return Outcome.Unchanged(state);

        var log = new Log(state);
        switch (state.Turn.Step)
        {
            case Step.Cleanup:
                // A cleanup step that stopped to ask for a discard, CR 514.1, finishes here.
                FinishCleanup(log);
                BeginNextTurn(log);
                return log.Done();

            case Step.Untap:
                // CR 502.4: no player receives priority during the untap step, so it runs its
                // actions and the upkeep step follows without anyone being asked.
                UntapStep(log);
                EmptyManaPools(log);
                BeginUpkeep(log);
                return log.Done();

            default:
                break;
        }

        // CR 511.3: as the end of combat step ends, all creatures and planeswalkers are removed
        // from combat. The combat system owns what that means; the moment is the turn's.
        if (state.Turn.Step == Step.EndOfCombat && combat is not null)
            log.Merge(combat.EndCombat(log.State));

        // CR 500.5 and CR 703.4q: as a step ends, unspent mana empties.
        EmptyManaPools(log);

        var next = NextStep(log.State);
        log.Add(new StepBegan(next));
        if (next == Step.Draw)
            DrawStep(log);
        if (next == Step.End)
            EndStep(log);
        CombatDamageStep(log, next);

        if (next == Step.Cleanup)
        {
            // CR 514.1 first, and it may stop the game to ask.
            if (RequestDiscard(log))
                return log.Done();
            FinishCleanup(log);
            BeginNextTurn(log);
            return log.Done();
        }

        // CR 117.3a: the active player receives priority at the beginning of every step and
        // phase but the untap step and, normally, the cleanup step.
        if (log.State.Pending is null && !(combat is not null && next is Step.DeclareAttackers or Step.DeclareBlockers))
            GrantPriority(log, log.State.Turn.ActivePlayer);
        return log.Done();
    }

    /// <summary>
    /// CR 117.3b: something that finished out of band — a resolution a question stopped (CR 608.2)
    /// or a combat damage batch a CR 616.1 question halted (CR 510.3) — opens the step again, and
    /// the active player receives priority. It is the same grant <see cref="Pass"/> makes after an
    /// ordinary resolution, said once so the choice system does not spell it a second time
    /// (<c>seam-requests/E4e.md</c>).
    /// <para>
    /// Nothing is opened while another question stands (CR 101.4: the game is still waiting), once
    /// the game has ended (CR 104.1), or while a player already holds priority — a question asked
    /// during a cast or an activation is answered by the player who has it, and the answer does not
    /// take it off them. So this may be asked after every answer, and only the answers that
    /// finished something open anything.
    /// </para>
    /// </summary>
    [RuleRef("117.3b")]
    public Outcome OpenStep(GameState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.Pending is not null || state.Outcome is not null || state.Priority.Holder is not null)
            return Outcome.Unchanged(state);
        if (combat is not null &&
            ((state.Turn.Step == Step.DeclareAttackers && state.Combat is not { AttackersDeclared: true })
            || (state.Turn.Step == Step.DeclareBlockers && state.Combat is not { BlockersDeclared: true })))
            return Outcome.Unchanged(state);

        var log = new Log(state);
        GrantPriority(log, state.Turn.ActivePlayer);
        return log.Done();
    }

    /// <summary>A turn begins: the beginning phase's untap step runs, then the upkeep step, CR 501.1.</summary>
    [RuleRef("501.1")]
    private void BeginTurn(Log log, PlayerId player, int number, bool extra)
    {
        log.Add(new TurnBegan(number, player, extra));
        log.Add(new StepBegan(Step.Untap));
        UntapStep(log);
        EmptyManaPools(log);
        BeginUpkeep(log);
    }

    /// <summary>CR 503.1: the upkeep step has no turn-based actions; the active player gets priority.</summary>
    [RuleRef("503.1")]
    private static void BeginUpkeep(Log log)
    {
        log.Add(new StepBegan(Step.Upkeep));
        GrantPriority(log, log.State.Turn.ActivePlayer);
    }

    /// <summary>
    /// CR 502.3: the active player untaps the permanents they control, all at once. CR 302.6:
    /// a permanent that has been under its controller's control continuously since their turn
    /// began is no longer summoning sick.
    /// </summary>
    [RuleRef("502.3")]
    private void UntapStep(Log log)
    {
        var active = log.State.Turn.ActivePlayer;

        // CR 702.26d: phasing in and out happens first, before anything untaps - a permanent
        // that phases in this step is one of the permanents that then untaps. Nothing brought
        // one back before this, so a phased-out permanent stayed out for the rest of the game.
        PhaseIn(log, active);

        foreach (var permanent in Permanents(log.State, active))
        {
            bool? untap = permanent.Tapped ? false : null;
            bool? ready = permanent.SummoningSick ? false : null;
            if (untap is not null || ready is not null)
                log.Add(new ObjectChanged(permanent.Id, Tapped: untap, SummoningSick: ready));
        }
    }

    /// <summary>
    /// CR 702.26d: "at the beginning of each player's untap step, that player's phased-out
    /// permanents phase in". Read from the state rather than through a filter, because a
    /// phased-out permanent is exactly what no filter will admit (CR 702.26b).
    /// </summary>
    [RuleRef("702.26d")]
    private static void PhaseIn(Log log, PlayerId active)
    {
        foreach (var id in log.State.Objects.Values
            .Where(o => o.PhasedOut && o.Zone.Zone == Cards.Zone.Battlefield && o.InitialController == active)
            .Select(o => o.Id)
            .OrderBy(id => id.Value)
            .ToList())
        {
            log.Add(new ObjectChanged(id, PhasedOut: false));
        }
    }

    /// <summary>CR 504.1 and CR 703.4d: the active player draws a card. CR 103.8a is the one exception.</summary>
    [RuleRef("504.1")]
    private void DrawStep(Log log)
    {
        var state = log.State;

        // CR 103.8a: in a two-player game the player who plays first skips the draw step of
        // their first turn. CR 103.8c: in every other multiplayer game, nobody skips it.
        if (state.Turn.Number == 1 && !state.Turn.IsExtraTurn && state.Players.Count == 2)
            return;

        log.Merge(zones.Draw(state, state.Turn.ActivePlayer, 1));
    }

    /// <summary>
    /// The step that follows this one, CR 500.1, with CR 510.4's exception: the first-strike
    /// combat damage step is not a step of every turn — it exists only when a creature with first
    /// or double strike is in combat as the combat damage step would begin. Skipping it matters
    /// beyond the log: each step is a round of priority nobody should be given.
    /// </summary>
    [RuleRef("510.4")]
    private Step NextStep(GameState state)
    {
        var next = Order[Order.IndexOf(state.Turn.Step) + 1];
        if (next == Step.Draw && state.Turn.Number == 1 && !state.Turn.IsExtraTurn && state.Players.Count == 2)
            return Step.PrecombatMain;
        if (combat is not null && next == Step.DeclareBlockers && state.Combat is { AttackersDeclared: true } fight
            && fight.Attackers.IsEmpty && fight.DefendingPlayers.IsEmpty)
            return Step.EndOfCombat;
        return next == Step.FirstStrikeCombatDamage && !HasFirstStrikeInCombat(state) ? Step.CombatDamage : next;
    }

    /// <summary>
    /// CR 510.4: whether any attacking or blocking creature has first strike or double strike.
    /// With no layer system to ask, the honest answer is the wave-1 one — walk the step whenever
    /// there is a combat at all — because a keyword granted by an effect cannot be seen from here.
    /// </summary>
    [RuleRef("510.4")]
    private bool HasFirstStrikeInCombat(GameState state)
    {
        if (state.Combat is not { AttackersDeclared: true } fight)
            return false;
        if (characteristics is null)
            return true;

        return fight.Attackers.Keys
            .Concat(fight.Blockers.Values.SelectMany(blockers => blockers))
            .Where(state.Objects.ContainsKey)
            .Select(id => characteristics.Of(state, id))
            .Any(c => c.Has(KeywordKind.FirstStrike) || c.Has(KeywordKind.DoubleStrike));
    }

    /// <summary>
    /// CR 510.1 and CR 703.4m: combat damage is assigned and dealt as a turn-based action at the
    /// beginning of a combat damage step, before any player receives priority. The combat system
    /// decides who assigns in which of the two steps; which steps there are is CR 510.4's, above.
    /// </summary>
    [RuleRef("703.4m")]
    private void CombatDamageStep(Log log, Step next)
    {
        if (combat is null || next is not (Step.FirstStrikeCombatDamage or Step.CombatDamage))
            return;

        log.Merge(combat.DealCombatDamage(log.State, next == Step.FirstStrikeCombatDamage));
    }

    /// <summary>
    /// CR 514.1: the active player discards down to their maximum hand size. Answering the
    /// question is the choice system's, so the game stops here and the next
    /// <see cref="Advance"/> finishes the step.
    /// </summary>
    [RuleRef("514.1")]
    private static bool RequestDiscard(Log log)
    {
        var state = log.State;
        var active = state.Turn.ActivePlayer;
        var hand = ObjectsIn(state, Zone.Hand, active);
        var over = hand.Count - state.Player(active).MaxHandSize;
        if (over <= 0)
            return false;

        var options = hand
            .Select(o => new ChoiceOption(o.Id.Value.ToString(CultureInfo.InvariantCulture), o.Face.Name, o.Id))
            .ToImmutableList();
        log.Add(new ChoiceRequested(new PendingChoice(
            new ChoiceId(state.NextChoiceId),
            active,
            "discard",
            "Discard down to your maximum hand size.",
            options,
            over,
            over,
            new ChoiceContext(null, null, null, ImmutableDictionary<string, string>.Empty))));
        return true;
    }

    /// <summary>
    /// CR 514.2: damage marked on permanents is removed and every "until end of turn" effect
    /// ends, simultaneously — and with them, CR 603.7c, every delayed triggered ability whose
    /// duration was this turn, fired or not. Then the step ends and mana pools empty, CR 500.5.
    /// </summary>
    [RuleRef("514.2")]
    private static void FinishCleanup(Log log)
    {
        foreach (var damaged in log.State.Objects.Values.Where(o => o.Damage > 0).OrderBy(o => o.Id.Value).ToList())
            log.Add(new ObjectChanged(damaged.Id, Damage: 0));

        var ending = log.State.ActiveEffects.Where(e => e.Until is UntilEndOfTurn).Select(e => e.Id).ToImmutableList();
        var dated = DatedDelayedTriggers(log.State);
        if (!ending.IsEmpty || !dated.IsEmpty)
            log.Add(new EndOfTurnEffectsEnded(ending, dated));

        EmptyManaPools(log);
    }

    /// <summary>
    /// CR 500.7: extra turns are taken first, most recently created first; otherwise the next
    /// player in turn order who has not lost takes the turn.
    /// </summary>
    [RuleRef("500.7")]
    private void BeginNextTurn(Log log)
    {
        var state = log.State;
        if (state.Outcome is not null)
            return;

        var number = state.Turn.Number + 1;
        if (!state.Turn.ExtraTurns.IsEmpty)
        {
            var who = state.Turn.ExtraTurns[0];
            log.Add(new ExtraTurnTaken(who));
            if (!log.State.Player(who).HasLost)
            {
                BeginTurn(log, who, number, extra: true);
                return;
            }
        }

        var next = NextInTurnOrder(log.State, state.Turn.ActivePlayer);
        if (log.State.Player(next).HasLost)
            return;

        BeginTurn(log, next, number, extra: false);
    }

    /// <summary>CR 117.3a and CR 117.3b: a fresh round of priority, which is nobody having passed yet.</summary>
    [RuleRef("117.3a")]
    private static void GrantPriority(Log log, PlayerId player)
    {
        var holder = log.State.Player(player).HasLost ? NextInTurnOrder(log.State, player) : player;
        log.Add(new PriorityGranted(holder));
    }

    /// <summary>CR 500.5 and CR 703.4q: as each step and phase ends, unspent mana empties.</summary>
    [RuleRef("500.5")]
    private static void EmptyManaPools(Log log)
    {
        foreach (var player in InGame(log.State))
            log.Add(new ManaPoolEmptied(player));
    }

    private static ImmutableList<PlayerId> InGame(GameState state) =>
        [.. state.Players.Where(p => !p.HasLost).Select(p => p.Id)];

    /// <summary>The next player in turn order who is still in the game, CR 103.8 and CR 104.3.</summary>
    [RuleRef("104.3")]
    private static PlayerId NextInTurnOrder(GameState state, PlayerId from)
        => PlayerTurnOrder.NextLiving(state, from) ?? from;

    /// <summary>
    /// The permanents a player controls, CR 110.1. Control is a characteristic the layer system
    /// computes, CR 613, so it is asked for rather than read off the object: a permanent that
    /// changed controller untaps in its new controller's untap step and not its old one's.
    /// Without the layer system the stored controller is all there is, which is the honest
    /// fallback and never right in a game where control changed.
    /// </summary>
    [RuleRef("110.1")]
    private List<GameObject> Permanents(GameState state, PlayerId controller) =>
        [.. state.Objects.Values
            .Where(o => o.Zone.Zone == Zone.Battlefield && ControllerOf(state, o) == controller)
            .OrderBy(o => o.Id.Value)];

    /// <summary>Who controls this permanent now, CR 613.1: the computed controller when there is a layer system to ask.</summary>
    [RuleRef("613.1")]
    private PlayerId ControllerOf(GameState state, GameObject permanent) =>
        characteristics is null ? permanent.InitialController : characteristics.ControllerOf(state, permanent.Id);

    /// <summary>The objects in one of a player's own zones, whether the zone is keyed by player or shared.</summary>
    private static List<GameObject> ObjectsIn(GameState state, Zone zone, PlayerId player) =>
        [.. state.Objects.Values
            .Where(o => o.Zone.Zone == zone && (o.Zone.Player == player || (o.Zone.Player is null && o.Owner == player)))
            .OrderBy(o => o.Id.Value)];

    /// <summary>
    /// Events and the state they produce, kept together so every helper appends rather than
    /// threading a pair through. Each event is folded as it is added, so a later helper reads
    /// what the earlier ones did.
    /// </summary>
    private sealed class Log(GameState state)
    {
        private Outcome _outcome = Outcome.Unchanged(state);

        public GameState State => _outcome.State;

        public void Add(GameEvent e)
        {
            _outcome = _outcome with { State = e.Apply(State), Events = _outcome.Events.Add(e) };
        }

        /// <summary>Take another subsystem's outcome: its events, and the state it folded them into.</summary>
        public void Merge(Outcome outcome)
        {
            _outcome = _outcome.Then(_ => outcome);
        }

        public Outcome Done() => _outcome;
    }
}
