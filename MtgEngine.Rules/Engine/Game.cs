using System.Collections.Immutable;
using MtgEngine.Domain.Enums;
using MtgEngine.Domain.Models;
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

    private Game(GameState state) => State = state;

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
        Guid? startingPlayerId = null)
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
        var game = new Game(GameReducer.Replay([started]));
        game._log.Add(started);

        foreach (var seat in seats)
            game.Shuffle(seat.PlayerId, random);

        return game;
    }

    /// <summary>Shuffles a player's library and records the order it came out in (CR 701.20).</summary>
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
    /// Draws opening hands and begins the first turn (CR 103.4, 103.6).
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

        Emit(new PriorityPassed(playerId, State.NextInTurnOrderAfter(playerId)));

        if (!State.Priority.AllPassed(State.ActivePlayers()))
            return;

        if (State.Stack.IsEmpty)
        {
            // CR 500.2: the step ends only when the stack is empty and all have passed.
            AdvanceStep();
        }
        else
        {
            ResolveTop();
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

    /// <summary>Taps a permanent (CR 701.21a).</summary>
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
        if (State.Priority.Holder != playerId)
            throw new InvalidOperationException("You do not have priority (CR 117.1).");
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
                Emit(new DamageCleared());
                if (PendingDiscards.Count == 0)
                    FinishCleanup();
                return;

            default:
                break;
        }

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
        // CR 103.7a: in a two-player game, the player who plays first skips the draw step of
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

    /// <summary>Who started the game, for CR 103.7a. Read from the log, which cannot drift.</summary>
    private Guid FirstPlayerId => ((GameStarted)_log[0]).StartingPlayerId;

    /// <summary>What the given player may see (CR 400.2).</summary>
    public GameView ViewFor(Guid playerId) => PlayerViewProjector.Project(State, playerId);

    private void Emit(GameEvent e)
    {
        State = GameReducer.Apply(State, e);
        _log.Add(e);
    }
}
