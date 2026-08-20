using System.Collections.Immutable;
using MtgEngine.Domain.Enums;
using MtgEngine.Domain.Models;
using MtgEngine.Rules.Abilities;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;
using MtgEngine.Rules.Views;

namespace MtgEngine.Rules.Engine;

/// <summary>How one player enters a game (CR 103).</summary>
public sealed record PlayerSetup(
    Guid PlayerId,
    string Name,
    int StartingLife,
    IReadOnlyList<CardDefinition> Deck);

/// <summary>
/// A game in progress: its log, the state folded from it, and the actions that append to it.
/// </summary>
/// <remarks>
/// The log is the game and the state is a cache of it — <see cref="GameReducer.Replay"/> of
/// <see cref="Log"/> always equals <see cref="State"/>. Every action here follows the same
/// shape: decide what happened, emit an event, let the reducer apply it. Nothing mutates state
/// directly, so there is no path by which the state and the log can disagree.
/// <para>
/// This class is not thread-safe. One game is one critical section; the session layer that
/// serialises player actions into it arrives in slice 7.
/// </para>
/// </remarks>
public sealed class Game
{
    private readonly List<GameEvent> _log = [];

    private Game(GameState state, IAbilitySource abilities)
    {
        State = state;
        _abilities = abilities;
    }

    private readonly IAbilitySource _abilities;

    /// <summary>The current state. Never sent anywhere — see <see cref="ViewFor"/>.</summary>
    public GameState State { get; private set; }

    /// <summary>Everything that has happened, in order.</summary>
    public IReadOnlyList<GameEvent> Log => _log;

    /// <summary>
    /// Seats the players, turns each deck into a library (CR 401.1), and shuffles them
    /// (CR 103.2). Opening hands are not drawn here — that is part of the mulligan procedure
    /// (CR 103.5), which needs the priority machinery slice 2 brings.
    /// </summary>
    public static Game Start(
        Guid gameId,
        IReadOnlyList<PlayerSetup> setups,
        GameRandom random,
        Guid? startingPlayerId = null,
        IAbilitySource? abilities = null)
    {
        ArgumentNullException.ThrowIfNull(setups);
        ArgumentNullException.ThrowIfNull(random);

        if (setups.Count < 2)
            throw new ArgumentException("A game needs at least two players.", nameof(setups));

        var seats = setups
            .Select(s => new Seat(
                s.PlayerId,
                s.Name,
                s.StartingLife,
                [.. s.Deck.Select(card => new DealtCard(ObjectId.New(), card))]))
            .ToImmutableList();

        // CR 103.1: the starting player is decided at random unless the caller has already
        // decided (a rematch gives it to the previous loser, and tests want it fixed).
        var first = startingPlayerId ?? random.Choose([.. setups.Select(s => s.PlayerId)]);

        var started = new GameStarted(gameId, seats, first);
        var game = new Game(GameReducer.Replay([started]), abilities ?? NoAbilities.Instance);
        game._log.Add(started);

        foreach (var seat in seats)
            game.Shuffle(seat.PlayerId, random);

        return game;
    }

    /// <summary>Shuffles a player's library and records the order it came out in (CR 701.24).</summary>
    public void Shuffle(Guid playerId, GameRandom random)
    {
        ArgumentNullException.ThrowIfNull(random);

        Emit(new LibraryShuffled(playerId, random.Shuffle(State.GetPlayer(playerId).Library)));
    }

    /// <summary>
    /// Draws a card: the top card of the library goes to its owner's hand (CR 121.3).
    /// </summary>
    /// <returns>
    /// The identity the card has in hand, or null if the library was empty — in which case the
    /// draw simply does not happen and the player is marked as having tried. They lose at the
    /// next state-based action check (CR 704.5b), not here.
    /// </returns>
    public ObjectId? Draw(Guid playerId)
    {
        var library = State.GetPlayer(playerId).Library;

        if (library.IsEmpty)
        {
            Emit(new DrawFromEmptyLibraryAttempted(playerId));
            return null;
        }

        return Move(library[0], Zone.Hand, MoveCause.Draw);
    }

    /// <summary>
    /// Moves an object to another zone, where it becomes a new object (CR 400.7).
    /// </summary>
    /// <param name="controllerId">
    /// Who controls it on arrival. Ignored for a library, hand, or graveyard, which are always
    /// the owner's (CR 400.3); defaults to the current controller.
    /// </param>
    /// <returns>The new identity.</returns>
    public ObjectId Move(
        ObjectId id,
        Zone to,
        MoveCause cause = MoveCause.Other,
        Guid? controllerId = null,
        ZonePosition position = ZonePosition.Top)
    {
        var moving = State.GetObject(id);
        var newId = ObjectId.New();

        Emit(new ObjectMoved(
            id,
            newId,
            moving.Zone,
            to,
            controllerId ?? moving.ControllerId,
            cause,
            position));

        return newId;
    }

    /// <summary>
    /// Changes a life total (CR 119.3). A player at or below zero life is not dead here; they
    /// lose at the next state-based action check (CR 704.5a).
    /// </summary>
    public void ChangeLife(Guid playerId, int delta)
    {
        if (delta == 0)
            return;

        Emit(new LifeChanged(playerId, delta, State.GetPlayer(playerId).Life + delta));
    }

    // ---- Starting play, and the turn cycle -------------------------------------------------

    /// <summary>Maximum hand size, checked during cleanup (CR 402.2).</summary>
    public const int MaxHandSize = 7;

    /// <summary>
    /// Draws opening hands and begins the first turn (CR 103.5, 103.8).
    /// </summary>
    /// <remarks>
    /// Mulligans (CR 103.5) are not here. Every mulligan decision is a player choice made in
    /// turn order, which needs the choice machinery that arrives with the effect system; until
    /// then a game opens on the hands it was dealt.
    /// </remarks>
    public void BeginPlay(int openingHandSize = MaxHandSize)
    {
        if (State.HasBegun)
            throw new InvalidOperationException("Play has already begun.");

        foreach (var playerId in State.TurnOrder)
        {
            for (var i = 0; i < openingHandSize; i++)
                Draw(playerId);
        }

        BeginTurn();
    }

    /// <summary>
    /// Players who must discard before the turn can end (CR 514.1).
    /// </summary>
    /// <remarks>
    /// Cleanup stalls here rather than discarding for them. Which card to discard is the
    /// player's choice, and an engine that picks is an engine that is wrong.
    /// </remarks>
    public IReadOnlyList<Guid> PendingDiscards =>
        State.CurrentStep != TurnStep.Cleanup
            ? []
            : [.. State.ActivePlayers()
                .Where(id => State.GetPlayer(id).Hand.Count > MaxHandSize)];

    /// <summary>
    /// Passes priority (CR 117.3d). When everyone has passed in succession, the top of the stack
    /// resolves, or the step ends if the stack is empty (CR 117.4).
    /// </summary>
    public void PassPriority(Guid playerId)
    {
        RequirePriority(playerId);

        var next = State.NextInTurnOrderAfter(playerId);
        Emit(new PriorityPassed(playerId, next));

        if (!State.Priority.AllPassed(State.ActivePlayers()))
        {
            // CR 117.5: state-based actions and triggers are dealt with each time a player
            // would receive priority — which includes receiving it from a pass, not only at the
            // start of a step. Anything they do changes the game under the players who already
            // passed, so those passes no longer count as "in succession" (CR 117.4).
            if (SettleBeforePriority() && !State.IsOver)
                Emit(new PriorityGranted(next));

            return;
        }

        if (State.Stack.IsEmpty)
        {
            // CR 500.2: the step ends only when the stack is empty and all have passed.
            AdvanceStep();
        }
        else
        {
            ResolveTop();
            SettleBeforePriority();
            if (State.IsOver)
                return;

            // CR 117.3b: the active player receives priority after a spell or ability resolves.
            Emit(new PriorityGranted(State.ActivePlayerId));
        }
    }

    /// <summary>
    /// Casts a spell from hand (CR 601). Timing only: costs and targets arrive with the effect
    /// system, so for now casting is legality plus the move to the stack.
    /// </summary>
    public ObjectId CastSpell(Guid playerId, ObjectId cardId)
    {
        RequirePriority(playerId);

        var card = State.GetObject(cardId);
        if (card.Zone != Zone.Hand)
            throw new InvalidOperationException("A spell is cast from hand.");

        if (card.Card.CardTypes.HasFlag(CardType.Land))
            throw new InvalidOperationException("A land is played, not cast (CR 305.1).");

        // CR 117.1a: an instant any time you have priority; anything else only at sorcery speed.
        var isInstant = card.Card.CardTypes.HasFlag(CardType.Instant);
        if (!isInstant && !State.IsSorcerySpeedFor(playerId))
            throw new InvalidOperationException(
                $"{card.Card.Name} can only be cast during your main phase with an empty stack (CR 505.6a).");

        var stackId = Move(cardId, Zone.Stack, MoveCause.Cast, playerId);
        Emit(new SpellCastEvent(playerId, stackId, card.Card.Name));
        SettleBeforePriority();
        // CR 117.3c: the caster receives priority again, and the run of passes is broken.
        Emit(new PriorityGranted(playerId));

        return stackId;
    }

    /// <summary>
    /// Plays a land (CR 305.1, 505.6b). A special action: it does not use the stack, cannot be
    /// countered, and nobody may respond to it (CR 116).
    /// </summary>
    public ObjectId PlayLand(Guid playerId, ObjectId cardId)
    {
        RequirePriority(playerId);

        var card = State.GetObject(cardId);
        if (card.Zone != Zone.Hand)
            throw new InvalidOperationException("A land is played from hand.");

        if (!card.Card.CardTypes.HasFlag(CardType.Land))
            throw new InvalidOperationException($"{card.Card.Name} is not a land.");

        if (!State.IsSorcerySpeedFor(playerId))
            throw new InvalidOperationException(
                "A land is played during your main phase with an empty stack (CR 505.6b).");

        if (State.GetPlayer(playerId).LandsPlayedThisTurn >= 1)
            throw new InvalidOperationException("You have already played a land this turn (CR 505.6b).");

        var onBattlefield = Move(cardId, Zone.Battlefield, MoveCause.Play, playerId);
        Emit(new LandDropUsed(playerId));
        SettleBeforePriority();
        Emit(new PriorityGranted(playerId));

        return onBattlefield;
    }

    /// <summary>Discards a card from hand (CR 701.8), which is how cleanup is satisfied.</summary>
    public void Discard(Guid playerId, ObjectId cardId)
    {
        var card = State.GetObject(cardId);
        if (card.Zone != Zone.Hand || card.OwnerId != playerId)
            throw new InvalidOperationException("That card is not in that player's hand.");

        Move(cardId, Zone.Graveyard, MoveCause.Discard);

        // A cleanup step that was waiting on this can now finish (CR 514.1).
        if (State.CurrentStep == TurnStep.Cleanup && PendingDiscards.Count == 0)
            FinishCleanup();
    }

    /// <summary>
    /// Brings a new object into a zone: a token (CR 111.1), or a card that was never in a deck.
    /// </summary>
    public ObjectId Create(
        Guid ownerId,
        CardDefinition card,
        Zone zone,
        Guid? controllerId = null,
        ZonePosition position = ZonePosition.Top)
    {
        var id = ObjectId.New();
        Emit(new ObjectCreated(id, card, ownerId, controllerId ?? ownerId, zone, position));
        return id;
    }

    /// <summary>
    /// Marks damage on a permanent (CR 120.3). It is not destroyed here — state-based actions
    /// compare the damage with its toughness the next time anyone would get priority (CR 704.5g).
    /// </summary>
    public void MarkDamage(ObjectId permanentId, int amount, bool fromDeathtouch = false)
    {
        if (amount <= 0)
            return;

        Emit(new DamageMarked(permanentId, amount, fromDeathtouch));
    }

    /// <summary>
    /// Creates a continuous effect from a resolved spell or ability (CR 611.2, 613.7b).
    /// </summary>
    /// <param name="untilEndOfTurn">
    /// True for the common duration, which ends during cleanup (CR 514.2) — not at the start of
    /// the end step, a difference that decides whether a pumped creature survives combat.
    /// </param>
    public Guid CreateContinuousEffect(
        string definitionId,
        IReadOnlyList<ObjectId> affected,
        bool untilEndOfTurn = true)
    {
        var id = Guid.NewGuid();
        Emit(new ContinuousEffectCreated(
            id,
            definitionId,
            [.. affected],
            untilEndOfTurn ? State.TurnNumber : null));

        return id;
    }

    /// <summary>What one object's characteristics currently are, after the layers (CR 613).</summary>
    public ComputedCharacteristics CharacteristicsOf(ObjectId id) =>
        Characteristics.Of(State, _abilities, State.GetObject(id));

    /// <summary>Puts counters on a permanent, or takes them off with a negative delta (CR 122).</summary>
    public void ChangeCounters(ObjectId permanentId, string kind, int delta)
    {
        if (delta == 0)
            return;

        Emit(new CountersChanged(permanentId, kind, delta));
    }

    /// <summary>Taps a permanent (CR 701.26a).</summary>
    public void Tap(ObjectId permanentId)
    {
        var permanent = State.GetObject(permanentId).Permanent
            ?? throw new InvalidOperationException("Only a permanent can be tapped.");

        if (permanent.IsTapped)
            throw new InvalidOperationException("It is already tapped.");

        Emit(new PermanentTapped(permanentId));
    }

    // ---- Internals -------------------------------------------------------------------------

    private void RequirePriority(Guid playerId)
    {
        if (State.IsOver)
            throw new InvalidOperationException("The game is over (CR 104.2).");

        if (State.Priority.Holder != playerId)
            throw new InvalidOperationException("You do not have priority (CR 117.1).");
    }

    /// <summary>
    /// Everything that happens before a player actually receives priority (CR 117.5, 704.3).
    /// </summary>
    /// <remarks>
    /// The order is the rules' order and it matters: state-based actions run as one batch and
    /// repeat until nothing more applies, <em>then</em> waiting triggers go on the stack, and then
    /// the whole thing repeats — because a trigger going on the stack can itself cause a
    /// state-based action, and a state-based action can cause something to trigger.
    /// <para>
    /// The previous engine ran state-based actions after every individual mutation and never
    /// collected triggers at all. Getting this loop right, in one place, is most of what slice 3
    /// is.
    /// </para>
    /// </remarks>
    /// <returns>Whether anything happened, which means the game changed under the players.</returns>
    private bool SettleBeforePriority()
    {
        var didSomething = false;

        for (var guard = 0; guard < 100; guard++)
        {
            if (State.IsOver)
                return didSomething;

            var actions = StateBasedActions.Check(State, _abilities);
            if (actions.Count > 0)
            {
                // CR 704.3: performed simultaneously as a single event, then check again.
                foreach (var action in actions)
                    Emit(action);

                didSomething = true;
                CheckForEnd();
                continue;
            }

            if (State.PendingTriggers.IsEmpty)
                return didSomething;

            PutTriggersOnStack();
            didSomething = true;
        }

        throw new InvalidOperationException(
            "State-based actions and triggers did not settle (CR 704.3).");
    }

    /// <summary>Ends the game when one player is left, or none (CR 104.2a, 104.4).</summary>
    private void CheckForEnd()
    {
        if (State.IsOver)
            return;

        var remaining = State.ActivePlayers().ToList();
        if (remaining.Count > 1)
            return;

        Emit(new GameEnded(remaining.Count == 1 ? remaining[0] : null));
    }

    /// <summary>
    /// Puts every waiting trigger on the stack in APNAP order (CR 603.3, 603.3b).
    /// </summary>
    /// <remarks>
    /// The active player's triggers go on lowest, then each other player's in turn order, so the
    /// last player's resolve first. With one player's several triggers the rules let that player
    /// choose the order; the engine keeps the order they triggered in until there is a choice
    /// system to ask with.
    /// </remarks>
    private void PutTriggersOnStack()
    {
        var waiting = State.PendingTriggers;

        foreach (var playerId in State.ApnapOrder())
        {
            foreach (var trigger in waiting.Where(t => t.ControllerId == playerId))
            {
                // CR 603.6: the source may already have left the battlefield. The ability still
                // goes on the stack — it triggered, and that is enough.
                var sourceCard = State.TryGetObject(trigger.SourceId, out var source)
                    ? source.Card
                    : LastKnownCard(trigger.SourceId);

                Emit(new TriggerPutOnStack(
                    ObjectId.New(),
                    trigger.SourceId,
                    sourceCard,
                    trigger.AbilityId,
                    trigger.Text,
                    trigger.ControllerId));
            }
        }
    }

    /// <summary>
    /// The card an object had, for a source that has since left the battlefield (CR 603.10).
    /// </summary>
    private CardDefinition LastKnownCard(ObjectId sourceId)
    {
        foreach (var e in _log.OfType<ObjectMoved>().Reverse())
        {
            if (e.OldId == sourceId && State.TryGetObject(e.NewId, out var moved))
                return moved.Card;
        }

        foreach (var e in _log.OfType<ObjectCreated>().Reverse())
        {
            if (e.Id == sourceId)
                return e.Card;
        }

        throw new InvalidOperationException($"No card is known for {sourceId}.");
    }

    /// <summary>
    /// Collects abilities that this event triggers (CR 603.2), to go on the stack later.
    /// </summary>
    /// <remarks>
    /// Runs on every event, because a trigger condition can be anything — and it reads the state
    /// as it was <em>before</em> the event applied, which is the state the trigger condition is
    /// about. "Whenever a creature dies" has to see the creature.
    /// </remarks>
    private void CollectTriggers(GameEvent e, GameState before)
    {
        // Triggers never trigger off other triggers being noticed; that would not terminate.
        if (e is AbilityTriggered or TriggerPutOnStack)
            return;

        foreach (var (id, obj) in before.Objects)
        {
            foreach (var ability in _abilities.TriggersOf(obj.Card))
            {
                if (obj.Zone != ability.FunctionsFrom)
                    continue;

                if (ability.Triggers(e, before, obj))
                {
                    _triggersFound.Add(new AbilityTriggered(
                        id, ability.Id, ability.Text, obj.ControllerId));
                }
            }
        }
    }

    private readonly List<AbilityTriggered> _triggersFound = [];

    /// <summary>Replacement effects that apply to this event, at most one per event (CR 614.5).</summary>
    /// <remarks>
    /// Returns at most one, because applying one produces new events that go through this again —
    /// which is how CR 614.5 works: each replacement effect applies only once to a given event,
    /// and the result is re-examined for others.
    /// <para>
    /// When several apply at once, the affected player chooses the order (CR 616.1). Until there
    /// is a way to ask them, this takes them in timestamp order and says so rather than pretending
    /// the question does not arise.
    /// </para>
    /// </remarks>
    private IEnumerable<(string Id, GameObject Source, Func<GameEvent, GameState, GameObject, IReadOnlyList<GameEvent>> Replace)> Replacements(
        GameEvent e, HashSet<(ObjectId, string)> applied)
    {
        if (e is EventReplaced)
            yield break;

        // Every object, not only the battlefield: "as this enters" functions from the stack
        // while the card is still a spell (CR 614.6, 614.1c), and that is the commonest
        // replacement effect there is. FunctionsFrom is what decides, so it has to be asked.
        foreach (var (id, source) in State.Objects)
        {
            foreach (var effect in _abilities.ReplacementsOf(source.Card))
            {
                if (applied.Contains((id, effect.Id)))
                    continue;

                if (source.Zone != effect.FunctionsFrom || !effect.Applies(e, State, source))
                    continue;

                applied.Add((id, effect.Id));
                yield return (effect.Id, source, effect.Replace);
                yield break;
            }
        }
    }



    private void BeginTurn()
    {
        var next = State.HasBegun
            ? State.NextInTurnOrderAfter(State.ActivePlayerId)
            : State.ActivePlayerId;

        Emit(new TurnBegan(State.TurnNumber + 1, next));
        EnterStep(TurnStep.Untap);
    }

    /// <summary>
    /// Walks forward until the game reaches a step where somebody has priority.
    /// </summary>
    /// <remarks>
    /// The untap step and cleanup grant nobody priority (CR 502.4, 514.3), so they are not
    /// places a game can rest; entering one performs its actions and moves on. Cleanup is the
    /// exception that can stall, waiting on a discard (CR 514.1).
    /// </remarks>
    private void AdvanceStep()
    {
        if (State.IsOver)
            return;

        var next = State.CurrentStep.Next();
        if (next is null)
        {
            BeginTurn();
            return;
        }

        EnterStep(next.Value);
    }

    private void EnterStep(TurnStep step)
    {
        if (State.IsOver)
            return;

        Emit(new StepBegan(step));

        switch (step)
        {
            case TurnStep.Untap:
                Untap();
                // CR 500.3: a step in which no player receives priority ends once its actions
                // are done.
                AdvanceStep();
                return;

            case TurnStep.Draw:
                DrawForTurn();
                break;

            case TurnStep.Cleanup:
                Emit(new PriorityWithdrawn());
                // CR 514.2: damage is removed and "until end of turn" effects end, at the same
                // time, as a turn-based action.
                Emit(new DamageCleared());
                foreach (var expiring in State.FloatingEffects
                    .Where(f => f.UntilEndOfTurn is not null && f.UntilEndOfTurn <= State.TurnNumber)
                    .ToList())
                {
                    Emit(new ContinuousEffectEnded(expiring.Id));
                }

                if (PendingDiscards.Count == 0)
                    FinishCleanup();
                return;

            default:
                break;
        }

        // CR 117.5: state-based actions and triggers are dealt with before anyone actually
        // receives priority.
        SettleBeforePriority();

        if (State.IsOver)
            return;

        // CR 117.3a: the active player receives priority at the beginning of most steps.
        Emit(new PriorityGranted(State.ActivePlayerId));
    }

    private void Untap()
    {
        Emit(new PriorityWithdrawn());

        var mine = State.Battlefield
            .Where(id => State.GetObject(id).ControllerId == State.ActivePlayerId)
            .ToList();

        // CR 302.6: a permanent its controller has controlled since their turn began is no
        // longer summoning sick. That is decided as the turn starts, before anything untaps.
        var sick = mine
            .Where(id => State.GetObject(id).Permanent?.HasSummoningSickness == true)
            .ToImmutableList();
        if (!sick.IsEmpty)
            Emit(new SummoningSicknessCleared(sick));

        // CR 502.3: the active player's permanents untap, simultaneously.
        var tapped = mine
            .Where(id => State.GetObject(id).Permanent?.IsTapped == true)
            .ToImmutableList();
        if (!tapped.IsEmpty)
            Emit(new PermanentsUntapped(tapped));
    }

    private void DrawForTurn()
    {
        // CR 103.8a: in a two-player game, the player who plays first skips the draw step of
        // their first turn. With more players everyone draws every turn.
        var skips = State.TurnOrder.Count == 2
            && State.TurnNumber == 1
            && State.ActivePlayerId == FirstPlayerId;

        if (!skips)
            Draw(State.ActivePlayerId);
    }

    private void FinishCleanup()
    {
        // CR 514.3: normally no player receives priority during cleanup, and the turn ends.
        BeginTurn();
    }

    private void ResolveTop()
    {
        var stackId = State.Stack[0];
        var spell = State.GetObject(stackId);

        Emit(new PriorityWithdrawn());

        if (spell.Ability is not null)
        {
            // CR 608.2m applies to cards. An ability was never a card and has no graveyard to go
            // to: it simply leaves the stack and stops existing.
            Emit(new StackObjectResolved(stackId, spell.Ability.Text));
            Emit(new ObjectCeasedToExist(stackId, Zone.Stack));
            return;
        }

        // CR 608.3: a permanent spell becomes a permanent. CR 608.2m: an instant or sorcery is
        // put into its owner's graveyard as the final part of its resolution.
        var destination = IsPermanentCard(spell.Card) ? Zone.Battlefield : Zone.Graveyard;
        Move(stackId, destination, MoveCause.Resolve, spell.ControllerId);

        Emit(new StackObjectResolved(stackId, spell.Card.Name));
    }

    /// <summary>Card types that exist on the battlefield (CR 110.4, 205.2a).</summary>
    private static bool IsPermanentCard(CardDefinition card) =>
        (card.CardTypes & (CardType.Creature | CardType.Artifact | CardType.Enchantment
            | CardType.Land | CardType.Planeswalker | CardType.Battle)) != 0;

    /// <summary>Who started the game, for CR 103.8a. Read from the log, which cannot drift.</summary>
    private Guid FirstPlayerId => ((GameStarted)_log[0]).StartingPlayerId;

    /// <summary>What the given player may see (CR 400.2).</summary>
    public GameView ViewFor(Guid playerId) => PlayerViewProjector.Project(State, playerId);

    private void Emit(GameEvent e) => Emit(e, []);

    /// <param name="applied">
    /// Replacement effects already used on this event. CR 614.5: each applies only once to a
    /// given event, and that carries down to whatever replaced it — otherwise an effect that
    /// replaces damage with damage would replace its own output forever.
    /// </param>
    private void Emit(GameEvent e, HashSet<(ObjectId, string)> applied)
    {
        foreach (var replacement in Replacements(e, applied))
        {
            // CR 614.1: the event never happens. What happens instead is emitted in its place,
            // and because the original did not occur, nothing triggers off it (CR 603.2g).
            _log.Add(new EventReplaced(replacement.Id, e.Describe()));
            foreach (var instead in replacement.Replace(e, State, replacement.Source))
                Emit(instead, applied);

            return;
        }

        var before = State;
        State = GameReducer.Apply(State, e);
        _log.Add(e);

        // CR 603.2: an ability triggers the moment its event happens, even mid-resolution.
        // Nothing happens yet — the trigger waits (CR 117.2a) — so this only records them.
        CollectTriggers(e, before);
        if (_triggersFound.Count == 0)
            return;

        var found = _triggersFound.ToList();
        _triggersFound.Clear();
        foreach (var trigger in found)
        {
            State = GameReducer.Apply(State, trigger);
            _log.Add(trigger);
        }
    }
}
