using System.Collections.Immutable;
using MtgEngine.Domain.Enums;
using MtgEngine.Domain.Models;
using MtgEngine.Rules.Abilities;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.Mana;
using MtgEngine.Rules.State;
using MtgEngine.Rules.Views;

namespace MtgEngine.Rules.Engine;

/// <summary>How one player enters a game (CR 103).</summary>
public sealed record PlayerSetup(
    Guid PlayerId,
    string Name,
    int StartingLife,
    IReadOnlyList<CardDefinition> Deck)
{
    /// <summary>
    /// The oracle id of this player's commander, for a Commander game (CR 903.3).
    /// </summary>
    /// <remarks>
    /// Null for every other format. When set, the card is taken out of the deck and put into the
    /// command zone before the game begins (CR 903.6) rather than shuffled into the library.
    /// </remarks>
    public string? CommanderOracleId { get; init; }

    /// <summary>
    /// This player's Attraction deck, if they brought one (CR 717.2).
    /// </summary>
    /// <remarks>
    /// Empty for every player who did not, which is nearly all of them. Attraction cards do not
    /// begin the game in a deck and do not count towards its size (CR 717.2) — they are a
    /// supplementary deck that exists in the command zone — so they arrive here beside the deck
    /// rather than inside it, the way a commander arrives as an id inside it.
    /// <para>
    /// It reaches the log as one <see cref="ObjectCreated"/> per card, in the shuffled order, so
    /// the deck a resumed game opens from is the deck the original opened from. Nothing about it
    /// is a state field: the cards are objects in a zone the reducer already folds.
    /// </para>
    /// </remarks>
    public IReadOnlyList<CardDefinition> AttractionDeck { get; init; } = [];
}

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

    private Game(GameState state, IAbilitySource abilities, GameRandom random)
    {
        State = state;
        _abilities = abilities;
        _random = random;
    }

    private readonly IAbilitySource _abilities;

    /// <summary>
    /// The game's randomness, kept so a mulligan's shuffle is part of the same sequence the
    /// opening shuffle came from (CR 103.5).
    /// </summary>
    private readonly GameRandom _random;

    /// <summary>The current state. Never sent anywhere — see <see cref="ViewFor"/>.</summary>
    public GameState State { get; private set; }

    /// <summary>Everything that has happened, in order.</summary>
    public IReadOnlyList<GameEvent> Log => _log;

    /// <summary>
    /// Rebuilds a game in progress from its log.
    /// </summary>
    /// <remarks>
    /// This is the whole of loading a saved game. State is a fold of the log, so replaying the
    /// events <em>is</em> restoring the position — there is nothing else to restore, and no way
    /// for the rebuilt game to be in a state the engine could not have reached.
    /// <para>
    /// The randomness is new. It is not a fresh <em>game</em> — the log has every shuffle's
    /// resulting order written into it, so replaying reproduces the board exactly — but the
    /// sequence a resumed game draws from afterwards should not repeat the one it drew from
    /// before, which is what continuing from the original seed would do.
    /// </para>
    /// </remarks>
    /// <param name="log">The events, in order, as they were emitted.</param>
    public static Game Resume(
        IReadOnlyList<GameEvent> log,
        GameRandom random,
        IAbilitySource? abilities = null)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(random);

        if (log.Count == 0 || log[0] is not GameStarted)
            throw new ArgumentException("A game log starts with the game starting.", nameof(log));

        var game = new Game(GameReducer.Replay(log), abilities ?? NoAbilities.Instance, random);
        game._log.AddRange(log);
        return game;
    }

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
        var game = new Game(GameReducer.Replay([started]), abilities ?? NoAbilities.Instance, random);
        game._log.Add(started);

        // CR 903.6: each player puts their commander from their deck face up into the command
        // zone, before anything is shuffled — a commander shuffled into the library first is a
        // commander that can be drawn.
        foreach (var setup in setups)
        {
            if (string.IsNullOrEmpty(setup.CommanderOracleId))
                continue;

            var inLibrary = game.State.GetPlayer(setup.PlayerId).Library
                .FirstOrDefault(id => string.Equals(
                    game.State.GetObject(id).Card.OracleId,
                    setup.CommanderOracleId,
                    StringComparison.Ordinal));

            if (inLibrary == default)
            {
                throw new InvalidOperationException(
                    $"{setup.Name}'s commander is not in their deck.");
            }

            var inCommandZone = game.Move(inLibrary, Zone.Command, MoveCause.Other, setup.PlayerId);
            game.Emit(new CommanderDesignated(
                setup.PlayerId, setup.CommanderOracleId, inCommandZone));
        }

        // CR 717.2: a player playing with Attractions begins with a supplementary Attraction
        // deck that exists in the command zone, shuffled before the game begins (CR 103.3a).
        // No new zone and no new state field: the deck is that player's Attraction cards sitting
        // in the command zone in order, exactly as a dungeon card sits there (CR 309.2b), and
        // the top card of the deck is the first of them. The shuffle's result goes into the log
        // as the order the cards were created in, which is the same promise LibraryShuffled
        // makes - a resumed game opens the deck the original opened.
        foreach (var setup in setups)
        {
            if (setup.AttractionDeck.Count == 0)
                continue;

            foreach (var attraction in random.Shuffle(setup.AttractionDeck))
            {
                game.Emit(new ObjectCreated(
                    ObjectId.New(),
                    attraction,
                    setup.PlayerId,
                    setup.PlayerId,
                    Zone.Command,
                    ZonePosition.Bottom));
            }
        }

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
    public void BeginPlay(int openingHandSize = MaxHandSize, bool withMulligans = true)
    {
        if (State.HasBegun || State.IsMulliganing)
            throw new InvalidOperationException("Play has already begun.");

        _openingHandSize = openingHandSize;

        foreach (var playerId in State.TurnOrder)
        {
            for (var i = 0; i < openingHandSize; i++)
                Draw(playerId);
        }

        if (!withMulligans)
        {
            AskNextOpeningHandAction();
            return;
        }

        // CR 103.5: the starting player declares first, then each other player in turn order.
        Emit(new MulligansBegan());
        AskNextMulligan();
    }

    /// <summary>
    /// Asks the next player who has not yet declared whether they will mulligan (CR 103.5).
    /// </summary>
    /// <remarks>
    /// Declarations go round in turn order, and only once everyone has declared do the
    /// mulligans happen — which is why this collects answers rather than acting on each one.
    /// </remarks>
    private void AskNextMulligan()
    {
        foreach (var playerId in State.PlayersFrom(FirstPlayerId))
        {
            // CR 103.5: "Once a player chooses not to take a mulligan... that player may not
            // take any further mulligans." They are out of the procedure, not merely done with
            // this round, so a later round must not ask them again.
            if (_keptHand.Contains(playerId) || _mulliganDeclared.ContainsKey(playerId))
                continue;

            // CR 103.5: a player may take mulligans until their opening hand would be zero
            // cards. With N mulligans taken they would bottom N, so at N == hand size there is
            // nothing left to keep.
            var taken = State.MulligansTaken.GetValueOrDefault(playerId);
            if (taken >= _openingHandSize)
            {
                _keptHand.Add(playerId);
                continue;
            }

            Ask(new PendingChoice
            {
                Id = "mulligan:" + playerId.ToString("N") + ":" + taken,
                PlayerId = playerId,
                Kind = ChoiceKind.Mulligan,
                Prompt = taken == 0
                    ? "Keep this hand, or take a mulligan?"
                    : $"Keep this hand and put {taken} card(s) on the bottom, or mulligan again?",
                Options = [new ChoiceOption("keep", "Keep"), new ChoiceOption("mulligan", "Mulligan")],
            });
            return;
        }

        TakeDeclaredMulligans();
    }

    private void ResolveMulliganDeclaration(Guid playerId, string pick)
    {
        var mulliganing = string.Equals(pick, "mulligan", StringComparison.Ordinal);
        _mulliganDeclared[playerId] = mulliganing;

        if (!mulliganing)
            _keptHand.Add(playerId);

        AskNextMulligan();
    }

    /// <summary>
    /// Everyone who declared a mulligan takes one, at the same time (CR 103.5).
    /// </summary>
    private void TakeDeclaredMulligans()
    {
        var mulliganing = _mulliganDeclared.Where(kv => kv.Value).Select(kv => kv.Key).ToList();

        if (mulliganing.Count == 0)
        {
            FinishMulligans();
            return;
        }

        foreach (var playerId in mulliganing)
        {
            // Hand back into the library, shuffle, draw a fresh hand of the full size.
            foreach (var cardId in State.GetPlayer(playerId).Hand)
                Move(cardId, Zone.Library, MoveCause.Other, position: ZonePosition.Bottom);

            Shuffle(playerId, _random);

            for (var i = 0; i < _openingHandSize; i++)
                Draw(playerId);

            Emit(new MulliganTaken(
                playerId, State.MulligansTaken.GetValueOrDefault(playerId) + 1));
        }

        // The round is over. Only the players who mulliganed declare again — the rest have
        // kept and are finished (CR 103.5).
        _mulliganDeclared.Clear();
        AskNextMulligan();
    }

    /// <summary>
    /// Asks each player who kept after mulliganing which cards go on the bottom (CR 103.5).
    /// </summary>
    private void FinishMulligans()
    {
        foreach (var playerId in State.PlayersFrom(FirstPlayerId))
        {
            var taken = State.MulligansTaken.GetValueOrDefault(playerId);
            if (taken == 0 || _bottomed.Contains(playerId))
                continue;

            var hand = State.GetPlayer(playerId).Hand;
            var bottom = Math.Min(taken, hand.Count);
            if (bottom == 0)
            {
                _bottomed.Add(playerId);
                continue;
            }

            Ask(new PendingChoice
            {
                Id = "bottom:" + playerId.ToString("N"),
                PlayerId = playerId,
                Kind = ChoiceKind.BottomAfterMulligan,
                Prompt = $"Put {bottom} card(s) from your hand on the bottom of your library.",
                Options = [.. hand.Select(id => new ChoiceOption(
                    id.Value.ToString("N"), State.GetObject(id).Card.Name))],
                MinPicks = bottom,
                MaxPicks = bottom,
            });
            return;
        }

        foreach (var playerId in State.TurnOrder)
        {
            Emit(new MulliganKept(playerId, State.MulligansTaken.GetValueOrDefault(playerId)));
        }

        Emit(new MulligansFinished());
        AskNextOpeningHandAction();
    }

    /// <summary>
    /// Offers each player in turn whatever their opening hand allows (CR 103.6).
    /// </summary>
    /// <remarks>
    /// "Once the mulligan process is complete, the starting player may take any such actions in
    /// any order. Then each other player in turn order may do the same." So it is a queue round
    /// the table starting from the player who goes first, and the first turn begins only once
    /// everybody has been past.
    /// <para>
    /// Whose turn it is to be asked is read out of <see cref="GameState.OpeningHandActed"/>
    /// rather than from a field here, because a saved game is its log and a field is not in one.
    /// A player with nothing to do is marked and skipped rather than being asked to pick from an
    /// empty list — a question with one possible answer is not a question (CR 118.3's lesson,
    /// one procedure over), and stopping the game to ask it is how a game stalls.
    /// </para>
    /// <para>
    /// Only CR 103.6a is implemented. The reveal of 103.6b is a different action with a different
    /// payload — the card stays revealed until the first turn begins — and no card printing it is
    /// otherwise readable, so offering it here would be a question about nothing.
    /// </para>
    /// </remarks>
    private void AskNextOpeningHandAction()
    {
        foreach (var playerId in State.PlayersFrom(FirstPlayerId))
        {
            if (State.OpeningHandActed.Contains(playerId))
                continue;

            var eligible = State.GetPlayer(playerId).Hand
                .Where(id => _abilities.MayBeginOnBattlefield(State.GetObject(id).Card))
                .ToList();

            if (eligible.Count == 0)
            {
                Emit(new OpeningHandActionsTaken(playerId));
                continue;
            }

            Ask(new PendingChoice
            {
                Id = "opening-hand:" + playerId.ToString("N"),
                PlayerId = playerId,
                Kind = ChoiceKind.OpeningHandBattlefield,
                Prompt = "Begin the game with any of these on the battlefield, "
                    + "or pick nothing to decline.",
                Options = [.. eligible.Select(id => new ChoiceOption(
                    id.Value.ToString("N"), State.GetObject(id).Card.Name))],
                MinPicks = 0,
                MaxPicks = eligible.Count,
            });

            return;
        }

        BeginTurn();
    }

    /// <summary>Puts the chosen cards onto the battlefield before the game starts (CR 103.6a).</summary>
    /// <remarks>
    /// An ordinary zone change, which is the whole of what the rule says to do: "the player
    /// taking this action puts that card onto the battlefield". It is not played and not cast,
    /// so nothing is paid and nothing goes on the stack.
    /// </remarks>
    private void BeginWithOnBattlefield(Guid playerId, IReadOnlyList<string> picks)
    {
        foreach (var pick in picks)
        {
            var id = State.GetPlayer(playerId).Hand
                .First(h => string.Equals(h.Value.ToString("N"), pick, StringComparison.Ordinal));

            Move(id, Zone.Battlefield, MoveCause.Other, playerId);
        }

        Emit(new OpeningHandActionsTaken(playerId));
        AskNextOpeningHandAction();
    }

    private void BottomAfterMulligan(Guid playerId, IReadOnlyList<string> picks)
    {
        foreach (var pick in picks)
        {
            var id = State.GetPlayer(playerId).Hand
                .First(h => string.Equals(h.Value.ToString("N"), pick, StringComparison.Ordinal));
            Move(id, Zone.Library, MoveCause.Other, position: ZonePosition.Bottom);
        }

        _bottomed.Add(playerId);
        FinishMulligans();
    }

    private readonly Dictionary<Guid, bool> _mulliganDeclared = [];

    /// <summary>Players who have kept and are out of the procedure for good (CR 103.5).</summary>
    private readonly HashSet<Guid> _keptHand = [];
    private readonly HashSet<Guid> _bottomed = [];
    private int _openingHandSize = MaxHandSize;

    /// <summary>
    /// Players who must discard before the turn can end (CR 514.1).
    /// </summary>
    /// <remarks>
    /// Kept for a caller that wants to know without inspecting the choice. The discard itself is
    /// asked for like every other decision — it used to be a list the caller had to poll and
    /// answer through a separate method, which meant a client that did not know to look simply
    /// hung at cleanup with nothing on screen saying why.
    /// </remarks>
    public IReadOnlyList<Guid> PendingDiscards =>
        State.CurrentStep != TurnStep.Cleanup
            ? []
            : [.. State.ActivePlayers()
                .Where(id => State.GetPlayer(id).Hand.Count > HandLimitFor(id))];

    /// <summary>
    /// How many cards a player may keep at cleanup, or no limit at all (CR 402.2, 514.1).
    /// </summary>
    /// <remarks>
    /// Seven unless something on the battlefield says otherwise, and asked rather than assumed
    /// for the same reason every other characteristic is computed: the answer changes when a
    /// permanent enters or leaves, and a number written into the cleanup step would go on being
    /// seven while the card that says otherwise sat on the battlefield doing nothing.
    /// </remarks>
    private int? HandLimitFor(Guid playerId)
    {
        var board = State.Battlefield.Select(State.GetObject).ToList();

        if (board.Any(o => ControllerOf(o) == playerId
            && _abilities.RemovesHandLimit(Characteristics.CardOf(State, _abilities, o))))
        {
            return null;
        }

        // "Your maximum hand size is reduced by three" and "each opponent's ... by two" are the
        // same rule pointed two ways, so the permanent's controller decides which players its
        // change reaches rather than the change naming them. Whose is asked of the *computed*
        // controller and not the id the object was created with, for the same reason every other
        // question here goes through ControllerOf: control is layer 2 (CR 613.1b), so a stolen
        // Gnat Miser shrinks the hand of whoever holds it now and of nobody else.
        //
        // Several of them add up (CR 402.2), in either direction and in any order, and the total
        // stops at zero - a negative limit would ask for more cards than a player has and there
        // is no such discard (CR 514.1).
        //
        // Both reads go through Characteristics.CardOf rather than the printed card, because a
        // permanent that has become a copy *is* the copied card (CR 613.2c) - so a Clone of Gnat
        // Miser shrinks a hand and a Clone of Reliquary Tower lifts the limit. Closed as a pair
        // on purpose: fixing one would leave this method asking two different questions about
        // the same battlefield.
        var limit = MaxHandSize;

        foreach (var permanent in board)
        {
            var controller = ControllerOf(permanent);

            foreach (var change in _abilities.HandSizeChangesOf(
                Characteristics.CardOf(State, _abilities, permanent)))
            {
                var reaches = change.Scope switch
                {
                    PlayerScope.You => controller == playerId,
                    PlayerScope.EachOpponent or PlayerScope.EachOtherPlayer =>
                        controller != playerId,
                    PlayerScope.EachPlayer => true,
                    _ => false,
                };

                if (reaches)
                    limit += change.Delta;
            }
        }

        return Math.Max(0, limit);
    }

    /// <summary>
    /// Asks a player over their maximum hand size which cards to discard (CR 514.1).
    /// </summary>
    /// <returns>True when a question was asked and cleanup has to wait.</returns>
    private bool AskDiscardIfNeeded()
    {
        foreach (var playerId in State.ActivePlayers())
        {
            if (HandLimitFor(playerId) is not { } limit)
                continue;

            var hand = State.GetPlayer(playerId).Hand;
            var excess = hand.Count - limit;
            if (excess <= 0)
                continue;

            Ask(new PendingChoice
            {
                Id = "discard:" + playerId.ToString("N") + ":" + State.TurnNumber,
                PlayerId = playerId,
                Kind = ChoiceKind.DiscardToHandSize,
                Prompt = $"Discard {excess} card(s) down to {limit}.",
                Options = [.. hand.Select(id => new ChoiceOption(
                    id.Value.ToString("N"), State.GetObject(id).Card.Name))],
                MinPicks = excess,
                MaxPicks = excess,
            });

            return true;
        }

        return false;
    }

    private void DiscardChosen(Guid playerId, IReadOnlyList<string> picks)
    {
        // Discarding down to hand size is one discard of however many cards, so a trigger
        // watching for "one or more" fires once (CR 603.2c).
        AsOneBatch(() =>
        {
            foreach (var pick in picks)
            {
                var id = State.GetPlayer(playerId).Hand.First(
                    h => string.Equals(h.Value.ToString("N"), pick, StringComparison.Ordinal));
                Move(id, Zone.Graveyard, MoveCause.Discard);
            }
        });

        // Another player may still be over; cleanup only ends when nobody is (CR 514.1).
        if (!AskDiscardIfNeeded())
            FinishCleanup();
    }

    /// <summary>
    /// Passes priority (CR 117.3d). When everyone has passed in succession, the top of the stack
    /// resolves, or the step ends if the stack is empty (CR 117.4).
    /// </summary>
    /// <summary>
    /// A player concedes and leaves the game immediately (CR 104.3a).
    /// </summary>
    /// <remarks>
    /// The one action that needs no priority and no timing: a player may concede at any time,
    /// even mid-resolution and even when it is not their turn. That is why this does not call
    /// <see cref="RequirePriority"/> — it is not a game action taken with priority, it is a
    /// player leaving.
    ///
    /// Conceding is losing, so it goes through the same <see cref="PlayerLost"/> the
    /// state-based actions use, and the same end check decides whether anyone is left
    /// (CR 104.2a). Nothing here needs to know how many players there are.
    /// </remarks>
    public void Concede(Guid playerId)
    {
        if (State.IsOver)
            return;

        var player = State.GetPlayer(playerId);
        if (player.HasLost)
            return;

        Emit(new PlayerLost(playerId, "conceded", "104.3a"));

        // A player who leaves may have been the one everybody was waiting on. A question put to
        // somebody who is no longer in the game can never be answered, so it goes with them —
        // otherwise the game sits forever holding a decision nobody can make.
        if (State.Choice is { } pending && pending.PlayerId == playerId)
            Emit(new ChoiceMade(pending.Id, []));

        CheckForEnd();

        if (!State.IsOver && State.Priority.Holder == playerId)
        {
            var next = State.NextInTurnOrderAfter(playerId);
            Emit(new PriorityGranted(next));
        }
    }

    public void PassPriority(Guid playerId)
    {
        RequirePriority(playerId);

        // CR 601.2b: an offer to cast for free is a window, not a standing permission. Passing is
        // the player declining it — which is why the offer is revoked here and not on a timer:
        // the rules give one window and this is the moment it closes.
        foreach (var id in State.Exile.Concat(State.GetPlayer(playerId).Graveyard).ToList())
        {
            if (State.TryGetObject(id, out var offered)
                && offered is { MayCastFree: true, OwnerId: var owner }
                && owner == playerId)
            {
                // CR 701.57a: a discover the player declines is drawn rather than left in
                // exile, which is the half that makes it worth casting nothing into.
                if (offered.ToHandIfCastDeclined)
                    Move(id, Zone.Hand, MoveCause.Other, owner);
                else
                    Emit(new FreeCastLapsed(id));
            }
        }

        // The described offers lapse on the same pass and for the same reason - the printed
        // window is inside the resolution, and this is the closest an engine that cannot cast
        // there gets to it. A permission with no end at all would be an Omniscience.
        foreach (var offer in State.HandCastOffers)
        {
            if (offer.PlayerId == playerId)
                Emit(new HandCastOfferLapsed(offer.Id));
        }

        var next = State.NextInTurnOrderAfter(playerId);
        Emit(new PriorityPassed(playerId, next));

        if (!State.Priority.AllPassed(State.ActivePlayers()))
        {
            // Whoever the pass went to is the player who would receive priority (CR 117.3d), so
            // a question raised by the settle has to hand it back to them and not to the active
            // player.
            _priorityRecipient = next;
            // CR 117.5: state-based actions and triggers are dealt with each time a player
            // would receive priority — which includes receiving it from a pass, not only at the
            // start of a step. Anything they do changes the game under the players who already
            // passed, so those passes no longer count as "in succession" (CR 117.4).
            if (SettleBeforePriority() && !State.IsOver && !State.IsWaitingForChoice)
                Emit(new PriorityGranted(next));

            return;
        }

        _priorityRecipient = State.ActivePlayerId;

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
    /// Casts a spell from hand (CR 601).
    /// </summary>
    /// <remarks>
    /// Every choice CR 601.2b asks for is a parameter here: the value of X, the modes, whether an
    /// additional cost was paid, what was tapped or sacrificed or exiled to pay it, and which
    /// alternative cost was used instead of the printed one. They are parameters rather than a
    /// conversation because a cast is a single atomic action — a spell half cast is not a state
    /// the rules have a word for, and the log would have to be able to rebuild it.
    /// </remarks>
    /// <summary>
    /// Why nothing may be cast or activated right now, or null (CR 702.18a) - split second.
    /// </summary>
    /// <remarks>
    /// Mana abilities are exempt, because they do not use the stack and so there is nothing for
    /// the restriction to hold up (CR 605.3b). Everything else waits.
    /// </remarks>
    private string? WhySplitSecondForbids()
    {
        foreach (var id in State.Stack)
        {
            var onStack = State.GetObject(id);

            if (onStack.Ability is null
                && _abilities.SpellOf(onStack.Card)?.HasSplitSecond == true)
            {
                return $"{onStack.Card.Name} has split second (CR 702.18a).";
            }
        }

        return null;
    }

    public ObjectId CastSpell(
        Guid playerId,
        ObjectId cardId,
        IReadOnlyList<Target>? targets = null,
        int variableValue = 0,
        IReadOnlyList<ObjectId>? tapToPay = null,
        IReadOnlyList<int>? modes = null,
        bool kicked = false,
        IReadOnlyList<ObjectId>? costPayment = null,
        bool faceDown = false,
        IReadOnlyList<ObjectId>? delve = null,
        bool buyback = false,
        bool dashed = false,
        bool evoked = false,
        bool overloaded = false,
        bool conspired = false,
        bool bestowed = false,
        int replicated = 0,
        bool offspring = false,
        bool entwined = false,
        IReadOnlyList<ObjectId>? spliced = null,
        (Guid Player, int Amount)? assist = null,
        int squad = 0,
        IReadOnlyList<int>? damageDivision = null,
        bool alternativeCost = false,
        bool blitzed = false,
        int multikicked = 0,
        bool bargained = false,
        bool asAdventure = false,
        int half = 0,
        bool fused = false,
        bool prepared = false,
        bool withFlash = false,
        bool prototyped = false,
        bool mutated = false,
        bool mutateOnTop = true,
        bool awakened = false,
        bool sneaked = false,
        bool teamwork = false,
        IReadOnlyList<int>? kickedWith = null,
        bool cleaved = false,
        bool freeFromHand = false,
        Guid? giftTo = null)
    {
        RequirePriority(playerId);

        var card = State.GetObject(cardId);

        // Noted before the card moves to the stack, because that move is what destroys the
        // answer: the object that was in the graveyard stops existing the moment it leaves
        // (CR 400.7), and a card asking where this was cast from is asking about a fact only
        // the casting knew.
        var castFrom = card.Zone;

        // CR 903.8: a commander may also be cast from the command zone.
        var fromCommandZone = card.Zone == Zone.Command && IsCommanderOf(playerId, card);

        // CR 702.34a: flashback is permission to cast the card from a zone it would not normally
        // be castable from, for a stated cost instead of its mana cost.
        var alternative = _abilities.SpellOf(card.Card)?.CastFrom;
        var fromElsewhere = alternative is not null
            && card.Zone == alternative.Zone

            // Mayhem: the card being in the graveyard is not permission by itself, any more than
            // a foretold card sitting in exile is. It had to get there by being discarded, and on
            // this turn (CR 702.181a).
            && (!alternative.OnlyIfDiscardedThisTurn
                || card.DiscardedOnTurn == State.TurnNumber)

            // "As long as you control a Zombie." The keywords all grant their permission outright
            // and the plain printed sentence usually does not, so the gate is asked here, against
            // the board as it is at the moment of the cast - which is what "as long as" means.
            // The card in the graveyard is the source the question is asked about, so a condition
            // that names the card itself has the right object to look at.
            && (alternative.Available is null
                || alternative.Available(State, _abilities, card, null));

        // CR 601.2b: an offer to cast without paying is permission to cast from wherever the
        // card is, for nothing — and it has to be read before both the timing rule and the zone
        // rule below, because the whole point is that neither would otherwise allow it.
        var onTheHouse = card.MayCastFree;

        // CR 601.2b: a standing offer to cast one described card from hand for nothing. Elected
        // rather than applied, because a player who has one may still want to pay full price and
        // keep it for something better - and because an offer taken by accident is an offer
        // spent, which is the one thing about this permission that cannot be undone.
        //
        // Matched by description against the card, and the *first* match is taken: two offers
        // that both cover this card are two separate permissions, and taking either leaves the
        // other standing, which is what the printed cards say.
        State.HandCastOffer? handOffer = null;

        if (freeFromHand)
        {
            if (card.Zone != Zone.Hand)
            {
                throw new InvalidOperationException(
                    "That offer casts a card from your hand (CR 601.2b).");
            }

            handOffer = State.HandCastOffers
                .FirstOrDefault(offer => offer.PlayerId == playerId && offer.Covers(card.Card))
                ?? throw new InvalidOperationException(
                    $"Nothing is offering to cast {card.Card.Name} from your hand without "
                        + "paying its mana cost (CR 601.2b).");
        }

        // CR 702.143a: a foretold card may be cast from exile for its foretell cost, but only on
        // a turn after the one it was foretold on. The card being in exile is not permission —
        // somebody paid to put this particular card there, and paid a turn early.
        // CR 702.169a: a plotted card is cast from exile for nothing, on a turn after the one
        // it was plotted on. Like foretell, the card sitting in exile is not permission by
        // itself - somebody paid to put this one there.
        var fromPlot = card.Zone == Zone.Exile
            && card.PlottedOnTurn is { } plottedOn
            && State.TurnNumber > plottedOn;

        // CR 702.185a: "its owner may cast this card after the current turn has ended for as
        // long as it remains exiled". For its ordinary cost - warp buys the early cast, not a
        // discount on the later one.
        var fromWarp = card.Zone == Zone.Exile
            && card.WarpedOnTurn is { } warpedOn
            && State.TurnNumber > warpedOn;

        // CR 601.3: something on the battlefield may permit a cast from the top of a library -
        // Future Sight, Melek, Vizier of the Menagerie. Read off the battlefield at this moment
        // rather than written onto the card, because the card on top is not a thing that knows
        // it is on top: the next draw makes it a card in a hand and makes a different card
        // castable, and a permission stamped onto an object would have to be restamped by every
        // draw, mill and shuffle in the game.
        //
        // The position is asked here and the permission there, and the split is deliberate: the
        // library belongs to the engine and what a permanent permits belongs to the permission.
        // Only the top card - a permission to cast the top card is not a permission to reach
        // past it, and "the top card" is index 0 by this engine's one convention.
        var fromLibraryTop = card.Zone == Zone.Library
            && State.GetPlayer(playerId).Library is [var onTop, ..]
            && onTop == cardId
            && CastPermissions.MayPlayFromTopOfLibrary(State, _abilities, playerId, card.Card);

        // CR 601.3e: permission to play a card from exile, paying its cost as normal. Unlike a
        // free cast this grants nothing about the price - only about the zone and the window.
        // The longer window carries no deadline to compare against: it is revoked outright when
        // the owner's next turn ends, so while the flag is still set the permission still holds.
        var fromImpulse = card.Zone == Zone.Exile
            && ((card.MayPlayUntilTurn is { } lastTurn && State.TurnNumber <= lastTurn)
                || card.MayPlayThroughOwnersNextTurn is not null);

        var foretellCost = _abilities.SpellOf(card.Card)?.ForetellCost;
        var fromForetell = foretellCost is not null
            && card.Zone == Zone.Exile
            && card.ForetoldOnTurn is { } toldOn
            && State.TurnNumber > toldOn;

        // CR 715.3d: a card exiled on an adventure may be played by the player who sent it
        // there - and only as its normal self. "It can't be cast as an Adventure this way."
        var fromAdventure = card.Zone == Zone.Exile && card.OnAdventure;

        // CR 702.127a: an aftermath half "may be cast from a graveyard" and "can't be cast from
        // any zone other than a graveyard". Both halves of the rule are needed - with only the
        // first, the card is simply a better card that can be cast twice from hand.
        var aftermath = half > 0
            && _abilities.HalvesOf(card.Card).FirstOrDefault(h => h.Index == half)
                is { HasAftermath: true };

        var fromGraveyard = aftermath && card.Zone == Zone.Graveyard;

        if (aftermath && card.Zone != Zone.Graveyard)
        {
            throw new InvalidOperationException(
                "An aftermath half is cast only from a graveyard (CR 702.127a).");
        }

        if (!aftermath
            && card.Zone == Zone.Graveyard
            && _abilities.HalvesOf(card.Card).Any(h => h.HasAftermath))
        {
            throw new InvalidOperationException(
                "Only the aftermath half may be cast from a graveyard (CR 702.127a).");
        }

        if (fromAdventure && asAdventure)
        {
            throw new InvalidOperationException(
                "A card exiled on an adventure cannot be cast as an Adventure again (CR 715.3d).");
        }

        // CR 722.3c: what is cast is a copy carrying the prepare spell's characteristics, and the
        // permanent itself is only where the permission comes from. This is the one cast where the
        // object cast from stays exactly where it is.
        //
        // The rule keeps that copy in exile from the moment the permanent becomes prepared, and
        // this creates it as it is cast instead. The two agree on everything a player can do -
        // the same spell, cast by the same person, at the same times - and differ only in whether
        // something that reads exile can see the copy sitting there beforehand.
        var preparedSpell = prepared ? _abilities.PreparedSpellOf(card.Card) : null;

        if (prepared
            && (preparedSpell is null
                || card.Zone != Zone.Battlefield
                || card.Permanent is not { IsPrepared: true }))
        {
            throw new InvalidOperationException(
                $"{card.Card.Name} is not a prepared permanent with a spell to cast.");
        }

        if (card.Zone != Zone.Hand && !fromCommandZone && !fromElsewhere && !fromForetell
            && !fromPlot && !fromWarp && !fromImpulse && !fromAdventure && !fromGraveyard
            && !onTheHouse && !prepared && !fromLibraryTop)
            throw new InvalidOperationException("A spell is cast from hand.");

        if (card.Card.CardTypes.HasFlag(CardType.Land))
            throw new InvalidOperationException("A land is played, not cast (CR 305.1).");

        // CR 117.1a: an instant any time you have priority; anything else only at sorcery speed.
        // "You may cast this as though it had flash if you pay {2} more." The surcharge is
        // checked before the timing is, so a client cannot buy instant speed without the card
        // offering it - and cannot buy it for free.
        // CR 718.3: the choice is made as the spell is cast, and CR 718.3a says the alternative
        // cost is the one to check against - so it replaces the printed cost rather than adding
        // to it, which is the opposite of the flash surcharge beside it.
        var prototype = prototyped ? _abilities.SpellOf(card.Card)?.PrototypeCost : null;

        if (prototyped && prototype is null)
        {
            throw new InvalidOperationException(
                $"{card.Card.Name} has no prototype to cast it as.");
        }

        var surcharge = withFlash ? _abilities.SpellOf(card.Card)?.FlashSurcharge : null;

        if (withFlash && surcharge is null)
        {
            throw new InvalidOperationException(
                $"{card.Card.Name} has no offer to cast it as though it had flash.");
        }

        // CR 702.190a: sneak buys one window and one only - "any time you could cast an instant
        // during your declare blockers step". Refused outside it rather than quietly falling back
        // to the printed timing, because a cheap creature castable in a main phase is a different
        // card from one that can only be swapped in mid-combat.
        if (sneaked)
        {
            if (_abilities.SpellOf(card.Card)?.SneakCost is null)
            {
                throw new InvalidOperationException(
                    $"{card.Card.Name} has no sneak (CR 702.190a).");
            }

            if (State.CurrentStep != TurnStep.DeclareBlockers)
            {
                throw new InvalidOperationException(
                    $"{card.Card.Name} may only be sneaked in during a declare blockers step "
                        + "(CR 702.190a).");
            }
        }

        // A prepared spell's timing is its own face's, not the creature's that is holding it.
        //
        // CR 702.8b: the third arm is a permission somebody else is holding up - "You may cast
        // creature spells as though they had flash" - rather than anything this card has. It is
        // asked here, of the board as it is at the moment of the cast, because that is what the
        // permanent's static ability means: the window closes the instant the Vedalken Orrery
        // leaves, and there is no state anywhere recording that it was ever open.
        //
        // It grants timing and nothing else. Every other question this method asks - which zone
        // the card may be cast from, what it costs, whether it may be cast at all - is settled
        // above and below without consulting it, which is the line between a timing permission
        // and a free cast.
        var isInstant = sneaked || withFlash || (prepared
            ? _abilities.PreparedIsInstantOf(card.Card)
            : card.Card.CardTypes.HasFlag(CardType.Instant)
                || Characteristics.Of(State, _abilities, card).Has(KeywordAbility.Flash)
                || CastPermissions.MayCastAsThoughItHadFlash(
                    State, _abilities, playerId, card.Card));
        // An offer made during a resolution is not bound by sorcery timing (CR 702.85a): cascade
        // hands you a sorcery while the spell that cascaded is still on the stack, and the whole
        // mechanic depends on your being allowed to cast it there.
        // An offer taken during a resolution is not bound by sorcery timing either, and for the
        // reason the free cast beside it is not: the Expertise is still on the stack when it
        // hands the window over.
        if (!isInstant && !onTheHouse && handOffer is null && !State.IsSorcerySpeedFor(playerId))
            throw new InvalidOperationException(
                $"{card.Card.Name} can only be cast during your main phase with an empty stack (CR 505.6a).");

        var normal = _abilities.SpellOf(card.Card);

        var adventure = asAdventure
            ? _abilities.AdventureOf(card.Card)
                ?? throw new InvalidOperationException(
                    $"{card.Card.Name} has no Adventure to cast (CR 715.3).")
            : null;

        // CR 702.148a: paying the cleave cost removes the words in square brackets, which the
        // engine holds as a second compiled spell rather than as edited text. From here down a
        // cleaved casting is that reading's - its targets and its effects - and the printed
        // reading is not consulted.
        var cleaveSpell = cleaved
            ? _abilities.CleaveSpellOf(card.Card)
                ?? throw new InvalidOperationException(
                    $"{card.Card.Name} has no cleave (CR 702.148a).")
            : null;

        // CR 702.174a: promising the gift is choosing an opponent, and both happen here or not
        // at all. The choice is validated now because it is a cost being declared (CR 702.174k),
        // not a target - nothing re-checks it later, and hexproof does not refuse a present.
        SpellDefinition? giftSpell = null;

        if (giftTo is { } recipient)
        {
            if (!_abilities.HasGift(card.Card))
            {
                throw new InvalidOperationException(
                    $"{card.Card.Name} has no gift to promise (CR 702.174a).");
            }

            if (recipient == playerId
                || !State.Players.TryGetValue(recipient, out var promisee)
                || promisee.HasLost)
            {
                throw new InvalidOperationException(
                    "A gift is promised to an opponent (CR 702.174a).");
            }

            // On an instant or sorcery the promise selects the promised reading - the delivery,
            // the promised sentences, and their targets (CR 702.174m). A permanent keeps its own
            // spell, and the promise rides the object into the enters trigger CR 702.174b gives
            // it.
            if (card.Card.CardTypes.HasFlag(CardType.Instant)
                || card.Card.CardTypes.HasFlag(CardType.Sorcery))
            {
                giftSpell = _abilities.GiftSpellOf(card.Card)
                    ?? throw new InvalidOperationException(
                        $"{card.Card.Name}'s promised reading is not implemented (CR 702.174).");
            }
        }

        // CR 709.4: a player choosing to cast a split card chooses which half, and the spell on
        // the stack is that half alone. Same shape as the Adventure below it, and the same
        // consequence: from here down this casting is the chosen half's.
        CastAs? chosenHalf = null;
        SpellDefinition? halfSpell = null;

        if (fused)
        {
            if (!_abilities.HasFuse(card.Card))
                throw new InvalidOperationException($"{card.Card.Name} does not have fuse.");

            if (card.Zone != Zone.Hand)
            {
                // CR 702.102a: fuse "applies while the card with fuse is in a player's hand".
                throw new InvalidOperationException(
                    "A split card is fused only from your hand (CR 702.102a).");
            }

            var both = _abilities.HalvesOf(card.Card);

            // CR 702.102b: "a fused split spell has the combined characteristics of its two
            // halves", so the targets of the second half sit after the first's - and the effects
            // that read them have to be moved with them. The second half was compiled on its own
            // and numbers its targets from zero, so left alone both halves point at whatever the
            // player chose first: one creature taking two spells' worth of attention while the
            // other stands untouched.
            var offset = both[0].Spell?.Targets.Count ?? 0;

            halfSpell = new SpellDefinition
            {
                Targets =
                [
                    .. both[0].Spell?.Targets ?? [],
                    .. both[1].Spell?.Targets ?? [],
                ],
                Effects =
                [
                    .. both[0].Spell?.Effects ?? [],
                    .. (both[1].Spell?.Effects ?? []).Select(e => EffectTargets.Shift(e, offset)),
                ],
            };

            chosenHalf = new CastAs(
                halfSpell,
                IsPermanentType(both[0].CardTypes | both[1].CardTypes),
                ExileOnResolve: false);
        }
        else if (half > 0)
        {
            var picked = _abilities.HalvesOf(card.Card).FirstOrDefault(h => h.Index == half)
                ?? throw new InvalidOperationException(
                    $"{card.Card.Name} has no half {half}.");

            halfSpell = picked.Spell;

            // CR 702.127a's third clause: "exile it instead of putting it anywhere else any time
            // it would leave the stack". Without it the card returns to the graveyard it was just
            // cast from and can be cast again every turn.
            chosenHalf = new CastAs(
                picked.Spell,
                IsPermanentType(picked.CardTypes),
                ExileOnResolve: picked.HasAftermath);
        }

        // CR 715.3a: "only the alternative characteristics are evaluated to see if it can be
        // cast", and CR 715.3b: on the stack it has only those. So from here down, this casting
        // is the Adventure's - its timing, its cost, its targets, its effects - and the creature
        // printed beside it is not consulted at all.
        // No fall-back when a half was chosen, and that is the point of writing it this way: a
        // creature half has no spell definition at all, because a creature spell is not a list of
        // effects. Falling through to the card's own spell gave it the *other* half's - so
        // casting the creature side of a split card demanded the instant side's target.
        var definition = preparedSpell ?? adventure ?? cleaveSpell ?? giftSpell
            ?? (fused || half > 0 ? halfSpell : normal);

        // CR 601.3e: a restriction the card prints on top of its ordinary timing. Checked after
        // the type's own timing rather than instead of it, because "cast this only during
        // combat" on a sorcery means both things and not the looser of the two.
        if (definition?.CastOnlyWhen is { } allowed && !allowed(State, playerId))
            throw new InvalidOperationException(
                $"{card.Card.Name} cannot be cast now (CR 601.3e).");

        // CR 601.3: and no rule or effect prohibits the player from casting it. Checked here,
        // before anything is spent or moved, because a cast this refuses must never have begun.
        if (CastLimitRefusing(card.Card, playerId) is { } limiting)
        {
            throw new InvalidOperationException(
                $"{card.Card.Name} cannot be cast: {limiting} limits how many spells "
                + "may be cast this turn (CR 601.3).");
        }

        // CR 601.3 again, with the prohibition coming from a name a player chose rather than
        // from a tally. Beside the limit above and before anything is spent or moved, for the
        // same reason: a cast this refuses must never have begun.
        if (Bans.CastingForbidden(State, _abilities, card.Card, playerId) is { } naming)
        {
            throw new InvalidOperationException(
                $"{card.Card.Name} cannot be cast: {naming.Card.Name} named it (CR 601.3).");
        }

        // CR 702.37b: a card with morph may be cast face down as a 2/2 creature spell for {3}.
        // Nothing else about the card applies while it is being cast that way — not its targets,
        // not its modes, not its cost — because the spell on the stack is not that card's spell.
        if (faceDown)
        {
            if (definition?.MorphCost is null)
                throw new InvalidOperationException($"{card.Card.Name} has no morph (CR 702.37a).");

            if (!State.IsSorcerySpeedFor(playerId))
            {
                throw new InvalidOperationException(
                    "A face-down creature spell is cast at sorcery speed (CR 702.37b).");
            }

            // CR 601.2f applies to this {3} like any other cost: a face-down spell is a spell
            // being cast, and casting one under a Thalia costs {4}. This was the one cast in the
            // engine that went straight from a literal to the pool, so every cost modifier on
            // the board was inert against it and no restricted mana could pay for it at all.
            //
            // Both questions are put to the face-down card rather than the one underneath. That
            // is the reading that can be wrong in a player's favour: the card being hidden is
            // exactly the case where "Dragon spells cost {1} less to cast" must not find a
            // Dragon, while "spend this mana only to cast creature spells" must still find a
            // creature spell (CR 702.37a).
            var faceDownCost = CostModification.Apply(
                ManaCostSpec.Parse("{3}"),
                CostModifiersFor(
                    CostModifierKind.Spells,
                    Characteristics.FaceDownSpell,
                    playerId,
                    castFrom,
                    null,
                    false));

            PayMana(
                playerId,
                faceDownCost,
                spend: ManaSpend.Casting(
                    Characteristics.FaceDownSpell, castFrom, IsCommanderOf(playerId, card)));

            var hidden = Move(cardId, Zone.Stack, MoveCause.Cast, playerId);
            _castFaceDown.Add(hidden);

            Emit(new SpellCastEvent(playerId, hidden, "a face-down creature"));
            _priorityRecipient = playerId;
            SettleBeforePriority();
            Emit(new PriorityGranted(playerId));

            return hidden;
        }

        var chosen = (targets ?? []).ToImmutableList();

        // CR 601.2b then 601.2c: modes are chosen first, and only then the targets — because
        // which targets the spell even has depends on which modes were taken.
        var chosenModes = RequireLegalModes(
            definition,
            modes,
            card.Card.Name,
            entwined,
            playerId,
            kicked || kickedWith is { Count: > 0 },
            teamwork,
            variableValue);

        // The spell's own targets come first and the chosen modes' after, so the spell's own
        // effects keep the indices the compiler gave them and each mode gets a slice at a known
        // offset. A modal card can carry text outside its modes — a kicker rider, most often —
        // and that text was compiled without any knowledge of which modes would be taken.
        var specs = definition?.Targets ?? [];

        // Each mode's specs were compiled against that mode's own list, so a peer index in one
        // is numbered from zero however many targets sit in front of it once the modes are
        // concatenated. The effects have always been shifted for this; the specs never carried
        // an index to shift until now.
        foreach (var i in chosenModes)
        {
            specs = specs.AddRange(
                TargetSpec.ShiftPeers(definition!.Modes[i].Targets, specs.Count));
        }

        // CR 702.47a: a spliced card's text is added to the spell, so its targets are chosen as
        // part of casting it and sit after the modes' - each addition keeping the indices its own
        // effects were compiled with, which is the same slicing modes needed.
        var splicedCards = RequireSpliceable(playerId, card, spliced);
        foreach (var onto in splicedCards)
        {
            specs = specs.AddRange(
                TargetSpec.ShiftPeers(_abilities.SpellOf(onto)?.Targets ?? [], specs.Count));
        }

        // CR 702.103a: a bestowed spell is an Aura spell with enchant creature, so it targets
        // where the same card cast as a creature does not. The target is added rather than
        // swapped: the card's own targets, if it had any, are still its own.
        if (bestowed)
        {
            if (_abilities.SpellOf(card.Card)?.BestowTarget is not { } enchanting)
            {
                throw new InvalidOperationException(
                    $"{card.Card.Name} has no bestow (CR 702.103a).");
            }

            specs = specs.Add(enchanting);
        }

        // CR 702.140a: paying the mutate cost gives a creature spell a target it does not
        // otherwise have — "a non-Human creature with the same owner as this spell". Added the
        // way bestow's is, after whatever the card itself targets, so the card's own effects keep
        // the indices the compiler numbered them with.
        if (mutated)
        {
            if (_abilities.SpellOf(card.Card)?.MutateTarget is not { } merging)
            {
                throw new InvalidOperationException(
                    $"{card.Card.Name} has no mutate (CR 702.140a).");
            }

            specs = specs.Add(merging);
        }

        // CR 702.96a: overload replaces every "target" the spell printed, so the check below has
        // nothing to check. They are not merely ignored - an overloaded spell has no targets at
        // all, so nothing about it can be made illegal by hexproof or lost to fizzling.
        if (overloaded)
        {
            if (_abilities.SpellOf(card.Card)?.OverloadCost is null)
            {
                throw new InvalidOperationException(
                    $"{card.Card.Name} has no overload (CR 702.96a).");
            }

            chosen = [];
            specs = [];
        }

        // CR 702.113b: the awaken half's target is chosen only when the awaken cost is being
        // paid - otherwise "the spell is cast as if it didn't have that target". Added last, so
        // that the land is the final entry however many targets the card itself printed, which
        // is where the awaken effects look for it on resolution.
        if (awakened)
        {
            if (_abilities.SpellOf(card.Card)?.AwakenTarget is not { } land)
            {
                throw new InvalidOperationException(
                    $"{card.Card.Name} has no awaken (CR 702.113a).");
            }

            specs = specs.Add(land);
        }

        if (WhySplitSecondForbids() is { } held)
            throw new InvalidOperationException(held);

        // CR 601.2c: "if the spell has a variable number of targets, the player announces how
        // many targets they will choose before they announce those targets", and once announced
        // that number does not change. The announcement is the length of the list handed in, so
        // "any number of target creatures" becomes exactly that many specs here — before the
        // check below, so a block of three is three targets that each have to be legal, and
        // before the cost below, because CR 601.2f charges strive against the number announced
        // at 601.2c.
        specs = VariableTargets.ExpandSpecs(specs, chosen.Count);

        // CR 601.2c: targets are chosen as the spell is cast, and they have to be legal now.
        RequireLegalTargets(specs, chosen, playerId, card.Card.Name, card, variableValue);

        // CR 601.2d comes next and before the cost at 601.2h, so a division that does not add up
        // is refused before any mana is spent. It was checked at the very end, beside the event
        // that records it, which meant an illegal announcement got as far as paying for the
        // spell. Checked here and carried down, so the number the event writes is the number
        // that was checked.
        var announcedDivision = RequireDivision(
            definition, chosen, damageDivision, variableValue);

        // CR 601.2h: the cost is paid last, and a spell whose cost cannot be paid is not cast at
        // all — the game rewinds rather than leaving it half-cast (CR 601.2i, 733).
        var cost = prototype is { } smaller
            ? smaller
            : prepared
            ? ManaCostSpec.Parse(_abilities.PreparedCostOf(card.Card) ?? card.Card.ManaCostRaw)
            : fromPlot
            ? ManaCostSpec.Free

            // The offer pays nothing at all, and nothing about the card is consulted: it is not
            // an alternative cost printed on the spell, it is a permission somebody else's spell
            // handed over (CR 601.2b).
            : handOffer is not null
            ? ManaCostSpec.Free
            : onTheHouse
            ? ManaCostSpec.Parse(card.OfferedCost)
            : fromForetell
            ? foretellCost!
            : fromElsewhere
            ? alternative!.Cost
            : asAdventure
            ? ManaCostSpec.Parse(_abilities.AdventureCostOf(card.Card) ?? card.Card.ManaCostRaw)
            : fused

            // CR 702.102c: "the total cost of a fused split spell includes the mana cost of each
            // half."
            ? ManaCostSpec.Parse(
                string.Concat(_abilities.HalvesOf(card.Card).Select(h => h.ManaCostRaw)))
            : half > 0
            ? ManaCostSpec.Parse(
                _abilities.HalvesOf(card.Card).First(h => h.Index == half).ManaCostRaw)

            // CR 702.148a: "you may cast this spell by paying [cost] rather than paying its
            // mana cost" - an alternative cost, so it replaces the printed one outright.
            : cleaved
            ? ManaCostSpec.Parse(
                _abilities.CleaveCostOf(card.Card)
                    ?? throw new InvalidOperationException(
                        $"{card.Card.Name} has no cleave (CR 702.148a)."))
            : definition?.AlternateCost ?? ManaCostSpec.Parse(card.Card.ManaCostRaw);

        // CR 118.8, 601.2h: a zone permission whose price includes life is checked here, with
        // the mana and before any of it is paid — "Flashback—{1}{U}, Pay 3 life." A player may
        // pay life only down to zero, so a caster who cannot afford it is refused having spent
        // nothing.
        if (fromElsewhere
            && alternative!.LifeCost > 0
            && alternative.LifeCost > State.GetPlayer(playerId).Life)
        {
            throw new InvalidOperationException(
                $"You cannot pay {alternative.LifeCost} life for {card.Card.Name} (CR 118.8).");
        }

        // The same check for an additional cost that asks for life — "Kicker—Pay 3 life",
        // "Buyback—Pay 4 life". Both are optional, so this is only owed when the caster said they
        // would pay it, and it is checked here with the mana rather than charged optimistically
        // later: a caster who cannot afford it is refused with nothing spent (CR 601.2h).
        var keywordLife =
            (kicked ? definition?.KickerLifeCost ?? 0 : 0)
            + (buyback ? definition?.BuybackLifeCost ?? 0 : 0);

        if (keywordLife > State.GetPlayer(playerId).Life)
        {
            throw new InvalidOperationException(
                $"You cannot pay {keywordLife} life for {card.Card.Name} (CR 118.8).");
        }

        // CR 903.8: {2} more for each previous cast from the command zone — the commander tax.
        // It counts casts from that zone specifically, so a commander cast from hand after being
        // bounced there is not taxed and does not add to the count.
        if (fromCommandZone)
        {
            var taxed = State.GetPlayer(playerId).CommanderCastsFromCommandZone * 2;
            if (taxed > 0)
                cost = cost with { Symbols = cost.Symbols.Add(ManaSymbol.Generic0(taxed)) };
        }

        // Added to the printed cost rather than replacing it: this buys timing, not a discount.
        if (surcharge is { } hasteTax)
            cost = cost with { Symbols = cost.Symbols.AddRange(hasteTax.Symbols) };

        // CR 601.2f: a cost reduction is worked out now, against the board as it is, and it can
        // only take generic mana off — never a coloured requirement.
        if (definition?.CostReduction is { } reduce)
        {
            var less = reduce(State, playerId, chosen);
            if (less > 0)
                cost = cost with { Symbols = ReduceGeneric(cost.Symbols, less) };
        }

        // "This spell costs {2} less to cast if it's bargained" — the same CR 601.2f reduction
        // with its condition on the cast rather than the board: the intention to pay was
        // declared a step earlier (CR 601.2b, 702.166b), so it is known here, where the total
        // cost is determined.
        if (bargained && definition is { BargainDiscount: > 0 })
            cost = cost with { Symbols = ReduceGeneric(cost.Symbols, definition.BargainDiscount) };

        // CR 702.66a: delve exiles cards from the caster's graveyard, each paying for one
        // generic mana. Like convoke it is a way of paying rather than a discount, so the cards
        // go on the same footing as the mana and are checked before any of it is spent.
        var delved = MatchDelve(definition, playerId, delve);
        if (delved.Count > 0)
            cost = cost with { Symbols = ReduceGeneric(cost.Symbols, delved.Count) };

        // CR 702.51a: convoke and improvise do not change the cost — they pay part of it with
        // something other than mana. So the symbols they cover are struck off before the pool is
        // asked for the rest, and the permanents are tapped as part of paying (CR 601.2g).
        var tapped = MatchTapToPay(definition?.TapToPayCost, playerId, cost, tapToPay);
        if (tapped.Count > 0)
            cost = cost with { Symbols = Reduce(cost.Symbols, tapped) };

        // CR 702.33a: kicker is an additional cost, so it is added to what is paid rather than
        // replacing it. A spell with no kicker cannot be kicked, and saying so is better than
        // quietly charging nothing extra.
        if (kicked)
        {
            if (definition?.KickerCost is not { } kickerCost)
                throw new InvalidOperationException($"{card.Card.Name} has no kicker (CR 702.33a).");

            cost = cost with { Symbols = cost.Symbols.AddRange(kickerCost.Symbols) };
        }

        // CR 702.33c: multikicker is the same additional cost paid any number of times, so the
        // symbols go on once per payment. Nothing caps it but the mana available - a player who
        // can pay eight times may, and the cards that read the count are written expecting that.
        if (multikicked > 0)
        {
            if (definition?.MultikickerCost is not { } multikickerCost)
            {
                throw new InvalidOperationException(
                    $"{card.Card.Name} has no multikicker (CR 702.33c).");
            }

            foreach (var _ in Enumerable.Range(0, multikicked))
                cost = cost with { Symbols = cost.Symbols.AddRange(multikickerCost.Symbols) };
        }

        // CR 702.122a: strive — "this spell costs [cost] more to cast for each target beyond the
        // first". An additional cost like multikicker, charged at CR 601.2f, but with nothing to
        // ask: the number of targets was announced at CR 601.2c and is the length of the list
        // above, so the price is read off the targets rather than off a second choice. A caster
        // who cannot pay for the third creature is refused the whole cast (CR 601.2h) and has
        // spent nothing, which is what makes striving a real decision.
        if (definition?.StriveCost is { } strivePer && chosen.Count > 1)
        {
            foreach (var _ in Enumerable.Range(0, chosen.Count - 1))
                cost = cost with { Symbols = cost.Symbols.AddRange(strivePer.Symbols) };
        }

        // CR 702.33b: "Kicker [A] and/or [B]" is two kicker abilities, so each may be paid at
        // most once and each payment adds its own price to the total (CR 601.2f). Refused by
        // name when the card offers no such choice, exactly as the single form is.
        if (kickedWith is { Count: > 0 })
        {
            if (definition is not { KickerCosts.Count: > 0 })
            {
                throw new InvalidOperationException(
                    $"{card.Card.Name} has no kicker costs to choose between (CR 702.33b).");
            }

            if (kickedWith.Distinct().Count() != kickedWith.Count)
            {
                throw new InvalidOperationException(
                    $"{card.Card.Name}'s kicker costs may each be paid once (CR 702.33b).");
            }

            foreach (var index in kickedWith)
            {
                if (index < 0 || index >= definition.KickerCosts.Count)
                {
                    throw new InvalidOperationException(
                        $"{card.Card.Name} has no kicker cost #{index} (CR 702.33b).");
                }

                cost = cost with
                {
                    Symbols = cost.Symbols.AddRange(definition.KickerCosts[index].Cost.Symbols),
                };
            }
        }

        if (overloaded && definition?.OverloadCost is { } overloadCost)
            cost = overloadCost;

        // CR 702.190a: sneak is an alternative cost. Its mana half replaces the mana cost; the
        // attacker it also asks for joins the chosen costs below.
        if (sneaked)
        {
            if (definition?.SneakCost is not { } sneakCost)
            {
                throw new InvalidOperationException(
                    $"{card.Card.Name} has no sneak (CR 702.190a).");
            }

            cost = sneakCost;
        }

        // CR 702.113a: awaken is an alternative cost, so it replaces the mana cost outright.
        if (awakened)
        {
            if (definition?.AwakenCost is not { } awakenCost)
            {
                throw new InvalidOperationException(
                    $"{card.Card.Name} has no awaken (CR 702.113a).");
            }

            cost = awakenCost;
        }

        if (bestowed && definition?.BestowCost is { } bestowCost)
            cost = bestowCost;

        // CR 702.140a: "you may pay [cost] rather than pay this spell's mana cost", which is an
        // alternative cost and so replaces the printed one outright - applied exactly where
        // bestow's is, and before the additional costs below so those are added to what is
        // actually being paid.
        if (mutated)
        {
            if (definition?.MutateCost is not { } mutateCost)
            {
                throw new InvalidOperationException(
                    $"{card.Card.Name} has no mutate (CR 702.140a).");
            }

            cost = mutateCost;
        }

        // CR 702.74a: evoke is an alternative cost like dash, and the two are never both paid -
        // a card printed with both would be cast one way or the other.
        if (evoked)
        {
            if (definition?.EvokeCost is not { } evokeCost)
            {
                throw new InvalidOperationException(
                    $"{card.Card.Name} has no evoke (CR 702.74a).");
            }

            cost = evokeCost;
        }

        // CR 702.109a: dash is an alternative cost, so it replaces the mana cost outright. It
        // is applied before buyback so that an additional cost added afterwards is added to the
        // cost actually being paid.
        if (dashed)
        {
            if (definition?.DashCost is not { } dashCost)
            {
                throw new InvalidOperationException(
                    $"{card.Card.Name} has no dash (CR 702.109a).");
            }

            cost = dashCost;
        }

        // CR 702.152a: blitz is an alternative cost, applied exactly where dash is. The two are
        // never both paid - a card printed with both would be cast one way or the other.
        if (blitzed)
        {
            if (definition?.BlitzCost is not { } blitzCost)
            {
                throw new InvalidOperationException(
                    $"{card.Card.Name} has no blitz (CR 702.152a).");
            }

            cost = blitzCost;
        }

        // CR 601.2f: surge and spectacle offer a cost the card only sometimes has. Both halves
        // are refused separately and for different reasons, because they are different mistakes:
        // reaching for an offer the card does not make is a client sending the wrong flag, and
        // reaching for one whose condition is false is a player who has misread the board. A
        // condition checked anywhere but here would be checked against a board the payment had
        // already changed.
        ConditionalCost? costTaken = null;

        if (alternativeCost)
        {
            if (definition?.ConditionalAlternativeCost is not { } offered)
            {
                throw new InvalidOperationException(
                    $"{card.Card.Name} has no alternative cost (CR 601.2f).");
            }

            if (!offered.IsAvailable(State, playerId))
            {
                throw new InvalidOperationException(
                    $"{card.Card.Name} cannot be cast for its {offered.Keyword} cost "
                        + $"({offered.Rule}).");
            }

            // CR 118.8, 601.2h: life is checked with the rest of the cost and before any of it is
            // paid, so a caster who cannot afford it is refused having spent nothing. A player
            // may pay life only down to zero.
            if (offered.LifeCost > State.GetPlayer(playerId).Life)
            {
                throw new InvalidOperationException(
                    $"You cannot pay {offered.LifeCost} life for {card.Card.Name} (CR 118.8).");
            }

            cost = offered.Cost;
            costTaken = offered;
        }

        // CR 702.171a: offspring is an ordinary optional additional cost - what it buys happens
        // later, when the creature arrives.
        if (offspring)
        {
            if (definition?.OffspringCost is not { } offspringCost)
            {
                throw new InvalidOperationException(
                    $"{card.Card.Name} has no offspring (CR 702.171a).");
            }

            cost = cost with { Symbols = cost.Symbols.AddRange(offspringCost.Symbols) };
        }

        // CR 702.55a: replicate is an additional cost paid as many times as the caster likes,
        // and each payment buys one copy. Added to the cost that many times over.
        if (replicated > 0)
        {
            if (definition?.ReplicateCost is not { } replicateCost)
            {
                throw new InvalidOperationException(
                    $"{card.Card.Name} has no replicate (CR 702.55a).");
            }

            for (var i = 0; i < replicated; i++)
                cost = cost with { Symbols = cost.Symbols.AddRange(replicateCost.Symbols) };
        }

        // CR 702.42a, 702.101a: what extra modes cost. Entwine buys all of them for one flat
        // price; escalate charges again for every mode past the first, so the two are added the
        // same way and counted differently. Read from the modes already chosen rather than from
        // a second parameter, because the choice was made above and the cost follows from it.
        if (entwined && definition?.EntwineCost is { } entwineCost)
            cost = cost with { Symbols = cost.Symbols.AddRange(entwineCost.Symbols) };

        if (!entwined && definition?.EscalateCost is { } escalateCost)
        {
            for (var i = definition.ModesToChoose; i < chosenModes.Count; i++)
                cost = cost with { Symbols = cost.Symbols.AddRange(escalateCost.Symbols) };
        }

        // CR 700.2h, CR 702.172a: a spree card's modes each name their own additional cost, and
        // every mode taken charges its own. Counted from the choice rather than from a second
        // parameter, exactly as escalate is - the modes were picked above and what is owed
        // follows from them.
        foreach (var index in chosenModes)
        {
            if (definition?.Modes[index].Cost is { } perMode)
                cost = cost with { Symbols = cost.Symbols.AddRange(perMode.Symbols) };
        }

        // CR 702.47a: each spliced card charges its splice cost on top, and the card itself
        // stays in hand - it was revealed, not cast.
        foreach (var onto in splicedCards)
        {
            if (_abilities.SpellOf(onto)?.SpliceCost is { } spliceCost)
                cost = cost with { Symbols = cost.Symbols.AddRange(spliceCost.Symbols) };
        }

        // CR 702.157a: squad is an additional cost paid as many times as the caster likes, and
        // each payment buys one token copy of the permanent this spell becomes.
        if (squad > 0)
        {
            if (definition?.SquadCost is not { } squadCost)
                throw new InvalidOperationException($"{card.Card.Name} has no squad (CR 702.157a).");

            for (var i = 0; i < squad; i++)
                cost = cost with { Symbols = cost.Symbols.AddRange(squadCost.Symbols) };
        }

        // CR 702.27a: buyback is an additional cost, so it is added rather than replacing, and a
        // spell without one cannot be bought back — saying so beats charging nothing extra.
        if (buyback)
        {
            if (definition?.BuybackCost is not { } buybackCost)
            {
                throw new InvalidOperationException(
                    $"{card.Card.Name} has no buyback (CR 702.27a).");
            }

            cost = cost with { Symbols = cost.Symbols.AddRange(buybackCost.Symbols) };
        }

        // CR 601.2b: an additional cost printed as a choice is announced as the spell is cast,
        // and the announcement is what the caster offered to pay with - see ChooseCostOption.
        // Resolved here rather than beside the other chosen costs below, because the mana arm of
        // a choice has to be in the total before CR 601.2f works the modifiers out: added
        // afterwards it would be a cost no reduction could reach.
        var pricesTaken = new List<CostOption>();

        if (definition?.CostChoices is { IsEmpty: false } choices)
        {
            // Where in the offered payment this choice's share begins. Everything appended to
            // `owed` ahead of the choice has already spoken for that many cards, and the matcher
            // takes them from the front in that order - so the two counts have to be derived
            // from the same list or one cost would be handed another cost's payment.
            var consumed = (definition.ChosenCosts).Sum(CardsAskedFor);

            if (fromElsewhere && alternative!.Extra is { } ahead)
                consumed += ahead.Sum(CardsAskedFor);

            foreach (var choice in choices)
            {
                var option = ChooseCostOption(choice, playerId, cardId, costPayment, consumed);
                pricesTaken.Add(option);
                consumed += option.Chosen.Sum(CardsAskedFor);
                cost = cost with { Symbols = cost.Symbols.AddRange(option.Mana.Symbols) };
            }
        }

        // CR 601.2f: what the board does to this spell's cost, worked out once here and never
        // recomputed - a modifier changes what the spell costs to cast, not what it is. It moves
        // the generic part only, because neither half can pay or demand a coloured pip; letting a
        // reduction would make a Dragon castable off two Islands.
        //
        // The whole battlefield is walked, not the caster's half of it: "spells your opponents
        // cast cost {1} more to cast" is printed by a permanent the caster does not control, and
        // reading only their own permanents is exactly why that cell of the grid was unread.
        cost = CostModification.Apply(
            cost,
            CostModifiersFor(
                CostModifierKind.Spells, card.Card, playerId, castFrom, null, false, chosen));

        // CR 601.2h: the whole cost is worked out and checked before any of it is paid, so a
        // spell whose additional cost cannot be met is refused with nothing spent.
        // CR 702.82a: a cost that belongs to the way the spell is being cast is added to the
        // ones printed on it, not swapped for them.
        var owed = definition?.ChosenCosts ?? [];
        if (fromElsewhere && alternative!.Extra is { } extra)
            owed = owed.AddRange(extra);

        // The cards half of each choice taken above. Appended after the card's own chosen costs
        // and in printed order, which is the order ChooseCostOption consumed the offered payment
        // in - the two have to agree or the matcher would hand one cost another cost's cards.
        foreach (var option in pricesTaken)
            owed = owed.AddRange(option.Chosen);

        // CR 601.2b, 702.119c: what an alternative cost asks for besides mana is chosen as the
        // offer is taken and paid as the total cost is paid, so it joins the list the cast
        // already pays from - and where it starts is remembered, because emerge's discount is
        // the mana value of whatever went into the first of those slots.
        var sacrificedFor = -1;
        if (costTaken is { Payments.IsEmpty: false } giving)
        {
            sacrificedFor = owed.Count;
            owed = owed.AddRange(giving.Payments);
        }

        // CR 118.3, 702.33a: a kicker's price need not be mana - "Kicker—Sacrifice a land" - and
        // what it asks for besides mana is paid on the same footing as everything else the cast
        // pays, so it joins this list rather than needing a channel of its own. Only when the
        // caster said they would kick: nothing is charged for declining.
        if (kicked && definition is { KickerPayments.IsEmpty: false } kickerPrice)
            owed = owed.AddRange(kickerPrice.KickerPayments);

        // CR 118.3, 702.27a: the same for buyback, which is the same kind of additional cost.
        if (buyback && definition is { BuybackPayments.IsEmpty: false } buybackPrice)
            owed = owed.AddRange(buybackPrice.BuybackPayments);

        // CR 702.194a: teamwork's cost is optional, so it joins the list only when the caster
        // has said they will pay it. Nothing is charged for saying no.
        if (teamwork)
        {
            owed = owed.Add(
                definition?.TeamworkCost
                ?? throw new InvalidOperationException(
                    $"{card.Card.Name} has no teamwork cost to pay (CR 702.194a)."));
        }

        // CR 702.190a: the other half of the sneak price. An additional entry rather than a
        // replacement, so a card that also charges something of its own still charges it.
        if (sneaked)
        {
            owed = owed.Add(
                definition?.SneakReturn
                ?? throw new InvalidOperationException(
                    $"{card.Card.Name} has no sneak (CR 702.190a)."));
        }

        // CR 702.166a: bargain's cost is optional, so it joins the list only when the caster has
        // said they will pay it - and a caster who says so on a card with no bargain is refused
        // rather than quietly cast for free.
        if (bargained)
        {
            owed = owed.Add(
                definition?.BargainCost
                ?? throw new InvalidOperationException(
                    $"{card.Card.Name} has no bargain cost to pay (CR 702.166a)."));
        }

        // CR 702.78a, 702.153a: a copying cost is charged only when it is taken, so it is
        // matched separately from the costs the card charges every time.
        var conspiracy = conspired
            ? MatchChosenCosts(
                definition?.CopyingCost is { } paying
                    ? [paying]
                    : throw new InvalidOperationException(
                        $"{card.Card.Name} has nothing to pay for a copy (CR 702.78a)."),
                playerId,
                cardId,
                costPayment)
            : [];

        var chosenCosts = MatchChosenCosts(owed, playerId, cardId, costPayment);

        // CR 702.119a: "its total cost is reduced by an amount of generic mana equal to the
        // sacrificed creature's mana value". Worked out here rather than at the offer, because
        // which creature is going is only known once the payment has been matched — and read
        // before anything is paid, since the creature is still on the battlefield until it is.
        if (costTaken is { ReducedByManaValueSacrificed: true }
            && sacrificedFor >= 0
            && sacrificedFor < chosenCosts.Count)
        {
            var worth = chosenCosts[sacrificedFor].Cards.Sum(id => State.GetObject(id).Card.Cmc);
            if (worth > 0)
                cost = cost with { Symbols = ReduceGeneric(cost.Symbols, worth) };
        }

        // CR 702.132a: another player may pay any amount of the generic mana in the total cost.
        // Their half comes out of their own pool first, and what is left is what the caster owes.
        if (assist is { } helping)
        {
            var helped = RequireAssist(definition, card.Card.Name, playerId, helping, cost);
            if (helped > 0)
            {
                // The helper pays out of their own pool, so a restriction on their mana is asked
                // about their side of it: "spend this mana only to cast your commander" names
                // whose commander it is, and a spell somebody else is casting is not theirs.
                PayMana(
                    helping.Player,
                    ManaCostSpec.Parse($"{{{helped}}}"),
                    spend: ManaSpend.Casting(
                        card.Card, castFrom, IsCommanderOf(helping.Player, card)));
                cost = cost.WithoutGeneric(helped);
            }
        }

        // What is being paid for, so mana that may only be spent on some things can tell
        // whether this is one of them (CR 106.6). The types are the card's, computed rather than
        // printed for the same reason everything else is: a land animated into a creature spell
        // is not a thing, but a card whose type line an effect has changed is.
        var manaSpent = PayMana(
            playerId,
            cost,
            variableValue,
            ManaSpend.Casting(card.Card, castFrom, IsCommanderOf(playerId, card)));

        foreach (var helper in tapped)
            Emit(new PermanentTapped(helper.Id));

        foreach (var spent in delved)
            Move(spent, Zone.Exile, MoveCause.Exile, playerId);

        // What a returned attacker was attacking, read before it leaves: a sneaked permanent
        // arrives against the same defender, and by the time the spell resolves the creature
        // that knew it is in its owner's hand (CR 702.190b).
        AttackTarget? joining = null;

        // What the additional cost is about to take, for a spell whose effect names it -
        // "sacrifice a creature ... where X is the sacrificed creature's power" (CR 608.2k).
        // Read here rather than after the loop for the reason the sneak attacker above is: the
        // object is about to stop existing, and the numbers with it.
        var costTakes = WhatTheCostTakes([.. chosenCosts.SelectMany(paid => paid.Cards)]);

        // Paid before the spell moves to the stack (CR 601.2f), which matters when the cost is
        // sacrificing a creature: the sacrifice happens whether or not the spell ever resolves,
        // and anything that triggers on it triggers now.
        foreach (var (chosenCost, cards) in chosenCosts)
        {
            foreach (var paid in cards)
            {
                if (chosenCost.Kind is ChosenCostKind.TapPermanents)
                {
                    Emit(new PermanentTapped(paid));
                    continue;
                }

                // A cost paid out of the graveyard or the hand goes to exile, not back to either
                // (CR 701.13a). The hand is where the pitch spells take theirs from, and a card
                // exiled that way is gone in a sense a discarded one is not.
                if (chosenCost.Kind
                    is ChosenCostKind.ExileFromGraveyard or ChosenCostKind.ExileFromHand)
                {
                    Move(paid, Zone.Exile, MoveCause.Exile, playerId);
                    continue;
                }

                // CR 108.3: home to its owner, not to whoever is paying. Until an alternative
                // cost could ask for it, no cast had ever charged this kind - it reached the
                // engine as ninjutsu's, which is an activated ability - and a spell paying with
                // one would have put the Island in the graveyard. Sneak notes the seat the
                // returned attacker was attacking on the way out (CR 702.190a), because the
                // arriving creature joins combat against the same player or planeswalker.
                if (chosenCost.Kind is ChosenCostKind.ReturnToHand)
                {
                    if (State.Combat.Attackers.TryGetValue(paid, out var wasAttacking))
                        joining ??= wasAttacking;

                    Move(paid, Zone.Hand, MoveCause.Return, State.GetObject(paid).OwnerId);
                    continue;
                }

                Move(
                    paid,
                    Zone.Graveyard,
                    chosenCost.Kind is ChosenCostKind.SacrificePermanents
                        ? MoveCause.Sacrifice
                        : MoveCause.Discard,
                    playerId);
            }
        }

        // CR 118.8: paid with the rest of the cost, after the mana and beside the cards. Life is
        // not damage - nothing prevents it and no lifelink sees it - so it is simply a change.
        if (costTaken is { LifeCost: > 0 } bleeding)
        {
            var before = State.GetPlayer(playerId).Life;
            Emit(new LifeChanged(playerId, -bleeding.LifeCost, before - bleeding.LifeCost));
        }

        // CR 118.8 again, for the life arm of a cost choice - "discard a card or pay 3 life".
        // Charged here beside the alternative cost's life for the same reason it is: this is the
        // moment the rest of the cost is paid, and a payment made anywhere else would be one the
        // legality check above never saw.
        foreach (var option in pricesTaken)
        {
            if (option.Life <= 0)
                continue;

            var before = State.GetPlayer(playerId).Life;
            Emit(new LifeChanged(playerId, -option.Life, before - option.Life));
        }

        // The same charge for a permission that names life as part of its price, and it is a
        // separate arm rather than a shared one because the two are never both taken: an
        // alternative cost replaces the printed cost from hand, and this is the cost of casting
        // from somewhere else.
        if (fromElsewhere && alternative is { LifeCost: > 0 } fromZone)
        {
            var before = State.GetPlayer(playerId).Life;
            Emit(new LifeChanged(playerId, -fromZone.LifeCost, before - fromZone.LifeCost));
        }

        // An additional cost paid in life — kicker's or buyback's, or both, since a card could
        // print each and a caster could take both. Counted once above so the affordability check
        // and the charge cannot disagree.
        if (keywordLife > 0)
        {
            var before = State.GetPlayer(playerId).Life;
            Emit(new LifeChanged(playerId, -keywordLife, before - keywordLife));
        }

        // The permanent never leaves the battlefield and so never becomes a new object. Losing
        // the designation happens here rather than on resolution because CR 722.3c says when: "at
        // the time the spell becomes cast". A copy that is countered still cost the preparation.
        ObjectId stackId;

        if (prepared)
        {
            stackId = ObjectId.New();
            Emit(new SpellCopied(stackId, card.Card, playerId, chosen));
            Emit(new Unprepared(cardId));
            _castAs[stackId] = new CastAs(
                preparedSpell!, IsPermanent: false, ExileOnResolve: false);
        }
        else
        {
            stackId = Move(cardId, Zone.Stack, MoveCause.Cast, playerId);
        }

        // Recorded against the spell here for the same reason the mana is: this is the first
        // moment there is an object on the stack to record it against, and the numbers it carries
        // were read before the payment that made them unreadable.
        if (costTakes is { } bought)
            Emit(new CostPaidRecorded(stackId, bought));

        // Which spell this object actually is (CR 715.3b). Recorded here for the same reason the
        // mana is: this is the first moment there is an object to record it against.
        if (adventure is not null)
            _castAs[stackId] = new CastAs(
                adventure, IsPermanent: false, ExileOnResolve: true, OnAdventure: true);
        else if (chosenHalf is { } picked)
            _castAs[stackId] = picked;
        else if (cleaveSpell is not null)
            _castAs[stackId] = new CastAs(
                cleaveSpell, IsPermanentCard(card.Card), ExileOnResolve: false);
        else if (giftSpell is not null)
            _castAs[stackId] = new CastAs(
                giftSpell, IsPermanentCard(card.Card), ExileOnResolve: false);

        if (half > 0 || _abilities.HalvesOf(card.Card).Count > 1)
            _halfCast[stackId] = half;

        // Recorded on the spell as it goes on the stack, because that is the first moment there
        // is an object to record it on and the pool has already moved on.
        if (manaSpent != ManaPool.Empty)
            Emit(new ManaColorsSpent(stackId, manaSpent));

        if (!chosenModes.IsEmpty)
            Emit(new ModesChosen(stackId, chosenModes));

        if (splicedCards.Count > 0)
            Emit(new CardsSpliced(stackId, splicedCards));

        if (squad > 0)
            Emit(new SpellSquadded(stackId, squad));

        if (kicked)
            Emit(new SpellKicked(stackId));

        if (bargained)
            Emit(new SpellBargained(stackId));

        // The fact that chooses which reading resolves, in the log rather than only in the
        // in-process table: a resumed game rebuilds nothing but the events, and the reading
        // that was paid for has to survive the trip (CR 702.148a).
        if (cleaved)
            Emit(new SpellCleaved(stackId));

        // The promise and the chosen opponent are one event, because CR 702.174a makes them
        // one act. Recorded here because this is the only moment that knows: the delivery
        // resolves later, a permanent's trigger later still.
        if (giftTo is { } promisedTo)
            Emit(new GiftPromised(stackId, promisedTo));

        // Both, for a multikicked spell: the flag every "if this was kicked" card reads, and the
        // number the few that say "for each time it was kicked" need.
        if (multikicked > 0)
        {
            Emit(new SpellKicked(stackId));
            Emit(new SpellMultikicked(stackId, multikicked));
        }

        // CR 702.33d: paying either of an "and/or" card's kicker costs kicks the spell, and
        // paying both kicks it twice. The flag, the count and the per-cost record all ride,
        // because "if it was kicked", "for each time it was kicked" and "if it was kicked with
        // its [A] kicker" are three different questions printed on these same cards
        // (CR 702.33f) — and the count goes on even for one payment, since "each time" is a
        // number, not a comparison.
        if (kickedWith is { Count: > 0 } whichKickers)
        {
            Emit(new SpellKicked(stackId));
            Emit(new SpellMultikicked(stackId, whichKickers.Count));
            Emit(new SpellKickedWith(
                stackId,
                [.. whichKickers.Select(i => definition!.KickerCosts[i].Printed)]));
        }

        if (buyback)
            Emit(new SpellBoughtBack(stackId));

        if (prototyped)
            Emit(new SpellPrototyped(stackId));

        // Recorded here because this is the only moment that knows: by the time the spell
        // resolves the choice is long past, and what warp buys happens on the far side of it.
        var offeredCost = _abilities.SpellOf(card.Card)?.ConditionalAlternativeCost;
        if (alternativeCost && offeredCost is { Keyword: "warp" })
            Emit(new SpellWarped(stackId));

        // The same record for escape, which is a zone permission rather than a cost swap - but
        // the question a card asks afterwards is identical: was this how the spell was cast?
        if (fromElsewhere && alternative is { Keyword: "escape" })
            Emit(new SpellEscaped(stackId));

        if (dashed)
            Emit(new SpellDashed(stackId));

        if (blitzed)
            Emit(new SpellBlitzed(stackId));

        if (evoked)
            Emit(new SpellEvoked(stackId));

        if (overloaded)
            Emit(new SpellOverloaded(stackId));

        if (awakened)
            Emit(new SpellAwakened(stackId));

        if (sneaked)
            Emit(new SpellSneaked(stackId, joining));

        if (teamwork)
            Emit(new SpellTeamwork(stackId));

        if (bestowed)
            Emit(new SpellBestowed(stackId));

        if (mutated)
            Emit(new SpellMutating(stackId, mutateOnTop));

        if (offspring)
            Emit(new SpellOffspring(stackId));

        // The cost is paid the way its kind says: conspire taps, casualty sacrifices. The rule
        // makes the copy a reflexive trigger; here it happens as the spell is cast, so nobody
        // gets a window between paying and the copy appearing.
        foreach (var (paid, creatures) in conspiracy)
        {
            foreach (var creature in creatures)
            {
                if (paid.Kind is ChosenCostKind.TapPermanents)
                    Emit(new PermanentTapped(creature));
                else
                    Move(creature, Zone.Graveyard, MoveCause.Sacrifice, playerId);
            }
        }

        if (conspired)
            Emit(new SpellCopied(ObjectId.New(), card.Card, playerId, chosen));

        // CR 702.55a: one copy per payment. Like conspire's, these appear as the spell is cast
        // rather than through the reflexive trigger the rule describes, so there is no window
        // between paying and the copies arriving.
        for (var i = 0; i < replicated; i++)
            Emit(new SpellCopied(ObjectId.New(), card.Card, playerId, chosen));

        if (!chosen.IsEmpty || variableValue > 0)
            Emit(new TargetsChosen(
                stackId, chosen, variableValue, announcedDivision));

        if (fromCommandZone)
        {
            Emit(new CommanderCastFromCommandZone(
                playerId, State.GetPlayer(playerId).CommanderCastsFromCommandZone + 1));
        }

        // CR 702.34a: "exile this card instead of putting it anywhere else any time it would
        // leave the stack." Remembered against the stack object, because that is the thing whose
        // departure it is about, and it stops existing when the spell does.
        if (fromElsewhere && alternative!.ExileOnResolve)
            _exileOnLeavingStack.Add(stackId);

        // CR 712.11a: a card cast "transformed" is put on the stack with its back face up, and
        // from then on it has only that face's characteristics (CR 712.8c) — a defeated Siege's
        // flip side is a creature spell here, is countered as one, and resolves as one, while
        // its mana value stays the front face's (CR 712.8e). Emitted before the cast is
        // announced so anything that triggers on the cast sees the face that is actually up.
        if (onTheHouse && card.CastsTransformed && card.Card.Faces.Count > 1)
            Emit(new PermanentTransformed(stackId, 1));

        // CR 601.2b: the offer is spent by the cast that used it, recorded here rather than left
        // to the pass that would otherwise sweep it. Emitted before the cast is announced, for
        // the reason PreventionEffectSpent is emitted inside the replacement that used the
        // shield: anything that triggers off this cast must not find the offer still standing.
        //
        // Without this the permission is one a player takes as many times as their hand has
        // answers, which is a strictly better card than the one printed - and a suite that only
        // ever casts once cannot see it.
        if (handOffer is { } taken)
            Emit(new HandCastOfferSpent(taken.Id));

        // CR 700.14: what the caster handed over, and where that leaves their running total for
        // the turn. Emitted before the cast so the two triggers off one spell are collected
        // against the states the rules put them against, and emitted even when nothing was paid
        // is not wanted - a free spell expends nothing and must not fire a threshold twice.
        if (manaSpent.Total > 0)
        {
            var before = State.GetPlayer(playerId).ManaSpentCastingThisTurn;
            Emit(new ManaSpentCasting(
                playerId, manaSpent.Total, before, before + manaSpent.Total));
        }

        Emit(new SpellCastEvent(playerId, stackId, card.Card.Name, castFrom));
        // CR 117.3c: the caster receives priority again.
        _priorityRecipient = playerId;
        SettleBeforePriority();
        // CR 117.3c: the caster receives priority again, and the run of passes is broken.
        Emit(new PriorityGranted(playerId));

        return stackId;
    }

    /// <summary>
    /// Activates an ability (CR 602.2). A mana ability resolves immediately and does not use the
    /// stack (CR 605.3b); everything else goes on the stack like a spell.
    /// </summary>
    /// <param name="variableValue">
    /// The number the player named for a cost that asks for one — "remove any number of storage
    /// counters" (CR 601.2b). Ignored by every ability that does not ask.
    /// </param>
    public ObjectId? ActivateAbility(
        Guid playerId,
        ObjectId sourceId,
        string abilityId,
        IReadOnlyList<Target>? targets = null,
        IReadOnlyList<ObjectId>? costPayment = null,
        int variableValue = 0)
    {
        var source = State.GetObject(sourceId);
        var ability = ActivatedAbilitiesOf(State, _abilities, source)
            .FirstOrDefault(a => string.Equals(a.Id, abilityId, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"{source.Card.Name} has no ability {abilityId}.");

        // CR 117.1d: a mana ability may be activated whenever a player has priority, and also
        // while they are paying a cost — which is the only reason mana is ever available.
        if (!ability.IsManaAbility)
            RequirePriority(playerId);
        else if (State.IsOver)
            throw new InvalidOperationException("The game is over (CR 104.2).");

        // CR 602.1a: only the controller may activate, unless the card says anyone may.
        if (ControllerOf(source) != playerId && !ability.AnyPlayerMayActivate)
            throw new InvalidOperationException("You do not control that permanent.");

        // CR 602.5c: an effect can shut the abilities off outright. Every one of them, mana
        // abilities included - the rule draws no distinction, and a pacifism that left a land
        // tapping for mana would be a different card.
        if (Characteristics.Of(State, _abilities, source).AbilitiesCantBeActivated)
        {
            throw new InvalidOperationException(
                $"{source.Card.Name}'s activated abilities can't be activated (CR 602.5c).");
        }

        // CR 602.5: the same prohibition said about a name somebody chose rather than about
        // this permanent. Asked of the source's name and not of its characteristics, because
        // "sources with the chosen name" reaches a card in any zone — and because a name is
        // not a characteristic CR 613 can move.
        if (Bans.ActivatingForbidden(State, _abilities, source, ability.IsManaAbility)
            is { } forbidding)
        {
            throw new InvalidOperationException(
                $"{source.Card.Name}'s activated abilities can't be activated: "
                    + $"{forbidding.Card.Name} named it (CR 602.5).");
        }

        // CR 702.18a: while a spell with split second is on the stack, only mana abilities may
        // be activated. Checked before the timing rule so the reason given is the real one.
        if (!ability.IsManaAbility && WhySplitSecondForbids() is { } waiting)
            throw new InvalidOperationException(waiting);

        // CR 602.5d: a printed timing restriction. Checked before the once-a-turn limit so that
        // an attempt refused for timing does not burn the turn's one activation.
        if (WhyTimingForbids(ability.Timing, playerId) is { } forbidden)
            throw new InvalidOperationException(forbidden);

        // CR 602.5b: a printed restriction on the board rather than on the phase. Asked of the
        // source permanent, because "you" in the printed condition means whoever controls it.
        if (ability.ActivateOnlyIf?.Invoke(State, _abilities, source, null) == false)
        {
            throw new InvalidOperationException(
                "That ability cannot be activated right now (CR 602.5b).");
        }

        // CR 602.2a: the ability that was activated is the ability that resolves. Written down
        // here, where the definition is still in hand, because looking it back up by id on a
        // card is a reconstruction and the reconstruction can be wrong: ability ids are
        // generated per card, so a copy's "a" and the copying card's own "a" are two different
        // abilities with one name. Mirage Mirror is the card that proves it — its own ability
        // makes it a copy, and the copied ability it then offers resolved as the ability that
        // had made the copy, targeting nothing and doing nothing at all.
        //
        // Unconditional, because a conditional write leaves the last copy's ability behind: the
        // permanent stops being a copy, its own ability is activated, and a stale entry under
        // the same key would answer for it.
        _grantedOnStack[GrantedKey(sourceId, abilityId)] = ability;

        if (ability.LoyaltyCost is { } loyaltyCost)
        {
            // CR 606.3: only one loyalty ability of each planeswalker each turn — the limit is on
            // the permanent, not on the ability, so a walker cannot use a different one instead.
            if (_loyaltyUsedThisTurn.Contains(sourceId))
            {
                throw new InvalidOperationException(
                    "A loyalty ability of that planeswalker has already been activated this turn "
                        + "(CR 606.3).");
            }

            // CR 118.3: a cost that removes counters cannot be paid without them to remove.
            var held = source.Permanent?.Counters.GetValueOrDefault(CounterKinds.Loyalty) ?? 0;
            if (loyaltyCost < 0 && held < -loyaltyCost)
            {
                throw new InvalidOperationException(
                    $"There are only {held} loyalty counters to remove (CR 118.3).");
            }
        }

        // CR 602.5b: a restriction on activating, checked before any cost is paid.
        if (ability.MaxActivationsPerTurn is { } limit)
        {
            var key = (sourceId, ability.Id);
            if (_activationsThisTurn.GetValueOrDefault(key) >= limit)
            {
                throw new InvalidOperationException(
                    $"{ability.Text} has already been activated this turn (CR 602.5b).");
            }

            _activationsThisTurn[key] = _activationsThisTurn.GetValueOrDefault(key) + 1;
        }

        if (source.Zone != ability.FunctionsFrom)
            throw new InvalidOperationException("That ability does not function from there (CR 602.5).");

        if (ability.RequiresTap)
        {
            var permanent = source.Permanent
                ?? throw new InvalidOperationException("Only a permanent can be tapped for a cost.");

            if (permanent.IsTapped)
                throw new InvalidOperationException("It is already tapped (CR 602.5b).");

            // CR 302.6: a creature's {T} ability needs it to have been around since the turn
            // began. A noncreature permanent has no such restriction, and CR 702.10c lifts it
            // for a creature with haste — which this check had, wrongly, been refusing.
            var computed = Characteristics.Of(State, _abilities, source);
            if (permanent.HasSummoningSickness
                && computed.IsCreature
                && !computed.Has(KeywordAbility.Haste))
            {
                throw new InvalidOperationException("It has summoning sickness (CR 302.6).");
            }
        }

        // CR 118.8: life can be paid only down to zero. Checked with the other legality
        // questions, before any of the cost is actually paid.
        if (ability.LifeCost > 0 && State.GetPlayer(playerId).Life < ability.LifeCost)
            throw new InvalidOperationException($"You cannot pay {ability.LifeCost} life (CR 118.8).");

        // CR 118.3: counters that are not there cannot be removed, so an ability wanting more
        // than the permanent holds is refused before anything else is spent.
        // "Remove any number" is the player's number, and the ability's own count is the floor
        // rather than the price. Read once here so the check and the charge cannot disagree.
        var countersToRemove = ability.CounterCost is { } priced
            ? ability.CounterCostIsChosen ? variableValue : priced.Count
            : 0;

        if (ability.CounterCost is { } counterCost)
        {
            var held = source.Permanent?.Counters.GetValueOrDefault(counterCost.Kind) ?? 0;
            if (held < countersToRemove || countersToRemove < counterCost.Count)
            {
                throw new InvalidOperationException(
                    $"There are only {held} {counterCost.Kind} counters to remove (CR 118.3).");
            }
        }

        // CR 118.3: energy is checked with the rest of the cost, before any of it is paid — a
        // player who cannot pay the energy has not activated the ability at all.
        if (ability.EnergyCost > 0 && State.GetPlayer(playerId).Energy < ability.EnergyCost)
        {
            throw new InvalidOperationException(
                $"You have {State.GetPlayer(playerId).Energy} energy and need "
                    + $"{ability.EnergyCost} (CR 107.4c).");
        }

        // CR 601.2h: the whole cost is checked, then paid. Working out which cards answer which
        // cost happens here so that an unpayable cost is refused before anything at all is spent.
        var chosen = MatchChosenCosts(ability.ChosenCosts, playerId, sourceId, costPayment);

        var chosenTargets = (targets ?? []).ToImmutableList();
        RequireLegalTargets(
            ability.Targets, chosenTargets, playerId, ability.Text, source, variableValue);

        // CR 602.2b: an activated ability's activation cost is the analogue of a spell's mana
        // cost, so CR 601.2f's increases and reductions apply to it in the same way. This is the
        // half that had no hook at all - the printed cost went straight to the pool - so every
        // "abilities you activate cost {1} less to activate" on the board was inert.
        var modifiers = CostModifiersFor(
            CostModifierKind.ActivatedAbilities,
            source.Card,
            playerId,
            null,
            sourceId,
            ability.IsManaAbility).ToList();

        // "This ability costs {1} less to activate for each Shrine you control" — a discount the
        // ability prints about itself, whose amount is not known until it is activated. It joins
        // the list the board contributed rather than being applied on its own, so CR 601.2f's
        // ordering decides between it and everything else: every increase first, then every
        // reduction, and the mana component floored at {0} once rather than twice.
        if (ability.CostReduction is { } printed)
        {
            var less = printed(State, _abilities, source, playerId);
            if (less > 0)
                modifiers.Add(new CostModifier { Amount = less });
        }

        var activationCost = CostModification.Apply(ability.ManaCost, modifiers);

        // The announced X is handed to the payment, not only to the cost checks above. An
        // activation cost may contain {X} the way a spell's mana cost can (CR 602.2b), and
        // without this "{X}, {T}: search for a card with mana value X or less" charged nothing
        // at all - a free tutor for as much as its controller cared to name.
        PayMana(
            playerId, activationCost, variableValue, spend: ManaSpend.Activating(source.Card));

        if (ability.LifeCost > 0)
        {
            var life = State.GetPlayer(playerId).Life;
            Emit(new LifeChanged(playerId, -ability.LifeCost, life - ability.LifeCost));
        }

        if (ability.EnergyCost > 0)
            Emit(new EnergyChanged(playerId, -ability.EnergyCost));

        if (ability.CounterCost is { } paidCounters && countersToRemove > 0)
            Emit(new CountersChanged(sourceId, paidCounters.Kind, -countersToRemove));

        if (ability.LoyaltyCost is { } paidLoyalty)
        {
            _loyaltyUsedThisTurn.Add(sourceId);

            // A zero-loyalty ability still counts as used; it just moves no counters.
            if (paidLoyalty != 0)
                Emit(new CountersChanged(sourceId, CounterKinds.Loyalty, paidLoyalty));
        }
        if (ability.RequiresTap)
            Emit(new PermanentTapped(sourceId));

        // CR 601.2f: costs are paid before the ability goes on the stack, so the source is
        // already gone by the time it resolves. That is the point of cycling — the card is
        // discarded whether or not anything responds.
        // What a returned attacker was attacking, read before it leaves: the ninja arrives
        // against the same defender, and by the time the ability resolves the creature that knew
        // it is in its owner's hand (CR 702.49a).
        AttackTarget? joining = null;

        // How much power was tapped to pay for this, which station asks for and nothing else
        // does yet (CR 702.184a): "put a number of charge counters on this permanent equal to
        // the tapped creature's power". Read before the tap rather than after, not because
        // tapping changes power but because reading state you have just written is how a
        // subtlety gets in later.
        var powerTapped = 0;

        // The same reading one question wider, for "where X is the sacrificed creature's power"
        // and its family (CR 608.2k). Station's number above is a total over everything tapped
        // and is folded into the ability's own amount; this one is a single object's whole set of
        // characteristics, kept apart because the sentences that ask for it ask for a different
        // one of them on different cards.
        var costTakes = WhatTheCostTakes([.. chosen.SelectMany(paid => paid.Cards)]);

        foreach (var (cost, cards) in chosen)
        {
            foreach (var card in cards)
            {
                if (cost.Kind is ChosenCostKind.TapPermanents)
                {
                    if (State.TryGetObject(card, out var tapping))
                    {
                        // A creature with no power is not a creature with zero power, and a
                        // negative one contributes nothing rather than taking counters away.
                        var tappedPower = Characteristics.Of(State, _abilities, tapping).Power;
                        if (tappedPower > 0)
                            powerTapped += tappedPower.Value;
                    }

                    Emit(new PermanentTapped(card));
                    continue;
                }

                if (cost.Kind
                    is ChosenCostKind.ExileFromGraveyard or ChosenCostKind.ExileFromHand)
                {
                    Move(card, Zone.Exile, MoveCause.Exile, playerId);
                    continue;
                }

                if (cost.Kind is ChosenCostKind.ReturnToHand)
                {
                    if (State.Combat.Attackers.TryGetValue(card, out var wasAttacking))
                        joining ??= wasAttacking;

                    Move(card, Zone.Hand, MoveCause.Return, playerId);
                    continue;
                }

                Move(
                    card,
                    Zone.Graveyard,
                    cost.Kind is ChosenCostKind.SacrificePermanents
                        ? MoveCause.Sacrifice
                        : MoveCause.Discard,
                    playerId);
            }
        }

        // Announced before the self-cost is paid, because the event names the object that has
        // the ability and a cost that discards or sacrifices it leaves that object behind: after
        // the move the id in the event names nothing, and anything watching for the activation -
        // "when you cycle this card" - is looking at a card that has already become a different
        // object (CR 400.7). The cost is still paid; only the order of the announcement changed.
        Emit(new AbilityActivated(playerId, sourceId, ability.Id, ability.Text));

        if (ability.SelfCost is SelfCost.DiscardSelf)
            Move(sourceId, Zone.Graveyard, MoveCause.Discard, playerId);
        else if (ability.SelfCost is SelfCost.SacrificeSelf)
            Move(sourceId, Zone.Graveyard, MoveCause.Sacrifice, playerId);
        else if (ability.SelfCost is SelfCost.ExileSelfFromGraveyard or SelfCost.ExileSelf)
        {
            // Both arms are the same move; only the zone the source starts in differs, and
            // FunctionsFrom has already refused an activation from anywhere else. Paid here with
            // the rest of the cost, on activation, so the permanent is in exile before the
            // ability is on the stack: an opponent given priority afterwards has nothing left to
            // sacrifice in response (CR 601.2h, 117.7).
            Move(sourceId, Zone.Exile, MoveCause.Exile, playerId);
        }
        else if (ability.SelfCost is SelfCost.ReturnSelfToHand)
            Move(sourceId, Zone.Hand, MoveCause.Return, State.GetObject(sourceId).OwnerId);

        _priorityRecipient = playerId;

        if (ability.IsManaAbility)
        {
            // CR 605.3b: it resolves immediately, and nobody gets a chance to respond.
            foreach (var production in ability.Produces)
            {
                var colour = production.Color;

                if (production.FromChosenColor)
                {
                    // Nothing named yet means no mana, not colourless mana: a permanent that has
                    // not answered its question cannot tap for a colour it has not chosen.
                    if (ColorNamed(source.Chosen) is not { } named)
                        continue;

                    colour = named;
                }

                // Which permanent made it, so "whenever you tap a land for mana" has
                // something to read. The pool itself does not remember where mana came from
                // (CR 106.1) and does not need to; this is a fact about the ability that
                // produced it, and it lives on the event that says so.
                // "Add {C} for each storage counter removed this way" - the amount is the count
                // the player named, which the cost has just charged them. Read from the same
                // number rather than from the counters left behind: those are already gone.
                var amount = production.FromCounterCost ? countersToRemove : production.Amount;

                if (amount <= 0)
                    continue;

                // The narrow half of the restriction travels beside the type mask rather than
                // inside it (CR 106.6): a tribe, a zone and a commander are not card types, and
                // the pool has had somewhere to put all three for longer than anything could
                // read the sentence that sets them. Dropping them here would be the same defect
                // as not reading the clause at all, one step further along - mana that lost its
                // restriction is strictly better than the mana printed.
                Emit(new ManaAdded(
                    playerId,
                    colour,
                    amount,
                    production.Restriction,
                    sourceId,
                    production.RestrictedTo,
                    production.RestrictedToZone,
                    production.RestrictedToCommander));
            }

            // "{T}: Add {C}{C}. This land doesn't untap during your next untap step" - a mana
            // ability is still allowed to do something other than make mana, and that half
            // happens now too rather than not at all. It cannot go on the stack: a mana ability
            // never does (CR 605.3a), and putting it there would make it counterable and
            // unusable while a spell was being paid for.
            RunEffects(ability.Effects, source);

            return null;
        }

        var stackId = ObjectId.New();
        Emit(new TriggerPutOnStack(
            stackId, sourceId, source.Card, ability.Id, ability.Text, playerId)
        {
            JoiningAgainst = joining,

            // "How much this ability is about" is exactly what the field means for a trigger,
            // and it is the same question here - so station reads its counters from it rather
            // than from a second field that would mean the same thing on a different day.
            SubjectAmount = powerTapped > 0 ? powerTapped : null,

            // The X this activation announced, so the ability resolves knowing it. It is on the
            // ability's own object because that is what resolves (CR 113.7a): read off the
            // permanent instead, the number would be whatever the permanent was cast for, which
            // is a different X entirely.
            VariableValue = variableValue,
        });

        // Beside the announced X, and after the ability is on the stack because until then there
        // is no object to hang it on (CR 602.2a).
        if (costTakes is { } bought)
            Emit(new CostPaidRecorded(stackId, bought));

        if (!chosenTargets.IsEmpty)
            Emit(new TargetsChosen(stackId, chosenTargets, variableValue));

        SettleBeforePriority();
        Emit(new PriorityGranted(playerId));

        return stackId;
    }

    /// <summary>
    /// Spells cast face down, so the permanent they become arrives face down (CR 707.2).
    /// </summary>
    /// <remarks>
    /// Held against the stack object rather than the card, because it is this casting that is
    /// face down and not the card — the same card cast again normally is an ordinary spell.
    /// </remarks>
    private readonly HashSet<ObjectId> _castFaceDown = [];

    /// <summary>
    /// Exiles a card face down to be cast on a later turn (CR 702.143a).
    /// </summary>
    /// <remarks>
    /// A special action, like turning a permanent face up: it uses no stack and cannot be
    /// responded to. What an opponent sees is a card leaving a hand for exile, which is the whole
    /// bluff — they are not told what it was.
    /// </remarks>
    /// <summary>
    /// Exiles a card from hand with time counters on it (CR 702.62a).
    /// </summary>
    /// <remarks>
    /// A special action rather than a way of casting: nothing goes on the stack, nobody may
    /// respond, and the spell itself is cast turns later and for nothing. The countdown is run by
    /// the engine at each upkeep rather than by a compiled trigger, which is a deviation worth
    /// naming — CR 702.62a makes the removal a triggered ability, so in a real game a player
    /// could respond to the last counter coming off. Here they cannot.
    /// </remarks>
    public void Suspend(Guid playerId, ObjectId cardId)
    {
        RequirePriority(playerId);

        var card = State.GetObject(cardId);

        if (card.Zone != Zone.Hand || card.OwnerId != playerId)
        {
            throw new InvalidOperationException(
                "Suspend exiles a card from your hand (CR 702.62a).");
        }

        if (_abilities.SpellOf(card.Card) is not { SuspendCost: { } cost, SuspendCount: > 0 } spell)
            throw new InvalidOperationException($"{card.Card.Name} has no suspend (CR 702.62a).");

        // CR 702.62a: "any time you could cast a sorcery".
        if (!State.IsSorcerySpeedFor(playerId))
            throw new InvalidOperationException("Suspend is a sorcery-speed action (CR 702.62a).");

        PayMana(playerId, cost);

        var exiled = Move(cardId, Zone.Exile, MoveCause.Exile, playerId);
        Emit(new CardSuspended(exiled, playerId, spell.SuspendCount));

        SettleBeforePriority();
    }

    /// <summary>
    /// Takes a time counter off each of this player's suspended cards (CR 702.62a).
    /// </summary>
    /// <remarks>
    /// Run as the upkeep begins, alongside the other turn-based actions. The card whose last
    /// counter comes off is offered for casting rather than cast outright: the engine cannot
    /// perform a cast from inside its own bookkeeping, and an offer the player takes through the
    /// ordinary path reaches the same board. CR 702.62a makes the cast mandatory, so declining is
    /// a choice the rules do not give — and one that only ever leaves the card stranded.
    /// </remarks>
    /// <summary>
    /// Puts a lore counter on each Saga the active player controls (CR 714.3c).
    /// </summary>
    /// <remarks>
    /// Only Sagas with a chapter ability advance. CR 714.3c says as much, and it matters here
    /// for a second reason: a Saga whose chapters this engine could not read has no ability to
    /// fire and no final chapter to reach, so advancing it would walk it up to a sacrifice that
    /// never did anything.
    /// </remarks>
    private void AdvanceSagas()
    {
        foreach (var id in State.Battlefield.ToList())
        {
            if (!State.TryGetObject(id, out var saga)
                || saga.Permanent is null
                || saga.ControllerId != State.ActivePlayerId)
            {
                continue;
            }

            if (!_abilities.TriggersOf(saga.Card).Any(t => t.Chapter is not null))
                continue;

            Emit(new CountersChanged(id, CounterKinds.Lore, 1));
        }
    }

    /// <summary>
    /// Rolls the active player's visit die and visits what it lit (CR 505.5, 701.52a, 717.4).
    /// </summary>
    /// <remarks>
    /// The roll happens here rather than through <see cref="RollDice"/> because a turn-based
    /// action has no card behind it: the deferred path finds its table by looking up
    /// (source, ability, effect index) in a compiled card, and this instruction is printed in the
    /// rules rather than on anything. What it shares with that path is the part that matters -
    /// the number goes into the log as <see cref="DiceRolled"/>, so a replay reads the result
    /// instead of rolling again, and the visits are computed from it by the same
    /// <see cref="Attractions.VisitEvents"/> the card-printed roll uses.
    /// <para>
    /// Skipped entirely when the active player controls no Attraction, which is CR 717.4's own
    /// condition and not an optimisation: a die rolled for nobody would still be a number in the
    /// log, and every "whenever you roll one or more dice" card in the corpus would trigger on
    /// each of their main phases for the rest of the game.
    /// </para>
    /// </remarks>
    private void RollToVisitAttractions()
    {
        var active = State.ActivePlayerId;

        var controlsOne = State.Battlefield.Any(id =>
            State.TryGetObject(id, out var obj)
            && obj.Permanent is not null
            && Attractions.Is(obj.Card)
            && Characteristics.ControllerOf(State, _abilities, obj) == active);

        if (!controlsOne)
            return;

        var result = _random.Choose([.. Enumerable.Range(1, Attractions.DieSides)]);
        Emit(new DiceRolled(active, Attractions.DieSides, result, result));

        foreach (var visit in Attractions.VisitEvents(State, _abilities, active, result))
            Emit(visit);
    }

    /// <summary>
    /// Ends floating effects whose "for as long as" condition has stopped holding (CR 611.2b).
    /// </summary>
    /// <returns>True when one ended, so the sweep runs again.</returns>
    private bool EndEffectsWhoseConditionFailed()
    {
        foreach (var floating in State.FloatingEffects)
        {
            if (_abilities.FloatingEffect(floating.DefinitionId) is not { While: { } holds })
                continue;

            if (holds(State, _abilities))
                continue;

            Emit(new ContinuousEffectEnded(floating.Id));
            return true;
        }

        return false;
    }

    private void TickSuspendedCards(Guid playerId)
    {
        foreach (var id in State.Exile.ToList())
        {
            if (!State.TryGetObject(id, out var card)
                || card.SuspendedBy != playerId
                || card.TimeCounters <= 0)
            {
                continue;
            }

            var left = card.TimeCounters - 1;
            Emit(new TimeCounterRemoved(id, left));

            if (left == 0)
                Emit(new FreeCastOffered(id, playerId));
        }
    }

    /// <summary>
    /// Exiles a card from hand to be cast for nothing on a later turn (CR 702.169a).
    /// </summary>
    /// <remarks>
    /// Foretell's twin, and the same special action: nothing goes on the stack and nobody may
    /// respond. What differs is the other end - a plotted card costs nothing when it is finally
    /// cast, where a foretold one costs its foretell cost.
    /// </remarks>
    public void Plot(Guid playerId, ObjectId cardId)
    {
        RequirePriority(playerId);

        var card = State.GetObject(cardId);

        if (card.Zone != Zone.Hand || card.OwnerId != playerId)
            throw new InvalidOperationException("Plot exiles a card from your hand (CR 702.169a).");

        if (_abilities.SpellOf(card.Card)?.PlotCost is not { } cost)
            throw new InvalidOperationException($"{card.Card.Name} has no plot (CR 702.169a).");

        // CR 702.169a: "any time you could cast a sorcery".
        if (!State.IsSorcerySpeedFor(playerId))
            throw new InvalidOperationException("Plot is a sorcery-speed action (CR 702.169a).");

        PayMana(playerId, cost);

        var exiled = Move(cardId, Zone.Exile, MoveCause.Exile, playerId);
        Emit(new CardPlotted(exiled, State.TurnNumber));

        SettleBeforePriority();
    }

    public void Foretell(Guid playerId, ObjectId cardId)
    {
        RequirePriority(playerId);

        var card = State.GetObject(cardId);

        if (card.Zone != Zone.Hand || card.OwnerId != playerId)
            throw new InvalidOperationException("Foretell exiles a card from your hand (CR 702.143a).");

        if (_abilities.SpellOf(card.Card)?.ForetellCost is null)
            throw new InvalidOperationException($"{card.Card.Name} has no foretell (CR 702.143a).");

        // CR 702.143a: foretelling is a sorcery-speed action, and it always costs {2} whatever the
        // card charges to cast afterwards.
        if (!State.IsSorcerySpeedFor(playerId))
            throw new InvalidOperationException("Foretell is a sorcery-speed action (CR 702.143a).");

        PayMana(playerId, ManaCostSpec.Parse("{2}"));

        var exiled = Move(cardId, Zone.Exile, MoveCause.Exile, playerId);
        Emit(new CardForetold(exiled, State.TurnNumber));

        SettleBeforePriority();
    }

    /// <summary>
    /// Turns a face-down permanent face up by paying its morph cost (CR 702.37b, 707.9a).
    /// </summary>
    /// <remarks>
    /// A special action: it uses no stack, needs no priority window of its own and cannot be
    /// responded to. Anything that wanted to answer it had to answer the face-down creature.
    /// </remarks>
    public void TurnFaceUp(Guid playerId, ObjectId permanentId)
    {
        var permanent = State.GetObject(permanentId);

        if (permanent.Permanent is not { IsFaceDown: true })
            throw new InvalidOperationException("That permanent is not face down (CR 707.9a).");

        if (ControllerOf(permanent) != playerId)
            throw new InvalidOperationException("Only its controller may turn it face up.");

        // CR 701.40b: a manifested permanent turns face up by paying the card's own mana cost,
        // and only if it is a creature card. CR 702.37e: a morphed one may only ever pay its
        // morph cost, which is why the two are told apart rather than both accepting either.
        var morph = _abilities.SpellOf(permanent.Card)?.MorphCost;

        if (morph is null && permanent.Permanent is { IsManifested: true })
        {
            if (!permanent.Card.CardTypes.HasFlag(CardType.Creature))
            {
                throw new InvalidOperationException(
                    "Only a manifested creature card can be turned face up (CR 701.40b).");
            }

            morph = ManaCostSpec.Parse(permanent.Card.ManaCostRaw ?? string.Empty);
        }

        if (morph is not { } morphCost)
            throw new InvalidOperationException("That card has no morph cost (CR 702.37a).");

        PayMana(playerId, morphCost);
        Emit(new PermanentTurned(permanentId, FaceDown: false));

        // CR 702.37e: megamorph turns up one bigger. The counter goes on as part of turning it
        // face up, so nothing ever sees the creature at its printed size without it.
        if (_abilities.SpellOf(permanent.Card)?.MorphAddsCounter == true)
            Emit(new CountersChanged(permanentId, CounterKinds.PlusOnePlusOne, 1));

        SettleBeforePriority();
    }

    /// <summary>
    /// Which graveyard cards a delve payment may use, refusing anything that is not one.
    /// </summary>
    /// <remarks>
    /// Offering a card that is not in your graveyard is not quietly ignored: a caster who thinks
    /// they are paying with four cards and pays with two has cast a different spell from the one
    /// they meant to, and finding out afterwards is worse than being told now.
    /// </remarks>
    private List<ObjectId> MatchDelve(
        SpellDefinition? definition, Guid playerId, IReadOnlyList<ObjectId>? offered)
    {
        if (offered is null || offered.Count == 0)
            return [];

        if (definition?.HasDelve != true)
            throw new InvalidOperationException("That spell has no delve (CR 702.66a).");

        var graveyard = State.GetPlayer(playerId).Graveyard;

        foreach (var id in offered)
        {
            if (!graveyard.Contains(id))
            {
                throw new InvalidOperationException(
                    "Delve exiles cards from your own graveyard (CR 702.66a).");
            }
        }

        return [.. offered.Distinct()];
    }

    /// <summary>Checks that targets match the specs and are legal right now (CR 601.2c).</summary>
    /// <param name="announced">
    /// The value chosen for X (CR 601.2b). It is announced <em>before</em> targets are chosen, so
    /// a filter written around it - "target creature with mana value X or less" - has its number
    /// by the time this runs. It is not on the stack object yet, which is why it is passed rather
    /// than read: the object is not built until the cast is known to be legal.
    /// </param>
    private void RequireLegalTargets(
        ImmutableList<TargetSpec> specs,
        ImmutableList<Target> chosen,
        Guid playerId,
        string what,
        GameObject? source = null,
        int announced = 0)
    {
        // CR 601.2c: "up to" targets may be left unchosen, so the count is a range rather than
        // a number. The optional ones are the trailing block — a card never asks for an optional
        // target before a required one — so matching in order and stopping early is exact.
        var required = specs.Count(spec => !spec.Optional);

        if (chosen.Count < required || chosen.Count > specs.Count)
        {
            throw new InvalidOperationException(
                specs.Count == required
                    ? $"{what} needs {specs.Count} target(s) and was given {chosen.Count} (CR 601.2c)."
                    : $"{what} takes {required} to {specs.Count} target(s) and was given "
                        + $"{chosen.Count} (CR 601.2c).");
        }

        for (var i = 0; i < chosen.Count; i++)
        {
            // CR 601.2c: the whole announcement is made at once, so a spec restricted by another
            // of its own targets - "any other target", "from a single graveyard" - has its peer
            // in hand here and nowhere earlier. An announcement that breaks such a restriction is
            // illegal, not legal and quietly inert, so it is refused with the rest of them.
            if (!specs[i].IsLegal(
                    State, _abilities, chosen[i], playerId, source,
                    PeersOf(specs, chosen, i), announced))
            {
                throw new InvalidOperationException($"Illegal target: {specs[i].Description}.");
            }
        }
    }

    /// <summary>
    /// The already-chosen targets a spec is measured against, or none when it has no peer.
    /// </summary>
    /// <remarks>
    /// One helper rather than the same lines at each of the four places that ask, because the
    /// places disagree about what they are holding — a cast holds the announcement, a resolution
    /// holds the object's targets, and a trigger holds the answers given so far — and only the
    /// indexing is common. Out of range is empty and not an exception: a trigger asks this while
    /// its list is still being filled, and "up to two" may be announced for one.
    /// <para>
    /// The group is the anchor plus everything else measured against the same anchor, which is
    /// this engine's spelling of "one instance of the word <em>target</em>" (CR 115.3). "Exile up
    /// to four target cards from a single graveyard" is one instance and four specs, so the
    /// fourth pick has to differ from all three before it — pointing each of them at the first
    /// and checking only that pair would let the third and fourth be the same card.
    /// </para>
    /// </remarks>
    private static List<Target> PeersOf(
        ImmutableList<TargetSpec> specs, IReadOnlyList<Target> chosen, int index)
    {
        if (index >= specs.Count
            || specs[index].PeerIndex is not { } anchor
            || anchor < 0
            || anchor >= chosen.Count
            || anchor >= index)
        {
            return [];
        }

        var peers = new List<Target> { chosen[anchor] };

        for (var j = 0; j < index && j < chosen.Count; j++)
        {
            if (j != anchor && specs[j].PeerIndex == anchor)
                peers.Add(chosen[j]);
        }

        return peers;
    }

    /// <summary>
    /// Checks the division announced as a spell is cast (CR 601.2d).
    /// </summary>
    /// <remarks>
    /// Three rules, and each one is a card that would otherwise be better than it is printed:
    /// the total must be exactly what the spell divides, every chosen target must be given at
    /// least one, and nothing may be given to a target that was not chosen. Without the middle
    /// rule a player could name three targets and give two of them nothing, which is how you
    /// would use a divided spell to hit one creature while looking like you spread it.
    /// <para>
    /// It is not about damage. CR 601.2d covers dividing <em>or distributing</em>, so the same
    /// three rules govern "distribute three +1/+1 counters among one, two, or three target
    /// creatures" - and the check is keyed on <see cref="IDividedEffect"/> rather than on any one
    /// verb, so a second divided verb inherits the rules instead of copying them.
    /// </para>
    /// </remarks>
    private static ImmutableList<int>? RequireDivision(
        SpellDefinition? definition,
        ImmutableList<Target> chosen,
        IReadOnlyList<int>? division,
        int variableValue)
    {
        // The total is read off the effect rather than held beside it, so the number the
        // engine checks against is the same number the effect will spend.
        if (definition?.Effects.OfType<IDividedEffect>().FirstOrDefault() is not { } dividing)
            return null;

        return RequireDivision(dividing, chosen.Count, division, variableValue);
    }

    /// <summary>
    /// The same three rules, against one divided effect and a count of targets (CR 601.2d).
    /// </summary>
    /// <remarks>
    /// Shared with the abilities, which announce their division from a question rather than from
    /// the cast. The rule is one rule; two copies of it would be two places for "at least one"
    /// to be forgotten, and that is the rule an unchecked division is worth cheating on.
    /// </remarks>
    private static ImmutableList<int>? RequireDivision(
        IDividedEffect dividing,
        int targetCount,
        IReadOnlyList<int>? division,
        int variableValue)
    {
        var total = AnnouncedTotal(dividing.Total, variableValue);

        if (division is null || division.Count != targetCount)
        {
            throw new InvalidOperationException(
                $"This divides among {targetCount} target(s) and no division was announced "
                + "(CR 601.2d).");
        }

        // Only the targets the division covers, which is not always all of them. CR 601.2d says
        // each target *the division is among* must get at least one; a spell may target other
        // things in the same breath that the division has nothing to do with. Rhino, Terrible
        // Trampler destroys a target artifact or land and then distributes three counters among
        // up to three *other* target creatures - asked of every slot, the artifact's zero made
        // the card unannounceable, so a fully read permanent could not be cast at all.
        if (division.Skip(dividing.FirstIndex).Take(dividing.TargetCount).Any(amount => amount < 1))
        {
            throw new InvalidOperationException(
                "Each target a divided spell or ability chooses must be assigned at least 1 "
                + "(CR 601.2d).");
        }

        if (division.Skip(dividing.FirstIndex).Take(dividing.TargetCount).Sum() != total)
        {
            throw new InvalidOperationException(
                $"A divided spell or ability must assign exactly {total}, not "
                + $"{division.Sum()} (CR 601.2d).");
        }

        return [.. division];
    }

    /// <summary>
    /// How much a divided effect has to spend, at the moment the division is announced.
    /// </summary>
    /// <remarks>
    /// X is announced first (CR 601.2b before CR 601.2d), so a spell that deals X damage divided
    /// as you choose knows its total by the time the division is checked. An amount counted off
    /// the board does not: "X is the number of creatures on the battlefield as you cast this"
    /// is fixed as the spell is cast but is not the announced X, and a division checked against
    /// the wrong number is a card that deals the wrong amount of damage. The compiler never emits
    /// one, and this refuses rather than guesses if it ever does.
    /// </remarks>
    private static int AnnouncedTotal(Amount total, int variableValue) =>
        total.Counter is null
            ? total.IsVariable ? variableValue : total.Fixed
            : throw new InvalidOperationException(
                "A division whose total is counted from the board cannot be announced "
                + "(CR 601.2d).");

    /// <summary>
    /// Asks an ability's controller how it divides what it divides (CR 601.2d).
    /// </summary>
    /// <remarks>
    /// A spell announces its division with the cast and never reaches here. An ability cannot:
    /// a triggered ability is put on the stack by the game rather than by a player, and chooses
    /// its targets from questions of its own (CR 603.3d), so there is no call to hang the
    /// announcement on. The question is asked from the settle sweep, which runs after the ability
    /// is on the stack and before priority is granted — which is the same moment, and keeps the
    /// division public before anybody may respond to it.
    /// <para>
    /// One pick per point, exactly as combat damage division is asked, because that is what makes
    /// every legal division sayable with a list of options: three counters between two creatures
    /// is three picks from two answers.
    /// </para>
    /// </remarks>
    private bool AskOwedDivision()
    {
        foreach (var stackId in State.Stack)
        {
            if (!State.TryGetObject(stackId, out var onStack)
                || onStack.Ability is not { } ability
                || !onStack.Division.IsEmpty
                || onStack.Targets.IsEmpty)
            {
                continue;
            }

            var effects = EffectsOfAbility(onStack.Card, ability.AbilityId, ability.SourceId);
            if (effects.OfType<IDividedEffect>().FirstOrDefault() is not { } dividing)
                continue;

            // An amount counted off the board cannot be announced, and the compiler never emits
            // one — see AnnouncedTotal. Skipped rather than thrown so that a granted ability from
            // outside the compiler cannot stop the game; it will divide nothing, which is the
            // fail-closed half of the same decision.
            if (dividing.Total.Counter is not null)
                continue;

            var total = AnnouncedTotal(dividing.Total, onStack.VariableValue);
            var slots = Math.Min(dividing.TargetCount, onStack.Targets.Count - dividing.FirstIndex);
            if (total < slots || slots < 1)
                continue;

            Ask(new PendingChoice
            {
                Id = "divide-targets:" + stackId,
                PlayerId = ControllerOf(onStack),
                Kind = ChoiceKind.DivideAmongTargets,
                Prompt = $"{ability.Text} Divide {total} among the {slots} target(s) it chose. "
                    + "Pick a target once per point; every target must be given at least one.",
                // The option's id is the target's *position*, not the object's, because two of an
                // ability's targets can be the same kind of thing and a division keyed on the
                // object could not tell the two shares apart. The label carries the name so the
                // person picking can.
                Options =
                [
                    .. Enumerable.Range(dividing.FirstIndex, slots).Select(
                        i => new ChoiceOption(
                            i.ToString(System.Globalization.CultureInfo.InvariantCulture),
                            DivisionTargetLabel(onStack.Targets[i], ControllerOf(onStack)))),
                ],
                MinPicks = total,
                MaxPicks = total,
                TotalToDivide = total,
                Context = [stackId.Value.ToString("N")],
            });

            return true;
        }

        return false;
    }

    /// <summary>
    /// What a target of a division is called, for the person dividing.
    /// </summary>
    /// <remarks>
    /// The same labelling every other question uses, and for the same reason: a board with three
    /// Grizzly Bears on it offers three identical buttons and the player cannot tell which one
    /// they are pointing at. The answer itself is the target's position, so a pick is never
    /// ambiguous to the engine - this is purely so it is unambiguous to the person.
    /// </remarks>
    private string DivisionTargetLabel(Target target, Guid chooserId) =>
        target.Kind == TargetKind.Player
            ? State.GetPlayer(target.Player).Name
            : State.TryGetObject(target.Subject, out var obj) ? LabelFor(obj, chooserId) : "(gone)";

    /// <summary>The object on the stack an announced division is about, and what divides.</summary>
    private (ObjectId StackId, GameObject OnStack, IDividedEffect Dividing) DividedOnStack(
        PendingChoice choice)
    {
        var stackId = State.Stack.First(
            id => string.Equals(id.Value.ToString("N"), choice.Context[0], StringComparison.Ordinal));

        var onStack = State.GetObject(stackId);
        var ability = onStack.Ability!;

        return (
            stackId,
            onStack,
            EffectsOfAbility(onStack.Card, ability.AbilityId, ability.SourceId)
                .OfType<IDividedEffect>()
                .First());
    }

    /// <summary>How much each target was given, read off one-pick-per-point (CR 601.2d).</summary>
    private static int[] AnnouncedAmounts(int targets, IReadOnlyList<string> picks)
    {
        var amounts = new int[targets];

        foreach (var pick in picks)
        {
            var slot = int.Parse(pick, System.Globalization.CultureInfo.InvariantCulture);
            if (slot < 0 || slot >= amounts.Length)
                throw new InvalidOperationException($"No such target to divide among: {pick}.");

            amounts[slot]++;
        }

        return amounts;
    }

    /// <summary>
    /// Refuses an announced division the rules do not allow (CR 601.2d).
    /// </summary>
    /// <remarks>
    /// The picks are one per point, so the total is right by construction — but "every chosen
    /// target gets at least one" is not, and it is the rule worth cheating on: a player who could
    /// leave a target at zero would name three creatures and put everything on one. Checked with
    /// the same <c>RequireDivision</c> a cast is checked with, so the two cannot drift.
    /// <para>
    /// Run before the answer is recorded, like every other answer's rules, because emitting first
    /// and validating during the resumption clears the question and <em>then</em> throws —
    /// leaving a game with nothing outstanding and no way to move.
    /// </para>
    /// </remarks>
    private void RequireAnnouncedDivision(PendingChoice choice, IReadOnlyList<string> picks)
    {
        var (_, onStack, dividing) = DividedOnStack(choice);
        var amounts = AnnouncedAmounts(onStack.Targets.Count, picks);
        var covered = dividing.FirstIndex
            + Math.Min(dividing.TargetCount, onStack.Targets.Count - dividing.FirstIndex);

        RequireDivision(dividing, covered, [.. amounts.Take(covered)], onStack.VariableValue);
    }

    /// <summary>Records the division an ability's controller announced (CR 601.2d).</summary>
    private void RecordAnnouncedDivision(PendingChoice choice, IReadOnlyList<string> picks)
    {
        var (stackId, onStack, _) = DividedOnStack(choice);

        Emit(new DivisionAnnounced(
            stackId, [.. AnnouncedAmounts(onStack.Targets.Count, picks)]));

        _priorityRecipient = choice.ResumePriorityTo;
        SettleBeforePriority();
        GrantPriorityAfterSettle(choice.ResumePriorityTo);
    }

    /// <summary>Names a described prevention effect among the replacements (CR 615.1).</summary>
    private const string PreventionKey = "prevent-effect:";

    /// <summary>
    /// Whether a prevention effect watches this damage at all — its kind and its source.
    /// </summary>
    /// <remarks>
    /// The predicate itself lives in <see cref="Preventions"/>, because the same question is
    /// asked of the shields a permanent's static ability puts up, which are replacement effects
    /// and never reach <see cref="GameState.Preventions"/> at all. Two copies of "does this
    /// shield cover this damage" is two chances to disagree.
    /// </remarks>
    private bool PreventionWatches(PreventionEffect effect, bool isCombat, ObjectId sourceId) =>
        Preventions.Watches(effect, State, _abilities, isCombat, sourceId);

    /// <summary>Whether a prevention effect shields this permanent (CR 615.1).</summary>
    private bool PreventionCovers(PreventionEffect effect, GameObject damaged) =>
        Preventions.Covers(effect, State, _abilities, damaged);

    /// <summary>Whether a prevention effect shields this player (CR 615.1).</summary>
    private bool PreventionCoversPlayer(PreventionEffect effect, Guid playerId) =>
        Preventions.CoversPlayer(effect, State, playerId);

    /// <summary>
    /// The end of a shield that only ever stopped one instance of damage (CR 615.8).
    /// </summary>
    /// <remarks>
    /// Written as its own step because the condition is not "the shield applied": a shield that
    /// prevented <em>nothing</em> is not used up. CR 615.10's number can cap a damage event at
    /// zero — "prevent 1 of that damage" against an event of nought — and CR 615.8 counts
    /// instances of damage <em>prevented</em>, so the two amounts are compared rather than the
    /// arm being trusted to have done something.
    /// <para>
    /// The one shape that cannot reach here is unpreventable damage: both call sites ask
    /// <see cref="IsUnpreventable"/> before the loop, which is what buys CR 615.12's "existing
    /// damage prevention shields won't be reduced by damage that can't be prevented".
    /// </para>
    /// </remarks>
    private static GameEvent[] SpendingOf(PreventionEffect effect, int amount, int left) =>
        effect.OnlyOnce && left < amount ? [new PreventionEffectSpent(effect.Id)] : [];

    /// <summary>
    /// Whether this damage can't be prevented (CR 615.12).
    /// </summary>
    /// <remarks>
    /// Asked before each prevention arm rather than inside them, and the difference is the half
    /// of CR 615.12 that is easy to miss: "existing damage prevention shields won't be reduced by
    /// damage that can't be prevented". An arm that ran and prevented zero would still have
    /// spent the shield. Skipping the arm is what keeps the shield whole.
    /// <para>
    /// The rule's other half — applicable prevention effects are still <em>applied</em>, so any
    /// additional effect they carry happens — is vacuous in this engine: no prevention it
    /// compiles has a rider. That is written down in <see cref="BanDamagePrevention"/> so the day
    /// one does, this is the line that has to change.
    /// </para>
    /// <para>
    /// Two sources of ban, and the split is the same one the prevention family already makes.
    /// A "this turn" ban is a one-shot a spell resolved and is held in state; a permanent's
    /// static ban is read off the battlefield here, so that it stops the moment the permanent
    /// does (CR 611.2c) rather than leaving an entry behind for a sweep to forget.
    /// </para>
    /// </remarks>
    private bool IsUnpreventable(
        bool isCombat, ObjectId sourceId, GameObject? victim, Guid? victimPlayer)
    {
        foreach (var ban in State.Unpreventable)
        {
            if (!Preventions.Watches(ban, State, _abilities, isCombat, sourceId))
                continue;

            var hits = victim is not null
                ? Preventions.Covers(ban, State, _abilities, victim)
                : victimPlayer is { } player && Preventions.CoversPlayer(ban, State, player);

            if (hits)
                return true;
        }

        foreach (var ban in StaticBansInPlay())
        {
            foreach (var said in ban.Bans.Unpreventable)
            {
                // CR 609.7a: a source named by a static ability is that permanent, and CR 613.1b
                // says "you control" is read around whoever controls it now. Both are bound here
                // rather than when the card was compiled, because neither is knowable then.
                var described = new PreventionEffect
                {
                    // Bookkeeping for the replacement pass, which this descriptor never enters:
                    // it is asked a question and thrown away.
                    Id = Guid.Empty,
                    ControllerId = ban.ControllerId,
                    Kind = said.Kind,
                    Source = said.DealtBySource ? ban.Host.Id : null,
                    SourceFilter = said.SourceFilter,
                    SourceController = said.SourceController,
                };

                if (Preventions.Watches(described, State, _abilities, isCombat, sourceId))
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether a damage event is one no prevention may touch (CR 615.12).
    /// </summary>
    /// <remarks>
    /// The event-shaped face of <see cref="IsUnpreventable"/>, so that the two call sites which
    /// hold a whole event rather than its pieces cannot take the victim and the source apart
    /// differently. Anything that is not a damage event answers false: a ban is about damage.
    /// </remarks>
    private bool IsBannedFrom(GameEvent e) => e switch
    {
        DamageMarked marked => State.TryGetObject(marked.Id, out var victim)
            && IsUnpreventable(marked.IsCombat, marked.SourceId, victim, null),
        PlayerDamaged hit => IsUnpreventable(hit.IsCombat, hit.SourceId, null, hit.PlayerId),
        _ => false,
    };

    /// <summary>Whether anything at all is banning prevention right now (CR 615.12).</summary>
    /// <remarks>
    /// Asked so the combat damage step can keep its wholesale fog: with no ban in play, a fog
    /// stops every point and the step can say so in one line instead of asking per event. The
    /// two answers are the same and one of them is far cheaper.
    /// </remarks>
    private bool AnyPreventionBan() =>
        !State.Unpreventable.IsEmpty
        || StaticBansInPlay().Any(b => !b.Bans.Unpreventable.IsEmpty);

    /// <summary>Whether this player can't gain life (CR 119.7).</summary>
    /// <remarks>
    /// Not a replacement effect, which is why it is asked outside the replacement pass. CR 119.7
    /// says a replacement effect that would replace a life gain event affecting a banned player
    /// "won't do anything" — a prohibition sits above that machinery rather than competing inside
    /// it, and modelled as a candidate it would be offered to CR 616.1's ordering question, which
    /// nobody gets to answer about a rule.
    /// </remarks>
    private bool CannotGainLife(Guid playerId)
    {
        foreach (var ban in State.LifeGainBans)
        {
            if (Bans.Covers(ban, State, playerId))
                return true;
        }

        foreach (var ban in StaticBansInPlay())
        {
            foreach (var scope in ban.Bans.NoLifeGain)
            {
                if (PlayerScopes.Around(scope, State, ban.ControllerId).Contains(playerId))
                    return true;
            }
        }

        return false;
    }

    /// <summary>Every prohibition a permanent on the battlefield is printing right now.</summary>
    /// <remarks>
    /// The scan itself lives on <see cref="Bans"/>, because a resolving counter effect has to ask
    /// the same question and holds no game to ask it of. This is the game's spelling of it.
    /// </remarks>
    private IEnumerable<(GameObject Host, Guid ControllerId, StaticBans Bans)> StaticBansInPlay()
        => Bans.InPlay(State, _abilities);

    /// <summary>
    /// Every cost modifier on the battlefield that applies to this payment (CR 601.2f).
    /// </summary>
    /// <remarks>
    /// The whole battlefield, because a modifier is printed by whoever controls the permanent and
    /// aimed at whoever it names. Reading only the payer's own permanents is the mistake that
    /// left four of the grid's five real cells unread — "spells your opponents cast cost {1}
    /// more" is on somebody else's board by definition.
    /// </remarks>
    /// <param name="kind">Whether a spell is being cast or an ability activated (CR 602.2b).</param>
    /// <param name="paying">
    /// The card being cast, or the card whose ability is being activated — what the filter is
    /// asked about either way.
    /// </param>
    /// <param name="payerId">Who is paying, which is who the scope has to name.</param>
    /// <param name="castFrom">The zone the spell is being cast from, or null for an ability.</param>
    /// <param name="abilitySourceId">Which permanent's ability, for a self-modifier.</param>
    /// <param name="isManaAbility">Whether it is a mana ability (CR 605.1a).</param>
    /// <param name="aimedAt">
    /// What the spell has chosen to target, for the modifiers that ask (CR 601.2c). Null for an
    /// activated ability and for a face-down cast, neither of which chooses one — so a modifier
    /// conditioned on being targeted never applies to either, which is what its printed word
    /// "spells" says.
    /// </param>
    private IEnumerable<CostModifier> CostModifiersFor(
        CostModifierKind kind,
        CardDefinition paying,
        Guid payerId,
        Zone? castFrom,
        ObjectId? abilitySourceId,
        bool isManaAbility,
        IReadOnlyList<Target>? aimedAt = null)
    {
        foreach (var id in State.Battlefield)
        {
            var permanent = State.GetObject(id);
            var controller = ControllerOf(permanent);

            foreach (var modifier in ModifiersOn(permanent.Card))
            {
                if (modifier.Kind != kind)
                    continue;

                // "This ability costs {1} less to activate" is a permanent talking about its own
                // abilities. Read as "abilities you activate" it would discount every other
                // permanent its controller has, which is a different and much better card.
                if (modifier.SourceOnly)
                {
                    if (abilitySourceId != id)
                        continue;
                }
                else if (!PlayerScopes.Around(modifier.Who, State, controller).Contains(payerId))
                {
                    continue;
                }

                // "Activated abilities of creatures you control" is about whose permanent the
                // ability is on, which is a different player from whoever is activating it and a
                // different question from the scope above.
                if (modifier.SourceController is { } whose
                    && !(abilitySourceId is { } activating
                        && State.TryGetObject(activating, out var abilitySource)
                        && PlayerScopes.Around(whose, State, controller)
                            .Contains(ControllerOf(abilitySource))))
                {
                    continue;
                }

                // CR 605.1a: "unless they're mana abilities" exempts the abilities that never use
                // the stack, which is the only exclusion these cards print.
                if (modifier.ExceptManaAbilities && isManaAbility)
                    continue;

                // CR 601.2c before 601.2f: the targets are chosen before the cost is worked out,
                // so "spells that target this creature" is a question the cast can already
                // answer. Asked of this permanent's own id, which is why the loop variable is
                // the right thing to compare and the modifier itself carries no id at all.
                if (modifier.TargetsSource
                    && (aimedAt is null
                        || !aimedAt.Any(
                            t => t.Kind == TargetKind.Permanent && t.Subject == id)))
                {
                    continue;
                }

                // The zone is read on both halves or on neither. A modifier carrying a zone that
                // nothing consulted would apply its reduction from every zone, which is a worse
                // card than the unread one.
                if (modifier.FromZone is { } only && only != castFrom)
                    continue;

                // "Spells with the chosen name cost {3} more to cast" - the subject is a name
                // a player picked as this permanent entered, so it is asked of the permanent
                // rather than of the modifier, and a permanent that has not been asked yet
                // taxes nothing.
                if (modifier.ChosenName)
                {
                    if (!Bans.NameMatches(permanent, paying.Name))
                        continue;
                }
                else if (!SearchFilters.Matches(modifier.FilterId, paying))
                {
                    continue;
                }

                yield return modifier;
            }
        }
    }

    /// <summary>
    /// What one card says about costs — old reducers and new modifiers read as one list.
    /// </summary>
    /// <remarks>
    /// A <c>CostReducer</c> is exactly one cell of the modifier grid: your spells, less, from
    /// anywhere. It is translated here rather than applied by a second path, because two paths
    /// for one rule are two chances to disagree about the order CR 601.2f states.
    /// <para>
    /// The second arm was a silent <c>false</c> in every real game for a while: nothing
    /// implemented <see cref="ICostModifierSource"/>, so the whole grid reached only the
    /// hand-written pool in the behaviour tests while 0 of the 32,765 corpus cards could produce a
    /// modifier. <see cref="IAbilitySource"/> now extends it and <c>CompiledPool</c> answers it,
    /// so the arm carries what the compiler reads. The note is kept because the shape of that
    /// failure is this project's most expensive one - a mechanism built, tested and reachable from
    /// nothing - and this seam is where it would happen again.
    /// </para>
    /// </remarks>
    private IEnumerable<CostModifier> ModifiersOn(CardDefinition card)
    {
        foreach (var reducer in _abilities.CostReducersOf(card))
            yield return new CostModifier { FilterId = reducer.FilterId, Amount = reducer.Amount };

        if (_abilities is ICostModifierSource source)
        {
            foreach (var modifier in source.CostModifiersOf(card))
                yield return modifier;
        }
    }

    /// <summary>
    /// The permanent refusing this cast, if the board holds one (CR 601.3).
    /// </summary>
    /// <remarks>
    /// "A player can begin to cast a spell only if a rule or effect allows that player to cast it
    /// and no rule or effect prohibits that player from casting it." Every other refusal in this
    /// method comes off the card being cast; this one comes off somebody else's board, which is
    /// why it is a sweep rather than a field.
    /// <para>
    /// The card is read through <see cref="Characteristics.CardOf"/> and not off the object,
    /// because a permanent that has become a copy of Rule of Law has Rule of Law's static ability
    /// and its printed card has none (CR 613.2c). That is the mistake this file records nine
    /// times over, and it is the same one here.
    /// </para>
    /// <para>
    /// The qualifier narrows both halves of the sentence at once, and it has to: "more than one
    /// noncreature spell" neither stops a creature spell nor counts one. Asking it only of the
    /// tally, or only of the spell, would produce a card that stops the wrong half of a turn.
    /// </para>
    /// </remarks>
    private string? CastLimitRefusing(CardDefinition casting, Guid playerId)
    {
        foreach (var id in State.Battlefield)
        {
            var permanent = State.GetObject(id);
            var card = Characteristics.CardOf(State, _abilities, permanent);

            foreach (var limit in _abilities.CastLimitsOf(card))
            {
                if (!LimitReaches(limit, permanent, playerId) || !limit.Counts(casting))
                    continue;

                if (SpellsCastTowards(limit, playerId) >= limit.Max)
                    return card.Name;
            }
        }

        return null;
    }

    /// <summary>Whether a permanent's cast limit is about this player (CR 601.3).</summary>
    /// <remarks>
    /// The scope is read around the permanent's <em>current</em> controller, which is layer 2
    /// (CR 613.1b) — a stolen Moderation limits the thief. The enchanted-player arm is separate
    /// because the player an Aura is attached to is not a scope around anybody: it is a fact
    /// stored on the permanent, and an Aura attached to nothing names nobody at all (CR 303.4b).
    /// </remarks>
    private bool LimitReaches(CastLimit limit, GameObject permanent, Guid playerId) =>
        limit.Who == PlayerScope.EnchantedPlayer
            ? permanent.Permanent?.AttachedToPlayer == playerId
            : PlayerScopes.Around(limit.Who, State, ControllerOf(permanent)).Contains(playerId);

    /// <summary>How many spells this player has cast this turn that the limit counts.</summary>
    /// <remarks>
    /// The bare limit reads the tally and the qualified ones read the cards, and the difference is
    /// deliberate rather than an inconsistency: the tally counts a spell whose object could not be
    /// described — a face-down cast — and the card list does not (see <c>GameReducer.Cast</c>). A
    /// limit on every spell must count that spell; a limit on "noncreature spells" cannot say
    /// whether it was one, and not counting it is the arm that errs towards letting a player play.
    /// </remarks>
    private int SpellsCastTowards(CastLimit limit, Guid playerId)
    {
        var player = State.GetPlayer(playerId);

        return limit.ExceptTypes == CardType.None && limit.ExceptSubtype is null
            ? player.SpellsCastThisTurn
            : player.SpellCardsCastThisTurn.Count(limit.Counts);
    }

    /// <summary>
    /// Pays a mana cost from the player's pool (CR 601.2h), or refuses if it cannot be paid.
    /// </summary>
    /// <returns>
    /// The mana that was actually spent, which is not what the cost named: a generic symbol can
    /// be paid with anything, and the cards that ask about this — sunburst, "if {U} was spent to
    /// cast this" — mean what was handed over rather than what was asked for (CR 202.2).
    /// </returns>
    private ManaPool PayMana(
        Guid playerId,
        ManaCostSpec cost,
        int variableValue = 0,
        ManaSpend spend = default)
    {
        if (cost.Symbols.IsEmpty && variableValue == 0)
            return ManaPool.Empty;

        var pool = State.GetPlayer(playerId).ManaPool;
        var remaining = ManaPayment.Pay(pool, cost, variableValue, spend)
            ?? throw new InvalidOperationException(
                $"Not enough mana: {cost} needs more than {pool} (CR 601.2h).");

        Emit(new ManaSpent(playerId, remaining));

        // What left the pool, read as the difference rather than tracked through the payment:
        // one subtraction cannot disagree with the payment the way a parallel tally could.
        return new ManaPool
        {
            Colored = pool.Colored
                .Select(each => KeyValuePair.Create(
                    each.Key,
                    each.Value - remaining.Colored.GetValueOrDefault(each.Key)))
                .Where(each => each.Value > 0)
                .ToImmutableDictionary(),
            Colorless = Math.Max(0, pool.Colorless - remaining.Colorless),
        };
    }

    /// <summary>
    /// Plays a land (CR 305.1, 505.6b). A special action: it does not use the stack, cannot be
    /// countered, and nobody may respond to it (CR 116).
    /// </summary>
    public ObjectId PlayLand(Guid playerId, ObjectId cardId)
    {
        RequirePriority(playerId);

        var card = State.GetObject(cardId);

        // A land is played from hand, or from exile while something says it may be - the same
        // permission a spell reads, and the reason this says "play" rather than "cast".
        //
        // A standing free-cast offer is one of those. "You may play the exiled card without
        // paying its mana cost" is what hideaway pays out with, and the card it buried is a land
        // about as often as it is anything else; an offer that only ever bought a cast would
        // leave those lands stranded in exile with the card saying they may be played. Nothing is
        // discounted by reading it here - a land has no mana cost - so the offer buys the zone and
        // nothing else, the land drop is still spent, and it lapses on the same pass a cast offer
        // does.
        var loosed = card.Zone == Zone.Exile
            && (card.MayCastFree
                || (card.MayPlayUntilTurn is { } through && State.TurnNumber <= through)
                || card.MayPlayThroughOwnersNextTurn is not null);

        // CR 601.3: and from the top of a library, while something on the battlefield says so -
        // Oracle of Mul Daya's half of the same permission Future Sight prints whole. The land
        // drop is still spent and the timing is still sorcery speed, both checked below: the
        // permission buys the zone and nothing else.
        var fromLibraryTop = card.Zone == Zone.Library
            && State.GetPlayer(playerId).Library is [var onTop, ..]
            && onTop == cardId
            && CastPermissions.MayPlayFromTopOfLibrary(State, _abilities, playerId, card.Card);

        if (card.Zone != Zone.Hand && !loosed && !fromLibraryTop)
            throw new InvalidOperationException("A land is played from hand.");

        if (!card.Card.CardTypes.HasFlag(CardType.Land))
            throw new InvalidOperationException($"{card.Card.Name} is not a land.");

        if (!State.IsSorcerySpeedFor(playerId))
            throw new InvalidOperationException(
                "A land is played during your main phase with an empty stack (CR 505.6b).");

        var allowed = LandDropsFor(playerId);
        if (State.GetPlayer(playerId).LandsPlayedThisTurn >= allowed)
        {
            throw new InvalidOperationException(
                $"You have already played {allowed} land(s) this turn (CR 505.6b).");
        }

        var onBattlefield = Move(cardId, Zone.Battlefield, MoveCause.Play, playerId);
        Emit(new LandDropUsed(playerId));
        _priorityRecipient = playerId;
        SettleBeforePriority();
        Emit(new PriorityGranted(playerId));

        return onBattlefield;
    }

    /// <summary>
    /// Declares attackers (CR 508.1). Declaring none is a declaration and moves the step along.
    /// </summary>
    /// <param name="attackers">
    /// Each attacking creature, and what it is attacking — a player, or a planeswalker that
    /// player controls (CR 508.1b).
    /// </param>
    public void DeclareAttackers(
        Guid playerId, IReadOnlyDictionary<ObjectId, AttackTarget> attackers)
    {
        ArgumentNullException.ThrowIfNull(attackers);

        if (State.CurrentStep != TurnStep.DeclareAttackers)
            throw new InvalidOperationException("Attackers are declared in the declare attackers step.");

        if (playerId != State.ActivePlayerId)
            throw new InvalidOperationException("Only the active player declares attackers (CR 508.1).");

        if (State.Combat.AttackersDeclared)
            throw new InvalidOperationException("Attackers have already been declared this combat.");

        foreach (var (attackerId, target) in attackers)
        {
            // The whole target and not only the player, because a prohibition can name the
            // player and their planeswalkers separately, and a battle is attacked through a
            // player who is not being attacked at all (CR 310.9b, 506.3).
            var reason = CombatRules.CannotAttack(
                State, _abilities, State.GetObject(attackerId), playerId, target.DefendingPlayer, target);
            if (reason is not null)
                throw new InvalidOperationException($"That creature cannot attack: {reason}.");

            if (target.DefendingPlayer == playerId || State.GetPlayer(target.DefendingPlayer).HasLost)
                throw new InvalidOperationException("That player cannot be attacked.");

            if (!target.IsPlaneswalker)
                continue;

            // CR 508.1b: what the slot names may be a battle. It is attacked through its
            // protector, not its controller (CR 310.9b) — which is what lets a Siege's own
            // controller attack it: the protector is an opponent, so the defending player check
            // above already passed for exactly the player the rule wants.
            if (State.TryGetObject(target.Planeswalker, out var siege)
                && siege.Card.CardTypes.HasFlag(CardType.Battle)
                && siege.Zone == Zone.Battlefield)
            {
                if (siege.Permanent?.ProtectorId != target.DefendingPlayer)
                {
                    throw new InvalidOperationException(
                        "A battle can only be attacked through the player who protects it "
                            + "(CR 310.9b).");
                }

                continue;
            }

            // CR 508.1b: a planeswalker may be attacked, and only one the defending player
            // controls — attacking your own is not a thing, and neither is attacking one that
            // belongs to a third player you are not attacking.
            if (!State.TryGetObject(target.Planeswalker, out var walker)
                || !walker.Card.CardTypes.HasFlag(CardType.Planeswalker)
                || walker.Zone != Zone.Battlefield)
            {
                throw new InvalidOperationException("That is not a planeswalker on the battlefield.");
            }

            if (walker.ControllerId != target.DefendingPlayer)
            {
                throw new InvalidOperationException(
                    "A planeswalker can only be attacked through the player who controls it (CR 508.1b).");
            }
        }

        // CR 508.1d: the declaration as a whole has to satisfy every requirement, which is a
        // different question from whether each chosen creature may attack.
        if (CombatRules.IllegalAttackSet(State, _abilities, playerId, [.. attackers.Keys]) is { } broken)
            throw new InvalidOperationException(broken);

        // CR 508.1h/j: what it costs to attack is totalled over the whole declaration and locked
        // in, and there are no partial payments — so it is worked out and refused *before* a
        // single event is emitted. A declaration the player cannot pay for leaves the game
        // exactly as it was, mana included, which is what CR 508.1's "the game returns to the
        // moment before the declaration" says and the only reading a caller can recover from.
        //
        // No question is asked. CR 508.1i lets the active player make mana here and this engine
        // has no sub-step inside a turn-based action to hold one, so the mana has to be floating
        // already — the same shape as every other payment the engine takes with the action rather
        // than suspending it (see "A cost is not paid by asking"). The board's job is to quote
        // the price with CombatTaxes.ToAttack before it offers the button.
        var tax = CombatTaxes.ToAttack(State, _abilities, attackers);
        if (!tax.Symbols.IsEmpty)
        {
            if (!ManaPayment.CanPay(State.GetPlayer(playerId).ManaPool, tax))
            {
                throw new InvalidOperationException(
                    $"Attacking costs {tax} and you have {State.GetPlayer(playerId).ManaPool} "
                        + "(CR 508.1h).");
            }

            PayMana(playerId, tax);
        }

        Emit(new AttackersDeclared(attackers.ToImmutableDictionary()));

        // CR 508.1f: attacking taps the creatures. It is not a cost, so vigilance simply skips
        // it (CR 702.20b) rather than the attack being paid for differently.
        foreach (var attackerId in attackers.Keys)
        {
            var computed = Characteristics.Of(State, _abilities, State.GetObject(attackerId));
            if (!computed.Has(KeywordAbility.Vigilance) && State.GetObject(attackerId).Permanent?.IsTapped == false)
                Emit(new PermanentTapped(attackerId));
        }

        // CR 702.154a: enlist's tap is part of attacking (CR 508.1g), so it is offered here -
        // after the attackers are declared and tapped, and before anyone has priority.
        foreach (var attackerId in attackers.Keys)
        {
            if (Characteristics.Of(State, _abilities, State.GetObject(attackerId))
                .Has(KeywordAbility.Enlist))
            {
                _enlistsOwed.Add(attackerId);
            }
        }

        SettleBeforePriority();
        if (State.IsOver)
            return;

        // CR 508.2: then the active player gets priority.
        Emit(new PriorityGranted(State.ActivePlayerId));
    }

    /// <summary>
    /// Declares blockers (CR 509.1), each attacker mapped to the creatures blocking it in the
    /// order their damage will be assigned (CR 510.1c).
    /// </summary>
    public void DeclareBlockers(
        Guid playerId, IReadOnlyDictionary<ObjectId, IReadOnlyList<ObjectId>> blocks)
    {
        ArgumentNullException.ThrowIfNull(blocks);

        if (State.CurrentStep != TurnStep.DeclareBlockers)
            throw new InvalidOperationException("Blockers are declared in the declare blockers step.");

        if (State.Combat.BlockersDeclared)
            throw new InvalidOperationException("Blockers have already been declared this combat.");

        foreach (var (attackerId, blockers) in blocks)
        {
            if (!State.Combat.Attackers.TryGetValue(attackerId, out var target))
                throw new InvalidOperationException("That creature is not attacking.");

            // A creature attacking a planeswalker is blocked by that planeswalker's controller
            // (CR 509.1a), which is the same player either way.
            if (target.DefendingPlayer != playerId)
                throw new InvalidOperationException("Only the defending player declares blockers (CR 509.1).");

            foreach (var blockerId in blockers)
            {
                var reason = CombatRules.CannotBlock(
                    State, _abilities, State.GetObject(blockerId), State.GetObject(attackerId), playerId);
                if (reason is not null)
                    throw new InvalidOperationException($"That creature cannot block: {reason}.");
            }
        }

        var illegal = CombatRules.IllegalBlockSet(
            State,
            _abilities,
            blocks.ToDictionary(kv => kv.Key, kv => new ImmutableListOfBlockers(kv.Value)));
        if (illegal is not null)
            throw new InvalidOperationException($"Illegal blocks: {illegal}.");

        // CR 509.1d/f: the other half of the same rule, charged to the declaring player and
        // refused whole for the same reason. The two are separate calls rather than one shared
        // one because the two declarations are made by different players at different moments,
        // and a tax that reached the wrong one of them would be free every other turn.
        var toll = CombatTaxes.ToBlock(State, _abilities, blocks.Values.SelectMany(b => b));
        if (!toll.Symbols.IsEmpty)
        {
            if (!ManaPayment.CanPay(State.GetPlayer(playerId).ManaPool, toll))
            {
                throw new InvalidOperationException(
                    $"Blocking costs {toll} and you have {State.GetPlayer(playerId).ManaPool} "
                        + "(CR 509.1d).");
            }

            PayMana(playerId, toll);
        }

        Emit(new BlockersDeclared(
            blocks.ToImmutableDictionary(kv => kv.Key, kv => kv.Value.ToImmutableList())));

        SettleBeforePriority();
        if (State.IsOver)
            return;

        // CR 509.2: then the active player gets priority.
        Emit(new PriorityGranted(State.ActivePlayerId));
    }

    /// <summary>
    /// Answers the decision the game is waiting on (CR 103.5, 603.3b, 616.1, 704.5j).
    /// </summary>
    /// <param name="picks">
    /// The option ids chosen. For an ordering choice the order of this list is the answer.
    /// </param>
    public void Choose(Guid playerId, IReadOnlyList<string> picks)
    {
        ArgumentNullException.ThrowIfNull(picks);

        var choice = State.Choice
            ?? throw new InvalidOperationException("The game is not waiting on a decision.");

        if (choice.PlayerId != playerId)
            throw new InvalidOperationException("That decision is not yours to make.");

        if (picks.Count < choice.MinPicks || picks.Count > choice.MaxPicks)
        {
            throw new InvalidOperationException(
                $"Pick between {choice.MinPicks} and {choice.MaxPicks}; got {picks.Count}.");
        }

        var legal = choice.Options.Select(o => o.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var pick in picks)
        {
            if (!legal.Contains(pick))
                throw new InvalidOperationException($"'{pick}' is not one of the options.");
        }

        // A division answers with one pick per point of damage, so the same blocker appearing
        // three times is how "three damage to it" is said. Everywhere else a repeat is a
        // mistake — an ordering cannot put the same trigger in two places.
        if (!choice.IsDivision && picks.Distinct(StringComparer.Ordinal).Count() != picks.Count)
            throw new InvalidOperationException("The same option was picked twice.");

        // Everything the answer has to satisfy is checked before the event is emitted. Emitting
        // first and validating during the resumption clears the pending choice and *then*
        // throws, which leaves the game with no question outstanding and no way to move — the
        // one failure worse than refusing the answer.
        // Two kinds of division and two different rules. Combat's is CR 510.1c - nothing may
        // be assigned more than lethal while another blocker has none. An announced division's
        // is CR 601.2d - every target the spell or ability chose has to be given at least one.
        // Sharing an arm here asked the combat question of an ability and threw looking for an
        // attacker that does not exist.
        if (choice.Kind == ChoiceKind.DivideCombatDamage)
            RequireLegalDivision(DivisionAttacker(choice), DivisionAmounts(choice, picks));
        else if (choice.Kind == ChoiceKind.DivideAmongTargets)
            RequireAnnouncedDivision(choice, picks);

        Emit(new ChoiceMade(choice.Id, [.. picks]));
        Resume(choice, picks);
    }

    /// <summary>Stops the game and asks (CR 103.5 and friends).</summary>
    /// <remarks>
    /// Stamps the choice with whoever was about to receive priority, so answering it hands the
    /// game back to the right player rather than to whoever usually acts.
    /// </remarks>
    private void Ask(PendingChoice choice) =>
        Emit(new ChoiceRequested(choice with { ResumePriorityTo = _priorityRecipient }));

    /// <summary>Who would receive priority once the current settle finishes (CR 117.5).</summary>
    private Guid? _priorityRecipient;

    /// <summary>
    /// Picks up whatever was interrupted by the question.
    /// </summary>
    /// <remarks>
    /// One explicit branch per kind rather than a captured continuation, because a continuation
    /// cannot be folded from a log — and a game that is mid-question has to replay as a game
    /// that is mid-question.
    /// </remarks>
    private void Resume(PendingChoice choice, IReadOnlyList<string> picks)
    {
        switch (choice.Kind)
        {
            case ChoiceKind.Mulligan:
                ResolveMulliganDeclaration(choice.PlayerId, picks[0]);
                break;

            case ChoiceKind.BottomAfterMulligan:
                BottomAfterMulligan(choice.PlayerId, picks);
                break;

            case ChoiceKind.OpeningHandBattlefield:
                BeginWithOnBattlefield(choice.PlayerId, picks);
                break;

            case ChoiceKind.LegendRule:
                KeepLegend(choice, picks[0]);
                break;

            case ChoiceKind.OrderTriggers:
                _triggerOrder[choice.PlayerId] = [.. picks];
                _priorityRecipient = choice.ResumePriorityTo;
                SettleBeforePriority();
                GrantPriorityAfterSettle(choice.ResumePriorityTo);
                break;

            case ChoiceKind.OrderReplacements:
                _replacementOrder = picks[0];
                ReplayHeldEvent();
                break;

            case ChoiceKind.DivideAmongTargets:
                RecordAnnouncedDivision(choice, picks);
                break;

            case ChoiceKind.DivideCombatDamage:
                RecordDamageDivision(choice, picks);
                break;

            case ChoiceKind.DiscardToHandSize:
                DiscardChosen(choice.PlayerId, picks);
                break;

            case ChoiceKind.OptionalPayment:
                ResolveOptionalPayment(picks);
                _priorityRecipient = choice.ResumePriorityTo;
                SettleBeforePriority();
                GrantPriorityAfterSettle(choice.ResumePriorityTo);
                break;

            case ChoiceKind.ChooseOptionalUntaps:
                UntapTheChosen(picks);
                _priorityRecipient = choice.ResumePriorityTo;
                SettleBeforePriority();
                GrantPriorityAfterSettle(choice.ResumePriorityTo);
                break;

            case ChoiceKind.NameCharacteristic:
                // Two events for one question, because the two answers are different things:
                // a colour and a creature type are characteristics, a card name is not one.
                // Which was asked is read back off the card here rather than carried on the
                // choice, because the card is what a replay reaches again.
                if (_entryChoiceBeingAsked is { } naming && picks.Count > 0)
                {
                    Emit(_abilities.ChoosesOnEntry(State.GetObject(naming).Card)
                            == ChoiceOnEntry.CardName
                        ? new NameChosen(naming, picks[0])
                        : new CharacteristicChosen(naming, picks[0]));
                }

                _entryChoiceBeingAsked = null;

                // The third question this kind now carries: a card name asked from a spell that
                // is resolving rather than from a permanent that is arriving. The two cannot both
                // be outstanding - a settle asks one question and stops - so which is being
                // answered is decided by which slot is filled, not by a word on the choice.
                ResolveCardNameChoice(picks);
                _priorityRecipient = choice.ResumePriorityTo;
                SettleBeforePriority();
                GrantPriorityAfterSettle(choice.ResumePriorityTo);
                break;

            case ChoiceKind.OrderLibraryTop:
                ResolveLibraryOrder(picks);
                break;

            case ChoiceKind.ChooseForCounters:
                ResolveCounterChoice(picks);
                break;

            case ChoiceKind.ChooseUntaps:
                UntapTheChosen(picks);
                break;

            case ChoiceKind.RingBearer:
                ResolveRingBearer(picks);
                _priorityRecipient = choice.ResumePriorityTo;
                SettleBeforePriority();
                GrantPriorityAfterSettle(choice.ResumePriorityTo);
                break;

            case ChoiceKind.VentureRoom:
                ResolveVentureRoom(picks);
                _priorityRecipient = choice.ResumePriorityTo;
                SettleBeforePriority();
                GrantPriorityAfterSettle(choice.ResumePriorityTo);
                break;

            case ChoiceKind.ClashKeepOnTop:
                ResolveClashDecision(picks);
                _priorityRecipient = choice.ResumePriorityTo;
                SettleBeforePriority();
                GrantPriorityAfterSettle(choice.ResumePriorityTo);
                break;

            case ChoiceKind.ChooseCreatureType:
                ResolveCreatureTypeChoice(picks);
                break;

            case ChoiceKind.ChooseBasicLandType:
                ResolveLandTypeChoice(picks);
                break;

            case ChoiceKind.ChooseColor:
                ResolveColorChoice(picks);
                break;

            // Unlike the colour above, this one is asked mid-resolution and has to hand
            // priority back to whoever was about to receive it (CR 117.5) - the mana is for
            // a spell that player was in the middle of paying for.
            case ChoiceKind.ChooseManaColor:
                ResolveManaColorChoice(picks);
                _priorityRecipient = choice.ResumePriorityTo;
                SettleBeforePriority();
                GrantPriorityAfterSettle(choice.ResumePriorityTo);
                break;

            case ChoiceKind.ChooseDamageSource:
                ResolveDamageSourceChoice(picks);
                _priorityRecipient = choice.ResumePriorityTo;
                SettleBeforePriority();
                GrantPriorityAfterSettle(choice.ResumePriorityTo);
                break;

            case ChoiceKind.Exploit:
                ResolveExploit(picks);
                _priorityRecipient = choice.ResumePriorityTo;
                SettleBeforePriority();
                GrantPriorityAfterSettle(choice.ResumePriorityTo);
                break;

            case ChoiceKind.Soulbond:
                ResolveSoulbond(picks);
                _priorityRecipient = choice.ResumePriorityTo;
                SettleBeforePriority();
                GrantPriorityAfterSettle(choice.ResumePriorityTo);
                break;

            case ChoiceKind.Populate:
                ResolvePopulate(picks);
                _priorityRecipient = choice.ResumePriorityTo;
                SettleBeforePriority();
                GrantPriorityAfterSettle(choice.ResumePriorityTo);
                break;

            case ChoiceKind.ManifestDread:
                ResolveManifestDread(picks);
                _priorityRecipient = choice.ResumePriorityTo;
                SettleBeforePriority();
                GrantPriorityAfterSettle(choice.ResumePriorityTo);
                break;

            case ChoiceKind.TakeFromTouched:
                ResolveTouchedChoice(picks);
                _priorityRecipient = choice.ResumePriorityTo;
                SettleBeforePriority();
                GrantPriorityAfterSettle(choice.ResumePriorityTo);
                break;

            case ChoiceKind.Connive:
                ResolveConnive(picks);
                _priorityRecipient = choice.ResumePriorityTo;
                SettleBeforePriority();
                GrantPriorityAfterSettle(choice.ResumePriorityTo);
                break;

            case ChoiceKind.ChooseCardInHand:
                ResolveHandChoice(picks);
                _priorityRecipient = choice.ResumePriorityTo;
                SettleBeforePriority();
                GrantPriorityAfterSettle(choice.ResumePriorityTo);
                break;

            case ChoiceKind.Enlist:
                ResolveEnlist(picks);
                _priorityRecipient = choice.ResumePriorityTo;
                SettleBeforePriority();
                GrantPriorityAfterSettle(choice.ResumePriorityTo);
                break;

            case ChoiceKind.ChoosePermanent:
                ResolvePermanentChoice(picks);
                _priorityRecipient = choice.ResumePriorityTo;
                SettleBeforePriority();
                GrantPriorityAfterSettle(choice.ResumePriorityTo);
                break;

            case ChoiceKind.Proliferate:
                ResolveProliferate(picks);
                _priorityRecipient = choice.ResumePriorityTo;
                SettleBeforePriority();
                GrantPriorityAfterSettle(choice.ResumePriorityTo);
                break;

            case ChoiceKind.SearchLibrary:
                ResolveSearch(picks);
                _priorityRecipient = choice.ResumePriorityTo;
                SettleBeforePriority();
                GrantPriorityAfterSettle(choice.ResumePriorityTo);
                break;

            case ChoiceKind.AssignAsThoughUnblocked:
                ResolveAssignAsThoughUnblocked(choice, picks);
                _priorityRecipient = choice.ResumePriorityTo;
                SettleBeforePriority();
                GrantPriorityAfterSettle(choice.ResumePriorityTo);
                break;

            case ChoiceKind.LibraryEnd:
                ResolveLibraryEnd(picks);
                _priorityRecipient = choice.ResumePriorityTo;
                SettleBeforePriority();
                GrantPriorityAfterSettle(choice.ResumePriorityTo);
                break;

            case ChoiceKind.EncodeOnCreature:
                ResolveEncoding(picks);
                _priorityRecipient = choice.ResumePriorityTo;
                SettleBeforePriority();
                GrantPriorityAfterSettle(choice.ResumePriorityTo);
                break;

            case ChoiceKind.Devour:
                ResolveDevour(picks);
                _priorityRecipient = choice.ResumePriorityTo;
                SettleBeforePriority();
                GrantPriorityAfterSettle(choice.ResumePriorityTo);
                break;

            case ChoiceKind.ChooseProtector:
                ResolveProtector(choice, picks);
                _priorityRecipient = choice.ResumePriorityTo;
                SettleBeforePriority();
                GrantPriorityAfterSettle(choice.ResumePriorityTo);
                break;

            case ChoiceKind.Amplify:
                ResolveAmplify(picks);
                _priorityRecipient = choice.ResumePriorityTo;
                SettleBeforePriority();
                GrantPriorityAfterSettle(choice.ResumePriorityTo);
                break;

            case ChoiceKind.ReadAhead:
                ResolveReadAhead(picks);
                _priorityRecipient = choice.ResumePriorityTo;
                SettleBeforePriority();
                GrantPriorityAfterSettle(choice.ResumePriorityTo);
                break;

            case ChoiceKind.Ripple:
                ResolveRipple(picks);
                _priorityRecipient = choice.ResumePriorityTo;
                SettleBeforePriority();
                GrantPriorityAfterSettle(choice.ResumePriorityTo);
                break;

            case ChoiceKind.PayOrSacrifice:
                ResolveSacrificeUnless(picks);
                _priorityRecipient = choice.ResumePriorityTo;
                SettleBeforePriority();
                GrantPriorityAfterSettle(choice.ResumePriorityTo);
                break;

            case ChoiceKind.LookAndTake:
                ResolveLookAndTake(picks);
                _priorityRecipient = choice.ResumePriorityTo;
                SettleBeforePriority();
                GrantPriorityAfterSettle(choice.ResumePriorityTo);
                break;

            case ChoiceKind.DiscardToEffect:
                ResolveDiscard(choice, picks);
                _priorityRecipient = choice.ResumePriorityTo;
                SettleBeforePriority();
                GrantPriorityAfterSettle(choice.ResumePriorityTo);
                break;

            case ChoiceKind.Scry:
            case ChoiceKind.Surveil:
                ResolveLook(choice, picks);
                _priorityRecipient = choice.ResumePriorityTo;
                SettleBeforePriority();
                GrantPriorityAfterSettle(choice.ResumePriorityTo);
                break;

            case ChoiceKind.ChooseTriggerMode:
                RecordTriggerMode(choice, picks[0]);
                _priorityRecipient = choice.ResumePriorityTo;
                SettleBeforePriority();
                GrantPriorityAfterSettle(choice.ResumePriorityTo);
                break;

            case ChoiceKind.ChooseTriggerTargets:
                RecordTriggerTarget(choice, picks[0]);
                _priorityRecipient = choice.ResumePriorityTo;
                SettleBeforePriority();
                GrantPriorityAfterSettle(choice.ResumePriorityTo);
                break;

            default:
                throw new InvalidOperationException($"No resumption for {choice.Kind}.");
        }
    }

    /// <summary>
    /// Gives priority back once a settle that was interrupted has finished (CR 117.5).
    /// </summary>
    private void GrantPriorityAfterSettle(Guid? recipient)
    {
        if (State.IsOver || State.IsWaitingForChoice)
            return;

        Emit(new PriorityGranted(recipient ?? State.ActivePlayerId));
    }

    /// <summary>Names the shield counter's replacement, so it is applied at most once per event.</summary>
    private const string ShieldCounterKey = "shield-counter";

    /// <summary>Identifies one waiting trigger: which object, and which of its abilities.</summary>
    private static string TriggerKey(PendingTrigger trigger) =>
        trigger.SourceId.Value.ToString("N") + "|" + trigger.AbilityId;

    /// <summary>Orders a player's triggers, until they have answered (CR 603.3b).</summary>
    private readonly Dictionary<Guid, List<string>> _triggerOrder = [];

    /// <summary>One permanent tapped to help cast a spell, and the symbol it pays.</summary>
    private readonly record struct Helper(ObjectId Id, ManaColor? Color);

    /// <summary>
    /// Checks the permanents offered to help cast a spell, and works out what each one pays.
    /// </summary>
    /// <remarks>
    /// Refuses the whole cast rather than tapping what it can: CR 601.2h pays the cost as one
    /// act, and a player who offered an illegal helper has spent nothing. A creature's colour is
    /// read from its computed characteristics, not its card, so a creature something turned white
    /// this turn can pay a {W} (CR 702.51a is about the creature's colour now).
    /// </remarks>
    private List<Helper> MatchTapToPay(
        TapToPay? spec, Guid playerId, ManaCostSpec cost, IReadOnlyList<ObjectId>? offered)
    {
        var helpers = new List<Helper>();
        if (spec is null || offered is null || offered.Count == 0)
            return helpers;

        foreach (var id in offered)
        {
            if (!State.TryGetObject(id, out var obj)
                || obj.Zone != Zone.Battlefield
                || ControllerOf(obj) != playerId)
            {
                throw new InvalidOperationException("You cannot tap that to help (CR 702.51a).");
            }

            if (obj.Permanent?.IsTapped != false)
                throw new InvalidOperationException("It is already tapped (CR 702.51a).");

            var computed = Characteristics.Of(State, _abilities, obj);
            if (spec.What.ObjectFilter?.Invoke(State, _abilities, obj, playerId) == false)
                throw new InvalidOperationException($"That is not {spec.What.Description}.");

            // A creature with no colour, or an artifact under improvise, can only pay generic.
            helpers.Add(new Helper(
                id,
                spec.ColorMatters ? FirstUsefulColour(computed, cost) : null));
        }

        return helpers;
    }

    /// <summary>
    /// The helper's colour that the cost actually asks for, or null if none of them is wanted.
    /// </summary>
    /// <remarks>
    /// A convoking creature may pay one mana of its own colour <em>or</em> {1} (CR 702.51a), so a
    /// green creature helping cast a spell with no green in it still pays — just generically.
    /// Returning null here is what makes it fall through to the generic half.
    /// </remarks>
    private static ManaColor? FirstUsefulColour(ComputedCharacteristics computed, ManaCostSpec cost)
    {
        foreach (var colour in computed.Colors)
        {
            if (cost.Symbols.Any(sym => sym.Colors.Contains(colour)))
                return colour;
        }

        return null;
    }

    /// <summary>
    /// Strikes off the symbols the tapped permanents pay (CR 702.51a).
    /// </summary>
    /// <remarks>
    /// A coloured helper pays its own colour where the cost still asks for one, and generic
    /// otherwise — which is the ordering that gets the most out of the same set of creatures, and
    /// is what a player convoking would do by hand.
    /// </remarks>
    /// <summary>
    /// Takes generic mana off a cost, never colour (CR 601.2f).
    /// </summary>
    /// <remarks>
    /// A cost cannot be reduced below nothing, and reducing past the generic it has is not an
    /// error — a one-mana spell with affinity for artifacts and six artifacts out is simply free.
    /// </remarks>
    private static ImmutableList<ManaSymbol> ReduceGeneric(
        ImmutableList<ManaSymbol> symbols, int amount)
    {
        var remaining = symbols.ToList();

        for (var i = 0; i < remaining.Count && amount > 0; i++)
        {
            var symbol = remaining[i];
            if (!symbol.Colors.IsEmpty || symbol.Generic <= 0)
                continue;

            var taken = Math.Min(amount, symbol.Generic);
            amount -= taken;
            remaining[i] = ManaSymbol.Generic0(symbol.Generic - taken);
        }

        return [.. remaining.Where(sym => !sym.Colors.IsEmpty || sym.Generic > 0 || sym.IsVariable)];
    }

    private static ImmutableList<ManaSymbol> Reduce(
        ImmutableList<ManaSymbol> symbols, IReadOnlyList<Helper> helpers)
    {
        var remaining = symbols.ToList();

        foreach (var helper in helpers)
        {
            var coloured = helper.Color is { } colour
                ? remaining.FindIndex(sym => sym.Colors.Contains(colour))
                : -1;

            if (coloured >= 0)
            {
                remaining.RemoveAt(coloured);
                continue;
            }

            var generic = remaining.FindIndex(sym => sym.Colors.IsEmpty && sym.Generic > 0);
            if (generic < 0)
                continue;

            var symbol = remaining[generic];
            if (symbol.Generic <= 1)
                remaining.RemoveAt(generic);
            else
                remaining[generic] = ManaSymbol.Generic0(symbol.Generic - 1);
        }

        return [.. remaining];
    }

    /// <summary>
    /// Works out which of the offered cards pays which cost, or refuses the whole activation.
    /// </summary>
    /// <remarks>
    /// The costs are matched in order and each consumes the cards it needs from the front of what
    /// was offered, so an ability with two chosen costs is unambiguous without the caller having
    /// to label anything. Every check happens before a single card moves (CR 601.2h): a player
    /// who offers an illegal payment has spent nothing.
    /// </remarks>
    /// <summary>
    /// Which price of a cost choice the caster is paying (CR 601.2b).
    /// </summary>
    /// <remarks>
    /// CR 601.2b says the choice is announced as the spell is cast, and the cast path already
    /// carries the announcement: the cards the caster offered to pay with. A caster who hands
    /// over a creature is sacrificing it; one who hands over nothing is paying the mana. Nothing
    /// new goes on the wire for this, which matters more than it looks - a parameter added to
    /// <c>GameHub.CastSpell</c> is a change every client has to make in the same commit, and this
    /// question is answerable without one.
    /// <para>
    /// The prices that ask for cards are tried first and in printed order, because the ones that
    /// do not are always available and would otherwise win every time: "pay {4} or sacrifice an
    /// artifact or creature" prints the mana first, and a caster offering a creature plainly
    /// means the second.
    /// </para>
    /// <para>
    /// Whether the <em>particular</em> card is a legal payment is not decided here. That is
    /// <see cref="RequirePayable"/>'s job when the payment is matched, and leaving it there is
    /// deliberate: a caster who offers the wrong permanent gets a refusal naming what was wrong
    /// with it, rather than silently having their spell charged the other price.
    /// </para>
    /// </remarks>
    private CostOption ChooseCostOption(
        CostChoice choice,
        Guid playerId,
        ObjectId cardId,
        IReadOnlyList<ObjectId>? offered,
        int consumed)
    {
        var left = (offered ?? []).Skip(consumed).ToList();

        foreach (var option in choice.Options)
        {
            if (!option.Chosen.IsEmpty && Fits(option, playerId, left))
                return option;
        }

        foreach (var option in choice.Options)
        {
            if (option.Chosen.IsEmpty)
                return option;
        }

        // Every price asks for cards and none of them was offered. CR 601.2h: an unpayable cost
        // cannot be paid, and the cast is refused with nothing spent.
        throw new InvalidOperationException(
            $"{State.GetObject(cardId).Card.Name} needs one of its additional costs paid "
                + "(CR 601.2b).");
    }

    /// <summary>Whether what the caster offered could be this price (CR 601.2b).</summary>
    /// <remarks>
    /// The zone is what separates the prices in practice - "sacrifice a creature or discard a
    /// card" is a battlefield permanent against a card in hand - and the filter is asked beside
    /// it so that a choice between two prices over the same zone still lands on the right one.
    /// </remarks>
    private bool Fits(CostOption option, Guid playerId, List<ObjectId> offered)
    {
        var i = 0;

        foreach (var cost in option.Chosen)
        {
            // The engine picks these, so nothing is offered for them (CR 701.9b).
            if (cost.Kind is ChosenCostKind.DiscardAtRandom)
                continue;

            if (PaidFrom(cost.Kind) is not { } zone)
                return false;

            for (var n = 0; n < cost.Count; n++, i++)
            {
                if (i >= offered.Count
                    || !State.TryGetObject(offered[i], out var card)
                    || card.Zone != zone)
                {
                    return false;
                }

                var mine = zone == Zone.Battlefield
                    ? ControllerOf(card) == playerId
                    : card.OwnerId == playerId;

                if (!mine)
                    return false;

                if (cost.What?.ObjectFilter?.Invoke(State, _abilities, card, playerId) == false)
                    return false;
            }
        }

        return true;
    }

    /// <summary>Where a chosen cost takes its cards from, or null when it takes none.</summary>
    private static Zone? PaidFrom(ChosenCostKind kind) => kind switch
    {
        ChosenCostKind.SacrificePermanents
            or ChosenCostKind.TapPermanents
            or ChosenCostKind.ReturnToHand => Zone.Battlefield,
        ChosenCostKind.DiscardCards or ChosenCostKind.ExileFromHand => Zone.Hand,
        ChosenCostKind.ExileFromGraveyard => Zone.Graveyard,
        _ => null,
    };

    /// <summary>How many offered cards a chosen cost speaks for.</summary>
    /// <remarks>
    /// Zero for the two kinds that take none: a random discard is the engine's pick (CR 701.9b),
    /// and crew takes as many as it needs to reach a total power (CR 702.122a) rather than a
    /// fixed count. Neither shares a card with a cost choice on any card in the corpus, and a
    /// count that guessed at crew would hand the choice the wrong slice of the payment.
    /// </remarks>
    private static int CardsAskedFor(ChosenCost cost) =>
        cost.Kind is ChosenCostKind.DiscardAtRandom || cost.MinTotalPower > 0 ? 0 : cost.Count;

    private List<(ChosenCost Cost, List<ObjectId> Cards)> MatchChosenCosts(
        ImmutableList<ChosenCost> costs,
        Guid playerId,
        ObjectId sourceId,
        IReadOnlyList<ObjectId>? offered)
    {
        var matched = new List<(ChosenCost, List<ObjectId>)>();
        if (costs.IsEmpty)
            return matched;

        var queue = new Queue<ObjectId>(offered ?? []);

        foreach (var cost in costs)
        {
            var taken = new List<ObjectId>();

            // At random is the one case that is not a choice, so the engine picks (CR 701.9b).
            if (cost.Kind is ChosenCostKind.DiscardAtRandom)
            {
                var hand = State.GetPlayer(playerId).Hand;
                if (hand.Count < cost.Count)
                    throw new InvalidOperationException("You have too few cards to discard (CR 601.2h).");

                taken.AddRange(_random.Shuffle(hand).Take(cost.Count));
                matched.Add((cost, taken));
                continue;
            }

            // Crew takes "any number ... with total power N or greater" (CR 702.122a), so the
            // count is not fixed and the requirement is on what the set adds up to.
            if (cost.MinTotalPower > 0)
            {
                var total = 0;
                while (total < cost.MinTotalPower && queue.TryDequeue(out var creature))
                {
                    RequirePayable(cost, playerId, sourceId, creature);
                    taken.Add(creature);
                    total += Characteristics.Of(State, _abilities, State.GetObject(creature)).Power ?? 0;
                }

                if (total < cost.MinTotalPower)
                {
                    throw new InvalidOperationException(
                        $"That is only {total} power; {cost.MinTotalPower} is needed (CR 702.122a).");
                }

                matched.Add((cost, taken));
                continue;
            }

            for (var i = 0; i < cost.Count; i++)
            {
                if (!queue.TryDequeue(out var card))
                {
                    throw new InvalidOperationException(
                        $"{State.GetObject(sourceId).Card.Name} needs {cost.Count} "
                            + "card(s) to pay its cost (CR 601.2f).");
                }

                RequirePayable(cost, playerId, sourceId, card);
                taken.Add(card);
            }

            matched.Add((cost, taken));
        }

        return matched;
    }

    /// <summary>Refuses a card that cannot pay the cost it was offered for.</summary>
    private void RequirePayable(ChosenCost cost, Guid playerId, ObjectId sourceId, ObjectId card)
    {
        if (!State.TryGetObject(card, out var obj))
            throw new InvalidOperationException("That card is not in the game.");

        if (cost.ExcludesSource && card == sourceId)
            throw new InvalidOperationException("That cost needs another permanent (CR 601.2f).");

        if (cost.Kind is ChosenCostKind.TapPermanents)
        {
            // CR 118.12: it has to be untapped and yours. Summoning sickness does not apply —
            // that rule is about a permanent's own {T} ability (CR 302.6), not about being tapped
            // to pay for someone else's, which is why a freshly cast creature can crew a Vehicle.
            if (obj.Zone != Zone.Battlefield || ControllerOf(obj) != playerId)
                throw new InvalidOperationException("You cannot tap that (CR 118.12).");

            if (obj.Permanent?.IsTapped != false)
                throw new InvalidOperationException("It is already tapped (CR 118.12).");

            if (cost.What?.ObjectFilter?.Invoke(State, _abilities, obj, playerId) == false)
                throw new InvalidOperationException($"That is not {cost.What.Description}.");

            return;
        }

        if (cost.Kind is ChosenCostKind.ExileFromGraveyard)
        {
            // CR 701.13a: from your own graveyard, and it has to answer whatever the card asked
            // for - "a creature card" is not "a card".
            if (obj.Zone != Zone.Graveyard || obj.OwnerId != playerId)
            {
                throw new InvalidOperationException(
                    "You cannot exile that from your graveyard (CR 701.13a).");
            }

            if (cost.What?.ObjectFilter?.Invoke(State, _abilities, obj, playerId) == false)
                throw new InvalidOperationException($"That is not {cost.What.Description}.");

            return;
        }

        if (cost.Kind is ChosenCostKind.ReturnToHand)
        {
            // CR 701.20a: it goes to its owner's hand, and it has to be yours to return.
            if (obj.Zone != Zone.Battlefield || ControllerOf(obj) != playerId)
                throw new InvalidOperationException("You cannot return that (CR 701.20a).");

            if (cost.What?.ObjectFilter?.Invoke(State, _abilities, obj, playerId) == false)
                throw new InvalidOperationException($"That is not {cost.What.Description}.");

            return;
        }

        if (cost.Kind is ChosenCostKind.SacrificePermanents)
        {
            // CR 701.21a: you can only sacrifice something you control, and only from the
            // battlefield. The spec carries whatever else the card asked for.
            if (obj.Zone != Zone.Battlefield || ControllerOf(obj) != playerId)
                throw new InvalidOperationException("You cannot sacrifice that (CR 701.21a).");

            if (cost.What?.ObjectFilter?.Invoke(State, _abilities, obj, playerId) == false)
                throw new InvalidOperationException($"That is not {cost.What.Description}.");

            return;
        }

        if (cost.Kind is ChosenCostKind.ExileFromHand)
        {
            // CR 701.13a: out of your own hand, and it has to answer what the card asked for -
            // "a blue card" is not "a card", and a pitch spell that took any card would be a
            // different and much better card.
            if (obj.Zone != Zone.Hand || obj.OwnerId != playerId)
            {
                throw new InvalidOperationException(
                    "You cannot exile that from your hand (CR 701.13a).");
            }

            if (cost.What?.ObjectFilter?.Invoke(State, _abilities, obj, playerId) == false)
                throw new InvalidOperationException($"That is not {cost.What.Description}.");

            return;
        }

        if (obj.Zone != Zone.Hand || obj.OwnerId != playerId)
            throw new InvalidOperationException("You cannot discard that (CR 701.9a).");

        // A discard cost can name what it wants — retrace's is a land card, not any card — and
        // until retrace there was nothing that did, so the filter was never consulted here.
        if (cost.What?.ObjectFilter?.Invoke(State, _abilities, obj, playerId) == false)
            throw new InvalidOperationException($"That is not {cost.What.Description}.");
    }

    /// <summary>
    /// Why a printed timing restriction forbids activating now, or null if it does not (CR 602.5d).
    /// </summary>
    /// <remarks>
    /// Returns the reason rather than a bool because it is thrown at the player, and "you can only
    /// do that as a sorcery" is an answer while "illegal" is a shrug. The board asks the same
    /// question through the view so it can grey the button out first; that copy is a courtesy and
    /// this one is the rule.
    /// </remarks>
    private string? WhyTimingForbids(ActivationTiming timing, Guid playerId) => timing switch
    {
        ActivationTiming.AnyTime => null,

        // CR 602.5d: "as a sorcery" is exactly the timing for casting one (CR 307.1) — your main
        // phase, your turn, and nothing else waiting to resolve.
        ActivationTiming.SorceryOnly when
            !State.CurrentStep.IsMainPhase() || !State.Stack.IsEmpty =>
            "That can only be activated any time you could cast a sorcery (CR 602.5d).",

        ActivationTiming.YourTurnOnly or ActivationTiming.SorceryOnly
            or ActivationTiming.BeforeAttackersDeclared or ActivationTiming.YourUpkeepOnly
            when State.ActivePlayerId != playerId =>
            "That can only be activated during your turn (CR 602.5d).",

        ActivationTiming.BeforeAttackersDeclared when
            State.CurrentStep > TurnStep.DeclareAttackers || State.Combat.AttackersDeclared =>
            "Attackers have already been declared (CR 602.5d).",

        ActivationTiming.CombatOnly when State.CurrentStep.PhaseOf() != Phase.Combat =>
            "That can only be activated during combat (CR 602.5d).",

        ActivationTiming.YourUpkeepOnly when State.CurrentStep != TurnStep.Upkeep =>
            "That can only be activated during your upkeep (CR 602.5d).",

        _ => null,
    };

    /// <summary>
    /// How often each limited ability has been activated this turn (CR 602.5b).
    /// </summary>
    /// <remarks>
    /// Not folded from the log, because it is not part of the game state a replay has to
    /// reproduce — it is a bookkeeping detail of the turn, cleared when the turn ends. A game
    /// rebuilt from its log replays the activations themselves, which is what matters.
    /// </remarks>
    private readonly Dictionary<(ObjectId, string), int> _activationsThisTurn = [];

    /// <summary>
    /// Planeswalkers that have already used a loyalty ability this turn (CR 606.3).
    /// </summary>
    /// <remarks>
    /// Keyed by the permanent rather than by the ability, because the rule is one loyalty ability
    /// per planeswalker per turn — not one activation of each. Cleared with the turn, like the
    /// activation counts beside it.
    /// </remarks>
    private readonly HashSet<ObjectId> _loyaltyUsedThisTurn = [];

    /// <summary>Once-per-turn triggers that have already fired this turn (CR 603.1).</summary>
    private readonly HashSet<(ObjectId, string)> _triggeredThisTurn = [];

    /// <summary>Targets picked so far for one waiting trigger (CR 603.3d).</summary>
    private readonly Dictionary<string, List<Target>> _triggerTargets = [];

    /// <summary>Modes picked for a trigger still being put on the stack (CR 603.3c).</summary>
    private readonly Dictionary<string, List<int>> _triggerModes = [];

    /// <summary>
    /// Granted abilities that are on the stack, by source and ability id (CR 613.1f).
    /// </summary>
    /// <remarks>
    /// An ability goes on the stack as an <em>id</em>, and what the id means is looked up again
    /// when it resolves - from the card of the permanent it came from. A granted ability is not
    /// on that card. It is on whatever gave it, so the lookup found nothing and the ability
    /// resolved as no effects at all: the creature tapped, the cost was paid, and the card was
    /// never drawn.
    /// <para>
    /// Nothing had noticed because every granted ability in the corpus until now was a mana
    /// ability, and a mana ability never uses the stack (CR 605.3b) - it resolves out of the
    /// definition that was just found, so the second lookup never happened.
    /// </para>
    /// <para>
    /// Kept here rather than in the state for the same reason the modal choices above are: the
    /// state is a fold of the event log and an ability definition is code, which no log can
    /// carry. What that costs is a granted ability still on the stack across a reload, which is
    /// the same thing it costs for a half-chosen modal trigger.
    /// </para>
    /// <para>
    /// It also answers the case the source cannot: CR 608.2 resolves an ability whose source has
    /// left the battlefield, and a granted ability read back off the source would be gone.
    /// </para>
    /// </remarks>
    private readonly Dictionary<string, ActivatedAbilityDefinition> _grantedOnStack =
        new(StringComparer.Ordinal);

    /// <summary>Granted triggered abilities, by the object they were granted to.</summary>
    private readonly Dictionary<string, TriggeredAbilityDefinition> _grantedTriggersOnStack =
        new(StringComparer.Ordinal);

    private static string GrantedKey(ObjectId sourceId, string abilityId) =>
        sourceId.Value.ToString("N") + ":" + abilityId;

    /// <summary>
    /// A scry or surveil owed to a player, asked at the next settle (CR 701.22, 701.25).
    /// </summary>
    /// <remarks>
    /// The question cannot be asked where it arises. Effects return events; asking halts the
    /// whole game, and resuming would have to continue a resolution from the middle — which the
    /// engine deliberately cannot do, because a continuation is not something a log can rebuild.
    /// Scry is almost always the last thing its ability does, so deferring the question to just
    /// after the resolution is the same game in every case a card can currently produce.
    /// </remarks>
    private readonly List<LookAtTopRequested> _looksOwed = [];

    /// <summary>
    /// Spells to be exiled rather than put anywhere else when they leave the stack (CR 702.34a).
    /// </summary>
    /// <remarks>
    /// Keyed by the stack object, which exists only for as long as the spell does — so the set
    /// cannot leak, and a card cast a second time by some other means is unaffected by what
    /// happened to an earlier casting.
    /// </remarks>
    private readonly HashSet<ObjectId> _exileOnLeavingStack = [];

    /// <summary>How many time counters a self-suspending spell asked for, by its stack id.</summary>
    private readonly Dictionary<ObjectId, int> _suspendOnResolve = [];

    /// <summary>Who may encode a ciphered spell, by the stack id it had while resolving.</summary>
    private readonly Dictionary<ObjectId, Guid> _cipherOnResolve = [];

    /// <summary>Encodings still to be asked about, oldest first.</summary>
    private readonly List<(ObjectId CardId, Guid PlayerId)> _encodingsOwed = [];

    /// <summary>
    /// Which of a card's spells a stack object actually is (CR 715.3b, 709.4).
    /// </summary>
    /// <remarks>
    /// A card can carry more than one spell - an Adventure beside a creature, two halves of a
    /// split card - and only one of them is on the stack. Everything that resolves a spell looks
    /// its effects up from the card, and the card is all of them at once, so which one was chosen
    /// has to be recorded when it is chosen or the wrong half resolves.
    /// <para>
    /// It carries what is on the stack rather than just the effects, because two other decisions
    /// depend on it: whether this resolves into a permanent, and where the card goes afterwards.
    /// Both are asked of the <em>spell</em>, not of the card - an Adventure on a creature card is
    /// a sorcery and goes to exile.
    /// </para>
    /// <para>
    /// Beside the state rather than in it, as the granted abilities are, and for the same
    /// reason: the state is a fold of the event log and a spell definition is code.
    /// </para>
    /// </remarks>
    private readonly Dictionary<ObjectId, CastAs> _castAs = [];

    /// <summary>Which face of a split card is on the stack, by stack object (CR 709.5d).</summary>
    /// <remarks>
    /// Kept apart from <see cref="_castAs"/>, which holds what the spell <em>is</em>. This is
    /// which half it was, and only a Room needs it - the door that opens when it enters is the
    /// one that was paid for.
    /// </remarks>
    private readonly Dictionary<ObjectId, int> _halfCast = [];

    /// <summary>One of a card's several spells, as it sits on the stack.</summary>
    private readonly record struct CastAs(
        SpellDefinition? Spell,
        bool IsPermanent,
        bool ExileOnResolve,

        // Exiling and going on an adventure are not the same thing, and conflating them made an
        // aftermath half castable out of exile for the rest of the game: both are exiled as they
        // resolve, and only one of them may be played from there (CR 715.3d against CR 702.127a).
        bool OnAdventure = false);

    /// <summary>
    /// Optional payments offered, asked at the next settle (CR 601.2b), each with the offer it
    /// came from.
    /// </summary>
    /// <remarks>
    /// The offer is captured when the request is <em>made</em> rather than looked up when it is
    /// asked, because a spell does not survive its own resolution: it goes to the graveyard and
    /// becomes a new object (CR 400.7), so by the time the question is put the locator points at
    /// nothing. A permanent's ability is fine either way, which is exactly why this went
    /// unnoticed until the first spell-sourced offer — the counterspell tax.
    /// </remarks>
    private readonly List<(
        OptionalPaymentRequested Request,
        MayPay Offer,
        ImmutableList<Target> Targets)> _paymentsOwed = [];

    /// <summary>Look-and-take offers owed to players, asked at the next settle (CR 701.20a).</summary>
    private readonly List<LookAndTakeRequested> _looksAndTakesOwed = [];

    /// <summary>Library searches owed to players, asked at the next settle (CR 701.23).</summary>
    private readonly List<LibrarySearchRequested> _searchesOwed = [];

    /// <summary>Seeks owed, performed at the next settle.</summary>
    /// <remarks>
    /// A list rather than a single field for the same reason the searches are one: a card can
    /// seek twice in a sentence, and a resolution is never stopped part of the way through.
    /// </remarks>
    private readonly List<SeekRequested> _seeksOwed = [];

    private readonly List<SacrificeUnlessPaidRequested> _sacrificeUnlessOwed = [];

    private readonly List<LibraryEndChoiceRequested> _libraryEndsOwed = [];

    /// <summary>Miracle offers to make once the drawn card has landed in hand.</summary>
    private readonly List<(ObjectId Id, Guid PlayerId, string Cost)> _miraclesOwed = [];

    /// <summary>Proliferations owed to players, asked at the next settle (CR 701.34a).</summary>
    private readonly List<ProliferateRequested> _proliferationsOwed = [];

    /// <summary>
    /// Permanent choices owed to players, asked at the next settle (CR 609.4), each with the
    /// effect that asked.
    /// </summary>
    /// <remarks>
    /// Captured with the request for the same reason the optional payment is: a spell does not
    /// survive its own resolution (CR 400.7), so a locator resolved at ask time points at nothing
    /// when the source was a sorcery rather than a permanent's ability.
    /// </remarks>
    private readonly List<(ChoosePermanentRequested Request, ChooseAndMove Effect)> _choicesOwed = [];

    /// <summary>Shuffles owed, performed at the next settle (CR 701.24a).</summary>
    private readonly List<ShuffleRequested> _shufflesOwed = [];

    /// <summary>Cascades owed, performed at the next settle (CR 702.85a).</summary>
    private readonly List<CascadeRequested> _cascadesOwed = [];

    private readonly List<RippleRequested> _ripplesOwed = [];

    private RippleRequested? _rippleBeingAsked;
    private readonly List<DiscoverRequested> _discoveriesOwed = [];

    /// <summary>Hand choices owed, asked at the next settle (CR 701.16).</summary>
    private readonly List<HandChoiceRequested> _handChoicesOwed = [];

    private readonly List<ColorChoiceRequested> _colorChoicesOwed = [];

    /// <summary>Card names a resolving spell has asked for and not yet been given (CR 201.4).</summary>
    private readonly List<CardNameChoiceRequested> _cardNameChoicesOwed = [];

    /// <summary>Mana colours an effect has asked for and not yet been given (CR 106.1a).</summary>
    private readonly List<ManaColorChoiceRequested> _manaColorChoicesOwed = [];

    /// <summary>Shields waiting to be told which source they name (CR 609.7b).</summary>
    private readonly List<DamageSourceChoiceRequested> _damageSourceChoicesOwed = [];
    private readonly List<CreatureTypeChoiceRequested> _creatureTypeChoicesOwed = [];

    /// <summary>Land types owed, asked at the next settle (CR 305.7).</summary>
    private readonly List<LandTypeChoiceRequested> _landTypeChoicesOwed = [];
    private readonly List<ConniveRequested> _connivesOwed = [];
    private readonly List<ManifestDreadRequested> _manifestDreadsOwed = [];

    /// <summary>Picks from what a resolution touched, owed until the next settle (CR 608.2c).</summary>
    private readonly List<TouchedChoiceRequested> _touchedChoicesOwed = [];
    private readonly List<PopulateRequested> _populatesOwed = [];
    private readonly List<ExploitRequested> _exploitsOwed = [];

    /// <summary>Soulbond pairings owed, asked at the next settle (CR 702.95a).</summary>
    private readonly List<SoulbondPairRequested> _soulbondsOwed = [];

    private readonly List<UntapChoiceRequested> _untapChoicesOwed = [];

    private readonly List<CounterChoiceRequested> _counterChoicesOwed = [];

    private readonly List<LibraryOrderRequested> _libraryOrdersOwed = [];

    /// <summary>
    /// Coin flips owed, with the effect and targets they belong to (CR 705.2).
    /// </summary>
    /// <remarks>
    /// Captured eagerly for the same reason every other deferred question is: the flip happens
    /// after the resolution that called for it, and a spell does not survive its own resolution.
    /// </remarks>
    private readonly List<(CoinFlipRequested Request, FlipCoin Effect, ImmutableList<Target> Targets)>
        _flipsOwed = [];

    /// <summary>Die rolls owed, with the effect and targets they belong to (CR 706.1).</summary>
    /// <remarks>The coin flip's list, for the same reasons.</remarks>
    private readonly List<(DiceRollRequested Request, RollDice Effect, ImmutableList<Target> Targets)>
        _rollsOwed = [];

    private readonly List<(ClashRequested Request, Clash Effect, ImmutableList<Target> Targets)>
        _clashesOwed = [];

    private readonly List<RingBearerRequested> _ringBearersOwed = [];

    /// <summary>Discards owed to players, asked at the next settle (CR 701.9).</summary>
    /// <remarks>
    /// Deferred for the same reason as <see cref="_looksOwed"/>. It is a separate list rather than
    /// one queue of questions because the two are asked differently and answered differently, and
    /// a single list would need a tag saying which — which is the type it already has.
    /// </remarks>
    private readonly List<DiscardRequested> _discardsOwed = [];

    /// <summary>Asks the oldest owed look, if any (CR 701.22).</summary>
    private bool AskOwedLook()
    {
        if (_looksOwed.Count == 0 || State.IsWaitingForChoice)
            return false;

        var owed = _looksOwed[0];
        _looksOwed.RemoveAt(0);

        var top = State.GetPlayer(owed.PlayerId).Library.Take(owed.Count).ToList();
        if (top.Count == 0)
            return false;

        Ask(new PendingChoice
        {
            Id = "look:" + owed.PlayerId.ToString("N"),
            PlayerId = owed.PlayerId,
            Kind = owed.ToGraveyard ? ChoiceKind.Surveil : ChoiceKind.Scry,
            Prompt = owed.ToGraveyard
                ? "Choose any number to put into your graveyard; the rest stay on top."
                : "Choose any number to put on the bottom of your library; the rest stay on top.",
            Options = [.. top.Select(id => new ChoiceOption(
                id.Value.ToString("N"), State.GetObject(id).Card.Name))],
            MinPicks = 0,
            MaxPicks = top.Count,
        });

        return true;
    }

    /// <summary>
    /// What an offer actually charges, which is not always what the card prints (CR 702.24a).
    /// </summary>
    /// <remarks>
    /// The locator reads the effect back out of the compiled card, and a card that charges once
    /// per counter prints one cost and asks for several. The number was worked out when the offer
    /// was made and travelled on the event as text, which is also the only place it can survive a
    /// replay — so the event is the authority and the compiled effect is the fallback for every
    /// offer that never scaled at all.
    /// </remarks>
    private static ManaCostSpec CostOwed(OptionalPaymentRequested owed, MayPay offer) =>
        string.IsNullOrEmpty(owed.CostText) ? offer.Cost : ManaCostSpec.Parse(owed.CostText);

    /// <summary>
    /// How far the search below will go before it gives up and asks (CR 605.3a).
    /// </summary>
    /// <remarks>
    /// A board with many lands that each make several colours is the only shape that reaches
    /// this, and it is also the shape most able to pay. Running out is answered "ask", never
    /// "decline" - see the note on <see cref="CouldPayMidResolution"/> for why the two are not
    /// symmetrical.
    /// </remarks>
    private const int ManaSearchBudget = 4096;

    /// <summary>
    /// Whether a player asked for mana in the middle of a resolution could produce it
    /// (CR 605.3a, 118.3).
    /// </summary>
    /// <remarks>
    /// The pool is not the answer to "can you pay?" while an effect is asking. CR 605.3a lets a
    /// player activate mana abilities at exactly that moment, so a player holding three untapped
    /// Forests can pay a {3} they do not yet have - and answering for them is how "unless its
    /// controller pays {3}" became a hard counter against everyone who had not floated first.
    /// <para>
    /// <strong>Optimistic on purpose, and the direction is the point.</strong> A wrong "no" here
    /// is silent and unrecoverable: the player is never asked, and the "if you don't" branch runs
    /// as though they had refused a question nobody put. A wrong "yes" costs one question they
    /// answer no to, and <see cref="ResolveOptionalPayment"/> re-checks the real pool before
    /// charging it, so nothing is paid that was not there. Every approximation below therefore
    /// leans towards asking.
    /// </para>
    /// <para>
    /// Only abilities that are free to activate are counted. One that itself costs mana cannot
    /// conjure mana the player does not have, and chasing the ones that merely convert it is a
    /// fixpoint this does not need - the offers that print this shape ask for generic mana, and
    /// lands are what pays them.
    /// </para>
    /// </remarks>
    private bool CouldPayMidResolution(Guid playerId, ManaCostSpec cost)
    {
        var pool = State.GetPlayer(playerId).ManaPool;
        if (ManaPayment.CanPay(pool, cost))
            return true;

        var sources = ManaSourcesAvailableTo(playerId);
        if (sources.Count == 0)
            return false;

        var budget = ManaSearchBudget;
        return CanReachCost(pool, cost, sources, 0, ref budget);
    }

    /// <summary>
    /// One mana ability's payout, flattened to what it would put in the pool.
    /// </summary>
    /// <remarks>
    /// A restriction travels with the mana rather than being dropped, because the pool arithmetic
    /// that decides payability already knows about restricted mana - and "spend this mana only to
    /// cast creature spells" genuinely cannot pay a counterspell's tax. Dropping it would be the
    /// one approximation here that errs towards asking a question with no answer.
    /// </remarks>
    private readonly record struct ManaBundle(ImmutableList<ManaProduction> Produces)
    {
        public ManaPool AddedTo(ManaPool pool)
        {
            foreach (var production in Produces)
            {
                if (production.Restriction is { } restricted)
                {
                    pool = pool.AddRestricted(
                        new RestrictedMana(production.Color, restricted), production.Amount);
                }
                else if (production.Color is { } colour)
                {
                    pool = pool.Add(colour, production.Amount);
                }
                else
                {
                    pool = pool.AddColorless(production.Amount);
                }
            }

            return pool;
        }
    }

    /// <summary>
    /// What each of a player's permanents could add to their pool right now (CR 605.1a).
    /// </summary>
    /// <remarks>
    /// One entry per permanent, holding every payout that permanent offers, because a permanent
    /// with two mana abilities taps once and makes one of the two - counting both would let a
    /// single dual land pay a cost of two colours. Which of them it makes is the player's choice,
    /// so the caller searches over them rather than picking.
    /// <para>
    /// The abilities are the <em>computed</em> ones (CR 613.1): a granted mana ability counts and
    /// a face-down permanent has none, which is the same rule the mana-colour menu reads.
    /// </para>
    /// </remarks>
    private List<List<ManaBundle>> ManaSourcesAvailableTo(Guid playerId)
    {
        var sources = new List<List<ManaBundle>>();
        var player = State.GetPlayer(playerId);

        foreach (var id in State.Battlefield)
        {
            var obj = State.GetObject(id);
            if (obj.Permanent is not { } permanent || ControllerOf(obj) != playerId)
                continue;

            var computed = Characteristics.Of(State, _abilities, obj);

            // CR 602.5c: an effect can shut the abilities off outright, mana abilities included.
            if (computed.AbilitiesCantBeActivated)
                continue;

            var bundles = new List<ManaBundle>();

            foreach (var ability in ActivatedAbilitiesOf(State, _abilities, obj))
            {
                if (!ability.IsManaAbility || ability.FunctionsFrom != Zone.Battlefield)
                    continue;

                // Free to activate, in every currency. See the note above on why the ones that
                // cost mana are left out rather than chased.
                if (!ability.ManaCost.Symbols.IsEmpty
                    || ability.CounterCost is not null
                    || !ability.ChosenCosts.IsEmpty
                    || ability.SelfCost != SelfCost.None
                    || ability.LifeCost > player.Life
                    || ability.EnergyCost > player.Energy)
                {
                    continue;
                }

                // CR 602.5b, 302.6: a {T} cost needs an untapped permanent, and a creature that
                // has been around since the turn began or has haste.
                if (ability.RequiresTap
                    && (permanent.IsTapped
                        || (permanent.HasSummoningSickness
                            && computed.IsCreature
                            && !computed.Has(KeywordAbility.Haste))))
                {
                    continue;
                }

                if (ability.ActivateOnlyIf?.Invoke(State, _abilities, obj, null) == false)
                    continue;

                var produces = ImmutableList.CreateBuilder<ManaProduction>();

                foreach (var production in ability.Produces)
                {
                    // The counter-priced amount is a number the player names as they activate,
                    // and it is not known here. Counted as the one mana its floor guarantees
                    // rather than skipped, which keeps the lean towards asking.
                    var amount = production.FromCounterCost ? 1 : production.Amount;
                    if (amount <= 0)
                        continue;

                    if (!production.FromChosenColor)
                    {
                        produces.Add(production with { Amount = amount });
                        continue;
                    }

                    // A permanent that has not answered its colour question cannot tap for a
                    // colour it has not chosen - the same reading the activation itself takes.
                    if (ColorNamed(obj.Chosen) is { } named)
                        produces.Add(production with { Color = named, Amount = amount });
                }

                if (produces.Count > 0)
                    bundles.Add(new ManaBundle(produces.ToImmutable()));
            }

            if (bundles.Count > 0)
                sources.Add(bundles);
        }

        return sources;
    }

    /// <summary>
    /// Whether some choice of one payout per permanent pays the cost (CR 605.3a).
    /// </summary>
    /// <remarks>
    /// A search rather than a sum, because a dual land is a decision: {W}{U} is payable off two
    /// Hallowed Fountains and unpayable off one, and a "how much mana could you make" total says
    /// yes to both. Each permanent may be used once or left alone, and the cost is re-asked of
    /// the pool at every step so a search that has already found enough stops.
    /// </remarks>
    private static bool CanReachCost(
        ManaPool pool,
        ManaCostSpec cost,
        List<List<ManaBundle>> sources,
        int index,
        ref int budget)
    {
        if (ManaPayment.CanPay(pool, cost))
            return true;

        if (index >= sources.Count)
            return false;

        // Out of budget is answered "ask". A needless question costs a click; a needless decline
        // costs the card the rule it prints.
        if (--budget <= 0)
            return true;

        foreach (var bundle in sources[index])
        {
            if (CanReachCost(bundle.AddedTo(pool), cost, sources, index + 1, ref budget))
                return true;
        }

        return CanReachCost(pool, cost, sources, index + 1, ref budget);
    }

    /// <summary>
    /// How many objects a chosen cost takes, floored at one.
    /// </summary>
    /// <remarks>
    /// A cost of nothing would be paid by picking nothing, which is the same answer as declining
    /// — so a miscompiled offer would hand the player the "if you do" branch for free, and read
    /// as a card strictly better than the one printed. Asked in one place so the ask and the
    /// answer cannot come to different conclusions about what was owed.
    /// <para>
    /// Multiplied by what the offer was made for (CR 702.24a): cumulative upkeep's fifth turn
    /// asks for five, and the number comes off the event rather than off the board because the
    /// counter that priced it may be gone by the time the answer arrives.
    /// </para>
    /// </remarks>
    private static int ChosenCountOf(OptionalPaymentRequested owed, MayPay offer) =>
        Math.Max(1, offer.ChosenCount) * Math.Max(1, owed.Times);

    /// <summary>Life owed, scaled the same way the objects and the mana are.</summary>
    private static int LifeOwed(OptionalPaymentRequested owed, MayPay offer) =>
        offer.LifeCost * Math.Max(1, owed.Times);

    /// <summary>
    /// How to say a chosen cost out loud.
    /// </summary>
    /// <remarks>
    /// The prompt is all the board shows about the price, so a wording that dropped the number
    /// would price two very different offers identically — "discard a card" and "discard three
    /// cards" are not the same decision, and the options alone do not say which is being asked.
    /// The description is the compiler's own words for the qualifying objects, minus the "target"
    /// it prefixes them with — nothing here is targeted, and a prompt saying so would be wrong
    /// about a rule as well as clumsy.
    /// </remarks>
    private static string ChosenCostPrompt(ChosenCostKind kind, int count, TargetSpec? what)
    {
        var noun = what?.Description is { Length: > 0 } described
            ? Untargeted(described)
            : kind == ChosenCostKind.DiscardCards ? "card" : "permanent";

        var many = count == 1 || noun.EndsWith('s') ? noun : noun + "s";

        var asking = kind switch
        {
            ChosenCostKind.DiscardCards => $"Discard {count} {many}",
            ChosenCostKind.ReturnToHand => $"Return {count} {many} to its owner's hand",
            ChosenCostKind.SacrificePermanents => $"Sacrifice {count} {many}",
            _ => $"Pay with {count} {many}",
        };

        return $"{asking}, or pick nothing to decline.";
    }

    /// <summary>A spec's description with the word "target" taken off the front.</summary>
    private static string Untargeted(string description) =>
        description.StartsWith("target ", StringComparison.OrdinalIgnoreCase)
            ? description["target ".Length..]
            : description;

    /// <summary>
    /// Asks the oldest offered optional payment, if any (CR 601.2b).
    /// </summary>
    /// <remarks>
    /// The offer is skipped when the player plainly cannot take it, so a game does not stop to
    /// ask a question with one answer (CR 118.3). What <em>cannot</em> means is the whole of this
    /// method's history. It used to mean an empty mana pool, and that made every "counter target
    /// spell unless its controller pays {3}" decline itself against anybody who had not floated
    /// the mana in advance — the engine playing a different game from the printed card, rather
    /// than a card going unread. CR 605.3a lets a player activate mana abilities whenever a rule
    /// or effect asks them for a mana payment, so untapped lands <em>are</em> mana this player
    /// has, and the question is theirs to answer.
    /// <para>
    /// The other two halves of that were already here and only the gate was shut.
    /// <see cref="ActivateAbility"/> asks for priority only when the ability is not a mana
    /// ability, and a mana ability returns from it without settling or granting priority — so
    /// the pending question is still pending when the mana arrives, and the answer is an
    /// ordinary <c>ChoiceMade</c> after ordinary logged activations. Nothing here is a
    /// continuation, which is why a replay reaches the same offer.
    /// </para>
    /// <para>
    /// <see cref="ResolveOptionalPayment"/> then re-checks the real pool before charging it, so a
    /// player who answers yes and taps nothing declines. That is what makes it safe for the check
    /// below to be optimistic: the board, not the answer, decides whether the cost was paid.
    /// </para>
    /// </remarks>
    private bool AskOwedPayment()
    {
        if (_paymentsOwed.Count == 0 || State.IsWaitingForChoice)
            return false;

        var (owed, offer, aimedAt) = _paymentsOwed[0];
        _paymentsOwed.RemoveAt(0);

        // CR 107.4c: energy is a counter the player has, not mana, so it is checked on its own
        // and paid on its own. A cost of {E}{E} parsed as mana would ask the pool for two of a
        // colour called E and always come back unpayable.
        // CR 118.4: life can be paid down to zero but no further, so the check is a floor and
        // not a margin - a player on exactly 2 may pay 2.
        var lifeDue = LifeOwed(owed, offer);

        if (offer.EnergyCost > State.GetPlayer(owed.PlayerId).Energy
            || lifeDue > State.GetPlayer(owed.PlayerId).Life
            || !CouldPayMidResolution(owed.PlayerId, CostOwed(owed, offer)))
        {
            RunDeferredBranch(
                owed.SourceId,
                offer.IfYouDont,
                aimedAt,
                owed.SubjectObject,
                owed.AbilityId,
                subjectPlayer: owed.SubjectPlayer);
            return false;
        }

        // A cost that is not a currency is a selection, and the question changes shape with it:
        // "unless that player discards a card" cannot be answered yes, only with a card. The
        // offer is otherwise the same offer — same branches, same locator, same player asked —
        // so it stays one question, with objects for options and nothing picked as the decline.
        if (offer.ChosenKind is { } chosen)
        {
            var owedCount = ChosenCountOf(owed, offer);
            var payable = PayableFor(owed.PlayerId, chosen, offer.ChosenWhat);

            // CR 118.3: a cost cannot be paid without the resources to pay it in full, so a
            // player holding fewer than it names is not offered it at all. The currencies above
            // skip the question for the same reason — a question with one possible answer is not
            // a question, and stopping the game to ask it is how a game stalls.
            if (payable.Count < owedCount)
            {
                RunDeferredBranch(
                    owed.SourceId,
                    offer.IfYouDont,
                    aimedAt,
                    owed.SubjectObject,
                    owed.AbilityId,
                    subjectPlayer: owed.SubjectPlayer);
                return false;
            }

            Ask(new PendingChoice
            {
                Id = $"pay:{owed.PlayerId:N}:{owed.EffectIndex}",
                PlayerId = owed.PlayerId,
                Kind = ChoiceKind.OptionalPayment,
                Prompt = ChosenCostPrompt(chosen, owedCount, offer.ChosenWhat),
                Options = [.. payable.Select(id => new ChoiceOption(
                    id.Value.ToString("N"), State.GetObject(id).Card.Name))],

                // Nothing picked is how the offer is turned down, which is why there is no
                // floor. The ceiling is the whole cost: picking part of one buys nothing
                // (CR 601.2h), so offering to take part of it would only mislead.
                MinPicks = 0,
                MaxPicks = owedCount,
            });

            _paymentBeingAsked = (owed, offer, aimedAt);
            return true;
        }

        // What the player is actually being charged, in words. The mana is the event's own text
        // — already written out once per age counter — and the life is added because a cost of
        // "Pay 1 life for each age counter" carries no symbols at all: without this the button
        // read "Pay " with nothing after it, and now that the number climbs the player would have
        // no way at all to see what this upkeep costs.
        var priced = owed.CostText;
        if (lifeDue > 0)
            priced = priced.Length == 0 ? $"{lifeDue} life" : $"{priced} and {lifeDue} life";

        Ask(new PendingChoice
        {
            Id = $"pay:{owed.PlayerId:N}:{owed.EffectIndex}",
            PlayerId = owed.PlayerId,
            Kind = ChoiceKind.OptionalPayment,
            Prompt = owed.YesLabel is null ? $"Pay {priced}?" : "Choose one.",
            Options =
            [
                new ChoiceOption("yes", owed.YesLabel ?? $"Pay {priced}"),
                new ChoiceOption("no", owed.NoLabel ?? "Don't pay"),
            ],
            MinPicks = 1,
            MaxPicks = 1,
        });

        // Held so the answer can be paired with the offer it answers, without the choice having
        // to carry the effects themselves.
        _paymentBeingAsked = (owed, offer, aimedAt);
        return true;
    }

    private (
        OptionalPaymentRequested Request,
        MayPay Offer,
        ImmutableList<Target> Targets)? _paymentBeingAsked;

    /// <summary>
    /// Reads the offer back out of the card that made it (CR 601.2b).
    /// </summary>
    /// <remarks>
    /// The whole reason this mechanism can exist in an engine that folds state from a log: the
    /// branches are not remembered, they are looked up again from the same compiled definition
    /// that produced them. A replay reaches the same offer because it reaches the same card.
    /// </remarks>
    private MayPay? FindOptionalPayment(OptionalPaymentRequested owed)
    {
        if (CardBehind(owed.SourceId) is not { } behind)
            return null;

        var effects = owed.AbilityId is { } abilityId
            ? EffectsOfAbility(behind, abilityId, owed.SourceId)
            : _abilities.SpellOf(behind)?.Effects ?? [];

        // Looked up through the whole tree rather than by position in the top-level list: a
        // locator effect can sit inside another effect's branch, and indexing the outer list
        // there finds the branch's owner instead of the thing that asked.
        return EffectTree.Locate<MayPay>(effects, owed.EffectIndex);
    }

    /// <summary>
    /// Runs one branch of a deferred question, as though it were the rest of the effect.
    /// </summary>
    /// <remarks>
    /// One method for every deferred question, because they all have the same three problems and
    /// two of them had already been solved twice in slightly different ways. The source has moved
    /// on (CR 400.7) — a spell that asked is in the graveyard, a permanent that asked may have
    /// died — so the branch runs against whatever it became, found by the id the move produced.
    /// Nothing left at all means nothing happens, which is right for an effect whose source has
    /// ceased to exist.
    /// <para>
    /// The targets and the subject are passed in rather than read off that source, and this is
    /// the part that was got wrong twice: the thing the branch runs against is the <em>permanent
    /// behind</em> the ability, and a permanent has no ability on it, so it knows neither what was
    /// targeted nor what the trigger was about. Both failures looked identical from outside — the
    /// player answers and nothing happens — which is why they went unnoticed until a card needed
    /// them.
    /// </para>
    /// </remarks>
    private void RunDeferredBranch(
        ObjectId sourceId,
        ImmutableList<IEffect> branch,
        ImmutableList<Target> aimedAt,
        ObjectId? subjectObject,
        string? abilityId = null,
        int? subjectAmount = null,
        Guid? subjectPlayer = null)
    {
        if (branch.IsEmpty)
            return;

        // The branch runs against the permanent, which is not the object that raised the offer:
        // that was an ability on the stack, and it has finished. An effect inside the branch that
        // has to be found again later - one that asks a question and is looked up by (source,
        // ability, index) when the answer comes back - would be searched for among the card's
        // *spell* effects and not among the ability's, and never found. The request was then
        // dropped in silence: no choice, no error, the branch simply did nothing.
        //
        // So the ability the branch belongs to is carried back onto the object the branch runs
        // against. Nothing else about it is restored; only the id the locator needs.
        GameObject Wearing(GameObject on) =>
            abilityId is null || on.Ability is not null
                ? on with { Targets = aimedAt }
                : on with
                {
                    Targets = aimedAt,
                    Ability = new AbilityOnStack
                    {
                        SourceId = on.Id,
                        AbilityId = abilityId,
                        Text = string.Empty,
                    },
                };

        if (State.TryGetObject(sourceId, out var source))
        {
            RunEffects(branch, Wearing(source), subjectObject, subjectAmount, subjectPlayer);
            return;
        }

        if (_resolvedSources.TryGetValue(sourceId, out var moved)
            && State.TryGetObject(moved, out var landed))
        {
            RunEffects(branch, Wearing(landed), subjectObject, subjectAmount, subjectPlayer);
        }
    }

    /// <summary>
    /// Where an object that has left the stack ended up, so a deferred branch can still find it.
    /// </summary>
    /// <remarks>
    /// A spell becomes a new object when it leaves the stack (CR 400.7). Anything the spell set
    /// in motion and the engine asks about afterwards — an optional payment, most of all — needs
    /// a way back to it, and the old id is what it was recorded under.
    /// </remarks>
    private readonly Dictionary<ObjectId, ObjectId> _resolvedSources = [];

    /// <summary>Pays or declines an offered optional cost, and runs the branch (CR 601.2b).</summary>
    private void ResolveOptionalPayment(IReadOnlyList<string> picks)
    {
        if (_paymentBeingAsked is not var (owed, offer, aimedAt) || _paymentBeingAsked is null)
            return;

        _paymentBeingAsked = null;

        // Two shapes of answer, one question. A price is answered yes or no; a cost that is a
        // selection is answered with the objects themselves, and picking fewer than it named is
        // how that player declines — there is no partial payment (CR 601.2h), so there is no
        // third outcome to read out of a short answer.
        var paying = offer.ChosenKind is null
            ? picks.Count > 0 && string.Equals(picks[0], "yes", StringComparison.Ordinal)
            : picks.Count >= ChosenCountOf(owed, offer);

        var due = CostOwed(owed, offer);
        var lifeDue = LifeOwed(owed, offer);

        if (paying
            && offer.EnergyCost <= State.GetPlayer(owed.PlayerId).Energy
            && lifeDue <= State.GetPlayer(owed.PlayerId).Life
            && ManaPayment.CanPay(State.GetPlayer(owed.PlayerId).ManaPool, due))
        {
            if (offer.EnergyCost > 0)
                Emit(new EnergyChanged(owed.PlayerId, -offer.EnergyCost));

            if (lifeDue > 0)
            {
                Emit(new LifeChanged(
                    owed.PlayerId,
                    -lifeDue,
                    State.GetPlayer(owed.PlayerId).Life - lifeDue));
            }

            PayMana(owed.PlayerId, due);

            // The objects go before the branch runs, because instructions are followed in the
            // order written (CR 608.2c) and the branch can look at the board: "you may sacrifice
            // a creature. If you do, draw a card for each creature you control" counts what is
            // there when that clause applies (CR 608.2h), which is a board the sacrificed
            // creature has already left. Paying afterwards would let the card count itself.
            if (offer.ChosenKind is { } chosen)
                TakeChosenPayment(owed.PlayerId, chosen, picks);

            RunDeferredBranch(
                owed.SourceId,
                offer.IfYouDo,
                aimedAt,
                owed.SubjectObject,
                owed.AbilityId,
                subjectPlayer: owed.SubjectPlayer);
            return;
        }

        RunDeferredBranch(
            owed.SourceId,
            offer.IfYouDont,
            aimedAt,
            owed.SubjectObject,
            owed.AbilityId,
            subjectPlayer: owed.SubjectPlayer);
    }

    /// <summary>
    /// Makes the oldest owed coin flip, if any, and runs whichever branch it names (CR 705.2).
    /// </summary>
    /// <remarks>
    /// Unlike the other deferred items this asks nobody — a coin is not a decision — so it does
    /// not raise a choice and the game never stops. It still has to wait for the settle, because
    /// the randomness lives here and an effect cannot reach it.
    /// </remarks>
    /// <summary>A clash part-way through: both cards revealed, waiting on where they go.</summary>
    /// <param name="Pending">Whose decisions are still outstanding, in APNAP order.</param>
    private sealed record ClashInFlight(
        ClashRequested Request,
        Clash Effect,
        ImmutableList<Target> Targets,
        ImmutableList<(Guid Player, ObjectId Card, int ManaValue)> Revealed,
        ImmutableList<Guid> Pending);

    private ClashInFlight? _clashInFlight;

    /// <summary>
    /// Starts the oldest owed clash: both players reveal, then the first is asked (CR 701.30c).
    /// </summary>
    /// <remarks>
    /// The decisions come before the winner is worked out because they change what winning does:
    /// "if you win, draw a card" draws a different card depending on whether the revealed one
    /// went to the bottom. The rules put the moves first and so does this.
    /// </remarks>
    private RingBearerRequested? _ringBearerBeingAsked;

    private readonly List<VentureRoomRequested> _venturesOwed = [];

    private VentureRoomRequested? _ventureBeingAsked;

    /// <summary>Asks which arrow to follow out of a forked room (CR 701.49b).</summary>
    /// <remarks>
    /// Only ever raised for a fork: a room with one arrow moves the marker inside the effect,
    /// because "choose one of them to follow" is not a decision when there is one. A question with
    /// a single answer stops the game to ask something nobody can get wrong, and every one of
    /// those is a window an opponent gets for free.
    /// </remarks>
    private bool AskOwedVenture()
    {
        if (_venturesOwed.Count == 0 || State.IsWaitingForChoice)
            return false;

        var owed = _venturesOwed[0];
        _venturesOwed.RemoveAt(0);

        if (owed.Rooms.Count == 0)
            return false;

        _ventureBeingAsked = owed;

        Ask(new PendingChoice
        {
            Id = "venture:" + owed.PlayerId.ToString("N"),
            PlayerId = owed.PlayerId,
            Kind = ChoiceKind.VentureRoom,
            Prompt = $"Choose which room of {owed.Dungeon} to venture into.",
            Options = [.. owed.Rooms.Select(room => new ChoiceOption(room, room))],
            MinPicks = 1,
            MaxPicks = 1,
        });

        return true;
    }

    /// <summary>Moves the venture marker into the room the player chose (CR 701.49b).</summary>
    private void ResolveVentureRoom(IReadOnlyList<string> picks)
    {
        if (_ventureBeingAsked is not { } owed)
            return;

        _ventureBeingAsked = null;

        if (picks.Count == 0 || !owed.Rooms.Contains(picks[0], StringComparer.Ordinal))
            return;

        Emit(new VentureMarkerMoved(owed.PlayerId, owed.Dungeon, picks[0]));
    }

    /// <summary>Asks which creature bears the Ring (CR 701.54a).</summary>
    /// <remarks>
    /// CR 701.54d: the Ring tempts a player even when the choice is impossible, so a player with
    /// no creatures is not a stalled game - the count has already gone up and there is simply
    /// nobody to carry it.
    /// </remarks>
    private bool AskOwedRingBearer()
    {
        if (_ringBearersOwed.Count == 0 || State.IsWaitingForChoice)
            return false;

        var owed = _ringBearersOwed[0];
        _ringBearersOwed.RemoveAt(0);

        var mine = State.Battlefield
            .Select(State.GetObject)
            .Where(o => Characteristics.Of(State, _abilities, o) is { IsCreature: true } computed
                && computed.ControllerId == owed.PlayerId)
            .ToList();

        if (mine.Count == 0)
            return false;

        // One creature is not a choice, and the rules still say to choose it.
        if (mine.Count == 1)
        {
            Emit(new RingBearerChosen(owed.PlayerId, mine[0].Id));
            return false;
        }

        _ringBearerBeingAsked = owed;

        Ask(new PendingChoice
        {
            Id = "ring-bearer:" + owed.PlayerId.ToString("N"),
            PlayerId = owed.PlayerId,
            Kind = ChoiceKind.RingBearer,
            Prompt = "Choose a creature to be your Ring-bearer.",
            Options = [.. mine.Select(o => new ChoiceOption(o.Id.Value.ToString("N"), o.Card.Name))],
            MinPicks = 1,
            MaxPicks = 1,
        });

        return true;
    }

    /// <summary>Records the chosen Ring-bearer.</summary>
    private void ResolveRingBearer(IReadOnlyList<string> picks)
    {
        if (_ringBearerBeingAsked is not { } owed)
            return;

        _ringBearerBeingAsked = null;

        if (picks.Count == 0 || !Guid.TryParse(picks[0], out var chosen))
            return;

        var id = new ObjectId(chosen);
        if (State.TryGetObject(id, out var creature) && creature.Zone == Zone.Battlefield)
            Emit(new RingBearerChosen(owed.PlayerId, id));
    }

    private bool SettleOwedClash()
    {
        if (State.IsWaitingForChoice)
            return false;

        // A clash part-way through is the sweep's business too: the second player's question is
        // asked from here, not from inside the answer to the first. Every other owed question in
        // this engine is raised by the sweep, and clash asking one from within Choose was the one
        // place that broke the rule - and the corpus soak found it.
        if (_clashInFlight is not null)
            return AskNextClashDecision();

        if (_clashesOwed.Count == 0)
            return false;

        var (owed, effect, aimedAt) = _clashesOwed[0];
        _clashesOwed.RemoveAt(0);

        // "Clash with an opponent" is a choice at a big table and forced at a small one. Only
        // the forced case is read: with several opponents the player picks, and asking that is a
        // third question this does not yet ask - so the clash does nothing rather than being
        // aimed at somebody the card never chose.
        var opponents = State.TurnOrder
            .Where(id => id != owed.PlayerId && !State.GetPlayer(id).HasLost)
            .ToList();

        if (opponents.Count != 1)
            return false;

        var revealed = ImmutableList.CreateBuilder<(Guid Player, ObjectId Card, int ManaValue)>();

        foreach (var who in new[] { owed.PlayerId, opponents[0] })
        {
            var library = State.GetPlayer(who).Library;
            if (library.IsEmpty)
                continue;

            var top = State.GetObject(library[0]);
            Emit(new ClashRevealed(who, top.Id, top.Card.Cmc));
            revealed.Add((who, top.Id, top.Card.Cmc));
        }

        if (revealed.Count == 0)
            return true;

        _clashInFlight = new ClashInFlight(
            owed,
            effect,
            aimedAt,
            revealed.ToImmutable(),
            [.. revealed.Select(r => r.Player)]);

        return AskNextClashDecision();
    }

    /// <summary>Asks the next clashing player where their card goes, or finishes the clash.</summary>
    private bool AskNextClashDecision()
    {
        if (_clashInFlight is not { } flight)
            return false;

        if (flight.Pending.IsEmpty)
        {
            FinishClash(flight);
            return true;
        }

        var who = flight.Pending[0];
        var card = flight.Revealed.First(r => r.Player == who);

        Ask(new PendingChoice
        {
            Id = "clash:" + who.ToString("N"),
            PlayerId = who,
            Kind = ChoiceKind.ClashKeepOnTop,
            Prompt = "Put the revealed card on the bottom of your library?",
            Options =
            [
                new ChoiceOption("top", "Leave it on top"),
                new ChoiceOption("bottom", "Put it on the bottom"),
            ],
            MinPicks = 1,
            MaxPicks = 1,
        });

        return true;
    }

    /// <summary>Moves the card if asked, then asks the next player or finishes.</summary>
    private void ResolveClashDecision(IReadOnlyList<string> picks)
    {
        if (_clashInFlight is not { } flight || flight.Pending.IsEmpty)
            return;

        var who = flight.Pending[0];
        var card = flight.Revealed.First(r => r.Player == who);

        if (picks.Count > 0 && string.Equals(picks[0], "bottom", StringComparison.Ordinal))
            Emit(new CardPutOnBottom(who, card.Card));

        // Recorded and left there. The sweep asks the next player, or finishes the clash.
        _clashInFlight = flight with { Pending = flight.Pending.RemoveAt(0) };
    }

    /// <summary>Works out who won and runs the branch if it was the clashing player.</summary>
    private void FinishClash(ClashInFlight flight)
    {
        _clashInFlight = null;

        // CR 701.30d: a player wins if their card's mana value is higher than every other card
        // revealed. Strictly higher, so a tie is nobody winning - which is why this compares
        // rather than taking a maximum.
        var mine = flight.Revealed
            .Where(r => r.Player == flight.Request.PlayerId)
            .Select(r => (int?)r.ManaValue)
            .FirstOrDefault();

        var won = mine is { } best
            && flight.Revealed.All(r => r.Player == flight.Request.PlayerId || r.ManaValue < best);

        if (won && !flight.Effect.IfWon.IsEmpty)
        {
            RunDeferredBranch(
                flight.Request.SourceId,
                flight.Effect.IfWon,
                flight.Targets,
                flight.Request.SubjectObject);
        }
    }

    /// <summary>Reads the clash's branch back out of the card that called for it.</summary>
    private Clash? FindClash(ClashRequested owed)
    {
        if (CardBehind(owed.SourceId) is not { } behind)
            return null;

        var effects = owed.AbilityId is { } abilityId
            ? EffectsOfAbility(behind, abilityId, owed.SourceId)
            : _abilities.SpellOf(behind)?.Effects ?? [];

        return EffectTree.Locate<Clash>(effects, owed.EffectIndex);
    }

    private bool SettleOwedFlip()
    {
        if (_flipsOwed.Count == 0 || State.IsWaitingForChoice)
            return false;

        var (owed, effect, aimedAt) = _flipsOwed[0];
        _flipsOwed.RemoveAt(0);

        // The outcome goes in the log, not the roll — a replay reads what happened rather than
        // flipping again, exactly as a shuffle records its order.
        var won = _random.Choose([true, false]);
        Emit(new CoinFlipped(owed.PlayerId, won));

        var branch = won ? effect.IfWon : effect.IfLost;
        if (branch.IsEmpty)
            return true;

        RunDeferredBranch(owed.SourceId, branch, aimedAt, owed.SubjectObject);
        return true;
    }

    /// <summary>Reads the flip's branches back out of the card that called for it.</summary>
    private FlipCoin? FindFlip(CoinFlipRequested owed)
    {
        if (CardBehind(owed.SourceId) is not { } behind)
            return null;

        var effects = owed.AbilityId is { } abilityId
            ? EffectsOfAbility(behind, abilityId, owed.SourceId)
            : _abilities.SpellOf(behind)?.Effects ?? [];

        // Looked up through the whole tree rather than by position in the top-level list: a
        // locator effect can sit inside another effect's branch, and indexing the outer list
        // there finds the branch's owner instead of the thing that asked.
        return EffectTree.Locate<FlipCoin>(effects, owed.EffectIndex);
    }

    /// <summary>Makes the oldest owed die roll, if any (CR 706.2, 706.3a).</summary>
    /// <remarks>
    /// The outcome goes in the log, not the roll — a replay reads the number that came up rather
    /// than rolling again, exactly as a shuffle records its order. The result then picks the rows
    /// of the table that cover it, and every covered row runs in printed order with the result as
    /// its subject amount, which is what "equal to the result" inside a row reads.
    /// </remarks>
    private bool SettleOwedRoll()
    {
        if (_rollsOwed.Count == 0 || State.IsWaitingForChoice)
            return false;

        var (owed, effect, aimedAt) = _rollsOwed[0];
        _rollsOwed.RemoveAt(0);

        // CR 706.2b then 706.6: an extra die granted by a replacement effect is rolled with the
        // printed one and the lowest results are ignored — ignored rolls never happened, so one
        // die comes out and one number goes in the log however many dice went in.
        var natural = 0;
        for (var die = 0; die <= Math.Max(0, owed.ExtraDice); die++)
            natural = Math.Max(natural, _random.Choose(Enumerable.Range(1, effect.Sides).ToList()));

        // No modifier machinery exists yet, so the result is the natural result (CR 706.2).
        var result = natural;
        Emit(new DiceRolled(owed.PlayerId, effect.Sides, natural, result));

        var branch = effect.Rows
            .Where(row => row.Covers(result))
            .SelectMany(row => row.Effects)
            .ToImmutableList();

        if (branch.IsEmpty)
            return true;

        RunDeferredBranch(
            owed.SourceId, branch, aimedAt, owed.SubjectObject, subjectAmount: result);

        return true;
    }

    /// <summary>Reads the roll's table back out of the card that called for it.</summary>
    private RollDice? FindRoll(DiceRollRequested owed)
    {
        if (CardBehind(owed.SourceId) is not { } behind)
            return null;

        var effects = owed.AbilityId is { } abilityId
            ? EffectsOfAbility(behind, abilityId, owed.SourceId)
            : _abilities.SpellOf(behind)?.Effects ?? [];

        return EffectTree.Locate<RollDice>(effects, owed.EffectIndex);
    }

    /// <summary>
    /// Speeds up the active player if an opponent has lost life this turn (CR 702.179b).
    /// </summary>
    /// <remarks>
    /// A rule of the game rather than a triggered ability: nothing goes on the stack and nobody
    /// gets to respond to it, so it is settled here alongside the state-based actions instead.
    /// <para>
    /// Only the active player, because the rule says "during your turn", and only a player who
    /// already has speed, because going from nothing to something is what the printed keyword
    /// does. Once a turn, which the flag on the player carries so that it survives a replay.
    /// </para>
    /// </remarks>
    private bool IncreaseSpeedIfOwed()
    {
        var active = State.GetPlayer(State.ActivePlayerId);

        if (active.SpeedIncreasedThisTurn || active.Speed is 0 or >= 4)
            return false;

        var anyOpponentLost = State.Players
            .Any(p => p.Key != active.PlayerId && p.Value.LostLifeThisTurn);

        if (!anyOpponentLost)
            return false;

        Emit(new SpeedChanged(active.PlayerId, active.Speed + 1, TurnIncrease: true));
        return true;
    }

    /// <summary>
    /// Performs the oldest owed shuffle, if any (CR 701.24a).
    /// </summary>
    /// <remarks>
    /// The named cards go in first and card by card, because each move is a zone change that
    /// gives its card a new identity (CR 400.7) - so the order is read off the library only once
    /// everything that is going into it has arrived.
    /// </remarks>
    private bool SettleOwedShuffle()
    {
        if (_shufflesOwed.Count == 0 || State.IsWaitingForChoice)
            return false;

        var owed = _shufflesOwed[0];
        _shufflesOwed.RemoveAt(0);

        if (!State.Players.ContainsKey(owed.PlayerId))
            return false;

        // Exactly the cards the effect named, not whatever is in the graveyard now: the spell
        // that asked for this has landed there in the meantime and is not one of them.
        foreach (var id in owed.AlsoShuffleIn)
        {
            // Followed forward first. A spell that shuffles itself back names the id it had on
            // the stack, and by the time the shuffle settles it has become the card in the
            // graveyard - a different object (CR 400.7), and the only one that can be moved.
            var now = _resolvedSources.TryGetValue(id, out var landed) ? landed : id;

            // A graveyard, a hand, or the battlefield - the three zones a card is shuffled in
            // from, and the test has to admit all three because it is the only thing standing
            // between the request and a silent no-op. Restricted to the graveyard it dropped the
            // hand half of every Timetwister and the whole of "at the beginning of the end step,
            // ~'s owner shuffles it into their library", which left the permanent where it was
            // on a card that compiled and looked finished.
            //
            // A card already in a library or in exile is *not* moved: it is either where the
            // shuffle wanted it or somewhere the effect never named, and moving it again would
            // be this method inventing a zone change nobody asked for.
            if (State.TryGetObject(now, out var card)
                && card.Zone is Zone.Graveyard or Zone.Hand or Zone.Battlefield)
            {
                Move(now, Zone.Library, MoveCause.Other, owed.PlayerId);
            }
        }

        Emit(new LibraryShuffled(
            owed.PlayerId, _random.Shuffle(State.GetPlayer(owed.PlayerId).Library)));

        return true;
    }

    /// <summary>
    /// Performs the oldest owed discover, if any (CR 701.57a).
    /// </summary>
    /// <remarks>
    /// Cascade's twin, and written beside it rather than folded into it: the two differ in the
    /// comparison and in where a declined card goes, and a single method taking two flags would
    /// read as one mechanic with options rather than as the two rules it is.
    /// </remarks>
    private bool SettleOwedDiscover()
    {
        if (_discoveriesOwed.Count == 0 || State.IsWaitingForChoice)
            return false;

        var owed = _discoveriesOwed[0];
        _discoveriesOwed.RemoveAt(0);

        var exiled = new List<ObjectId>();
        ObjectId? found = null;

        foreach (var id in State.GetPlayer(owed.PlayerId).Library)
        {
            var card = State.GetObject(id);
            var moved = Move(id, Zone.Exile, MoveCause.Exile, owed.PlayerId);

            // "Mana value N or less", where cascade says strictly less - one character, and the
            // whole difference between discovering a card of exactly its number and not.
            if (!card.Card.CardTypes.HasFlag(CardType.Land) && card.Card.Cmc <= owed.AtMost)
            {
                found = moved;
                break;
            }

            exiled.Add(moved);
        }

        if (found is { } offered)
            Emit(new FreeCastOffered(offered, owed.PlayerId, ToHandIfDeclined: true));

        foreach (var id in _random.Shuffle(exiled))
            Move(id, Zone.Library, MoveCause.Other, owed.PlayerId, ZonePosition.Bottom);

        return true;
    }

    /// <summary>
    /// Performs the oldest owed cascade, if any (CR 702.85a).
    /// </summary>
    /// <remarks>
    /// Nobody is asked anything here — the exiling is not a decision. What the player gets is the
    /// offer at the end, and they take it by casting the card like any other.
    /// </remarks>
    private bool SettleOwedCascade()
    {
        if (_cascadesOwed.Count == 0 || State.IsWaitingForChoice)
            return false;

        var owed = _cascadesOwed[0];
        _cascadesOwed.RemoveAt(0);

        var library = State.GetPlayer(owed.PlayerId).Library;
        var exiled = new List<ObjectId>();
        ObjectId? found = null;

        // CR 702.85a: exile until a nonland card with lesser mana value turns up, or the library
        // runs out — a player who cascades into an empty library simply exiles everything.
        foreach (var id in library)
        {
            var card = State.GetObject(id);
            var moved = Move(id, Zone.Exile, MoveCause.Exile, owed.PlayerId);

            if (!card.Card.CardTypes.HasFlag(CardType.Land) && card.Card.Cmc < owed.LessThan)
            {
                found = moved;
                break;
            }

            exiled.Add(moved);
        }

        if (found is { } offered)
            Emit(new FreeCastOffered(offered, owed.PlayerId));

        // "In a random order" is the game's decision, so it goes through the shared source and
        // the order lands in the log like any other shuffle.
        foreach (var id in _random.Shuffle(exiled))
            Move(id, Zone.Library, MoveCause.Other, owed.PlayerId, ZonePosition.Bottom);

        return true;
    }

    /// <summary>
    /// Asks whether to ripple, and does it if the answer is yes (CR 702.60a).
    /// </summary>
    /// <remarks>
    /// The reveal is optional and the question is a real one: a ripple puts what it showed on the
    /// bottom of the library in a random order, so a player who has just arranged their top cards
    /// will usually decline. Reading "you may" as "you do" would have been the cheap way and
    /// would have made the card worse than printed on exactly the boards it is played on.
    /// </remarks>
    private bool AskOwedRipple()
    {
        if (_ripplesOwed.Count == 0 || State.IsWaitingForChoice)
            return false;

        var owed = _ripplesOwed[0];
        _ripplesOwed.RemoveAt(0);

        if (State.GetPlayer(owed.PlayerId).Library.IsEmpty)
            return false;

        _rippleBeingAsked = owed;

        Ask(new PendingChoice
        {
            Id = $"ripple:{owed.SourceId.Value:N}",
            PlayerId = owed.PlayerId,
            Kind = ChoiceKind.Ripple,
            Prompt = "You may reveal the top "
                + owed.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " cards of your library and cast the ones with this spell's name for free.",
            Options = [new ChoiceOption("ripple", "Reveal them")],
            MinPicks = 0,
            MaxPicks = 1,
        });

        return true;
    }

    /// <summary>
    /// Reveals the top cards and offers the matching ones for free (CR 702.60a).
    /// </summary>
    /// <remarks>
    /// One deviation, and it is cascade's: a card offered for free and declined stays in exile
    /// rather than going to the bottom of the library, because the offer lapses when its window
    /// closes and there is nowhere in that moment to say where it should have gone. It only ever
    /// touches a card sharing the spell's name, which is a card its caster would cast.
    /// </remarks>
    private void ResolveRipple(IReadOnlyList<string> picks)
    {
        if (_rippleBeingAsked is not { } owed)
            return;

        _rippleBeingAsked = null;

        if (picks.Count == 0)
            return;

        if (!State.TryGetObject(owed.SourceId, out var spell))
            return;

        var name = spell.Card.Name;
        var top = State.GetPlayer(owed.PlayerId).Library.Take(owed.Count).ToList();
        var buried = new List<ObjectId>();

        foreach (var id in top)
        {
            var card = State.GetObject(id);

            // CR 702.60a: only cards with the same name as this spell may be cast this way. The
            // rest are the ones that go on the bottom.
            if (!string.Equals(card.Card.Name, name, StringComparison.Ordinal))
            {
                buried.Add(id);
                continue;
            }

            var exiled = Move(id, Zone.Exile, MoveCause.Exile, owed.PlayerId);
            if (exiled is { } offered)
                Emit(new FreeCastOffered(offered, owed.PlayerId));
        }

        // CR 702.60a says "in any order", which is the player's decision, and this does not ask
        // - the same reading the look-and-take vocabulary already takes. The cards end face down
        // on the bottom of a library either way, and the difference is one nobody can observe.
        // Randomised rather than left in library order so that no information leaks out of the
        // order they went back in.
        foreach (var id in _random.Shuffle(buried))
            Move(id, Zone.Library, MoveCause.Other, owed.PlayerId, ZonePosition.Bottom);
    }

    /// <summary>
    /// Asks the oldest owed hand choice, if any (CR 701.16).
    /// </summary>
    /// <remarks>
    /// The options are another player's cards, which is only legal because the effect revealed
    /// the hand first — the reveal is not decoration, it is what earns the right to show these.
    /// An empty hand is not a stalled game: there is nothing to choose and nothing happens.
    /// </remarks>
    /// <summary>
    /// Asks for the colour or creature type a permanent names as it enters (CR 614.12).
    /// </summary>
    /// <returns>True when a question was asked and the settle has to stop.</returns>
    /// <remarks>
    /// Asked at the next settle rather than in the middle of the permanent arriving, which is a
    /// deviation worth naming: the rules say "as it enters", and this is "immediately after".
    /// Nothing can act in between — a settle runs before any player receives priority — so the
    /// only thing that could tell the difference is another ability resolving simultaneously,
    /// and none of the cards that choose this way has one.
    /// </remarks>
    /// <summary>
    /// "As this creature enters, you may sacrifice any number of creatures. It enters with N
    /// +1/+1 counters on it for each creature sacrificed this way" (CR 702.81a).
    /// </summary>
    /// <remarks>
    /// Asked in this sweep rather than as the permanent arrives - the same deviation the entry
    /// choice makes, and here it is load-bearing rather than incidental. Two devouring creatures
    /// are printed 0/0, so the counters have to be on before anything checks toughness. They are:
    /// every owed choice in this loop is asked before the state-based actions below it, and the
    /// sweep returns the moment it asks.
    /// <para>
    /// The creature cannot eat itself: it is on the battlefield by now, which is exactly the
    /// deviation that makes this reachable, and "any number of creatures" would otherwise offer
    /// the devourer its own body.
    /// </para>
    /// </remarks>
    private bool AskOwedDevour()
    {
        if (State.IsWaitingForChoice)
            return false;

        foreach (var id in State.Battlefield)
        {
            if (_devourAsked.Contains(id))
                continue;

            var obj = State.GetObject(id);
            var each = _abilities.DevourCountOf(obj.Card);
            if (each <= 0)
                continue;

            _devourAsked.Add(id);

            var food = State.Battlefield
                .Where(other => other != id)
                .Select(State.GetObject)
                .Where(o => ControllerOf(o) == ControllerOf(obj)
                    && Characteristics.Of(State, _abilities, o).IsCreature)
                .ToList();

            if (food.Count == 0)
                continue;

            _devourBeingAsked = (id, each);

            Ask(new PendingChoice
            {
                Id = $"devour:{id}",
                PlayerId = ControllerOf(obj),
                Kind = ChoiceKind.Devour,
                Prompt = $"Sacrifice any number of creatures to {obj.Card.Name}. Each is worth "
                    + $"{each} +1/+1 counter(s).",
                Options = [.. food.Select(o => new ChoiceOption(
                    o.Id.Value.ToString("N"), o.Card.Name))],
                MinPicks = 0,
                MaxPicks = food.Count,
            });

            return true;
        }

        return false;
    }

    private readonly HashSet<ObjectId> _devourAsked = [];

    private (ObjectId Id, int Each)? _devourBeingAsked;

    /// <summary>
    /// Battles whose protector has to be designated, answered or asked (CR 310.9a, 704.5x).
    /// </summary>
    /// <remarks>
    /// One sweep serves both moments the rules name — the choice as the battle enters
    /// (CR 310.9a) and the re-choice when its protector stops being eligible (CR 704.5x,
    /// 704.5y) — because they are the same question with the same candidates. It runs in the
    /// settle loop before the state-based actions, which is where every owed choice runs, so a
    /// battle reaching the actions with no protector is one with nobody left to choose.
    /// <para>
    /// With exactly one eligible player the choice is forced and made rather than offered —
    /// every Siege at a two-player table (CR 310.12a) — so a duel never stops to ask a question
    /// with one answer. A battle currently being attacked keeps its state as it is (CR 704.5x
    /// defers even the re-choice); one that has never had a protector cannot be under attack, so
    /// the entry choice is never deferred by this.
    /// </para>
    /// </remarks>
    /// <returns>True when a protector was designated without asking; the settle goes round.</returns>
    private bool ChooseForcedProtectors()
    {
        foreach (var (id, obj) in ProtectorsOwed())
        {
            var eligible = StateBasedActions.EligibleProtectors(State, _abilities, obj).ToList();

            if (eligible.Count == 1)
            {
                Emit(new ProtectorChosen(id, eligible[0]));
                return true;
            }
        }

        return false;
    }

    /// <summary>Asks the controller which player protects their battle (CR 310.9a).</summary>
    /// <returns>True when a question was asked and the settle has to stop.</returns>
    private bool AskOwedProtector()
    {
        if (State.IsWaitingForChoice)
            return false;

        foreach (var (id, obj) in ProtectorsOwed())
        {
            var eligible = StateBasedActions.EligibleProtectors(State, _abilities, obj).ToList();
            if (eligible.Count < 2)
                continue;

            Ask(new PendingChoice
            {
                Id = "protector:" + id.Value.ToString("N"),
                PlayerId = ControllerOf(obj),
                Kind = ChoiceKind.ChooseProtector,
                Prompt = $"Choose a player to protect {obj.Card.Name} (CR 310.9a).",
                Options = [.. eligible.Select(player => new ChoiceOption(
                    player.ToString("N"), State.GetPlayer(player).Name))],
                MinPicks = 1,
                MaxPicks = 1,
            });

            return true;
        }

        return false;
    }

    /// <summary>The battles on the battlefield with no eligible protector designated.</summary>
    private IEnumerable<(ObjectId Id, GameObject Battle)> ProtectorsOwed()
    {
        foreach (var id in State.Battlefield)
        {
            var obj = State.GetObject(id);
            if (!obj.Card.CardTypes.HasFlag(CardType.Battle) || obj.Permanent is null)
                continue;

            // CR 704.5x: while creatures are attacking the battle, its protector is not
            // re-chosen. A battle that has never had one cannot be under attack.
            if (State.Combat.Attackers.Values.Any(at => at.Planeswalker == id))
                continue;

            var standing = obj.Permanent.ProtectorId;
            var fine = standing is { } chosen
                && !State.GetPlayer(chosen).HasLost
                && StateBasedActions.EligibleProtectors(State, _abilities, obj).Contains(chosen);

            if (!fine)
                yield return (id, obj);
        }
    }

    /// <summary>Records the protector the controller picked (CR 310.9a).</summary>
    private void ResolveProtector(PendingChoice choice, IReadOnlyList<string> picks)
    {
        if (picks.Count == 0)
            return;

        var battleId = new ObjectId(Guid.ParseExact(
            choice.Id["protector:".Length..], "N"));

        if (!State.TryGetObject(battleId, out var battle)
            || battle.Zone != Zone.Battlefield)
        {
            return;
        }

        Emit(new ProtectorChosen(battleId, Guid.ParseExact(picks[0], "N")));
    }

    /// <summary>Eats what was chosen and puts the counters on (CR 702.81a).</summary>
    private void ResolveDevour(IReadOnlyList<string> picks)
    {
        if (_devourBeingAsked is not { } owed)
            return;

        _devourBeingAsked = null;

        if (!State.TryGetObject(owed.Id, out var devourer)
            || devourer.Zone != Zone.Battlefield)
        {
            return;
        }

        var eaten = 0;
        foreach (var pick in picks)
        {
            var id = new ObjectId(Guid.ParseExact(pick, "N"));
            if (!State.TryGetObject(id, out var food) || food.Zone != Zone.Battlefield)
                continue;

            Move(id, Zone.Graveyard, MoveCause.Sacrifice, ControllerOf(food));
            eaten++;
        }

        if (eaten > 0)
            Emit(new CountersChanged(owed.Id, CounterKinds.PlusOnePlusOne, eaten * owed.Each));
    }

    /// <summary>
    /// "As this creature enters, you may reveal any number of cards from your hand that share a
    /// creature type with it. It enters with N +1/+1 counters on it for each" (CR 702.38a).
    /// </summary>
    /// <remarks>
    /// Devour's question asked of the hand instead of the battlefield, and asked in the same
    /// sweep for the same reason: a replacement effect cannot stop and ask, and every amplifying
    /// creature is printed with a body that the counters are meant to be added to rather than a
    /// 0/0 that dies without them - so the deviation is safe here in a way it is not for devour.
    /// <para>
    /// The types compared are the arriving permanent's <em>computed</em> ones, so a changeling
    /// amplifies off anything and a creature that was made a Dragon this turn amplifies off
    /// Dragons. What is in hand is compared by its printed types, which is all a card in a hand
    /// has (CR 613.2) - plus its own changeling, which is a characteristic-defining ability and
    /// therefore true in every zone.
    /// </para>
    /// </remarks>
    private bool AskOwedAmplify()
    {
        if (State.IsWaitingForChoice)
            return false;

        foreach (var id in State.Battlefield)
        {
            if (_amplifyAsked.Contains(id))
                continue;

            var obj = State.GetObject(id);
            var each = _abilities.AmplifyCountOf(obj.Card);
            if (each <= 0)
                continue;

            _amplifyAsked.Add(id);

            var mine = Characteristics.Of(State, _abilities, obj);
            var controller = ControllerOf(obj);

            var showable = State.GetPlayer(controller).Hand
                .Select(State.GetObject)
                .Where(held => SharesACreatureType(mine, held))
                .ToList();

            if (showable.Count == 0)
                continue;

            _amplifyBeingAsked = (id, each);

            Ask(new PendingChoice
            {
                Id = $"amplify:{id.Value:N}",
                PlayerId = controller,
                Kind = ChoiceKind.Amplify,
                Prompt = $"Reveal any number of cards from your hand to amplify "
                    + $"{obj.Card.Name}. Each is worth {each} +1/+1 counter(s).",
                Options = [.. showable.Select(held => new ChoiceOption(
                    held.Id.Value.ToString("N"), held.Card.Name))],
                MinPicks = 0,
                MaxPicks = showable.Count,
            });

            return true;
        }

        return false;
    }

    /// <summary>Whether a card in hand shares a creature type with an arriving permanent.</summary>
    private static bool SharesACreatureType(ComputedCharacteristics mine, GameObject held) =>
        held.Card.CardTypes.HasFlag(CardType.Creature)
        && (held.Card.Keywords.HasFlag(KeywordAbility.Changeling)
            || mine.IsEveryCreatureType
            || held.Card.Subtypes.Any(mine.HasSubtype));

    private readonly HashSet<ObjectId> _amplifyAsked = [];

    private (ObjectId Id, int Each)? _amplifyBeingAsked;

    /// <summary>Shows what was chosen and puts the counters on (CR 702.38a).</summary>
    private void ResolveAmplify(IReadOnlyList<string> picks)
    {
        if (_amplifyBeingAsked is not { } owed)
            return;

        _amplifyBeingAsked = null;

        if (!State.TryGetObject(owed.Id, out var amplified)
            || amplified.Zone != Zone.Battlefield)
        {
            return;
        }

        var mine = Characteristics.Of(State, _abilities, amplified);
        var shown = ImmutableList.CreateBuilder<ObjectId>();

        foreach (var pick in picks)
        {
            var id = new ObjectId(Guid.ParseExact(pick, "N"));

            // Re-checked rather than trusted: the option list was built for this player's hand
            // as it was, and an answer arrives from outside the engine.
            if (!State.TryGetObject(id, out var held)
                || held.Zone != Zone.Hand
                || held.OwnerId != ControllerOf(amplified)
                || !SharesACreatureType(mine, held))
            {
                continue;
            }

            shown.Add(id);
        }

        if (shown.Count == 0)
            return;

        // The reveal is what the cost of the counters is, so it goes in the log before them.
        Emit(new CardsRevealed(ControllerOf(amplified), shown.ToImmutable()));

        Emit(new CountersChanged(
            owed.Id, CounterKinds.PlusOnePlusOne, shown.Count * owed.Each));
    }

    /// <summary>
    /// "As this Saga enters, choose a number between one and its final chapter number, and it
    /// enters with that many lore counters on it" (CR 702.155b).
    /// </summary>
    /// <remarks>
    /// Read ahead replaces the intrinsic ability that gives every other Saga its first counter
    /// (CR 714.3b), so a Saga with it sits at zero until this is answered - which is safe, and
    /// deliberately so: nothing sacrifices a Saga for being below its final chapter, and this
    /// sweep runs before state-based actions do.
    /// <para>
    /// The counters arrive in one lump, which without CR 702.155a's restriction would fire every
    /// chapter from the first. The restriction lives in the chapter predicate the compiler
    /// builds, where it can see whether the Saga entered this turn.
    /// </para>
    /// </remarks>
    private bool AskOwedReadAhead()
    {
        if (State.IsWaitingForChoice)
            return false;

        foreach (var id in State.Battlefield)
        {
            if (_readAheadAsked.Contains(id))
                continue;

            var obj = State.GetObject(id);
            if (!_abilities.HasReadAhead(obj.Card))
                continue;

            var final = FinalChapterOf(obj.Card);
            if (final <= 0)
                continue;

            _readAheadAsked.Add(id);
            _readAheadBeingAsked = id;

            Ask(new PendingChoice
            {
                Id = $"read-ahead:{id.Value:N}",
                PlayerId = ControllerOf(obj),
                Kind = ChoiceKind.ReadAhead,
                Prompt = $"Choose the chapter {obj.Card.Name} starts at.",
                Options =
                [
                    .. Enumerable.Range(1, final).Select(n => new ChoiceOption(
                        n.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        "Chapter "
                            + n.ToString(System.Globalization.CultureInfo.InvariantCulture))),
                ],
                MinPicks = 1,
                MaxPicks = 1,
            });

            return true;
        }

        return false;
    }

    /// <summary>The greatest chapter number a Saga has, which is its final chapter (CR 714.2d).</summary>
    private int FinalChapterOf(CardDefinition card)
    {
        var final = 0;
        foreach (var trigger in _abilities.TriggersOf(card))
        {
            if (trigger.Chapter is { } chapter && chapter > final)
                final = chapter;
        }

        return final;
    }

    private readonly HashSet<ObjectId> _readAheadAsked = [];

    private ObjectId? _readAheadBeingAsked;

    /// <summary>Starts the Saga at the chapter that was picked (CR 702.155b).</summary>
    private void ResolveReadAhead(IReadOnlyList<string> picks)
    {
        if (_readAheadBeingAsked is not { } saga)
            return;

        _readAheadBeingAsked = null;

        if (!State.TryGetObject(saga, out var onBoard) || onBoard.Zone != Zone.Battlefield)
            return;

        var final = FinalChapterOf(onBoard.Card);

        // Clamped rather than trusted: the answer arrives from outside the engine, and a Saga
        // started past its final chapter would be sacrificed before it did anything.
        var chosen = picks.Count > 0
            && int.TryParse(
                picks[0],
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out var n)
            && n >= 1
            && n <= final
            ? n
            : 1;

        Emit(new CountersChanged(saga, CounterKinds.Lore, chosen));
    }

    private bool AskOwedEntryChoice()
    {
        if (State.IsWaitingForChoice)
            return false;

        foreach (var id in State.Battlefield)
        {
            var obj = State.GetObject(id);
            var kind = _abilities.ChoosesOnEntry(obj.Card);
            if (kind == ChoiceOnEntry.None)
                continue;

            // Asked of the field the answer lands in, not of a shared one: a card name and a
            // characteristic are kept apart on the object, so "has this been asked yet" has
            // to be asked of whichever half this card fills.
            var answered = kind == ChoiceOnEntry.CardName ? obj.ChosenName : obj.Chosen;
            if (answered is not null)
                continue;

            var options = kind switch
            {
                ChoiceOnEntry.Color =>
                    (IReadOnlyList<string>)["white", "blue", "black", "red", "green"],
                ChoiceOnEntry.CardName => CardNamesOffered(
                    ControllerOf(obj),
                    _abilities.ChosenNameFilterOf(obj.Card) ?? SearchFilters.AnyCard),
                _ => CreatureTypesInPlay(),
            };

            if (options.Count == 0)
                continue;

            _entryChoiceBeingAsked = id;

            Ask(new PendingChoice
            {
                Id = $"chosen:{id.Value:N}",
                PlayerId = ControllerOf(obj),
                Kind = ChoiceKind.NameCharacteristic,
                Prompt = kind switch
                {
                    ChoiceOnEntry.Color => $"Choose a color for {obj.Card.Name}.",
                    ChoiceOnEntry.CardName => $"Choose a card name for {obj.Card.Name}.",
                    _ => $"Choose a creature type for {obj.Card.Name}.",
                },
                Options = [.. options.Select(o => new ChoiceOption(o, o))],
                MinPicks = 1,
                MaxPicks = 1,
            });

            return true;
        }

        return false;
    }

    private ObjectId? _entryChoiceBeingAsked;

    /// <summary>
    /// The card names a player may be offered (CR 201.4).
    /// </summary>
    /// <remarks>
    /// CR 201.4 lets a player name any card in the Oracle reference — thirty-odd thousand of
    /// them, which no board can show. So the offer is narrowed the way the creature-type offer
    /// below is: to the names actually in this game. Naming a card nothing has is legal and
    /// useless, and every card that asks this is naming something its controller is worried
    /// about.
    /// <para>
    /// <strong>And narrowed to what the chooser is allowed to know.</strong> The list is the
    /// public zones plus the chooser's own hand — never an opponent's hand or anybody's
    /// library, because an option list is shown to the player and a list built from a hidden
    /// zone would hand them its contents. That is not a nicety: Sorcerous Spyglass and
    /// Anointed Peacekeeper pay real card text for the words "look at an opponent's hand"
    /// before they name, and Meddling Mage does not. A shared offer would have given the
    /// Mage what those two are printed to buy.
    /// </para>
    /// <para>
    /// Filtered by what the card allows (CR 201.4a). A Meddling Mage offering Island is a
    /// strictly better card than the printed one, and the qualifier is the only thing
    /// standing between the two.
    /// </para>
    /// </remarks>
    private IReadOnlyList<string> CardNamesOffered(Guid controller, string filter)
    {
        var seen = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var obj in State.Objects.Values)
        {
            // Exactly the zones the view already shows this player. Exile is left out
            // although much of it is public, because the projection does not carry it at
            // all - offering a name out of it would show something no board shows, and an
            // offer that reveals more than the view is the same leak by another route.
            var visible = obj.Zone switch
            {
                Zone.Battlefield or Zone.Graveyard or Zone.Stack => true,
                Zone.Hand => obj.OwnerId == controller,
                _ => false,
            };

            if (!visible || obj.Card.Name.Length == 0)
                continue;

            if (SearchFilters.Matches(filter, obj.Card))
                seen.Add(obj.Card.Name);
        }

        return [.. seen];
    }

    private CardNameChoiceRequested? _cardNameChoiceBeingAsked;

    /// <summary>
    /// Asks the oldest owed card name, if any (CR 201.4).
    /// </summary>
    /// <returns>True when a question was asked and the settle has to stop.</returns>
    /// <remarks>
    /// The entry choice's question asked from a resolution instead of from a permanent arriving,
    /// and deliberately the <em>same</em> question: one offer, one <see cref="ChoiceKind"/>, one
    /// prompt shape. Sharing <see cref="CardNamesOffered"/> is not tidiness — that method is
    /// where the rule lives that an offer never contains a card in an opponent's hand, and a
    /// second list built here would have been a second place for that rule to be got wrong.
    /// Sorcerous Spyglass and Anointed Peacekeeper pay printed text for the words "look at an
    /// opponent's hand" before they name; Cranial Extraction does not, and names blind.
    /// <para>
    /// <see cref="ChoiceKind.NameCharacteristic"/> rather than a kind of its own, for the reason
    /// that kind's own remarks give: the board renders a prompt and a list of strings, and which
    /// of the questions it is answering changes nothing it draws. <c>Resume</c> tells the two
    /// apart by which of them is outstanding, which is a fact the engine already has, rather
    /// than by a word every client would have had to learn.
    /// </para>
    /// <para>
    /// An empty offer asks nothing. That is not a fail-open: the search queued behind this one
    /// is still holding <see cref="SearchFilters.ChosenName"/>, and
    /// <see cref="AskOwedSearch"/> drops a search that reaches it unfilled.
    /// </para>
    /// </remarks>
    private bool AskOwedCardNameChoice()
    {
        if (_cardNameChoicesOwed.Count == 0 || State.IsWaitingForChoice)
            return false;

        var owed = _cardNameChoicesOwed[0];
        _cardNameChoicesOwed.RemoveAt(0);

        if (!State.Players.ContainsKey(owed.ChooserId))
            return false;

        var options = CardNamesOffered(owed.ChooserId, owed.FilterId);
        if (options.Count == 0)
            return false;

        _cardNameChoiceBeingAsked = owed;

        Ask(new PendingChoice
        {
            Id = $"name:{owed.SourceId.Value:N}",
            PlayerId = owed.ChooserId,
            Kind = ChoiceKind.NameCharacteristic,
            Prompt = "Choose a card name.",
            Options = [.. options.Select(o => new ChoiceOption(o, o))],
            MinPicks = 1,
            MaxPicks = 1,
        });

        return true;
    }

    /// <summary>
    /// Records the name that was chosen, and hands it to the search that was waiting for it
    /// (CR 201.4).
    /// </summary>
    /// <remarks>
    /// Two things happen here and they are for different readers. The
    /// <see cref="NameChosen"/> is for the log and the board: it says which card was named, and
    /// it lands on the spell that asked, so a replay reaches the same answer without asking
    /// again. The rewrite is for the search sentence that has been queued behind this question
    /// since the same resolution, still carrying <see cref="SearchFilters.ChosenName"/> where
    /// its filter should be.
    /// <para>
    /// The rewrite is confined to searches <em>this</em> player is about to make, because the
    /// chooser is the searcher: an extraction's controller does both. It is also confined to the
    /// sentinel — an ordinary tutor's filter is a word and is left alone.
    /// </para>
    /// <para>
    /// <strong>An answer off the menu never reaches here.</strong> A card name is a word rather
    /// than an object id, so it is the one answer in this engine that could arrive naming
    /// something the offer never held - the card in the opponent's hand that
    /// <see cref="CardNamesOffered"/> exists to hide. <c>Choose</c> refuses a pick that was not
    /// among the options before any of this runs, which is where that guard belongs: one place,
    /// for every question, at the door. Recomputing the offer here would be a second copy of the
    /// narrowing rule, and the whole point of sharing one is that there is nowhere for the two
    /// to drift apart.
    /// </para>
    /// </remarks>
    private void ResolveCardNameChoice(IReadOnlyList<string> picks)
    {
        if (_cardNameChoiceBeingAsked is not { } owed)
            return;

        _cardNameChoiceBeingAsked = null;

        if (picks.Count == 0)
            return;

        var named = picks[0];

        Emit(new NameChosen(owed.SourceId, named));

        for (var i = 0; i < _searchesOwed.Count; i++)
        {
            var search = _searchesOwed[i];

            if (search.PlayerId != owed.ChooserId
                || !search.FilterId.Contains(SearchFilters.ChosenName, StringComparison.Ordinal))
            {
                continue;
            }

            _searchesOwed[i] = search with
            {
                FilterId = search.FilterId.Replace(
                    SearchFilters.ChosenName, named, StringComparison.Ordinal),
            };
        }
    }

    /// <summary>
    /// The creature types a player could sensibly name (CR 205.3m).
    /// </summary>
    /// <remarks>
    /// Every creature type in the game is a list of several hundred words and no board can show
    /// it, so the offer is the types actually present across every zone in this game plus a
    /// handful of common ones. A player naming a type nothing has is legal and useless, and the
    /// cards that do this are always naming something they can see.
    /// </remarks>
    private IReadOnlyList<string> CreatureTypesInPlay()
    {
        var seen = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var obj in State.Objects.Values)
        {
            if (!obj.Card.CardTypes.HasFlag(CardType.Creature))
                continue;

            foreach (var subtype in obj.Card.Subtypes)
                seen.Add(subtype);
        }

        foreach (var common in new[] { "Goblin", "Elf", "Human", "Zombie", "Soldier" })
            seen.Add(common);

        return [.. seen];
    }

    private ImmutableList<ObjectId> _optionalUntaps = [];

    /// <summary>Untaps the ones their controller said yes to (CR 502.3).</summary>
    private void UntapTheChosen(IReadOnlyList<string> picks)
    {
        var chosen = picks
            .Select(pick => new ObjectId(Guid.ParseExact(pick, "N")))
            .Where(id => State.TryGetObject(id, out var o) && o.Permanent?.IsTapped == true)
            .ToImmutableList();

        foreach (var e in StunCounters.Untapping(State, chosen))
            Emit(e);
    }

    /// <summary>
    /// Asks which permanents their controller wants to untap, of those that may decline (CR 502.3).
    /// </summary>
    /// <returns>True when a question was asked and the settle has to stop.</returns>
    /// <remarks>
    /// One question for all of them rather than one each: the untap step is a single event, the
    /// answer is the same shape whichever permanent it is about, and a player with four of these
    /// should not be asked four times.
    /// </remarks>
    private bool AskOptionalUntaps()
    {
        if (_optionalUntaps.IsEmpty || State.IsWaitingForChoice)
            return false;

        // A permanent that has left, or untapped some other way, is no longer a question.
        var still = _optionalUntaps
            .Where(id => State.TryGetObject(id, out var o) && o.Permanent?.IsTapped == true)
            .ToImmutableList();

        _optionalUntaps = [];

        if (still.IsEmpty)
            return false;

        Ask(new PendingChoice
        {
            Id = $"untap:{State.ActivePlayerId:N}:{State.TurnNumber}",
            PlayerId = State.ActivePlayerId,
            Kind = ChoiceKind.ChooseOptionalUntaps,
            Prompt = "Choose which of these to untap; the rest stay tapped.",
            Options = [.. still.Select(id => new ChoiceOption(
                id.Value.ToString("N"), State.GetObject(id).Card.Name))],
            MinPicks = 0,
            MaxPicks = still.Count,
        });

        return true;
    }

    /// <summary>Asks a player to arrange cards back on top, if one is waiting (CR 701.19a).</summary>
    /// <returns>True when a question was asked and the settle has to stop.</returns>
    private bool AskOwedLibraryOrder()
    {
        if (_libraryOrdersOwed.Count == 0 || State.IsWaitingForChoice)
            return false;

        var owed = _libraryOrdersOwed[0];
        _libraryOrdersOwed.RemoveAt(0);

        // Only what is still on top: a card drawn or milled while this waited is no longer being
        // arranged, and naming it would describe a library that no longer exists.
        var top = State.GetPlayer(owed.PlayerId).Library
            .Take(owed.Cards.Count)
            .Where(owed.Cards.Contains)
            .ToImmutableList();

        // One card has one order, and none has none.
        if (top.Count < 2)
            return false;

        _libraryOrderBeingAsked = owed with { Cards = top };

        Ask(new PendingChoice
        {
            Id = $"arrange:{owed.PlayerId:N}:{State.TurnNumber}",
            PlayerId = owed.PlayerId,
            Kind = ChoiceKind.OrderLibraryTop,
            Prompt = "Put these back on top in any order; the first is the top card.",
            Options = [.. top.Select(id => new ChoiceOption(
                id.Value.ToString("N"), State.GetObject(id).Card.Name))],
            MinPicks = top.Count,
            MaxPicks = top.Count,
        });

        return true;
    }

    private LibraryOrderRequested? _libraryOrderBeingAsked;

    private void ResolveLibraryOrder(IReadOnlyList<string> picks)
    {
        if (_libraryOrderBeingAsked is not { } owed)
            return;

        _libraryOrderBeingAsked = null;

        if (picks.Count != owed.Cards.Count)
            return;

        var order = picks
            .Select(pick => new ObjectId(Guid.ParseExact(pick, "N")))
            .ToImmutableList();

        // The answer has to name the same cards it was asked about: an ordering that dropped one
        // or invented one would rewrite the library rather than rearrange it.
        if (!order.All(owed.Cards.Contains) || order.Distinct().Count() != order.Count)
            return;

        Emit(new LibraryOrdered(owed.PlayerId, order));
    }

    /// <summary>Asks where counters go, if an effect is waiting on it (CR 701.36a).</summary>
    /// <returns>True when a question was asked and the settle has to stop.</returns>
    private bool AskOwedCounterChoice()
    {
        if (_counterChoicesOwed.Count == 0 || State.IsWaitingForChoice)
            return false;

        var owed = _counterChoicesOwed[0];
        _counterChoicesOwed.RemoveAt(0);

        var still = owed.Candidates
            .Where(id => State.TryGetObject(id, out var o) && o.Zone == Zone.Battlefield)
            .ToImmutableList();

        if (still.IsEmpty)
            return false;

        // One candidate is not a choice: the rules say "choose", and with a single legal answer
        // the game stopping to ask would be a prompt with one button on it.
        if (still.Count == 1)
        {
            Emit(new CountersChanged(still[0], owed.Kind, owed.Count));
            return false;
        }

        _counterChoiceBeingAsked = owed;

        Ask(new PendingChoice
        {
            Id = $"counters:{owed.ChooserId:N}:{State.TurnNumber}",
            PlayerId = owed.ChooserId,
            Kind = ChoiceKind.ChooseForCounters,
            Prompt = $"Choose where {owed.Count} {owed.Kind} counter(s) go.",
            Options = [.. still.Select(id => new ChoiceOption(
                id.Value.ToString("N"), State.GetObject(id).Card.Name))],
            MinPicks = 1,
            MaxPicks = 1,
        });

        return true;
    }

    private CounterChoiceRequested? _counterChoiceBeingAsked;

    private void ResolveCounterChoice(IReadOnlyList<string> picks)
    {
        if (_counterChoiceBeingAsked is not { } owed)
            return;

        _counterChoiceBeingAsked = null;

        if (picks.Count == 0)
            return;

        var chosen = new ObjectId(Guid.ParseExact(picks[0], "N"));
        if (State.TryGetObject(chosen, out var permanent) && permanent.Zone == Zone.Battlefield)
            Emit(new CountersChanged(chosen, owed.Kind, owed.Count));
    }

    /// <summary>Asks which permanents to untap, if an effect is waiting (CR 701.21a).</summary>
    /// <returns>True when a question was asked and the settle has to stop.</returns>
    private bool AskOwedUntapChoice()
    {
        if (_untapChoicesOwed.Count == 0 || State.IsWaitingForChoice)
            return false;

        var owed = _untapChoicesOwed[0];
        _untapChoicesOwed.RemoveAt(0);

        // Anything that has left, or untapped some other way while this waited, is no longer a
        // candidate - the same guard the untap step's own offer makes, for the same reason.
        var still = owed.Candidates
            .Where(id => State.TryGetObject(id, out var o) && o.Permanent?.IsTapped == true)
            .ToImmutableList();

        if (still.IsEmpty)
            return false;

        Ask(new PendingChoice
        {
            Id = $"untap-choice:{owed.ChooserId:N}:{State.TurnNumber}",
            PlayerId = owed.ChooserId,
            Kind = ChoiceKind.ChooseUntaps,
            Prompt = $"Choose up to {owed.Most} to untap.",
            Options = [.. still.Select(id => new ChoiceOption(
                id.Value.ToString("N"), State.GetObject(id).Card.Name))],

            // "Up to" means none is an answer (CR 601.2c), so the minimum is nothing.
            MinPicks = 0,
            MaxPicks = Math.Min(owed.Most, still.Count),
        });

        return true;
    }

    /// <summary>Asks a player to name a colour, if an effect is waiting on one (CR 202.2).</summary>
    /// <returns>True when a question was asked and the settle has to stop.</returns>
    private ExploitRequested? _exploitBeingAsked;

    /// <summary>Asks which creature to sacrifice to exploit, if any (CR 702.110a).</summary>
    /// <remarks>
    /// The declining option is on the list rather than being a separate yes-or-no: the card says
    /// "you may sacrifice a creature", which is one decision, and asking it as two would let a
    /// player say yes and then find they had nothing they were willing to give.
    /// </remarks>
    private bool AskOwedExploit()
    {
        if (_exploitsOwed.Count == 0 || State.IsWaitingForChoice)
            return false;

        var owed = _exploitsOwed[0];
        _exploitsOwed.RemoveAt(0);

        var eligible = State.Battlefield
            .Select(State.GetObject)
            .Where(o => Characteristics.Of(State, _abilities, o) is { IsCreature: true } computed
                && computed.ControllerId == owed.ChooserId)
            .ToList();

        if (eligible.Count == 0)
            return false;

        _exploitBeingAsked = owed;

        Ask(new PendingChoice
        {
            Id = $"exploit:{owed.ExploiterId.Value:N}",
            PlayerId = owed.ChooserId,
            Kind = ChoiceKind.Exploit,
            Prompt = "You may sacrifice a creature.",
            Options =
            [
                .. eligible.Select(o => new ChoiceOption(o.Id.Value.ToString("N"), o.Card.Name)),
                new ChoiceOption("none", "Sacrifice nothing"),
            ],
            MinPicks = 1,
            MaxPicks = 1,
        });

        return true;
    }

    /// <summary>Sacrifices the chosen creature and says that this is what happened.</summary>
    private void ResolveExploit(IReadOnlyList<string> picks)
    {
        if (_exploitBeingAsked is not { } owed)
            return;

        _exploitBeingAsked = null;

        if (picks.Count == 0
            || string.Equals(picks[0], "none", StringComparison.Ordinal)
            || !Guid.TryParse(picks[0], out var chosen))
        {
            return;
        }

        var id = new ObjectId(chosen);
        if (!State.TryGetObject(id, out var creature) || creature.Zone != Zone.Battlefield)
            return;

        Move(id, Zone.Graveyard, MoveCause.Sacrifice, owed.ChooserId);

        // Announced after the move, so a trigger that reads it sees a board the sacrifice has
        // already happened on - which is what "when this creature exploits a creature" means.
        Emit(new CreatureExploited(owed.ExploiterId, id));
    }

    private SoulbondPairRequested? _soulbondBeingAsked;

    /// <summary>The answer that pairs with nobody — declining is on the same list (CR 702.95a).</summary>
    private const string DeclineSoulbond = "none";

    /// <summary>
    /// Offers the oldest owed soulbond pairing, if any (CR 702.95a).
    /// </summary>
    /// <remarks>
    /// CR 702.95c is enforced here, against the state as the ability resolves: if this creature
    /// is no longer a creature, no longer on the battlefield, no longer this player's, or no
    /// longer unpaired, nobody becomes paired — and the same tests pick the candidates, so a
    /// question with no legal answer is not asked at all. The two arms differ only in the set:
    /// "when this creature enters" offers every unpaired creature its controller has, and
    /// "whenever another creature you control enters" offers exactly the newcomer.
    /// </remarks>
    private bool AskOwedSoulbond()
    {
        if (_soulbondsOwed.Count == 0 || State.IsWaitingForChoice)
            return false;

        var owed = _soulbondsOwed[0];
        _soulbondsOwed.RemoveAt(0);

        bool Pairable(GameObject o) =>
            o.Zone == Zone.Battlefield
            && o.Permanent is { PairedWithId: null }
            && Characteristics.Of(State, _abilities, o) is { IsCreature: true } computed
            && computed.ControllerId == owed.ChooserId;

        if (!State.TryGetObject(owed.SourceId, out var source) || !Pairable(source))
            return false;

        var eligible = State.Battlefield
            .Where(id => id != owed.SourceId)
            .Where(id => owed.PartnerId is not { } chosen || id == chosen)
            .Select(State.GetObject)
            .Where(Pairable)
            .ToList();

        if (eligible.Count == 0)
            return false;

        _soulbondBeingAsked = owed;

        Ask(new PendingChoice
        {
            Id = $"soulbond:{owed.SourceId.Value:N}",
            PlayerId = owed.ChooserId,
            Kind = ChoiceKind.Soulbond,
            Prompt = $"Pair {source.Card.Name} with another creature?",
            Options =
            [
                .. eligible.Select(o => new ChoiceOption(o.Id.Value.ToString("N"), o.Card.Name)),
                new ChoiceOption(DeclineSoulbond, "Pair with nobody."),
            ],
            MinPicks = 1,
            MaxPicks = 1,
        });

        return true;
    }

    /// <summary>Pairs the two creatures the player chose to bond (CR 702.95b).</summary>
    private void ResolveSoulbond(IReadOnlyList<string> picks)
    {
        if (_soulbondBeingAsked is not { } owed)
            return;

        _soulbondBeingAsked = null;

        if (picks.Count == 0
            || string.Equals(picks[0], DeclineSoulbond, StringComparison.Ordinal)
            || !Guid.TryParse(picks[0], out var chosen))
        {
            return;
        }

        // The game halts while a choice stands, but the answer still has to name something that
        // was on the list: an id from outside is checked the way every other pick is.
        var id = new ObjectId(chosen);
        if (!State.TryGetObject(id, out var partner)
            || partner.Zone != Zone.Battlefield
            || partner.Permanent is not { PairedWithId: null }
            || !State.TryGetObject(owed.SourceId, out var source)
            || source.Permanent is not { PairedWithId: null })
        {
            return;
        }

        Emit(new CreaturesPaired(owed.SourceId, id));
    }

    private PopulateRequested? _populateBeingAsked;

    /// <summary>Asks which creature token to copy (CR 701.36a).</summary>
    /// <remarks>
    /// Creature tokens only, and only the chooser's own. With none of them the effect does
    /// nothing, which is the card working rather than a question left standing.
    /// </remarks>
    private bool AskOwedPopulate()
    {
        if (_populatesOwed.Count == 0 || State.IsWaitingForChoice)
            return false;

        var owed = _populatesOwed[0];
        _populatesOwed.RemoveAt(0);

        var eligible = State.Battlefield
            .Select(State.GetObject)
            .Where(o => o.Card.CardTypes.HasFlag(CardType.Token)
                && Characteristics.Of(State, _abilities, o) is { IsCreature: true } computed
                && computed.ControllerId == owed.ChooserId)
            .ToList();

        if (eligible.Count == 0)
            return false;

        _populateBeingAsked = owed;

        Ask(new PendingChoice
        {
            Id = $"populate:{owed.ChooserId:N}",
            PlayerId = owed.ChooserId,
            Kind = ChoiceKind.Populate,
            Prompt = "Choose a creature token to copy.",
            Options = [.. eligible.Select(o => new ChoiceOption(o.Id.Value.ToString("N"), o.Card.Name))],
            MinPicks = 1,
            MaxPicks = 1,
        });

        return true;
    }

    /// <summary>Creates a token copying the one chosen.</summary>
    private void ResolvePopulate(IReadOnlyList<string> picks)
    {
        if (_populateBeingAsked is not { } owed)
            return;

        _populateBeingAsked = null;

        if (picks.Count == 0
            || !Guid.TryParse(picks[0], out var chosen)
            || !State.TryGetObject(new ObjectId(chosen), out var original)
            || original.Zone != Zone.Battlefield)
        {
            return;
        }

        Emit(new ObjectCreated(
            ObjectId.New(),
            Abilities.TokenCards.AsToken(original.Card),
            owed.ChooserId,
            owed.ChooserId,
            Zone.Battlefield));
    }

    private ManifestDreadRequested? _manifestDreadBeingAsked;

    /// <summary>Asks which of the cards looked at to manifest (CR 701.62a).</summary>
    private bool AskOwedManifestDread()
    {
        if (_manifestDreadsOwed.Count == 0 || State.IsWaitingForChoice)
            return false;

        var owed = _manifestDreadsOwed[0];
        _manifestDreadsOwed.RemoveAt(0);

        // Only the cards still where they were looked at. Something may have moved them in
        // between, and a choice offering a card that is no longer there is not a choice.
        var still = owed.Looked
            .Where(id => State.TryGetObject(id, out var card) && card.Zone == Zone.Library)
            .ToList();

        if (still.Count == 0)
            return false;

        _manifestDreadBeingAsked = owed;

        Ask(new PendingChoice
        {
            Id = $"manifest-dread:{owed.ChooserId:N}",
            PlayerId = owed.ChooserId,
            Kind = ChoiceKind.ManifestDread,
            Prompt = "Choose a card to manifest. The other goes to your graveyard.",
            Options = [.. still.Select(id => new ChoiceOption(
                id.Value.ToString("N"), State.GetObject(id).Card.Name))],
            MinPicks = 1,
            MaxPicks = 1,
        });

        return true;
    }

    /// <summary>Manifests the chosen card and mills the rest of what was looked at.</summary>
    private void ResolveManifestDread(IReadOnlyList<string> picks)
    {
        if (_manifestDreadBeingAsked is not { } owed)
            return;

        _manifestDreadBeingAsked = null;

        if (picks.Count == 0 || !Guid.TryParse(picks[0], out var chosen))
            return;

        var keep = new ObjectId(chosen);

        foreach (var id in owed.Looked)
        {
            if (!State.TryGetObject(id, out var card) || card.Zone != Zone.Library)
                continue;

            if (id == keep)
            {
                var arrived = ObjectId.New();
                Emit(new ObjectMoved(
                    id, arrived, Zone.Library, Zone.Battlefield, owed.ChooserId, MoveCause.Other));
                Emit(new CardManifested(arrived));
                continue;
            }

            // CR 701.62a: everything looked at and not manifested goes to the graveyard, so this
            // is the whole of the rest and not only the second card.
            Emit(new ObjectMoved(
                id, ObjectId.New(), Zone.Library, Zone.Graveyard, owed.ChooserId, MoveCause.Mill));
        }
    }

    private TouchedChoiceRequested? _touchedChoiceBeingAsked;

    /// <summary>
    /// Asks which of the cards this resolution touched to move (CR 608.2c).
    /// </summary>
    /// <remarks>
    /// Only the candidates still where the sentence left them. Between the resolution ending and
    /// this question being asked, a replacement or another player's trigger may have moved one,
    /// and a menu naming a card that is somewhere else is a menu with a wrong answer on it.
    /// <para>
    /// Nothing left to offer is the card working rather than a stalled game: a mill that turned up
    /// no permanent card offers nothing and takes nothing, which is what the card says.
    /// </para>
    /// </remarks>
    private bool AskOwedTouchedChoice()
    {
        if (_touchedChoicesOwed.Count == 0 || State.IsWaitingForChoice)
            return false;

        var owed = _touchedChoicesOwed[0];
        _touchedChoicesOwed.RemoveAt(0);

        var still = owed.Candidates
            .Where(id => State.TryGetObject(id, out var card) && card.Zone == owed.From)
            .ToList();

        if (still.Count == 0)
            return false;

        _touchedChoiceBeingAsked = owed;

        Ask(new PendingChoice
        {
            Id = $"take-from-touched:{owed.ChooserId:N}",
            PlayerId = owed.ChooserId,
            Kind = ChoiceKind.TakeFromTouched,
            Prompt = owed.Least == 0
                ? $"You may choose up to {Math.Min(owed.Most, still.Count)} to move to your {owed.To}."
                : $"Choose up to {Math.Min(owed.Most, still.Count)} to move to your {owed.To}.",
            Options = [.. still.Select(id => new ChoiceOption(
                id.Value.ToString("N"), State.GetObject(id).Card.Name))],
            MinPicks = Math.Min(owed.Least, still.Count),
            MaxPicks = Math.Min(owed.Most, still.Count),
        });

        return true;
    }

    /// <summary>Moves what was picked, and nothing else (CR 608.2c).</summary>
    /// <remarks>
    /// Each pick is checked against the candidates the question was built from as well as against
    /// where it is now. The answer arrives as ids from outside the engine, and an id that was
    /// never on the menu is not made legal by naming a card in the right zone.
    /// <para>
    /// A card goes to its <em>owner's</em> hand, not the chooser's (CR 400.3). Every card this
    /// reads about today was milled from the chooser's own library, so the two are the same
    /// player - which is exactly why taking the chooser would look right for ever.
    /// </para>
    /// </remarks>
    private void ResolveTouchedChoice(IReadOnlyList<string> picks)
    {
        if (_touchedChoiceBeingAsked is not { } owed)
            return;

        _touchedChoiceBeingAsked = null;

        foreach (var pick in picks)
        {
            if (!Guid.TryParse(pick, out var chosen))
                continue;

            var id = new ObjectId(chosen);

            if (!owed.Candidates.Contains(id)
                || !State.TryGetObject(id, out var card)
                || card.Zone != owed.From)
            {
                continue;
            }

            Emit(new ObjectMoved(
                id, ObjectId.New(), owed.From, owed.To, card.OwnerId, owed.Cause));
        }
    }

    private ConniveRequested? _conniveBeingAsked;

    /// <summary>Asks which card a connive discards (CR 701.50a).</summary>
    /// <remarks>
    /// An empty hand is not a stalled game: there is nothing to discard, so nothing is discarded
    /// and no counter is earned. That is the card working, not the question going unanswered.
    /// </remarks>
    private bool AskOwedConnive()
    {
        if (_connivesOwed.Count == 0 || State.IsWaitingForChoice)
            return false;

        var owed = _connivesOwed[0];
        _connivesOwed.RemoveAt(0);

        var hand = State.GetPlayer(owed.ChooserId).Hand;
        if (hand.IsEmpty)
            return false;

        _conniveBeingAsked = owed;

        Ask(new PendingChoice
        {
            Id = $"connive:{owed.SourceId.Value:N}",
            PlayerId = owed.ChooserId,
            Kind = ChoiceKind.Connive,
            Prompt = "Choose a card to discard.",
            Options = [.. hand.Select(id => new ChoiceOption(
                id.Value.ToString("N"), State.GetObject(id).Card.Name))],
            MinPicks = 1,
            MaxPicks = 1,
        });

        return true;
    }

    /// <summary>Discards the chosen card and grows the permanent if it was not a land.</summary>
    private void ResolveConnive(IReadOnlyList<string> picks)
    {
        if (_conniveBeingAsked is not { } owed)
            return;

        _conniveBeingAsked = null;

        if (picks.Count == 0
            || !Guid.TryParse(picks[0], out var chosen)
            || !State.TryGetObject(new ObjectId(chosen), out var card)
            || card.Zone != Zone.Hand)
        {
            return;
        }

        Move(card.Id, Zone.Graveyard, MoveCause.Discard, owed.ChooserId);

        // CR 701.50a: the counter is earned by discarding a *nonland* card, and it goes on the
        // permanent that connived - which may have left by now, in which case there is nothing
        // to put it on and that is the right answer.
        if (card.Card.CardTypes.HasFlag(CardType.Land))
            return;

        if (State.TryGetObject(owed.SourceId, out var conniver)
            && conniver.Zone == Zone.Battlefield)
        {
            Emit(new CountersChanged(owed.SourceId, CounterKinds.PlusOnePlusOne, 1));
        }
    }

    private CreatureTypeChoiceRequested? _creatureTypeChoiceBeingAsked;

    /// <summary>Asks which creature type a permanent becomes (CR 205.1b).</summary>
    /// <remarks>
    /// The offer is the same list an entering permanent gets - the types actually in this game
    /// plus a handful of common ones. Every creature type there is runs to several hundred words
    /// and no board can show them; the cards that ask this are always naming something in front
    /// of the player.
    /// </remarks>
    private bool AskOwedCreatureTypeChoice()
    {
        if (_creatureTypeChoicesOwed.Count == 0 || State.IsWaitingForChoice)
            return false;

        var owed = _creatureTypeChoicesOwed[0];
        _creatureTypeChoicesOwed.RemoveAt(0);

        var options = CreatureTypesInPlay();
        if (options.Count == 0)
            return false;

        _creatureTypeChoiceBeingAsked = owed;

        Ask(new PendingChoice
        {
            Id = $"creature-type:{owed.SourceId.Value:N}",
            PlayerId = owed.ChooserId,
            Kind = ChoiceKind.ChooseCreatureType,
            Prompt = "Choose a creature type.",
            Options = [.. options.Select(o => new ChoiceOption(o, o))],
            MinPicks = 1,
            MaxPicks = 1,
        });

        return true;
    }

    /// <summary>Applies the named creature type to whatever asked for it.</summary>
    private void ResolveCreatureTypeChoice(IReadOnlyList<string> picks)
    {
        if (_creatureTypeChoiceBeingAsked is not { } owed)
            return;

        _creatureTypeChoiceBeingAsked = null;

        if (picks.Count == 0 || owed.Affected.IsEmpty)
            return;

        Emit(new ContinuousEffectCreated(
            Guid.NewGuid(),
            Cards.GenerativeEffects.BecomesCreatureTypeId(picks[0]),
            owed.Affected,
            State.TurnNumber));
    }

    private LandTypeChoiceRequested? _landTypeChoiceBeingAsked;

    /// <summary>Asks which basic land type a land becomes (CR 305.6, 305.7).</summary>
    /// <remarks>
    /// The offer is the five CR 305.6 names and nothing else. Unlike the creature type's menu it
    /// is not narrowed to what is in play: a land does not have to see a Swamp to become one, and
    /// narrowing it that way would leave the commonest board — one player, one colour — with a
    /// question that could not be asked and an ability that had been paid for.
    /// </remarks>
    private bool AskOwedLandTypeChoice()
    {
        if (_landTypeChoicesOwed.Count == 0 || State.IsWaitingForChoice)
            return false;

        var owed = _landTypeChoicesOwed[0];
        _landTypeChoicesOwed.RemoveAt(0);
        _landTypeChoiceBeingAsked = owed;

        Ask(new PendingChoice
        {
            Id = $"land-type:{owed.SourceId.Value:N}",
            PlayerId = owed.ChooserId,
            Kind = ChoiceKind.ChooseBasicLandType,
            Prompt = "Choose a basic land type.",

            // From the compiler's own list, so the five are spelled out once in this repository.
            Options = [.. Cards.EffectPhrase.BasicLandTypes.Select(t => new ChoiceOption(t, t))],
            MinPicks = 1,
            MaxPicks = 1,
        });

        return true;
    }

    /// <summary>Applies the named land type to whatever asked for it (CR 305.7).</summary>
    /// <remarks>
    /// Which of the two effects the answer builds is the whole of the difference between the two
    /// printed spellings, and the request carried it here from the sentence: "becomes the basic
    /// land type of your choice" replaces the land's types and takes its rules text with them,
    /// and the same sentence ending "in addition to its other types" keeps both.
    /// <para>
    /// The effect is stamped with the turn number, which is what ends it in the cleanup step
    /// (CR 514.2). Every card in this family prints "until end of turn", and the compiler refuses
    /// the sentence without it, so there is no arm here for a duration nothing asks for.
    /// </para>
    /// </remarks>
    private void ResolveLandTypeChoice(IReadOnlyList<string> picks)
    {
        if (_landTypeChoiceBeingAsked is not { } owed)
            return;

        _landTypeChoiceBeingAsked = null;

        if (picks.Count == 0 || owed.Affected.IsEmpty)
            return;

        // An answer from outside is checked against the offer rather than trusted: a type that is
        // not one of the five would build a retyping the rule never grants.
        if (!Cards.EffectPhrase.Specs.IsBasicLandType(picks[0]))
            return;

        Emit(new ContinuousEffectCreated(
            Guid.NewGuid(),
            owed.InAddition
                ? Cards.GenerativeEffects.GainsCreatureTypeId(picks[0])
                : Cards.GenerativeEffects.BecomesCreatureTypeId(picks[0]),
            owed.Affected,
            State.TurnNumber));
    }

    private bool AskOwedColorChoice()
    {
        if (_colorChoicesOwed.Count == 0 || State.IsWaitingForChoice)
            return false;

        var owed = _colorChoicesOwed[0];
        _colorChoicesOwed.RemoveAt(0);
        _colorChoiceBeingAsked = owed;

        Ask(new PendingChoice
        {
            Id = $"colour:{owed.SourceId.Value:N}",
            PlayerId = owed.ChooserId,
            Kind = ChoiceKind.ChooseColor,
            Prompt = "Choose a color.",

            // The five colours, and only those: colourless is not one (CR 105.1), so an effect
            // that asked for "a color" and was handed colourless would protect from nothing.
            Options =
            [
                new ChoiceOption(nameof(ManaColor.White), "White"),
                new ChoiceOption(nameof(ManaColor.Blue), "Blue"),
                new ChoiceOption(nameof(ManaColor.Black), "Black"),
                new ChoiceOption(nameof(ManaColor.Red), "Red"),
                new ChoiceOption(nameof(ManaColor.Green), "Green"),
            ],
            MinPicks = 1,
            MaxPicks = 1,
        });

        return true;
    }

    /// <summary>
    /// Pays out any owed mana colour that has only one answer (CR 118.3).
    /// </summary>
    /// <remarks>
    /// A question with one possible answer is not a question, and the effect that raised it
    /// already says so - this is the second lock, for a menu that narrowed between the ask and
    /// now. Settled rather than asked, so the sweep goes round again: the mana can have triggered
    /// something, and returning as though a question were pending would leave that on the floor.
    /// </remarks>
    private bool SettleForcedManaColors()
    {
        if (_manaColorChoicesOwed.Count == 0 || State.IsWaitingForChoice)
            return false;

        var owed = _manaColorChoicesOwed[0];
        if (owed.Options.Count > 1)
            return false;

        _manaColorChoicesOwed.RemoveAt(0);

        if (owed.Options.Count == 0 || !State.Players.ContainsKey(owed.PlayerId))
            return false;

        Emit(new ManaAdded(
            owed.PlayerId,
            Abilities.AddChosenMana.Colour(owed.Options[0]),
            owed.Amount,
            null,
            owed.SourceId));

        return true;
    }

    /// <summary>
    /// Asks the oldest owed mana colour, if any (CR 106.1a).
    /// </summary>
    /// <remarks>
    /// Asked from the sweep rather than where the effect ran, for the reason every deferred
    /// question here is: a resolution is never stopped half way through. The mana lands a beat
    /// after the ability that promised it and still before anybody receives priority, which is
    /// what CR 106.4 asks for - it is in the pool for the spell the player was about to cast.
    /// </remarks>
    private bool AskOwedManaColorChoice()
    {
        if (_manaColorChoicesOwed.Count == 0 || State.IsWaitingForChoice)
            return false;

        var owed = _manaColorChoicesOwed[0];
        _manaColorChoicesOwed.RemoveAt(0);

        if (owed.Options.Count <= 1 || !State.Players.ContainsKey(owed.PlayerId))
            return false;

        _manaColorChoiceBeingAsked = owed;

        Ask(new PendingChoice
        {
            Id = $"mana-colour:{owed.PlayerId:N}",
            PlayerId = owed.PlayerId,
            Kind = ChoiceKind.ChooseManaColor,
            Prompt = owed.Amount == 1
                ? "Choose a color of mana to add."
                : $"Choose a color; {owed.Amount} mana of it are added.",

            // The menu the effect worked out, not one built again here. "Any type that land
            // produced" was read off a permanent that may have left the battlefield by now, and
            // a question whose options moved underneath it is not the one that was asked.
            Options = [.. owed.Options.Select(c => new ChoiceOption(c.ToString(), ColorLabel(c)))],
            MinPicks = 1,
            MaxPicks = 1,
        });

        return true;
    }

    private ManaColorChoiceRequested? _manaColorChoiceBeingAsked;

    /// <summary>Adds the mana in the colour that was named (CR 106.4).</summary>
    private void ResolveManaColorChoice(IReadOnlyList<string> picks)
    {
        if (_manaColorChoiceBeingAsked is not { } owed)
            return;

        _manaColorChoiceBeingAsked = null;

        if (picks.Count == 0 || !Enum.TryParse<ManaColor>(picks[0], out var chosen))
            return;

        // Only from the menu that was offered. Every other choice in this engine answers with
        // an object's id and is unambiguous by construction; a colour is a word, and a word can
        // arrive from outside naming something that was never on the list.
        if (!owed.Options.Contains(chosen))
            return;

        Emit(new ManaAdded(
            owed.PlayerId,
            Abilities.AddChosenMana.Colour(chosen),
            owed.Amount,
            null,
            owed.SourceId));
    }

    /// <summary>
    /// Puts up an owed shield whose menu has narrowed to one source (CR 118.3).
    /// </summary>
    /// <remarks>
    /// The mana colour's forced arm, for the same rule and the same reason: a question with one
    /// possible answer is not a question, and a Circle of Protection activated when the only red
    /// permanent on the table is the one about to burn you has exactly one answer. The menu is
    /// re-read here rather than trusted, because the board can have changed between the effect
    /// resolving and this sweep — a source that has left is not on the list, and if that empties
    /// it the shield is simply not made (CR 609.7b).
    /// </remarks>
    private bool SettleForcedDamageSource()
    {
        if (_damageSourceChoicesOwed.Count == 0 || State.IsWaitingForChoice)
            return false;

        var owed = _damageSourceChoicesOwed[0];
        var live = LiveDamageSources(owed);

        if (live.Count > 1)
            return false;

        _damageSourceChoicesOwed.RemoveAt(0);

        if (live.Count == 0)
            return false;

        Emit(new PreventionEffectCreated(owed.Shield with { Source = live[0] }));
        return true;
    }

    /// <summary>
    /// Asks the oldest owed source choice, if any (CR 609.7b).
    /// </summary>
    /// <remarks>
    /// Asked from the sweep rather than where the effect ran, for the reason every deferred
    /// question here is: a resolution is never stopped half way through. The shield goes up a
    /// beat after the ability that promised it and still before anybody receives priority, which
    /// is what a Circle of Protection needs — the damage it was activated against has not
    /// happened yet (CR 615.4).
    /// </remarks>
    private bool AskOwedDamageSourceChoice()
    {
        if (_damageSourceChoicesOwed.Count == 0 || State.IsWaitingForChoice)
            return false;

        var owed = _damageSourceChoicesOwed[0];
        var live = LiveDamageSources(owed);

        if (live.Count <= 1 || !State.Players.ContainsKey(owed.PlayerId))
            return false;

        _damageSourceChoicesOwed.RemoveAt(0);
        _damageSourceChoiceBeingAsked = owed;

        Ask(new PendingChoice
        {
            Id = $"damage-source:{owed.PlayerId:N}",
            PlayerId = owed.PlayerId,
            Kind = ChoiceKind.ChooseDamageSource,
            Prompt = "Choose a source of damage to prevent.",
            Options =
            [
                .. live.Select(id => new ChoiceOption(
                    id.Value.ToString("N"), State.GetObject(id).Card.Name)),
            ],
            MinPicks = 1,
            MaxPicks = 1,
        });

        return true;
    }

    /// <summary>
    /// The offered sources that are still objects (CR 609.7a).
    /// </summary>
    /// <remarks>
    /// The menu the effect worked out, minus whatever has left the game since. It is not rebuilt
    /// from the board: the effect's own reading of "a red source" is what was promised, and a
    /// list computed again here would quietly widen or narrow it. What this does drop is an id
    /// that no longer names anything — a shield round a source that has gone watches nothing
    /// (<see cref="Preventions.Watches"/> says so), so offering it is offering a wasted answer.
    /// </remarks>
    private List<ObjectId> LiveDamageSources(DamageSourceChoiceRequested owed) =>
        [.. owed.Options.Where(id => State.TryGetObject(id, out _))];

    private DamageSourceChoiceRequested? _damageSourceChoiceBeingAsked;

    /// <summary>Puts the shield round the source that was named (CR 609.7b).</summary>
    /// <remarks>
    /// An answer that is not on the menu makes no shield at all. Every other selection choice in
    /// this engine can afford to ignore an unknown pick and carry on with the rest; here the rest
    /// is a prevention effect with an empty source slot, which prevents damage from everything.
    /// </remarks>
    private void ResolveDamageSourceChoice(IReadOnlyList<string> picks)
    {
        if (_damageSourceChoiceBeingAsked is not { } owed)
            return;

        _damageSourceChoiceBeingAsked = null;

        if (picks.Count == 0 || !Guid.TryParseExact(picks[0], "N", out var picked))
            return;

        var chosen = new ObjectId(picked);
        if (!owed.Options.Contains(chosen) || !State.TryGetObject(chosen, out _))
            return;

        Emit(new PreventionEffectCreated(owed.Shield with { Source = chosen }));
    }

    /// <summary>What a mana type is called on a button (CR 106.1b).</summary>
    private static string ColorLabel(ManaColor colour) => colour switch
    {
        ManaColor.White => "White",
        ManaColor.Blue => "Blue",
        ManaColor.Black => "Black",
        ManaColor.Red => "Red",
        ManaColor.Green => "Green",
        _ => "Colorless",
    };

    private ColorChoiceRequested? _colorChoiceBeingAsked;

    /// <summary>Applies a named colour to whatever asked for it.</summary>
    private void ResolveColorChoice(IReadOnlyList<string> picks)
    {
        if (_colorChoiceBeingAsked is not { } owed)
            return;

        _colorChoiceBeingAsked = null;

        if (picks.Count == 0 || !Enum.TryParse<ManaColor>(picks[0], out var colour))
            return;

        if (owed.Affected.IsEmpty)
            return;

        // Which effect the answer builds is the only thing that differs between the uses: the
        // question, the asking and the answering are one mechanism.
        var definition = owed.Use switch
        {
            ColorChoiceUse.BecomesColor => Cards.GenerativeEffects.BecomesColorId(colour),
            _ => ProtectionFrom(colour) is var granted && granted != KeywordAbility.None
                ? Cards.GenerativeEffects.GrantId(granted)
                : null,
        };

        if (definition is null)
            return;

        Emit(new ContinuousEffectCreated(
            Guid.NewGuid(), definition, owed.Affected, State.TurnNumber));
    }

    /// <summary>The keyword that is protection from one colour (CR 702.16).</summary>
    private static KeywordAbility ProtectionFrom(ManaColor colour) => colour switch
    {
        ManaColor.White => KeywordAbility.ProtectionFromWhite,
        ManaColor.Blue => KeywordAbility.ProtectionFromBlue,
        ManaColor.Black => KeywordAbility.ProtectionFromBlack,
        ManaColor.Red => KeywordAbility.ProtectionFromRed,
        ManaColor.Green => KeywordAbility.ProtectionFromGreen,
        _ => KeywordAbility.None,
    };

    private bool AskOwedHandChoice()
    {
        if (_handChoicesOwed.Count == 0 || State.IsWaitingForChoice)
            return false;

        var owed = _handChoicesOwed[0];
        _handChoicesOwed.RemoveAt(0);

        var eligible = State.GetPlayer(owed.OwnerId).Hand
            .Where(id => SearchFilters.Matches(owed.FilterId, State.GetObject(id).Card))
            .ToList();

        if (eligible.Count == 0)
            return false;

        _handChoiceBeingAsked = owed;

        Ask(new PendingChoice
        {
            Id = $"hand:{owed.ChooserId:N}",
            PlayerId = owed.ChooserId,
            Kind = ChoiceKind.ChooseCardInHand,
            Prompt = owed.ChooserId == owed.OwnerId
                ? $"You may put {owed.Prompt} from your hand onto the battlefield."
                : $"Choose {owed.Prompt} from that player's hand.",
            Options = [.. eligible.Select(id => new ChoiceOption(
                id.Value.ToString("N"), State.GetObject(id).Card.Name))],
            MinPicks = owed.Optional ? 0 : 1,
            MaxPicks = 1,
        });

        return true;
    }

    private HandChoiceRequested? _handChoiceBeingAsked;

    /// <summary>Moves the card the chooser picked out of its owner's hand.</summary>
    private void ResolveHandChoice(IReadOnlyList<string> picks)
    {
        if (_handChoiceBeingAsked is not { } owed)
            return;

        _handChoiceBeingAsked = null;

        if (picks.Count == 0)
            return;

        var id = new ObjectId(Guid.ParseExact(picks[0], "N"));
        if (!State.TryGetObject(id, out var card) || card.Zone != Zone.Hand)
            return;

        // The effect is looked up first so the discard path keeps reading its destination off
        // the card exactly as before; the event carries it only for the effects that have no
        // RevealAndTake behind them to find.
        var destination = FindHandChoice(owed)?.Destination ?? owed.Destination;

        // The owner discards it, not the chooser — the cause names who lost the card, and a
        // "whenever a player discards" trigger has to see the right one.
        var landed = Move(
            id,
            destination,
            destination switch
            {
                Zone.Graveyard => MoveCause.Discard,
                Zone.Exile => MoveCause.Exile,

                // Not MoveCause.Play: putting a land onto the battlefield is not playing it, and
                // reading it as a land drop would spend the one the turn allows (CR 305.1).
                _ => MoveCause.Other,
            },
            owed.OwnerId);

        if (owed.Tapped && destination == Zone.Battlefield)
            Emit(new PermanentTapped(landed));
    }

    /// <summary>Reads the choice back out of the card that asked it (CR 701.16).</summary>
    private RevealAndTake? FindHandChoice(HandChoiceRequested owed)
    {
        if (CardBehind(owed.SourceId) is not { } behind)
            return null;

        var effects = owed.AbilityId is { } abilityId
            ? EffectsOfAbility(behind, abilityId, owed.SourceId)
            : _abilities.SpellOf(behind)?.Effects ?? [];

        // Looked up through the whole tree rather than by position in the top-level list: a
        // locator effect can sit inside another effect's branch, and indexing the outer list
        // there finds the branch's owner instead of the thing that asked.
        return EffectTree.Locate<RevealAndTake>(effects, owed.EffectIndex);
    }

    /// <summary>
    /// Asks the oldest owed permanent choice, if any (CR 609.4).
    /// </summary>
    /// <remarks>
    /// Nothing to choose is not a failure — the effect simply does as much as it can (CR 608.2b
    /// is about targets, not about choices made on resolution), so an empty set is skipped rather
    /// than stalling the game on a question with no answers.
    /// </remarks>
    private bool AskOwedPermanentChoice()
    {
        if (_choicesOwed.Count == 0 || State.IsWaitingForChoice)
            return false;

        var (owed, effect) = _choicesOwed[0];
        _choicesOwed.RemoveAt(0);

        // Both of the spec's filters, not just the first. A spec carries an ordinary filter and
        // a source-aware one, and "another creature" lives entirely in the second: asking only
        // the first offered the source its own name and let a card sacrifice itself to an
        // ability that says it may not.
        var asking = State.TryGetObject(owed.SourceId, out var raiser) ? raiser : null;

        // "Sacrifice a creature" looks at the battlefield; "return a creature card from your
        // graveyard to your hand" is the same question asked of a different zone, and "exile a
        // card from your hand" is a third. The control test belongs only to the battlefield: a
        // graveyard and a hand are already one player's, and asking who controls a card in one
        // is not a question the rules ask (CR 108.4).
        //
        // The hand is hidden and stays hidden. What makes that safe is not this method: the
        // options of a pending choice are projected only to the player being asked
        // (PlayerViewProjector.ProjectChoice), and the player being asked here is always the one
        // whose hand it is - a card that made somebody else pick from your hand would be a
        // different question and is not this one.
        var from = effect.From switch
        {
            Zone.Graveyard => State.GetPlayer(owed.PlayerId).Graveyard,
            Zone.Hand => State.GetPlayer(owed.PlayerId).Hand,
            _ => State.Battlefield,
        };

        var eligible = from
            .Where(id => effect.What.ObjectFilter?.Invoke(
                State, _abilities, State.GetObject(id), owed.PlayerId) != false)
            .Where(id => effect.What.SourceFilter?.Invoke(
                State, _abilities, State.GetObject(id), asking, owed.PlayerId) != false)
            .Where(id => effect.From != Zone.Battlefield
                || ControllerOf(State.GetObject(id)) == owed.PlayerId)
            .ToList();

        if (eligible.Count == 0)
            return false;

        _permanentChoiceBeingAsked = (owed, effect);

        Ask(new PendingChoice
        {
            Id = $"choose:{owed.PlayerId:N}",
            PlayerId = owed.PlayerId,
            Kind = ChoiceKind.ChoosePermanent,
            Prompt = $"Choose {owed.Prompt}.",
            Options = [.. eligible.Select(id => new ChoiceOption(
                id.Value.ToString("N"), State.GetObject(id).Card.Name))],
            MinPicks = 1,
            MaxPicks = 1,
        });

        return true;
    }

    private (ChoosePermanentRequested Request, ChooseAndMove Effect)? _permanentChoiceBeingAsked;

    /// <summary>Attackers with enlist that have not yet been offered the choice this combat.</summary>
    private readonly List<ObjectId> _enlistsOwed = [];

    private ObjectId? _enlistBeingAsked;

    /// <summary>The answer that takes no creature, for a choice that is "up to one".</summary>
    private const string DeclineEnlist = "none";

    /// <summary>
    /// Offers one attacker's enlist (CR 702.154a).
    /// </summary>
    /// <remarks>
    /// "Up to one untapped creature you control that you didn't choose to attack with and that
    /// either has haste or has been under your control continuously since this turn began" - the
    /// last clause is summoning sickness said the long way round, which is what the flag on the
    /// permanent already records.
    /// <para>
    /// A creature cannot enlist itself (CR 702.154c), and with nothing eligible the question is
    /// not asked at all - an offer that cannot be taken is not an offer.
    /// </para>
    /// </remarks>
    private bool AskOwedEnlist()
    {
        if (_enlistsOwed.Count == 0 || State.IsWaitingForChoice)
            return false;

        var attackerId = _enlistsOwed[0];
        _enlistsOwed.RemoveAt(0);

        if (!State.TryGetObject(attackerId, out var attacker))
            return false;

        var controller = ControllerOf(attacker);

        var eligible = State.Battlefield
            .Where(id => id != attackerId)
            .Where(id => !State.Combat.Attackers.ContainsKey(id))
            .Select(State.GetObject)
            .Where(o => o.Permanent is { IsTapped: false }
                && ControllerOf(o) == controller
                && CanBeEnlisted(o))
            .ToList();

        if (eligible.Count == 0)
            return false;

        _enlistBeingAsked = attackerId;

        Ask(new PendingChoice
        {
            Id = $"enlist:{attackerId.Value:N}",
            PlayerId = controller,
            Kind = ChoiceKind.Enlist,
            Prompt = $"Tap a creature to enlist for {attacker.Card.Name}?",
            Options =
            [
                .. eligible.Select(o => new ChoiceOption(o.Id.Value.ToString("N"), o.Card.Name)),
                new ChoiceOption(DeclineEnlist, "Enlist nobody."),
            ],
            MinPicks = 1,
            MaxPicks = 1,
        });

        return true;
    }

    /// <summary>Whether this permanent may be tapped to pay an enlist cost.</summary>
    /// <remarks>
    /// CR 702.154a asks for a creature "that either has haste or has been under your control
    /// continuously since this turn began". That is not the summoning-sickness flag read on its
    /// own: CR 302.6 makes haste <em>ignore</em> the sickness rather than remove it, so a hasty
    /// creature still carries <see cref="PermanentState.HasSummoningSickness"/> and reading the
    /// flag alone made every hasty creature permanently ineligible to be enlisted.
    /// <para>
    /// GAME_ENGINE_FEATURE.md recorded the clause as "summoning sickness said the long way round,
    /// which the permanent already records". The first half is right and the second is not, which
    /// is how the defect got written: the sentence the rule is really asking is "could this
    /// creature have attacked", and that is the same question <see cref="CombatRules.CannotAttack"/>
    /// answers, not a field.
    /// </para>
    /// </remarks>
    private bool CanBeEnlisted(GameObject candidate)
    {
        var computed = Characteristics.Of(State, _abilities, candidate);

        return computed.CardTypes.HasFlag(CardType.Creature)
            && (candidate.Permanent?.HasSummoningSickness != true
                || computed.Has(KeywordAbility.Haste));
    }

    /// <summary>
    /// Taps the enlisted creature and gives the attacker its power (CR 702.154a).
    /// </summary>
    /// <remarks>
    /// The pump is a fixed number rather than a running count, because the rule fixes it now:
    /// "+X/+0 where X is the tapped creature's power" is read as the ability resolves, and a
    /// creature that grows or dies afterwards does not change what was added.
    /// <para>
    /// **A simplification, stated rather than hidden.** The rule makes the pump a triggered
    /// ability linked to the tap (CR 702.154b), so it uses the stack and can be responded to.
    /// Here it happens with the tap. What that costs is the window between them.
    /// </para>
    /// </remarks>
    private void ResolveEnlist(IReadOnlyList<string> picks)
    {
        var attackerId = _enlistBeingAsked;
        _enlistBeingAsked = null;

        if (attackerId is not { } attacking
            || picks.Count == 0
            || string.Equals(picks[0], DeclineEnlist, StringComparison.Ordinal))
        {
            return;
        }

        var helper = State.Battlefield.FirstOrDefault(
            id => string.Equals(id.Value.ToString("N"), picks[0], StringComparison.Ordinal));

        if (helper == default || !State.TryGetObject(helper, out var enlisted))
            return;

        var power = Characteristics.Of(State, _abilities, enlisted).Power ?? 0;

        Emit(new PermanentTapped(helper));

        if (power != 0)
        {
            Emit(new ContinuousEffectCreated(
                Guid.NewGuid(),
                Cards.GenerativeEffects.PumpId(power, 0),
                [attacking],
                State.TurnNumber));
        }
    }

    /// <summary>Reads the choice back out of the card that asked it (CR 609.4).</summary>
    private ChooseAndMove? FindPermanentChoice(ChoosePermanentRequested owed)
    {
        if (CardBehind(owed.SourceId) is not { } behind)
            return null;

        var effects = owed.AbilityId is { } abilityId
            ? EffectsOfAbility(behind, abilityId, owed.SourceId)
            : _abilities.SpellOf(behind)?.Effects ?? [];

        // Looked up through the whole tree rather than by position in the top-level list: a
        // locator effect can sit inside another effect's branch, and indexing the outer list
        // there finds the branch's owner instead of the thing that asked.
        return EffectTree.Locate<ChooseAndMove>(effects, owed.EffectIndex);
    }

    /// <summary>Moves the permanent the player chose.</summary>
    private void ResolvePermanentChoice(IReadOnlyList<string> picks)
    {
        if (_permanentChoiceBeingAsked is not var (owed, effect) || _permanentChoiceBeingAsked is null)
            return;

        _permanentChoiceBeingAsked = null;

        if (picks.Count == 0)
            return;

        // Checked against the zone the effect asked about rather than against the battlefield.
        // The card must still be where it was when it was offered — it can have moved while the
        // question stood — but which zone that is belongs to the effect: a sacrifice asks about
        // the battlefield and a reanimation asks about a graveyard.
        var id = new ObjectId(Guid.ParseExact(picks[0], "N"));
        if (State.TryGetObject(id, out var chosen) && chosen.Zone == effect.From)
            Move(id, effect.Destination, effect.Cause, owed.PlayerId);
    }

    /// <summary>
    /// Asks the oldest owed proliferation, if any (CR 701.34a).
    /// </summary>
    /// <remarks>
    /// Offers permanents and players together, because counters live on both — poison and energy
    /// are on the player, and proliferating them is the whole point of half the cards that do
    /// this. The two are told apart by the option id, the same way a trigger's targets are.
    /// </remarks>
    private bool AskOwedProliferate()
    {
        if (_proliferationsOwed.Count == 0 || State.IsWaitingForChoice)
            return false;

        var owed = _proliferationsOwed[0];
        _proliferationsOwed.RemoveAt(0);

        var options = new List<ChoiceOption>();

        foreach (var id in State.Battlefield)
        {
            var obj = State.GetObject(id);
            if (obj.Permanent is { Counters.IsEmpty: false })
                options.Add(new ChoiceOption(id.Value.ToString("N"), obj.Card.Name));
        }

        foreach (var playerId in State.TurnOrder)
        {
            var player = State.GetPlayer(playerId);
            if (!player.HasLost && player.PoisonCounters > 0)
                options.Add(new ChoiceOption($"player:{playerId:N}", player.Name));
        }

        if (options.Count == 0)
            return false;

        _proliferateBeingAsked = owed;

        Ask(new PendingChoice
        {
            Id = $"proliferate:{owed.PlayerId:N}",
            PlayerId = owed.PlayerId,
            Kind = ChoiceKind.Proliferate,
            Prompt = "Choose any number of permanents and players to proliferate.",
            Options = [.. options],
            // CR 701.34a: "any number", which includes none.
            MinPicks = 0,
            MaxPicks = options.Count,
        });

        return true;
    }

    private ProliferateRequested? _proliferateBeingAsked;

    /// <summary>Gives each chosen permanent and player another of each counter it has.</summary>
    private void ResolveProliferate(IReadOnlyList<string> picks)
    {
        if (_proliferateBeingAsked is null)
            return;

        _proliferateBeingAsked = null;

        foreach (var pick in picks.Distinct(StringComparer.Ordinal))
        {
            if (pick.StartsWith("player:", StringComparison.Ordinal))
            {
                var playerId = Guid.ParseExact(pick["player:".Length..], "N");
                if (State.Players.ContainsKey(playerId))
                    Emit(new PoisonCountersChanged(playerId, 1));

                continue;
            }

            var id = new ObjectId(Guid.ParseExact(pick, "N"));
            if (!State.TryGetObject(id, out var obj) || obj.Permanent is not { } permanent)
                continue;

            // "Another counter of each kind already there" — every kind, not one of them.
            foreach (var (kind, count) in permanent.Counters)
            {
                if (count > 0)
                    Emit(new CountersChanged(id, kind, 1));
            }
        }
    }

    /// <summary>
    /// Asks the oldest owed library search, if any (CR 701.23).
    /// </summary>
    /// <remarks>
    /// The library is shuffled whether or not anything is found (CR 701.23e), because the search
    /// itself is what reveals the order — so skipping the shuffle on a failed search would hand
    /// the searcher information they are not entitled to keep.
    /// </remarks>
    /// <summary>
    /// Offers to cast a card revealed as it was drawn, for its miracle cost (CR 702.94a).
    /// </summary>
    /// <remarks>
    /// The same simplification the monarch and cipher hooks make: the rules put a triggered
    /// ability on the stack and this makes the offer directly, so the window in which an opponent
    /// could respond before the card is cast is what is lost. The offer itself closes the way
    /// every other one does, when its player passes priority.
    /// </remarks>
    private bool OfferOwedMiracles()
    {
        if (_miraclesOwed.Count == 0 || State.IsWaitingForChoice)
            return false;

        var offered = false;

        foreach (var (id, playerId, cost) in _miraclesOwed)
        {
            // Still in hand: a card drawn and then discarded before this ran is no longer there
            // to be revealed.
            if (State.TryGetObject(id, out var card) && card.Zone == Zone.Hand)
            {
                Emit(new FreeCastOffered(id, playerId, cost));
                offered = true;
            }
        }

        _miraclesOwed.Clear();
        return offered;
    }

    /// <summary>Asks a permanent's owner which end of their library it goes to.</summary>
    private bool AskOwedLibraryEnd()
    {
        if (_libraryEndsOwed.Count == 0 || State.IsWaitingForChoice)
            return false;

        var owed = _libraryEndsOwed[0];
        _libraryEndsOwed.RemoveAt(0);

        // Gone in the meantime: something else got there first, and there is nothing to file.
        if (!State.TryGetObject(owed.SubjectId, out var doomed)
            || doomed.Zone != Zone.Battlefield)
        {
            return false;
        }

        _libraryEndBeingAsked = owed.SubjectId;

        Ask(new PendingChoice
        {
            Id = $"libraryend:{owed.SubjectId}",
            PlayerId = owed.PlayerId,
            Kind = ChoiceKind.LibraryEnd,
            Prompt = $"Put {doomed.Card.Name} on the top or bottom of your library.",
            Options = [new ChoiceOption("top", "Top"), new ChoiceOption("bottom", "Bottom")],
            MinPicks = 1,
            MaxPicks = 1,
        });

        return true;
    }

    private ObjectId? _libraryEndBeingAsked;

    /// <summary>Files the permanent where its owner said.</summary>
    private void ResolveLibraryEnd(IReadOnlyList<string> picks)
    {
        if (_libraryEndBeingAsked is not { } id)
            return;

        _libraryEndBeingAsked = null;

        if (!State.TryGetObject(id, out var doomed) || doomed.Zone != Zone.Battlefield)
            return;

        var onTop = picks.Count > 0
            && string.Equals(picks[0], "top", StringComparison.Ordinal);

        Move(
            id,
            Zone.Library,
            MoveCause.Other,
            doomed.OwnerId,
            onTop ? ZonePosition.Top : ZonePosition.Bottom);
    }

    /// <summary>Asks which creature a ciphered card is encoded on (CR 702.99a).</summary>
    /// <remarks>
    /// Optional, and only creatures the player controls. With none to put it on the card simply
    /// stays in exile encoded on nothing, which is what the rule leaves it as.
    /// </remarks>
    private bool AskOwedEncoding()
    {
        if (_encodingsOwed.Count == 0 || State.IsWaitingForChoice)
            return false;

        var (cardId, playerId) = _encodingsOwed[0];
        _encodingsOwed.RemoveAt(0);

        if (!State.TryGetObject(cardId, out var card) || card.Zone != Zone.Exile)
            return false;

        var creatures = State.Battlefield
            .Select(State.GetObject)
            .Where(o => ControllerOf(o) == playerId
                && Characteristics.Of(State, _abilities, o).CardTypes.HasFlag(CardType.Creature))
            .ToList();

        if (creatures.Count == 0)
            return false;

        _encodingBeingAsked = cardId;

        Ask(new PendingChoice
        {
            Id = $"cipher:{cardId}",
            PlayerId = playerId,
            Kind = ChoiceKind.EncodeOnCreature,
            Prompt = $"Encode {card.Card.Name} on a creature you control?",
            Options = [.. creatures.Select(o => new ChoiceOption(
                o.Id.Value.ToString("N"), o.Card.Name))],
            MinPicks = 0,
            MaxPicks = 1,
        });

        return true;
    }

    private ObjectId? _encodingBeingAsked;

    /// <summary>Records the encoding, or leaves the card in exile connected to nothing.</summary>
    private void ResolveEncoding(IReadOnlyList<string> picks)
    {
        if (_encodingBeingAsked is not { } cardId)
            return;

        _encodingBeingAsked = null;

        if (picks.Count == 0)
            return;

        var creature = new ObjectId(Guid.ParseExact(picks[0], "N"));
        if (State.TryGetObject(creature, out var host) && host.Zone == Zone.Battlefield)
            Emit(new SpellEncoded(cardId, creature));
    }

    /// <summary>
    /// Asks whether to pay a non-mana cost rather than let a permanent be sacrificed.
    /// </summary>
    /// <remarks>
    /// The whole cost or nothing: a partial payment is not a payment (CR 601.2h), so the choice
    /// takes exactly the printed number of things or none at all. A player who cannot afford it
    /// is not asked - there is nothing to decide - and the permanent simply goes.
    /// </remarks>
    private bool AskOwedSacrificeUnless()
    {
        if (_sacrificeUnlessOwed.Count == 0 || State.IsWaitingForChoice)
            return false;

        var owed = _sacrificeUnlessOwed[0];
        _sacrificeUnlessOwed.RemoveAt(0);

        // Gone already - something else got it first, and there is nothing left to ransom.
        if (!State.TryGetObject(owed.SubjectId, out var doomed)
            || doomed.Zone != Zone.Battlefield)
        {
            return false;
        }

        var payable = PayableFor(owed.PlayerId, owed.Kind, owed.FilterId);

        if (payable.Count < owed.Count)
        {
            Move(owed.SubjectId, Zone.Graveyard, MoveCause.Sacrifice, owed.PlayerId);
            return false;
        }

        _sacrificeUnlessBeingAsked = owed;

        Ask(new PendingChoice
        {
            Id = $"ransom:{owed.SubjectId}",
            PlayerId = owed.PlayerId,
            Kind = ChoiceKind.PayOrSacrifice,
            Prompt = $"Choose {owed.Count} to keep {doomed.Card.Name}, or none to sacrifice it.",
            Options = [.. payable.Select(id => new ChoiceOption(
                id.Value.ToString("N"), State.GetObject(id).Card.Name))],
            MinPicks = 0,
            MaxPicks = owed.Count,
        });

        return true;
    }

    /// <summary>
    /// What a player could give up to meet a chosen cost (CR 118.1).
    /// </summary>
    /// <remarks>
    /// Asked of a player and a cost rather than of one of the two requests that carry one,
    /// because the rules write the same offer both ways (CR 118.12a): "sacrifice this unless you
    /// sacrifice a creature" and "you may sacrifice a creature; if you do, ...". A second copy
    /// of this list is how the two would come to disagree about which permanents "a creature you
    /// control" means — and only one of the two has a test that would notice.
    /// <para>
    /// Control is the computed one, not the one stored on the object (CR 613.1b): a permanent
    /// somebody stole cannot be sacrificed by the player it started under, and can be by the one
    /// holding it now.
    /// </para>
    /// </remarks>
    private IReadOnlyList<ObjectId> PayableFor(
        Guid payerId, ChosenCostKind kind, string filterId) =>
        PayableFor(
            payerId,
            kind,
            (state, obj) => SearchFilters.Matches(
                filterId, state, _abilities, state.GetObject(obj)));

    /// <summary>
    /// The same list, described the way an <em>activation</em> cost describes it (CR 118.12a).
    /// </summary>
    /// <remarks>
    /// A <see cref="TargetSpec"/> asks about the object on the board rather than about the card
    /// that was printed, which is the whole difference: "a permanent with mana value 1 or greater"
    /// and "an untapped creature you control" are questions a filter id cannot put. Null accepts
    /// everything the kind can reach, which is what "discard a card" means.
    /// </remarks>
    private IReadOnlyList<ObjectId> PayableFor(
        Guid payerId, ChosenCostKind kind, TargetSpec? what) =>
        PayableFor(
            payerId,
            kind,
            (state, obj) => what?.ObjectFilter?.Invoke(
                state, _abilities, state.GetObject(obj), payerId) != false);

    private IReadOnlyList<ObjectId> PayableFor(
        Guid payerId, ChosenCostKind kind, Func<GameState, ObjectId, bool> qualifies) =>
        kind switch
        {
            ChosenCostKind.SacrificePermanents =>
                [.. State.Battlefield
                    .Where(id => ControllerOf(State.GetObject(id)) == payerId
                        && qualifies(State, id))],

            ChosenCostKind.DiscardCards =>
                [.. State.GetPlayer(payerId).Hand.Where(id => qualifies(State, id))],

            // The same permanents a sacrifice could take, going somewhere kinder. Only the
            // destination differs, which is why it is a kind rather than an effect of its own.
            ChosenCostKind.ReturnToHand =>
                [.. State.Battlefield
                    .Where(id => ControllerOf(State.GetObject(id)) == payerId
                        && qualifies(State, id))],

            _ => [],
        };

    /// <summary>
    /// Moves what a player picked to pay a chosen cost (CR 118.1).
    /// </summary>
    /// <remarks>
    /// Where the cards go is the only thing that differs between the kinds, which is why they
    /// are one method: a return is a sacrifice with a kinder destination, and a discard is the
    /// same move out of a different zone. A pick that names something no longer there is skipped
    /// rather than throwing — the cost has still been paid, because paying is what the player
    /// chose and not what the events turned out to be (CR 118.12).
    /// </remarks>
    private void TakeChosenPayment(Guid payerId, ChosenCostKind kind, IReadOnlyList<string> picks)
    {
        // Paying a cost is one action however many cards it takes (CR 118.1), so a discard of
        // three to pay for something is one discard as far as a batched trigger is concerned.
        AsOneBatch(() => TakeEachChosenPayment(payerId, kind, picks));
    }

    private void TakeEachChosenPayment(
        Guid payerId, ChosenCostKind kind, IReadOnlyList<string> picks)
    {
        foreach (var pick in picks)
        {
            var id = new ObjectId(Guid.ParseExact(pick, "N"));
            if (!State.TryGetObject(id, out var spent))
                continue;

            // Home to its owner, not to whoever is paying: control of a permanent says nothing
            // about whose hand its card belongs in (CR 108.3).
            if (kind == ChosenCostKind.ReturnToHand)
            {
                Move(id, Zone.Hand, MoveCause.Return, spent.OwnerId);
                continue;
            }

            Move(
                id,
                Zone.Graveyard,
                kind == ChosenCostKind.DiscardCards
                    ? MoveCause.Discard
                    : MoveCause.Sacrifice,
                payerId);
        }
    }

    private SacrificeUnlessPaidRequested? _sacrificeUnlessBeingAsked;

    /// <summary>Takes the payment, or sacrifices the permanent when it was declined.</summary>
    private void ResolveSacrificeUnless(IReadOnlyList<string> picks)
    {
        if (_sacrificeUnlessBeingAsked is not { } owed)
            return;

        _sacrificeUnlessBeingAsked = null;

        if (picks.Count < owed.Count)
        {
            if (State.TryGetObject(owed.SubjectId, out var doomed)
                && doomed.Zone == Zone.Battlefield)
            {
                Move(owed.SubjectId, Zone.Graveyard, MoveCause.Sacrifice, owed.PlayerId);
            }

            return;
        }

        TakeChosenPayment(owed.PlayerId, owed.Kind, picks);
    }

    private bool AskOwedSearch()
    {
        if (_searchesOwed.Count == 0 || State.IsWaitingForChoice)
            return false;

        var owed = _searchesOwed[0];
        _searchesOwed.RemoveAt(0);

        // A search still holding the chosen-name sentinel is one whose question was never
        // answered - no name could be offered, or the answer was not one of the names on the
        // menu. It does not happen, and if it did the filter would match nothing, so the search
        // would silently "find nothing" and shuffle a library the card never told it to touch.
        // CR 608.2b: an effect that cannot work out what it means does nothing at all.
        if (owed.FilterId.Contains(SearchFilters.ChosenName, StringComparison.Ordinal))
            return false;

        var found = SearchCandidates(owed);

        if (found.Count == 0)
        {
            if (owed.Zones.HasFlag(SearchIn.Library))
                Shuffle(owed.Searched, _random);

            return false;
        }

        _searchBeingAsked = owed;

        Ask(new PendingChoice
        {
            Id = $"search:{owed.PlayerId:N}",
            PlayerId = owed.PlayerId,
            Kind = ChoiceKind.SearchLibrary,
            Prompt = SearchPrompt(owed),
            Options = [.. found.Select(id => new ChoiceOption(
                id.Value.ToString("N"), State.GetObject(id).Card.Name))],
            // CR 701.23c: a player may always fail to find, even when the card is there.
            MinPicks = 0,

            // "Any number of" has no printed ceiling, so the ceiling is whatever the library
            // turned out to hold - which is only knowable here, with the search already done.
            MaxPicks = owed.Count < 0 ? found.Count : Math.Max(1, owed.Count),
        });

        return true;
    }

    private LibrarySearchRequested? _searchBeingAsked;

    /// <summary>
    /// Every card in the searched zones that the instruction admits (CR 701.23a).
    /// </summary>
    /// <remarks>
    /// One list across however many zones the card names, in the order library, graveyard, hand,
    /// because the union is what the player picks from and no card in the corpus distinguishes
    /// which zone the find came out of. The mana-value bounds are asked of the printed cost
    /// (CR 202.3): a card outside the battlefield has only its printed cost, and nothing on the
    /// battlefield is changing it.
    /// </remarks>
    private List<ObjectId> SearchCandidates(LibrarySearchRequested owed)
    {
        var player = State.GetPlayer(owed.Searched);
        var looked = new List<ObjectId>();

        if (owed.Zones.HasFlag(SearchIn.Library))
            looked.AddRange(player.Library);

        if (owed.Zones.HasFlag(SearchIn.Graveyard))
            looked.AddRange(player.Graveyard);

        if (owed.Zones.HasFlag(SearchIn.Hand))
            looked.AddRange(player.Hand);

        return
        [
            .. looked.Where(id => SearchFilters.Matches(owed.FilterId, State.GetObject(id).Card)
                && (owed.MaxManaValue is not { } cap || State.GetObject(id).Card.Cmc <= cap)
                && (owed.MinManaValue is not { } floor || State.GetObject(id).Card.Cmc >= floor)
                && (owed.ExactManaValue is not { } exact
                    || State.GetObject(id).Card.Cmc == exact)),
        ];
    }

    /// <summary>What the player being asked is told they are looking through.</summary>
    private string SearchPrompt(LibrarySearchRequested owed)
    {
        var zones = new List<string>();

        if (owed.Zones.HasFlag(SearchIn.Library))
            zones.Add("library");

        if (owed.Zones.HasFlag(SearchIn.Graveyard))
            zones.Add("graveyard");

        if (owed.Zones.HasFlag(SearchIn.Hand))
            zones.Add("hand");

        var whose = owed.ZonesOf is { } them
            ? State.GetPlayer(them).Name + "'s "
            : "your ";

        return $"Search {whose}{string.Join(", ", zones)} for a {owed.FilterId} card.";
    }

    /// <summary>Moves the found cards and shuffles the library (CR 701.23e).</summary>
    private void ResolveSearch(IReadOnlyList<string> picks)
    {
        if (_searchBeingAsked is not { } owed)
            return;

        _searchBeingAsked = null;

        var searched = owed.Searched;

        // "Shuffle and put that card on top" - the card never leaves the library, so the two
        // steps have to happen in the printed order. Moving it first and shuffling afterwards
        // folds it back in at random, which is the opposite of what the card says and looks
        // identical from every angle except the one that matters.
        if (owed.Destination == Zone.Library)
        {
            Shuffle(searched, _random);

            // Every card found, not only the first. "Shuffle and put those cards on top in any
            // order" is Goblin Recruiter, Dwarven Recruiter, Congregation at Dawn and Scouting
            // Trek, and each of them fetches several; taking picks[0] alone left the rest sitting
            // in the shuffled library, which is a tutor that finds four cards and delivers one
            // while the log says it succeeded.
            //
            // Walked backwards so the first card the player named ends up on top. CR 401.4 gives
            // the order to the cards' owner whenever an effect puts two or more of them in one
            // position in a library, and the order they were picked in is the only statement of
            // it this game has - the answer is a list, and a list has an order.
            for (var i = picks.Count - 1; i >= 0; i--)
            {
                var found = new ObjectId(Guid.ParseExact(picks[i], "N"));
                if (State.TryGetObject(found, out var onTop) && onTop.Zone == Zone.Library)
                    Move(found, Zone.Library, MoveCause.Other, searched, ZonePosition.Top);
            }

            return;
        }

        // Every card found, not just the first: "up to two basic land cards" is one search that
        // fetches two, and taking only the head would have quietly halved every plural tutor.
        foreach (var pick in picks)
        {
            var id = new ObjectId(Guid.ParseExact(pick, "N"));

            // A pick has to have come from one of the zones this search was told to look in.
            // Checking the zone rather than trusting the answer is what stops a client naming a
            // card in a zone the instruction never reached - the board's list is a courtesy and
            // the engine is the authority.
            if (!State.TryGetObject(id, out var card) || !SearchedZone(owed, card))
                continue;

            // CR 400.3: a card put into a hand, library or graveyard goes to its owner's, and
            // the owner here is the player whose zones were searched rather than the searcher -
            // an extraction exiles the cards it finds, and exile is shared, but a search of
            // somebody else's library that puts a card in "your hand" would be a different card.
            var landed = Move(
                id,
                owed.Destination,
                owed.Destination == Zone.Exile ? MoveCause.Exile : MoveCause.Other,
                owed.PlayerId);

            if (owed.Tapped && owed.Destination == Zone.Battlefield)
                Emit(new PermanentTapped(landed));

            // CR 701.3c: a card put onto the battlefield attached to something arrives on it.
            // Emitted here, in the same step as the move and before any state-based action can
            // run, because an Aura on the battlefield attached to nothing is put into its
            // owner's graveyard the moment one does (CR 704.5m) - so this whole family has to
            // land already attached rather than be attached afterwards.
            if (owed.Destination == Zone.Battlefield
                && (owed.AttachTo is not null || owed.AttachToPlayer is not null))
            {
                Emit(new PermanentAttached(landed, owed.AttachTo, owed.AttachToPlayer));
            }
        }

        // CR 701.23e shuffles what was searched, and only if a library was: "if you search your
        // library this way, shuffle" is the rider two-zone cards print, and a search that only
        // ever looked at a graveyard has nothing to randomise.
        if (owed.Zones.HasFlag(SearchIn.Library))
            Shuffle(searched, _random);
    }

    /// <summary>Whether a found card is in one of the zones the search was told to look in.</summary>
    private static bool SearchedZone(LibrarySearchRequested owed, GameObject card) =>
        card.Zone switch
        {
            Zone.Library => owed.Zones.HasFlag(SearchIn.Library),
            Zone.Graveyard => owed.Zones.HasFlag(SearchIn.Graveyard),
            Zone.Hand => owed.Zones.HasFlag(SearchIn.Hand),
            _ => false,
        };

    /// <summary>
    /// Performs the oldest owed seek, if any.
    /// </summary>
    /// <remarks>
    /// Settled rather than asked. Seeking takes a card at random from among those that match, so
    /// there is nobody to put a question to - the same shape as a discard at random, and for the
    /// same reason: the choice belongs to the game, so the game makes it through the one seeded
    /// source and the moves it produces are what the log carries.
    /// <para>
    /// It deliberately does <em>not</em> shuffle afterwards. Shuffling is the half of a search
    /// (CR 701.23e) that seeking exists to avoid, and doing it here would give every seek a side
    /// effect its card does not print.
    /// </para>
    /// <para>
    /// Finding nothing is not a failure: a library with no matching card leaves the seek doing
    /// nothing at all, which is what the mechanic says and is why this returns false rather than
    /// waiting for anything.
    /// </para>
    /// </remarks>
    private bool SettleOwedSeek()
    {
        if (_seeksOwed.Count == 0 || State.IsWaitingForChoice)
            return false;

        var owed = _seeksOwed[0];
        _seeksOwed.RemoveAt(0);

        // CR 202.3: mana value comes from the printed cost, and a card in a library has only its
        // printed cost - nothing on the battlefield is changing it.
        var found = State.GetPlayer(owed.PlayerId).Library
            .Where(id => SearchFilters.Matches(owed.FilterId, State.GetObject(id).Card)
                && (owed.MaxManaValue is not { } cap || State.GetObject(id).Card.Cmc <= cap)
                && (owed.MinManaValue is not { } floor || State.GetObject(id).Card.Cmc >= floor)
                && (owed.ExactManaValue is not { } exact
                    || State.GetObject(id).Card.Cmc == exact))
            .ToList();

        if (found.Count == 0)
            return false;

        var did = false;

        foreach (var id in _random.Shuffle(found).Take(Math.Max(1, owed.Count)))
        {
            var landed = Move(id, owed.Destination, MoveCause.Other, owed.PlayerId);

            if (owed.Tapped && owed.Destination == Zone.Battlefield)
                Emit(new PermanentTapped(landed));

            did = true;
        }

        return did;
    }

    /// <summary>
    /// Settles or asks the oldest owed look-and-take, if any (CR 701.20a).
    /// </summary>
    /// <remarks>
    /// Named for the settling half rather than the asking one, and deliberately: with nothing
    /// among the cards it turned up that the filter admits, the instruction is carried out here
    /// rather than put to a player who has no answer to give (CR 118.3). Both outcomes send the
    /// sweep round again, which is what a <c>Settle…</c> name promises its caller — see
    /// <see cref="SettleBeforePriority"/>.
    /// </remarks>
    private bool SettleOwedLookAndTake()
    {
        if (_looksAndTakesOwed.Count == 0 || State.IsWaitingForChoice)
            return false;

        var owed = _looksAndTakesOwed[0];
        _looksAndTakesOwed.RemoveAt(0);

        var top = State.GetPlayer(owed.PlayerId).Library.Take(owed.Count).ToList();
        if (top.Count == 0)
            return false;

        // "Reveal the top ten cards of your library" - the same look with the cards face up, so
        // the log and every opponent see which cards the choice was made from. The flag clears
        // itself: each revealed card becomes a new object when it moves on (CR 400.7).
        if (owed.Reveal)
            Emit(new CardsRevealed(owed.PlayerId, [.. top]));

        // "You may reveal a creature card from among them" - the same look, with only some of
        // what was seen worth offering. The filter vocabulary is the one searching already uses,
        // rather than a second one that would drift away from it - and so is the mana-value
        // ceiling beside it, which is the search's own bound asked of a smaller pile.
        var takeable = top
            .Where(id => SearchFilters.Matches(owed.FilterId, State.GetObject(id).Card)
                && (owed.MaxManaValue is not { } cap || State.GetObject(id).Card.Cmc <= cap))
            .ToList();

        // CR 118.3, the same rule the optional payment states in as many words: a question with
        // one possible answer is not a question. "You may reveal a creature card from among them"
        // over four cards with no creature in them was stopping the game on a prompt with an
        // empty list on it - the zero-card discard's mistake in a second place, and the sibling
        // guards (the mulligan bottom, the cleanup discard) are what say this one is missing.
        //
        // Resolved rather than dropped, because the rest of the instruction still runs: "put the
        // rest on the bottom of your library" happens whether or not anything was taken, and
        // skipping the request outright would have left four cards sitting on top.
        if (takeable.Count == 0)
        {
            _lookAndTakeBeingAsked = owed;
            ResolveLookAndTake([]);
            return true;
        }

        // "Put all Goblin cards revealed this way into your hand" names no choice: every match
        // goes, and the only legal answer to a prompt built from it would be all of its options.
        // Carried out here for the same reason the empty pile above is - CR 118.3 - rather than
        // stopping the game on a question whose answer the card has already given.
        if (owed.TakeAll)
        {
            _lookAndTakeBeingAsked = owed;
            ResolveLookAndTake([.. takeable.Select(id => id.Value.ToString("N"))]);
            return true;
        }

        // How many the sentence allows, clamped to how many of the cards seen the filter admits.
        // Null is "any number of permanent cards from among them", which is bounded only by the
        // pile itself, and the clamp is what keeps a prompt from offering more picks than options.
        var limit = owed.TakeLimit is { } allowed
            ? Math.Min(allowed, takeable.Count)
            : takeable.Count;

        if (limit <= 0)
        {
            _lookAndTakeBeingAsked = owed;
            ResolveLookAndTake([]);
            return true;
        }

        _lookAndTakeBeingAsked = owed;

        var howMany = limit == 1 ? "one" : $"up to {limit}";

        Ask(new PendingChoice
        {
            Id = $"take:{owed.PlayerId:N}",
            PlayerId = owed.PlayerId,
            Kind = ChoiceKind.LookAndTake,
            Prompt = owed.ShuffleAfter
                ? $"Choose {howMany} to put into your {owed.Destination}; "
                    + "the rest are shuffled into your library."
                : $"Choose {howMany} to put into your {owed.Destination}; "
                    + "the rest go on the bottom.",
            Options = [.. takeable.Select(id => new ChoiceOption(
                id.Value.ToString("N"), State.GetObject(id).Card.Name))],
            // Taking nothing is legal, and is the right answer when none of them is worth having.
            MinPicks = 0,
            MaxPicks = limit,
        });

        return true;
    }

    private LookAndTakeRequested? _lookAndTakeBeingAsked;

    /// <summary>Takes the chosen cards and buries the rest in a random order (CR 701.20a).</summary>
    /// <remarks>
    /// Reads a list rather than one answer, because "put two of them into your hand" and "put any
    /// number of permanent cards from among them onto the battlefield" are the same instruction
    /// with a different ceiling — and because the take-all shape settles through here with every
    /// match named at once, never having asked.
    /// </remarks>
    private void ResolveLookAndTake(IReadOnlyList<string> picks)
    {
        if (_lookAndTakeBeingAsked is not { } owed)
            return;

        _lookAndTakeBeingAsked = null;

        var top = State.GetPlayer(owed.PlayerId).Library.Take(owed.Count).ToList();

        // Checked again rather than trusted: the filter and the mana-value bound decided what was
        // offered, and an answer naming something else has to be refused rather than quietly
        // honoured. The ceiling is re-applied for the same reason - an answer longer than the
        // sentence allows is not one the card offered.
        var taken = new List<ObjectId>();
        foreach (var pick in picks)
        {
            if (!Guid.TryParseExact(pick, "N", out var picked))
                continue;

            var id = new ObjectId(picked);
            if (!top.Contains(id) || taken.Contains(id))
                continue;

            var card = State.GetObject(id).Card;
            if (!SearchFilters.Matches(owed.FilterId, card))
                continue;

            if (owed.MaxManaValue is { } cap && card.Cmc > cap)
                continue;

            taken.Add(id);
        }

        if (owed.TakeLimit is { } allowed && taken.Count > allowed)
            taken = [.. taken.Take(allowed)];

        foreach (var id in taken)
        {
            var landed = Move(id, owed.Destination, MoveCause.Other, owed.PlayerId);

            // CR 702.75a: hideaway's card has to stay findable, because the second line on every
            // hideaway card says "the exiled card" and means the one this permanent buried. The
            // link is recorded here rather than by the effect that asked for the look, because a
            // card that changes zones is a new object (CR 400.7) and only the move knows the id
            // it landed under.
            if (owed.Source is { } by && owed.Destination == Zone.Exile)
                Emit(new ExiledUntilLeaves(landed, by));

            // "With three +1/+1 counters on it. It gains hexproof until your next turn." — the
            // dressing on the taking, done here because only the move knows the id the card
            // lands under (CR 400.7). Battlefield only: counters live on permanents, and the
            // reducer refuses one anywhere else.
            if (owed.Destination != Zone.Battlefield)
                continue;

            if (owed.TappedOnTaken)
                Emit(new PermanentTapped(landed));

            if (owed.CountersOnTaken > 0)
            {
                Emit(new CountersChanged(
                    landed, CounterKinds.PlusOnePlusOne, owed.CountersOnTaken));
            }

            if (owed.TakenGrantId is { } granted)
            {
                Emit(new ContinuousEffectCreated(
                    Guid.NewGuid(),
                    granted,
                    [landed],
                    owed.GrantUntilTakersNextTurn ? null : State.TurnNumber)
                {
                    UntilTurnOf = owed.GrantUntilTakersNextTurn ? owed.PlayerId : null,
                });
            }
        }

        var rest = top.Where(id => !taken.Contains(id)).ToList();

        if (owed.RestTo != Zone.Library)
        {
            // Into the graveyard, where order does not matter and nothing is randomised.
            foreach (var id in rest)
                Move(id, owed.RestTo, MoveCause.Other, owed.PlayerId);

            return;
        }

        // "Then shuffle" - the rest go back and the whole library is shuffled, which is a
        // different instruction from burying them on the bottom: a card that was near the top
        // before the reveal can be anywhere afterwards.
        if (owed.ShuffleAfter)
        {
            foreach (var id in rest)
                Move(id, Zone.Library, MoveCause.Other, owed.PlayerId, ZonePosition.Bottom);

            Shuffle(owed.PlayerId, _random);
            return;
        }

        // "In a random order" is the game's decision, not the player's, so it goes through the
        // shared source and the resulting order lands in the log like any other shuffle. "In any
        // order" is the player's decision and this does not ask — the cards end face down on the
        // bottom of a library either way, and the difference is one nobody can observe.
        foreach (var id in _random.Shuffle(rest))
            Move(id, Zone.Library, MoveCause.Other, owed.PlayerId, ZonePosition.Bottom);
    }

    /// <summary>
    /// Asks the oldest owed discard, if any (CR 701.9a).
    /// </summary>
    /// <remarks>
    /// A discard at random is not a choice, so it is performed here rather than asked. The rest is
    /// the player's decision and the game waits on it.
    /// </remarks>
    private bool AskOwedDiscard()
    {
        if (_discardsOwed.Count == 0 || State.IsWaitingForChoice)
            return false;

        var owed = _discardsOwed[0];
        _discardsOwed.RemoveAt(0);

        var hand = State.GetPlayer(owed.PlayerId).Hand;
        if (hand.IsEmpty)
            return false;

        var count = Math.Min(owed.Count, hand.Count);

        // Discarding nothing is not a decision, and asking anyway is not merely noise: the
        // options offered are the whole hand while the range is 0..0, so a player who answers
        // the question the game put to them is refused for answering it. "Discards a card for
        // each time it was kicked" on an unkicked spell is the printed shape that reaches this
        // (Bloodhusk Ritualist), and it broke three soak tables before this guard existed. The
        // two sibling sites - the mulligan bottom and the cleanup discard - have carried the
        // same guard all along; this one was written without it.
        if (count == 0)
            return false;

        if (owed.AtRandom)
        {
            // CR 701.9b: the card is chosen at random, which is the game's decision and not the
            // player's — and the roll is recorded through the shared source so a replay agrees.
            AsOneBatch(() =>
            {
                foreach (var id in _random.Shuffle(hand).Take(count))
                    Move(id, Zone.Graveyard, MoveCause.Discard, owed.PlayerId);
            });

            return false;
        }

        Ask(new PendingChoice
        {
            Id = "discard:" + owed.PlayerId.ToString("N"),
            PlayerId = owed.PlayerId,
            Kind = ChoiceKind.DiscardToEffect,
            Prompt = $"Choose {count} card(s) to discard.",
            Options = [.. hand.Select(id => new ChoiceOption(
                id.Value.ToString("N"), State.GetObject(id).Card.Name))],
            MinPicks = count,
            MaxPicks = count,
        });

        return true;
    }

    /// <summary>Discards the cards the player picked (CR 701.9a).</summary>
    private void ResolveDiscard(PendingChoice choice, IReadOnlyList<string> picks)
    {
        // The cards the player picked go at once, however many the effect asked for.
        AsOneBatch(() =>
        {
            foreach (var pick in picks)
            {
                var id = new ObjectId(Guid.ParseExact(pick, "N"));
                if (State.TryGetObject(id, out var card) && card.Zone == Zone.Hand)
                    Move(id, Zone.Graveyard, MoveCause.Discard, choice.PlayerId);
            }
        });
    }

    /// <summary>Moves the cards the player picked out of the top of their library.</summary>
    private void ResolveLook(PendingChoice choice, IReadOnlyList<string> picks)
    {
        var toGraveyard = choice.Kind == ChoiceKind.Surveil;

        foreach (var pick in picks)
        {
            var id = new ObjectId(Guid.ParseExact(pick, "N"));
            if (!State.TryGetObject(id, out _))
                continue;

            Move(
                id,
                toGraveyard ? Zone.Graveyard : Zone.Library,
                toGraveyard ? MoveCause.Mill : MoveCause.Other,
                choice.PlayerId,
                toGraveyard ? ZonePosition.Top : ZonePosition.Bottom);
        }
    }

    /// <summary>
    /// The day-night check the untap step makes (CR 502.2).
    /// </summary>
    /// <remarks>
    /// "If it's day and the previous turn's active player didn't cast any spells during that
    /// turn, it becomes night. If it's night and the previous turn's active player cast two or
    /// more spells during that turn, it becomes day." Both halves ask about the player whose turn
    /// it *was*, which is why the state records who that was rather than stepping back through
    /// the turn order - a step that would be wrong the moment somebody takes an extra turn.
    /// <para>
    /// While it is neither, the check does not happen and it stays neither (CR 731.2). Nothing
    /// makes it day or night except a permanent that says so.
    /// </para>
    /// </remarks>
    private void TurnTheSky()
    {
        if (State.IsDay is not { } day || State.PreviousActivePlayerId is not { } before)
            return;

        var cast = State.GetPlayer(before).SpellsCastLastTurn;

        if (day && cast == 0)
            Emit(new DayNightChanged(false));
        else if (!day && cast >= 2)
            Emit(new DayNightChanged(true));
    }

    /// <summary>
    /// The standing consequences of daybound and nightbound (CR 702.145c–g).
    /// </summary>
    /// <remarks>
    /// Four "any time" rules rather than triggered abilities, so they are asked wherever
    /// state-based actions are: a daybound permanent on the battlefield makes it day if it is
    /// neither; a nightbound one makes it night if it is neither and no daybound permanent is
    /// out; and a permanent showing the wrong face for the time of day turns over at once.
    /// <para>
    /// Looped rather than done in one pass, because turning one permanent over can make another
    /// one wrong - and because making it day is itself a change that the face rules then have to
    /// see. The guard is what stops a card that somehow has both keywords from flipping for ever.
    /// </para>
    /// </remarks>
    /// <summary>The keywords of the face a permanent is actually showing (CR 712.4).</summary>
    /// <remarks>
    /// <strong>Transforming changes which face is up, not which card it is.</strong> So the card's
    /// own keywords answer for the front face however the permanent is turned - and asking them
    /// here meant a daybound werewolf at night was found to be showing the wrong face, turned
    /// over, and then found to be showing the wrong face again, for ever.
    /// <para>
    /// It never settled, so the game stopped with "state-based actions and triggers did not
    /// settle" rather than looping silently, which is the one merciful thing about it. Every
    /// day/night card in the corpus did this - seventeen of them - and none of it was reachable
    /// without putting one on a battlefield and letting night fall.
    /// </para>
    /// </remarks>
    private static KeywordAbility Showing(GameObject permanent)
    {
        var face = permanent.Permanent?.FaceIndex ?? 0;

        // Through the face definition rather than the raw face, because that is where a face's
        // keywords are worked out - the bulk data gives every face the whole card's list, and a
        // werewolf that is daybound *and* nightbound at once is wrong whichever way it is turned.
        return permanent.Card.Faces.Count > face
            ? Cards.CardFaces.Definition(permanent.Card, face).Keywords
            : permanent.Card.Keywords;
    }

    private void KeepTheTimeOfDay()
    {
        for (var guard = 0; guard < 8; guard++)
        {
            var bound = State.Battlefield
                .Select(id => State.TryGetObject(id, out var found) ? found : null)
                .OfType<GameObject>()
                .Where(o => o.Permanent is not null
                    && (Showing(o).HasFlag(KeywordAbility.Daybound)
                        || Showing(o).HasFlag(KeywordAbility.Nightbound)))
                .ToList();

            if (bound.Count == 0)
                return;

            // CR 702.145d and 702.145g: the keywords make it one or the other when it is neither,
            // and daybound wins because a card showing its front face is the one that is out.
            if (State.IsDay is null)
            {
                var anyDaybound = bound.Any(
                    o => o.Card.Keywords.HasFlag(KeywordAbility.Daybound));

                Emit(new DayNightChanged(anyDaybound));
                continue;
            }

            // CR 702.145c and 702.145f: a permanent showing the wrong face for the time of day is
            // turned over by its controller, immediately and not as a state-based action.
            var wrong = bound.FirstOrDefault(o =>
                (State.IsDay == false && Showing(o).HasFlag(KeywordAbility.Daybound))
                || (State.IsDay == true && Showing(o).HasFlag(KeywordAbility.Nightbound)));

            if (wrong is null)
                return;

            Transform(wrong.Id);
        }
    }

    /// <summary>
    /// The second of the monarch's two inherent abilities (CR 725.2).
    /// </summary>
    /// <remarks>
    /// "Whenever a creature deals combat damage to the monarch, its controller becomes the
    /// monarch." Hooked where every <see cref="PlayerDamaged"/> passes rather than beside the
    /// method that deals damage to a player, because combat builds its own event and never calls
    /// that method - which is exactly how the first version of this managed to watch a damage
    /// path that combat does not use.
    /// <para>
    /// A simplification, stated rather than hidden: the rules make this a triggered ability with
    /// no source, so it uses the stack and can be responded to. This engine keys a pending
    /// trigger to the permanent that produced it and has nowhere to put a sourceless one, so the
    /// crown moves as the damage lands. The window is what is lost; nothing else differs.
    /// </para>
    /// </remarks>
    private void StealTheCrown(PlayerDamaged damaged)
    {
        if (!damaged.IsCombat
            || State.MonarchId != damaged.PlayerId
            || !State.TryGetObject(damaged.SourceId, out var dealer))
        {
            return;
        }

        // Both questions are about the permanent as it is now, not as it was printed and first
        // put onto the battlefield (CR 613.1b for control, layer 4 for the type). Reading the
        // stored controller meant the monarch's own creature, taken and turned on them, looked
        // like the monarch damaging themselves - so the crown did not move at all.
        var dealing = Characteristics.Of(State, _abilities, dealer);
        if (!dealing.IsCreature || dealing.ControllerId == damaged.PlayerId)
            return;

        Emit(new MonarchChanged(dealing.ControllerId));
    }

    /// <summary>
    /// The second of the initiative's three inherent abilities (CR 726.2).
    /// </summary>
    /// <remarks>
    /// "Whenever one or more creatures a player controls deal combat damage to the player who
    /// has the initiative, the controller of those creatures takes the initiative" — the
    /// monarch's hook a rule over, in the same place for the same reason: this is where every
    /// <see cref="PlayerDamaged"/> passes, including the ones combat builds itself.
    /// <para>
    /// "One or more creatures" is one trigger for the whole batch, and the guard is what batches
    /// it: the first creature's damage moves the initiative to its controller, after which the
    /// holder is no longer the player being damaged and the remaining creatures' damage falls
    /// through the second check. One taking, one venture — which is what the rule's wording is
    /// for. The taking itself triggers the third inherent ability, so the taker ventures into
    /// Undercity here too, with the same stated simplification every sourceless trigger in this
    /// engine makes: it happens directly, and the window is what is lost.
    /// </para>
    /// </remarks>
    private void TakeTheInitiativeFromCombat(PlayerDamaged damaged)
    {
        if (!damaged.IsCombat
            || State.InitiativeId != damaged.PlayerId
            || !State.TryGetObject(damaged.SourceId, out var dealer))
        {
            return;
        }

        // Both questions are about the permanent as it is now (CR 613.1b for control, layer 4
        // for the type), exactly as the crown's hook asks them.
        var dealing = Characteristics.Of(State, _abilities, dealer);
        if (!dealing.IsCreature || dealing.ControllerId == damaged.PlayerId)
            return;

        Emit(new InitiativeTaken(dealing.ControllerId));

        foreach (var e in Dungeons.VentureEvents(State, dealing.ControllerId, Dungeons.Undercity))
            Emit(e);
    }

    /// <summary>
    /// Notes that an Assassin or a commander connected, which is what freerunning asks
    /// (CR 702.173a).
    /// </summary>
    /// <remarks>
    /// Read off the creature as it deals the damage, because the rule asks what it was "at the
    /// time it dealt that damage". Reconstructing it later would ask about a creature that may
    /// have changed types, or died, since.
    /// </remarks>
    private void NoteFreerunning(PlayerDamaged damaged)
    {
        if (!damaged.IsCombat || !State.TryGetObject(damaged.SourceId, out var dealer))
            return;

        var now = Characteristics.Of(State, _abilities, dealer);
        if (!now.IsCreature)
            return;

        var enabling = now.HasSubtype("Assassin")
            || IsCommanderOf(now.ControllerId, dealer);

        if (!enabling)
            return;

        if (!State.GetPlayer(now.ControllerId).AssassinOrCommanderConnectedThisTurn)
            Emit(new FreerunningEnabled(now.ControllerId));
    }

    /// <summary>
    /// "Whenever that creature deals combat damage to a player, its controller may cast a copy of
    /// the encoded card without paying its mana cost" (CR 702.99a).
    /// </summary>
    /// <remarks>
    /// A <em>copy</em>, so the encoded card never leaves exile and the creature keeps it for the
    /// next time it connects. Casting the card itself would spend the encoding, which is the one
    /// thing cipher is for.
    /// <para>
    /// The same simplification the monarch hook makes and for the same reason: the rules make
    /// this a triggered ability that uses the stack, and the offer is made as the damage lands
    /// instead. The window to respond before the copy is cast is what is lost.
    /// </para>
    /// </remarks>
    private void OfferCipheredCopies(PlayerDamaged damaged)
    {
        if (!damaged.IsCombat || !State.TryGetObject(damaged.SourceId, out var dealer))
            return;

        var encoded = State.Exile
            .Select(State.GetObject)
            .Where(o => o.EncodedOn == dealer.Id)
            .ToList();

        foreach (var card in encoded)
        {
            var copy = ObjectId.New();

            Emit(new ObjectCreated(
                copy, card.Card, dealer.ControllerId, dealer.ControllerId, Zone.Exile));

            Emit(new FreeCastOffered(copy, dealer.ControllerId));
        }
    }

    /// <summary>Records one answered mode and keeps the rest of the question open.</summary>
    /// <remarks>
    /// Filed against the trigger exactly as an answered target is, and for the same reason: state
    /// folds from the log, so the engine holds no continuation and the choice id has to carry
    /// everything needed to know what was asked.
    /// </remarks>
    private void RecordTriggerMode(PendingChoice choice, string pick)
    {
        var body = choice.Id["trigger-modes:".Length..];
        var key = body[..body.LastIndexOf(':')];

        if (!_triggerModes.TryGetValue(key, out var list))
            _triggerModes[key] = list = [];

        // Taking fewer than the maximum is an answer, and it has to be remembered: without it
        // the question is asked again the moment the trigger is looked at, for ever.
        if (string.Equals(pick, NoFurtherMode, StringComparison.Ordinal))
        {
            _triggerModesClosed.Add(key);
            return;
        }

        if (int.TryParse(
            pick,
            System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture,
            out var index))
        {
            list.Add(index);
        }
    }

    /// <summary>The most modes a trigger may be given (CR 700.2d).</summary>
    /// <remarks>
    /// Almost every modal ability prints one number and takes exactly that many, and those
    /// compile with a maximum equal to the count. A maximum below the count would be a card that
    /// asks for more modes than it allows, so the larger of the two is the honest reading.
    /// </remarks>
    private static int ModeCeiling(TriggeredAbilityDefinition ability) =>
        Math.Max(ability.ModesToChoose, ability.ModesMax);

    /// <summary>The option id that means "I have taken all the modes I want".</summary>
    /// <remarks>
    /// Not a number, so it can never collide with a mode index however many modes a card prints.
    /// </remarks>
    private const string NoFurtherMode = "no-further-mode";

    /// <summary>Triggers whose controller has said they want no more modes.</summary>
    private readonly HashSet<string> _triggerModesClosed = new(StringComparer.Ordinal);

    /// <summary>Records one answered target and keeps the rest of the question open.</summary>
    private void RecordTriggerTarget(PendingChoice choice, string pick)
    {
        // The choice id carries which trigger asked, so the answer can be filed against it
        // without the engine holding a continuation — state has to be foldable from the log.
        var body = choice.Id["trigger-targets:".Length..];
        var key = body[..body.LastIndexOf(':')];

        if (!_triggerTargets.TryGetValue(key, out var list))
            _triggerTargets[key] = list = [];

        list.Add(TargetFromOptionId(pick));
    }

    /// <summary>
    /// Rebuilds a target from the option id the player picked.
    /// </summary>
    /// <remarks>
    /// The kind has to come out of the id, because the id is the whole of what is remembered: the
    /// question goes into the log and the answer comes back as a string, so nothing that was
    /// computed when the options were offered is still in hand.
    /// <para>
    /// This rebuilt every object as a permanent, and the effect was silent and total — a trigger
    /// targeting a card in a graveyard offered the right cards, accepted a legal pick, went on
    /// the stack, and then fizzled on resolution because the target it carried was a permanent
    /// target and no permanent had that id. Every graveyard-targeting and spell-targeting trigger
    /// in the game did nothing, and did it without an error.
    /// </para>
    /// </remarks>
    private static Target TargetFromOptionId(string optionId)
    {
        var value = optionId[(optionId.IndexOf(':', StringComparison.Ordinal) + 1)..];

        if (optionId.StartsWith("player:", StringComparison.Ordinal))
            return Target.ToPlayer(Guid.ParseExact(value, "N"));

        var id = new ObjectId(Guid.ParseExact(value, "N"));

        return optionId.StartsWith("card:", StringComparison.Ordinal)
            ? Target.ToCard(id)
            : optionId.StartsWith("spell:", StringComparison.Ordinal)
                ? Target.ToSpell(id)
                : Target.ToPermanent(id);
    }

    /// <summary>
    /// Checks the modes offered for a modal spell, and refuses the cast if they are wrong.
    /// </summary>
    /// <remarks>
    /// The modes arrive with the cast rather than being asked for, the same way a chosen cost
    /// does: choosing them is part of casting (CR 601.2b), so the player can decide before they
    /// commit and the engine never has to suspend a cast.
    /// </remarks>
    private ImmutableList<int> RequireLegalModes(
        SpellDefinition? definition,
        IReadOnlyList<int>? offered,
        string cardName,
        bool entwined,
        Guid caster,
        bool kicked = false,
        bool teamwork = false,
        int variableValue = 0)
    {
        if (definition is null || definition.Modes.IsEmpty)
            return [];

        var picked = (offered ?? []).ToImmutableList();

        // CR 702.42a: entwine is not a wider range but a fixed one - paying it takes every mode,
        // and there is nothing in between to choose.
        var least = definition.ModesToChoose;
        var ceiling = definition.ModesMax;

        // CR 700.2d: a maximum the card offers only under a condition, asked here because here is
        // where the board still is what it was as the spell was cast. A card whose condition is
        // false keeps the printed maximum rather than being refused - "you may choose both
        // instead" adds a permission, it does not take the ordinary one away.
        if (definition.ExtraModes is { } wider
            && wider.Max > ceiling
            && wider.IsAvailable(State, _abilities, caster))
        {
            ceiling = wider.Max;
        }

        // CR 700.2d: a count the card swaps in while a cast fact holds — "choose both instead",
        // "choose any number instead". It replaces the printed count rather than widening it,
        // because the card says "instead": a teamwork caster of "choose both" must take both.
        // The fact is known before anything else about the spell is checked, because it is an
        // intention declared as part of this very cast (CR 601.2b).
        if (definition.ModesOnFact is { } swapped
            && swapped.Fact switch
            {
                CastFact.Kicked => kicked,
                CastFact.Teamwork => teamwork,
                _ => false,
            })
        {
            least = swapped.Min;
            ceiling = swapped.Max < 0 ? definition.Modes.Count : swapped.Max;
        }

        // CR 601.2b: "Choose X." — the count is the X announced with this cast, and there is
        // nothing else it could be: the card gives the variable no definition of its own.
        if (definition.ModesFromX)
        {
            least = Math.Max(0, variableValue);
            ceiling = least;
        }

        var most = entwined
            ? definition.Modes.Count

            // A repeated mode is still one pick, so the number of picks may exceed the number of
            // modes on offer - which is exactly what "you may choose the same mode more than
            // once" buys and what clamping to the mode count would take away again.
            : definition.ModesMayRepeat
            ? Math.Max(ceiling, least)
            : Math.Clamp(ceiling, least, definition.Modes.Count);

        if (entwined)
        {
            if (definition.EntwineCost is null)
                throw new InvalidOperationException($"{cardName} has no entwine (CR 702.42a).");

            least = definition.Modes.Count;
        }

        if (picked.Count < least || picked.Count > most)
        {
            throw new InvalidOperationException(
                $"{cardName} needs {(least == most ? $"{least}" : $"{least} to {most}")} "
                    + $"mode(s) chosen and was given {picked.Count} (CR 700.2d).");
        }

        foreach (var index in picked)
        {
            if (index < 0 || index >= definition.Modes.Count)
                throw new InvalidOperationException($"{cardName} has no such mode.");
        }

        // CR 700.2d: the same mode cannot be chosen twice unless the card says otherwise. A card
        // that does say otherwise is treated as though the mode appeared that many times in
        // sequence, which is what the resolution loop does with a repeated index.
        if (!definition.ModesMayRepeat && picked.Distinct().Count() != picked.Count)
            throw new InvalidOperationException($"{cardName} cannot take the same mode twice (CR 700.2d).");

        return picked;
    }

    /// <summary>
    /// How much of the generic cost the nominated player is actually paying (CR 702.132a).
    /// </summary>
    /// <remarks>
    /// The rule gives the chosen player a window to make mana before anything is paid. Here the
    /// offer arrives with the cast, the way modes and chosen costs do, so both players' mana has
    /// to be floating already - the engine cannot suspend a cast to ask a question, and this is
    /// the same deviation every other cast-time choice carries.
    /// <para>
    /// Capped at the generic part rather than refused when it overshoots: a player offering to
    /// pay four towards a cost with three generic in it has offered to pay three.
    /// </para>
    /// </remarks>
    private int RequireAssist(
        SpellDefinition? definition,
        string cardName,
        Guid caster,
        (Guid Player, int Amount) assist,
        ManaCostSpec cost)
    {
        if (definition?.HasAssist != true)
            throw new InvalidOperationException($"{cardName} has no assist (CR 702.132a).");

        if (assist.Player == caster)
            throw new InvalidOperationException("Assist is paid by another player (CR 702.132a).");

        if (!State.Players.ContainsKey(assist.Player))
            throw new InvalidOperationException("No such player.");

        if (assist.Amount < 0)
            throw new InvalidOperationException("Assist cannot be a negative amount.");

        return Math.Min(assist.Amount, cost.GenericPart);
    }

    /// <summary>
    /// The cards being spliced onto this spell, refusing the cast if any of them cannot be
    /// (CR 702.47a).
    /// </summary>
    /// <remarks>
    /// Splice is a choice made as the spell is cast, which is what makes it reachable at all: the
    /// engine cannot suspend a cast to ask a question, and it does not have to - the cards arrive
    /// with the cast the way modes and chosen costs do.
    /// <para>
    /// The card is revealed from hand and stays there. Nothing moves, so nothing has to be put
    /// back if the spell is countered - which is exactly what the rule says happens.
    /// </para>
    /// </remarks>
    private ImmutableList<Domain.Models.CardDefinition> RequireSpliceable(
        Guid playerId, GameObject card, IReadOnlyList<ObjectId>? spliced)
    {
        if (spliced is null || spliced.Count == 0)
            return [];

        var cards = ImmutableList.CreateBuilder<Domain.Models.CardDefinition>();
        var seen = new HashSet<ObjectId>();

        foreach (var id in spliced)
        {
            // Revealed from hand, so it has to be in hand - and the same card cannot be revealed
            // twice to pay for two copies of its own text.
            if (!seen.Add(id))
                throw new InvalidOperationException("The same card cannot be spliced twice.");

            var onto = State.GetObject(id);

            if (onto.Zone != Zone.Hand || onto.OwnerId != playerId)
                throw new InvalidOperationException("A spliced card is revealed from your hand (CR 702.47a).");

            if (_abilities.SpellOf(onto.Card) is not { SpliceCost: not null } splice)
                throw new InvalidOperationException($"{onto.Card.Name} has no splice (CR 702.47a).");

            // Each card names the spells it may be spliced onto, and it is asked of that card
            // rather than assumed to be Arcane: "splice onto instant or sorcery" is the same
            // keyword aimed at a card type. The quality is read off the spell being cast rather
            // than computed - a card on the stack is not a permanent and nothing is changing its
            // types.
            if (!SearchFilters.Matches(splice.SpliceOnto, card.Card))
            {
                throw new InvalidOperationException(
                    $"{card.Card.Name} is not a spell {onto.Card.Name} can be spliced onto "
                        + "(CR 702.47a).");
            }

            cards.Add(onto.Card);
        }

        return cards.ToImmutable();
    }

    /// <summary>Everything a spec could legally be pointed at right now (CR 115.1).</summary>
    /// <summary>
    /// A trigger's source dressed as the ability it is about to become, for target legality.
    /// </summary>
    /// <remarks>
    /// Targets are chosen as the ability goes on the stack (CR 603.3d), so the checks that read
    /// the ability - what "that player" means, which object counts as "another" - have to be
    /// able to see it a moment before it exists. Everything they ask about is already decided by
    /// then and carried on the waiting trigger.
    /// </remarks>
    private GameObject? AsPendingAbility(PendingTrigger trigger)
    {
        if (SourceNow(trigger.SourceId) is not { } source)
            return null;

        return source with
        {
            Ability = new AbilityOnStack
            {
                SourceId = trigger.SourceId,
                AbilityId = trigger.AbilityId,
                Text = trigger.Text,
                SubjectPlayer = trigger.SubjectPlayer,
                SubjectObject = trigger.SubjectObject,
            },
        };
    }

    /// <summary>
    /// The object a trigger's source is now, following one zone change if it has made one.
    /// </summary>
    /// <remarks>
    /// A leave-the-battlefield trigger names a source that no longer exists under that id
    /// (CR 400.7), and looking it up plainly returns nothing - so every source-dependent check
    /// was skipped for exactly the abilities that need one. "When this dies, return
    /// <em>another</em> target card from your graveyard" then offered the card that had just
    /// died, which is the one card the word forbids.
    /// </remarks>
    private GameObject? SourceNow(ObjectId sourceId)
    {
        if (State.TryGetObject(sourceId, out var alive))
            return alive;

        foreach (var candidate in State.Objects.Values)
        {
            if (candidate.PreviousId == sourceId)
                return candidate;
        }

        return null;
    }

    /// <param name="announced">
    /// The X the asking object was cast for, for a filter written around one (CR 607.2). Null
    /// where nothing announced one, and a spec that reads X then offers nothing rather than
    /// measuring against a zero nobody chose.
    /// </param>
    /// <param name="peers">
    /// The earlier targets this spec is measured against (CR 601.2c, 115.3), when it names any.
    /// A trigger chooses its targets one question at a time, so by the time the second question
    /// is asked the answers to the first are what "other" and "single" are about — and the
    /// offered list has to honour that, or the board shows a pick the engine will refuse.
    /// </param>
    private List<(string Id, string Label, Target Target)> LegalTargetsFor(
        TargetSpec spec,
        Guid controllerId,
        GameObject? source = null,
        int? announced = null,
        IReadOnlyList<Target>? peers = null)
    {
        var options = new List<(string, string, Target)>();

        if (spec.Kind is TargetKind.Player or TargetKind.Any)
        {
            foreach (var playerId in State.TurnOrder)
            {
                var target = Target.ToPlayer(playerId);
                if (spec.IsLegal(
                        State, _abilities, target, controllerId, source, peers, announced))
                {
                    options.Add((
                        "player:" + playerId.ToString("N"),
                        State.GetPlayer(playerId).Name,
                        target));
                }
            }
        }

        if (spec.Kind is not TargetKind.Player)
        {
            var zone = spec.Kind switch
            {
                TargetKind.SpellOnStack => Zone.Stack,
                TargetKind.CardInGraveyard => Zone.Graveyard,
                _ => Zone.Battlefield,
            };

            foreach (var id in ObjectsIn(zone))
            {
                var target = spec.Kind switch
                {
                    TargetKind.SpellOnStack => Target.ToSpell(id),
                    TargetKind.CardInGraveyard => Target.ToCard(id),
                    _ => Target.ToPermanent(id),
                };

                if (spec.IsLegal(
                        State, _abilities, target, controllerId, source, peers, announced))
                {
                    // The prefix carries which kind of target this is, because the option id is
                    // all that survives the question — the Target built here is thrown away when
                    // the game stops to ask, and rebuilt from the answer.
                    var prefix = spec.Kind switch
                    {
                        TargetKind.SpellOnStack => "spell:",
                        TargetKind.CardInGraveyard => "card:",
                        _ => "object:",
                    };

                    options.Add((
                        prefix + id.Value.ToString("N"),
                        LabelFor(State.GetObject(id), controllerId),
                        target));
                }
            }
        }

        return options;
    }

    /// <summary>
    /// What to call one option, so two of the same card can be told apart.
    /// </summary>
    /// <remarks>
    /// The card name alone is not an option: a board with three Grizzly Bears on it, two of them
    /// the opponent's, offers three identical buttons and the player cannot tell which one they
    /// are pointing at. The engine already learned this about ordering triggers — an ordering
    /// whose picks are indistinguishable is not an ordering — and it is the same mistake here.
    /// </remarks>
    private string LabelFor(GameObject obj, Guid chooserId) =>
        obj.ControllerId == chooserId
            ? obj.Card.Name + " (yours)"
            : obj.Card.Name + " (" + State.GetPlayer(obj.ControllerId).Name + "'s)";

    /// <summary>Every object in a zone, across all players for the per-player ones.</summary>
    private IEnumerable<ObjectId> ObjectsIn(Zone zone) => zone switch
    {
        Zone.Battlefield => State.Battlefield,
        Zone.Stack => State.Stack,
        _ => State.TurnOrder.SelectMany(p => State.GetPlayer(p).Graveyard),
    };

    /// <summary>Discards a card from hand (CR 701.9), which is how cleanup is satisfied.</summary>
    public void Discard(Guid playerId, ObjectId cardId)
    {
        var card = State.GetObject(cardId);
        if (card.Zone != Zone.Hand || card.OwnerId != playerId)
            throw new InvalidOperationException("That card is not in that player's hand.");

        Move(cardId, Zone.Graveyard, MoveCause.Discard);
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

    /// <summary>Puts counters of a kind on a permanent (CR 121.1).</summary>
    /// <remarks>
    /// For putting a board into a state, the way <see cref="Create"/> and <see cref="Attach"/>
    /// are: a permanent that has been storing counters up for several turns is a position, and
    /// playing out those turns to reach it says nothing the test is about.
    /// </remarks>
    public void AddCounters(ObjectId permanentId, string kind, int count)
    {
        if (count != 0)
            Emit(new CountersChanged(permanentId, kind, count));
    }

    /// <summary>Attaches a permanent to another permanent, or to a player (CR 701.3).</summary>
    /// <remarks>
    /// The position an Aura or Equipment is in, set directly. A resolving Aura attaches itself as
    /// part of its own effect; this is for putting a board into a state rather than playing into
    /// one, the way <see cref="Create"/> and <see cref="MarkDamage"/> are — and an Aura's abilities
    /// mostly say nothing until it is on something, so a test that cannot attach cannot reach them.
    /// </remarks>
    public void Attach(ObjectId permanentId, ObjectId? to, Guid? toPlayer = null) =>
        Emit(new PermanentAttached(permanentId, to, toPlayer));

    /// <summary>
    /// Turns a permanent to its other face (CR 701.28).
    /// </summary>
    /// <remarks>
    /// A permanent with fewer than two faces cannot transform and nothing happens (CR 712.9) -
    /// silently, because the rules treat an impossible transform as one that simply does not
    /// occur rather than as an error.
    /// </remarks>
    public void Transform(ObjectId permanentId)
    {
        if (!State.TryGetObject(permanentId, out var permanent)
            || permanent.Permanent is not { } onBattlefield
            || permanent.Card.Faces.Count < 2)
        {
            return;
        }

        Emit(new PermanentTransformed(
            permanentId, onBattlefield.FaceIndex == 0 ? 1 : 0));
    }

    /// <summary>
    /// Marks damage on a permanent (CR 120.3). It is not destroyed here — state-based actions
    /// compare the damage with its toughness the next time anyone would get priority (CR 704.5g).
    /// </summary>
    public void MarkDamage(
        ObjectId permanentId, int amount, bool fromDeathtouch = false, ObjectId sourceId = default)
    {
        if (amount <= 0)
            return;

        Emit(new DamageMarked(permanentId, amount, fromDeathtouch, sourceId));
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

    /// <summary>
    /// Adds mana to a player's pool (CR 106.1).
    /// </summary>
    /// <remarks>
    /// Mana normally arrives from a mana ability, which goes through
    /// <see cref="ActivateAbility"/>. This is for the effects that add it directly — a ritual,
    /// or a triggered ability — and for a test that needs a pool without a board to make one.
    /// </remarks>
    public void AddMana(Guid playerId, ManaColor? color, int amount = 1)
    {
        if (amount <= 0)
            return;

        Emit(new ManaAdded(playerId, color, amount));
    }

    /// <summary>
    /// Deals damage to a player (CR 119.3, 120.3).
    /// </summary>
    /// <remarks>
    /// Combat damage is flagged because a great deal turns on it, including whether it counts
    /// toward the twenty-one from a single commander (CR 903.10a).
    /// </remarks>
    public void MarkDamageToPlayer(
        Guid playerId, ObjectId sourceId, int amount, bool isCombat = true)
    {
        if (amount <= 0)
            return;

        Emit(new PlayerDamaged(playerId, sourceId, amount, isCombat));
    }

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

        if (State.IsWaitingForChoice)
            throw new InvalidOperationException(
                $"The game is waiting on a decision: {State.Choice!.Prompt}");

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
    /// <para>
    /// <strong>The naming of the arms is the contract.</strong> A step called <c>Ask…</c> only
    /// ever puts a question to somebody, so returning from the loop is the whole point of it: the
    /// game halts and nothing else may happen until the answer comes back. A step called
    /// <c>Settle…</c> or <c>Offer…</c> can finish without asking anybody anything, so it must send
    /// the loop round again — <c>{ didSomething = true; continue; }</c> — because everything below
    /// it here, the state-based actions and the waiting triggers among them, still has to run.
    /// Returning from one of those is the bug this engine has now found four times, in the roll,
    /// the shuffle, the coin flip and the miracle offer, each time by accident and each time after
    /// shipping. <c>Every_settling_step_of_the_sweep_goes_round_rather_than_returning</c> is what
    /// stops there being a fifth; a step that starts settling has to be renamed to match.
    /// </para>
    /// <para>
    /// The <c>continue</c> is safe for a step that <em>sometimes</em> asks, which is what the
    /// clash and the look-and-take do: the top of this loop returns the moment a question is
    /// outstanding, so it never runs past one.
    /// </para>
    /// </remarks>
    /// <returns>Whether anything happened, which means the game changed under the players.</returns>
    private bool SettleBeforePriority()
    {
        var didSomething = false;

        for (var guard = 0; guard < 100; guard++)
        {
            if (State.IsOver || State.IsWaitingForChoice)
                return didSomething;

            // CR 704.5j: the legend rule is a choice, not a rule the engine may answer. Asked
            // before the rest of the batch, because the answer changes what the batch is.
            if (AskLegendRuleIfNeeded())
                return true;

            // An ability that divides something has to say how before anybody has priority
            // (CR 601.2d). Asked first among the questions below, because it is part of putting
            // the ability on the stack rather than something an effect asked for.
            if (AskOwedDivision())
                return true;

            // A discard, scry or surveil an effect asked for. They wait until here so that a
            // resolution is never stopped half way through — see the note on _looksOwed.
            if (AskOwedPayment())
                return true;

            if (AskOwedDiscard())
                return true;

            // Look-and-take settles as well as asking: when nothing among the cards it turned up
            // matches the filter there is nobody to ask, so it files them and the sweep goes
            // round again. `continue` covers both halves, because the top of this loop returns
            // the moment a question is outstanding - which is the shape every mixed step here
            // now takes, rather than each one guessing which of the two it was.
            if (SettleOwedLookAndTake())
            {
                didSomething = true;
                continue;
            }

            // Before the search, and that order is the whole mechanism: the extraction family
            // prints "Choose a nonland card name." and then searches for cards with that name,
            // and the search is queued holding a sentinel the answer fills in. Asked the other
            // way round, the search would run against a filter no card answers to and the
            // spell would report itself as having looked and found nothing.
            if (AskOwedCardNameChoice())
                return true;

            if (AskOwedSearch())
                return true;

            // A seek asks nobody anything, so it is performed here and the sweep goes round
            // again: the cards it moved can have triggered something, and a settle that returned
            // as though it were waiting would leave those triggers on the floor.
            if (SettleOwedSeek())
            {
                didSomething = true;
                continue;
            }

            if (AskOwedSacrificeUnless())
                return true;

            if (AskOwedEncoding())
                return true;

            if (AskOwedLibraryEnd())
                return true;

            // A miracle is an offer, not a question (CR 702.94a): the card is turned face up and
            // may be cast for its miracle cost, and nobody is stopped to answer anything - the
            // player takes it by casting, like a cascade's hit. Returning as though a question
            // were pending abandoned the whole rest of this sweep, state-based actions included,
            // on the strength of a settle step that had asked nothing at all.
            if (OfferOwedMiracles())
            {
                didSomething = true;
                continue;
            }

            if (AskOwedProliferate())
                return true;

            if (AskOwedPermanentChoice())
                return true;

            if (AskOwedEnlist())
                return true;

            if (AskOwedExploit())
                return true;

            if (AskOwedSoulbond())
                return true;

            if (AskOwedPopulate())
                return true;

            if (AskOwedManifestDread())
                return true;

            if (AskOwedTouchedChoice())
                return true;

            if (AskOwedConnive())
                return true;

            if (AskOwedHandChoice())
                return true;

            if (AskOwedCreatureTypeChoice())
                return true;

            if (AskOwedLandTypeChoice())
                return true;

            if (AskOwedColorChoice())
                return true;

            // The forced arm first, so a menu with one item pays out and the sweep goes
            // round again rather than stopping the game over a decision with one outcome.
            if (SettleForcedManaColors())
            {
                didSomething = true;
                continue;
            }

            if (AskOwedManaColorChoice())
                return true;

            // The same pair, for the same rule: a menu that has narrowed to one source puts the
            // shield up here and sends the sweep round again, rather than stopping the game on a
            // question with one button on it.
            if (SettleForcedDamageSource())
            {
                didSomething = true;
                continue;
            }

            if (AskOwedDamageSourceChoice())
                return true;

            if (AskOwedUntapChoice())
                return true;

            if (AskOwedCounterChoice())
                return true;

            if (AskOwedLibraryOrder())
                return true;

            if (AskOwedDevour())
                return true;

            if (AskOwedAmplify())
                return true;

            // CR 310.9a: a battle's protector is designated by its controller. A forced
            // designation is an event rather than a question, so the sweep goes round again;
            // a real one stops the settle the way every question does.
            if (ChooseForcedProtectors())
            {
                didSomething = true;
                continue;
            }

            if (AskOwedProtector())
                return true;

            if (AskOwedReadAhead())
                return true;

            if (AskOwedEntryChoice())
                return true;

            if (AskOptionalUntaps())
                return true;

            // A shuffle asks nobody anything, so it is performed here and the sweep goes round
            // again - the same treatment as a seek or a roll, and for the same reason. Returning
            // as though a question were pending left the *second* of two owed shuffles unperformed
            // until something else pushed the sweep round: "each player shuffles their graveyard
            // into their library" emits one per player, and one player's graveyard sat there,
            // unshuffled, while priority was handed back mid-resolution.
            if (SettleOwedShuffle())
            {
                didSomething = true;
                continue;
            }

            if (AskOwedRingBearer())
                return true;

            if (AskOwedVenture())
                return true;

            // A clash is three steps and only the middle one is a question: it reveals, asks each
            // clashing player where their card goes, and then works out who won and runs the
            // branch. Two of the three ask nobody - the reveal that found two empty libraries,
            // and the finish - and both used to return as though somebody were being asked, so
            // the branch a clash had just won ran and priority went back with the state-based
            // actions unchecked. `continue` is right for all three, because the top of this loop
            // returns the moment a question really is outstanding.
            if (SettleOwedClash())
            {
                didSomething = true;
                continue;
            }

            // Neither a discover nor a cascade asks anything: the exiling is not a decision, and
            // what the player gets is an offer they take by casting the card (CR 701.57a,
            // 702.85a). Both said so in their own remarks and both then returned from the sweep
            // as though a question were pending, which is the third and fourth instance of one
            // bug - the same one the shuffle and the roll above record.
            if (SettleOwedDiscover())
            {
                didSomething = true;
                continue;
            }

            if (SettleOwedCascade())
            {
                didSomething = true;
                continue;
            }

            if (AskOwedRipple())
                return true;

            // A coin flip asks nobody anything either - the flip is the game's decision, made
            // through the one seeded source, and only its outcome goes in the log. So the branch
            // it picks runs here and the sweep goes round again: a flip that burned a creature
            // to death left it standing, and a scry owed alongside was never asked at all.
            if (SettleOwedFlip())
            {
                didSomething = true;
                continue;
            }

            // A roll asks nobody anything - there is no modifier to choose between (CR 706.2b
            // is a replacement, applied on the way in) - so it is made here and the sweep goes
            // round again, the way a seek does. Returning as though a question were pending
            // handed priority back with the state-based actions unchecked: a creature the row
            // had just dealt lethal damage to went on standing there, alive, on a board where
            // the damage was plainly marked on it.
            if (SettleOwedRoll())
            {
                didSomething = true;
                continue;
            }

            if (AskOwedLook())
                return true;

            if (IncreaseSpeedIfOwed())
            {
                didSomething = true;
                continue;
            }

            // CR 702.145c-g: the day-night consequences are "any time" rules rather than
            // triggers, so they belong in the same sweep that checks state-based actions -
            // asked before them, because turning a permanent over changes what the actions see.
            var before = State;
            KeepTheTimeOfDay();
            if (!ReferenceEquals(before, State))
            {
                didSomething = true;
                continue;
            }

            // CR 611.2b: an effect that lasts "for as long as ..." ends the moment its condition
            // stops holding, and does not come back if the condition does. That makes it an
            // ending rather than a pause, which is why it is done here - where the game notices
            // things about itself - and not inside the effect's own Applies.
            if (EndEffectsWhoseConditionFailed())
            {
                didSomething = true;
                continue;
            }

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

            // CR 603.8: a state trigger watches the game rather than an event, so this is the
            // only place it can be noticed - the same sweep that checks state-based actions, and
            // for the same reason. Checked after the actions have settled, so a creature that has
            // just died is not asked whether it controls any Islands.
            if (CheckStateTriggers())
            {
                didSomething = true;
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

    /// <summary>
    /// Asks a player which duplicate legendary permanent to keep, if they have any (CR 704.5j).
    /// </summary>
    /// <returns>True when a question was asked and the settle has to stop.</returns>
    private bool AskLegendRuleIfNeeded()
    {
        // Both halves computed. Legendary because an effect can grant it - the Ring does, to
        // its bearer - and the controller because control is layer 2 (CR 613.1b): a stolen
        // legend belongs to whoever controls it now, and reading the stored value grouped it
        // with its old controller's permanents instead. That is the same mistake this file
        // records finding in eight other places.
        var groups = State.Battlefield
            .Select(State.GetObject)
            .Select(o => (Object: o, Computed: Characteristics.Of(State, _abilities, o)))
            .Where(pair => pair.Computed.IsLegendary)

            // CR 704.5j asks about the *name*, and a name is a copiable value (CR 707.2): a
            // Clone of Kenrith is a second Kenrith and the two cannot both stay. Grouping by the
            // printed name asked which card each permanent came from, which is the one thing the
            // rule is not about.
            .GroupBy(pair => (pair.Computed.ControllerId, pair.Computed.Name))
            .Where(g => g.Count() > 1)
            .ToList();

        if (groups.Count == 0)
            return false;

        var group = groups[0];
        Ask(new PendingChoice
        {
            Id = "legend:" + group.Key.ControllerId.ToString("N") + ":" + group.Key.Name,
            PlayerId = group.Key.ControllerId,
            Kind = ChoiceKind.LegendRule,
            Prompt = $"You control more than one {group.Key.Name}. Choose the one to keep; "
                + "the rest go to the graveyard.",
            Options = [.. group.Select(pair => new ChoiceOption(
                pair.Object.Id.Value.ToString("N"),
                $"{pair.Computed.Name} ({pair.Computed.Power}/{pair.Computed.Toughness})"))],
            Context = [group.Key.Name],
        });

        return true;
    }

    private void KeepLegend(PendingChoice choice, string keptId)
    {
        foreach (var option in choice.Options)
        {
            if (string.Equals(option.Id, keptId, StringComparison.Ordinal))
                continue;

            var doomed = State.Objects.Keys.First(
                id => string.Equals(id.Value.ToString("N"), option.Id, StringComparison.Ordinal));

            Move(doomed, Zone.Graveyard, MoveCause.StateBasedAction);
        }

        _priorityRecipient = choice.ResumePriorityTo;
        SettleBeforePriority();
        GrantPriorityAfterSettle(choice.ResumePriorityTo);
    }

    /// <summary>
    /// Whose decision a replacement order is (CR 616.1: the affected object's controller, or
    /// the affected player).
    /// </summary>
    private Guid AffectedPlayer(GameEvent e) => e switch
    {
        DamageMarked damage when State.TryGetObject(damage.Id, out var obj) => obj.ControllerId,
        PlayerDamaged damaged => damaged.PlayerId,
        ObjectMoved moved when State.TryGetObject(moved.OldId, out var obj) => obj.ControllerId,

        // CR 616.1 asks the affected object's controller, and a permanent created straight onto
        // the battlefield has one on the event - it was never anywhere the state could be asked
        // about. Falling through to the active player put "you may have this enter as a copy of"
        // to the wrong person whenever it arrived on somebody else's turn.
        ObjectCreated made => made.ControllerId,
        LifeChanged life => life.PlayerId,

        // CR 706.2b: the player who rolls chooses among effects modifying the roll.
        DiceRollRequested rolling => rolling.PlayerId,
        _ => State.ActivePlayerId,
    };

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
    /// <summary>
    /// Fires abilities whose condition is true rather than abilities watching an event (CR 603.8).
    /// </summary>
    /// <returns>True when anything armed or disarmed, so the settle goes round again.</returns>
    /// <remarks>
    /// Firing once per condition rather than once per check is the whole difficulty. A trigger
    /// whose resolution does not clear its own condition - "when an opponent has 10 or less life,
    /// this becomes a creature" - would otherwise trigger on every settle for the rest of the
    /// game. So the fact that it has fired is recorded, and it can only fire again once the
    /// condition has stopped being true.
    /// </remarks>
    private bool CheckStateTriggers()
    {
        var changed = false;
        var live = new HashSet<string>(StringComparer.Ordinal);

        foreach (var id in State.Battlefield)
        {
            var obj = State.GetObject(id);

            // The same reader every other trigger goes through, rather than the card: a state
            // trigger on a copied card is one of the copy's abilities (CR 707.2a), and asking
            // the printed card watched the wrong permanent's condition. It also writes the
            // ability down where a resolution can find it again, which a card lookup cannot.
            foreach (var ability in TriggersWatching(State, obj))
            {
                if (ability.StateCondition is not { } holds)
                    continue;

                var key = $"{id.Value:N}:{ability.Id}";
                live.Add(key);

                var armed = State.ArmedStateTriggers.Contains(key);
                var nowTrue = holds(State, _abilities, obj, null);

                if (nowTrue == armed)
                    continue;

                Emit(new StateTriggerArmed(key, nowTrue));
                changed = true;

                if (nowTrue)
                {
                    Emit(new AbilityTriggered(
                        id, ability.Id, ability.Text, ControllerOf(obj)));
                }
            }
        }

        // A permanent that has left takes its armed conditions with it. The object it becomes
        // elsewhere is a different one (CR 400.7) and gets a different key, so nothing is
        // carried over - this only stops the set growing without bound.
        foreach (var stale in State.ArmedStateTriggers.Where(k => !live.Contains(k)))
        {
            Emit(new StateTriggerArmed(stale, false));
            changed = true;
        }

        return changed;
    }

    private void PutTriggersOnStack()
    {
        var waiting = State.PendingTriggers;

        foreach (var playerId in State.ApnapOrder())
        {
            var mine = waiting.Where(t => t.ControllerId == playerId).ToList();

            // CR 603.3b: a player with more than one waiting trigger chooses the order theirs
            // go on the stack in. The engine kept the order they happened to trigger in, which
            // is a legal order and not necessarily the one they wanted — with two triggers it
            // decides which resolves first.
            // Asked only when the answer could differ. One ability can trigger twice at once -
            // "whenever you cast or copy a spell" sees a spell and its copy - and both waiting
            // triggers are then the same source and the same ability, so their options are the
            // same string and the question cannot be answered at all: every ordering repeats a
            // pick, which is refused. Any order of two identical triggers is the same order, so
            // the one they arrived in is used and nobody is asked.
            var orderable = mine.Select(TriggerKey).Distinct(StringComparer.Ordinal).Count();

            if (mine.Count > 1 && orderable == mine.Count
                && !_triggerOrder.TryGetValue(playerId, out _))
            {
                Ask(new PendingChoice
                {
                    Id = "triggers:" + playerId.ToString("N"),
                    PlayerId = playerId,
                    Kind = ChoiceKind.OrderTriggers,
                    Prompt = "Choose the order your triggered abilities go on the stack. "
                        + "The last one you pick resolves first.",
                    // Keyed by source *and* ability: two copies of the same card share an
                    // ability id, so keying on that alone gives two options that cannot be told
                    // apart — and an ordering whose picks are indistinguishable is not an
                    // ordering.
                    Options = [.. mine.Select(t =>
                        new ChoiceOption(TriggerKey(t), t.Text))],
                    MinPicks = mine.Count,
                    MaxPicks = mine.Count,
                });
                return;
            }

            if (_triggerOrder.TryGetValue(playerId, out var order))
            {
                // Ordered by the answer, then anything the answer did not name. Dropping those
                // would be losing an ability that triggered, which CR 603.3b does not allow: the
                // choice is what order they go on the stack in, not whether they go on it.
                var named = order
                    .Select(key => mine.FirstOrDefault(t =>
                        string.Equals(TriggerKey(t), key, StringComparison.Ordinal)))
                    .Where(t => t is not null)
                    .Select(t => t!)
                    .ToList();

                mine = [.. named, .. mine.Where(t => !named.Contains(t))];
                _triggerOrder.Remove(playerId);
            }

            foreach (var trigger in mine)
            {
                // CR 603.6: the source may already have left the battlefield. The ability still
                // goes on the stack — it triggered, and that is enough.
                var sourceCard = State.TryGetObject(trigger.SourceId, out var source)
                    ? source.Card
                    : LastKnownCard(trigger.SourceId);

                // CR 603.3d: the controller chooses the ability's targets as it is put on the
                // stack, not when it resolves. Until this existed a targeting trigger reached
                // the stack with no targets at all and quietly did nothing on the way down.
                var modal = TriggerBehind(sourceCard, trigger.AbilityId);
                var modeKey = TriggerKey(trigger);
                var pickedModes = _triggerModes.TryGetValue(modeKey, out var picked) ? picked : [];

                // CR 603.3c: modes are chosen as the ability goes on the stack, and before its
                // targets - the modes decide what there is to target at all.
                //
                // CR 700.2d: "choose one or both" is a range, not a count, and the ability
                // carries both ends of it. Only the lower end was ever read here, so a trigger
                // offering a range asked for its minimum and stopped: the second half of "choose
                // one or both" could not be taken at all, on a card that compiled cleanly and
                // played without complaint. ModesMax was written by the compiler and read by
                // nothing, which is why nothing noticed.
                if (modal is { ModesToChoose: > 0 } offering
                    && pickedModes.Count < ModeCeiling(offering)
                    && !_triggerModesClosed.Contains(modeKey))
                {
                    // Past the minimum every further mode is optional, so there has to be a way
                    // to stop. Offered as an option rather than as a wider MaxPicks because the
                    // modes are picked one at a time and the order they were picked in is what
                    // slices the targets below (CR 601.2c).
                    var modeOptions = offering.Modes
                        .Select((one, index) => (Mode: one, Index: index))
                        .Where(one => !pickedModes.Contains(one.Index))
                        .Select(one => new ChoiceOption(
                            one.Index.ToString(
                                System.Globalization.CultureInfo.InvariantCulture),
                            one.Mode.Text))
                        .ToList();

                    if (pickedModes.Count >= offering.ModesToChoose)
                        modeOptions.Add(new ChoiceOption(NoFurtherMode, "Choose no more modes."));

                    Ask(new PendingChoice
                    {
                        Id = "trigger-modes:" + modeKey + ":" + pickedModes.Count.ToString(
                            System.Globalization.CultureInfo.InvariantCulture),
                        PlayerId = trigger.ControllerId,
                        Kind = ChoiceKind.ChooseTriggerMode,
                        Prompt = trigger.Text,
                        Options = [.. modeOptions],
                        MinPicks = 1,
                        MaxPicks = 1,
                    });
                    return;
                }

                // A modal ability targets whatever its chosen modes target, in the order they
                // were picked - the same slicing the spell path does.
                //
                // The source id on the other arm is load-bearing and was missing. An ability that
                // is not on its source card - one an Aura granted, a trigger from the card
                // under a mutated permanent, or a dungeon room, which no card carries at all -
                // is found by (source, ability id) and not by card, so a
                // lookup without the source fell through every arm of TargetsOfAbility and came
                // back empty. The trigger then went on the stack with nothing to target, resolved,
                // and did nothing. Its *effects* were found correctly, by the same lookup with the
                // source passed - so the ability looked implemented, and only the half that
                // chooses a target was blind.
                var specs = ImmutableList<TargetSpec>.Empty;

                if (modal is { ModesToChoose: > 0 })
                {
                    // The same shift the spell path makes, for the same reason: a mode's peer
                    // index is numbered from that mode's own list (CR 601.2c).
                    foreach (var index in pickedModes)
                    {
                        specs = specs.AddRange(
                            TargetSpec.ShiftPeers(modal.Modes[index].Targets, specs.Count));
                    }
                }
                else
                {
                    specs = TargetsOfAbility(sourceCard, trigger.AbilityId, trigger.SourceId)
                        ?? [];
                }

                if (!specs.IsEmpty)
                {
                    var key = TriggerKey(trigger);
                    var chosen = _triggerTargets.TryGetValue(key, out var got) ? got : [];

                    if (chosen.Count < specs.Count)
                    {
                        var spec = specs[chosen.Count];
                        // The source is handed over so the offered list honours protection the
                        // same way the engine does: the board may never offer what the rules
                        // would refuse.
                        // The source is offered as the ability it is about to become, not as the
                        // bare permanent: "target creature that player controls" is answered from
                        // the trigger's subject, and a permanent carries no such thing. Offering
                        // the permanent meant the filter found no subject, refused every option,
                        // and the ability was removed for having no legal target.
                        // The X the source was cast for, so "target ... with mana value X or
                        // less" on a permanent's own trigger measures against the value
                        // announced for the spell that became it (CR 607.2).
                        // The answers already given are what "any other target" and "from a
                        // single graveyard" measure against (CR 601.2c). A trigger fills its
                        // list one question at a time, so the peer is simply one of the answers
                        // already in hand - and offering a pick the engine would then refuse is
                        // the one thing the board must never be shown.
                        var options = LegalTargetsFor(
                            spec,
                            trigger.ControllerId,
                            AsPendingAbility(trigger),
                            SourceNow(trigger.SourceId)?.VariableValue,
                            PeersOf(specs, chosen, chosen.Count));

                        // CR 601.2c: "up to" targets may be left unchosen, so a trailing
                        // optional spec with nothing legal left is not an ability with no
                        // targets - it is an ability with the targets it has, and it goes on the
                        // stack with them. Removing it instead is how "exile up to two target
                        // cards from a single graveyard" would do nothing at all whenever one
                        // card was there to exile, which is most of the time this trigger fires.
                        if (options.Count == 0 && spec.Optional)
                        {
                            Emit(new TriggerPutOnStack(
                                ObjectId.New(),
                                trigger.SourceId,
                                sourceCard,
                                trigger.AbilityId,
                                trigger.Text,
                                trigger.ControllerId)
                            {
                                Targets = [.. chosen],
                                Modes = [.. pickedModes],
                                SubjectPlayer = trigger.SubjectPlayer,
                                SubjectObject = trigger.SubjectObject,
                                SubjectAmount = trigger.SubjectAmount,
                                VariableValue = SourceNow(trigger.SourceId)?.VariableValue ?? 0,
                            });
                            _triggerTargets.Remove(key);
                            _triggerModes.Remove(modeKey);
                            _triggerModesClosed.Remove(modeKey);
                            continue;
                        }

                        // CR 603.3d: an ability that needs a target and has none legal is
                        // removed from the stack — it never goes on it at all.
                        if (options.Count == 0)
                        {
                            Emit(new TriggerRemovedForNoTargets(
                                trigger.SourceId,
                                trigger.AbilityId,
                                trigger.Text,
                                trigger.ControllerId));
                            continue;
                        }

                        Ask(new PendingChoice
                        {
                            Id = "trigger-targets:" + key + ":" + chosen.Count.ToString(
                                System.Globalization.CultureInfo.InvariantCulture),
                            PlayerId = trigger.ControllerId,
                            Kind = ChoiceKind.ChooseTriggerTargets,
                            Prompt = trigger.Text + " Choose " + spec.Description + ".",
                            Options = [.. options.Select(o => new ChoiceOption(o.Id, o.Label))],
                            MinPicks = 1,
                            MaxPicks = 1,
                        });
                        return;
                    }

                    Emit(new TriggerPutOnStack(
                        ObjectId.New(),
                        trigger.SourceId,
                        sourceCard,
                        trigger.AbilityId,
                        trigger.Text,
                        trigger.ControllerId)
                    {
                        Targets = [.. chosen],
                        Modes = [.. pickedModes],
                        SubjectPlayer = trigger.SubjectPlayer,
                        SubjectObject = trigger.SubjectObject,
                        SubjectAmount = trigger.SubjectAmount,
                        VariableValue = SourceNow(trigger.SourceId)?.VariableValue ?? 0,
                    });
                    _triggerTargets.Remove(key);
                    _triggerModes.Remove(modeKey);
                    _triggerModesClosed.Remove(modeKey);
                    continue;
                }

                Emit(new TriggerPutOnStack(
                    ObjectId.New(),
                    trigger.SourceId,
                    sourceCard,
                    trigger.AbilityId,
                    trigger.Text,
                    trigger.ControllerId)
                {
                    Modes = [.. pickedModes],
                    SubjectPlayer = trigger.SubjectPlayer,
                    SubjectObject = trigger.SubjectObject,
                    SubjectAmount = trigger.SubjectAmount,
                    VariableValue = SourceNow(trigger.SourceId)?.VariableValue ?? 0,
                });

                _triggerModes.Remove(modeKey);
                _triggerModesClosed.Remove(modeKey);
            }
        }
    }

    /// <summary>
    /// The card an object had, for a source that has since left the battlefield (CR 603.10).
    /// </summary>
    /// <summary>
    /// The card behind a source id, whether or not that object still exists.
    /// </summary>
    /// <remarks>
    /// Every deferred question carries a locator — which permanent, which ability, which effect
    /// index — and reads the branch back out of the card when the answer arrives. That lookup
    /// asked the live state for the object and gave up when it was gone, which is precisely the
    /// case the locator exists to survive: a death trigger's source is in the graveyard under a
    /// new id by the time anything it asked for is answered (CR 400.7). Every "when this dies,
    /// you may ..." dropped its offer in silence.
    /// <para>
    /// Returns null rather than throwing where <see cref="LastKnownCard"/> throws, because a
    /// source with no history at all is a question that can no longer be answered rather than a
    /// broken game — the token that made the offer may simply have ceased to exist.
    /// </para>
    /// </remarks>
    /// <summary>
    /// Who controlled an object, whether or not that object still exists (CR 400.7).
    /// </summary>
    /// <remarks>
    /// <see cref="CardBehind"/> for controllers. Handed to every resolution so that a sentence
    /// asking about something an earlier sentence already destroyed still has an answer.
    /// </remarks>
    private Guid? ControllerBehind(ObjectId sourceId) => ObjectBehind(sourceId)?.ControllerId;

    /// <summary>
    /// The object an id became, whether or not that id still names anything (CR 400.7).
    /// </summary>
    /// <remarks>
    /// <see cref="CardBehind"/> for whole objects. Handed to every resolution so an effect asked
    /// about something an earlier event already moved still has somewhere to look.
    /// </remarks>
    private GameObject? ObjectBehind(ObjectId sourceId)
    {
        if (State.TryGetObject(sourceId, out var live))
            return live;

        foreach (var e in _log.OfType<ObjectMoved>().Reverse())
        {
            if (e.OldId == sourceId && State.TryGetObject(e.NewId, out var moved))
                return moved;
        }

        return null;
    }

    private CardDefinition? CardBehind(ObjectId sourceId)
    {
        if (State.TryGetObject(sourceId, out var live))
            return live.Card;

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

        return null;
    }

    /// <remarks>
    /// The id is followed backwards through every move it has made until a creation is reached,
    /// rather than forwards to wherever it went. Forwards only worked while the object it became
    /// still existed: a permanent that left the battlefield and then ceased to be - a token, or a
    /// card exiled and cleaned up - had no object at the far end, and its creation is filed under
    /// the id it had in the library, several moves back. A leave-the-battlefield trigger on such
    /// a permanent then threw as it went on the stack, which the corpus soak found on one table
    /// in eight hundred.
    /// </remarks>
    private CardDefinition LastKnownCard(ObjectId sourceId)
    {
        // Forwards first, because it is the cheap answer and right whenever the object is still
        // there: it gives the card as it is now rather than as it was created.
        foreach (var e in _log.OfType<ObjectMoved>().Reverse())
        {
            if (e.OldId == sourceId && State.TryGetObject(e.NewId, out var moved))
                return moved.Card;
        }

        var seen = new HashSet<ObjectId>();
        var chasing = sourceId;

        while (seen.Add(chasing))
        {
            foreach (var e in _log.OfType<ObjectCreated>().Reverse())
            {
                if (e.Id == chasing)
                    return e.Card;
            }

            var earlier = _log.OfType<ObjectMoved>().LastOrDefault(e => e.NewId == chasing);
            if (earlier is null)
                break;

            chasing = earlier.OldId;
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

        // Which state an ability looks at depends on which side of the event its source is on
        // (CR 603.6). An enters-the-battlefield ability triggers on the game as it is *after* the
        // permanent arrived — the object it is on did not exist a moment earlier — while a
        // leaves-the-battlefield ability triggers on the game as it was *before*, because by the
        // time the event has happened the permanent is gone. Looking only at the state before the
        // event, as this did at first, silently loses every ETB trigger there is.
        // CR 702.140d: an ability that triggers when a creature mutates is on the permanent the
        // merge produced, and that permanent has the abilities of every card representing it
        // (CR 702.140e) — the one that arrived in this very event included. A mutated permanent
        // keeps its id (CR 730.2c), so it is in both states and the loops below would read it
        // only as it was *before*: the card whose trigger the whole cast was for is still a spell
        // on the stack there, and its own mutate trigger would never fire. It is considered once,
        // afterwards, which is the only state that has all of its abilities.
        //
        // Specializing is the same shape and was the same bug. "When this creature specializes"
        // is printed on the specialized version, which is the card the permanent only has once
        // the event has applied — so read as it was *before*, the permanent is still the base
        // card and the trigger the whole ability exists for is on nobody. The version compiled,
        // the ability activated, the definition swapped, and the two Zombies were never created:
        // a card reading perfectly and doing less than it says.
        var remade = e switch
        {
            PermanentMutated merged => merged.Id,
            PermanentSpecialized became => became.Id,
            _ => (ObjectId?)null,
        };

        foreach (var (id, obj) in before.Objects)
        {
            if (id != remade)
                Consider(e, before, id, obj);
        }

        foreach (var (id, obj) in State.Objects)
        {
            if (!before.Objects.ContainsKey(id) || id == remade)
                Consider(e, State, id, obj);
        }
    }

    private void Consider(GameEvent e, GameState state, ObjectId id, GameObject obj)
    {
        var hidden = obj.Permanent is { IsFaceDown: true };
        var source = new TriggerSource(obj, _abilities);

        foreach (var ability in TriggersWatching(state, obj))
        {
            // CR 707.2: a face-down permanent has no abilities, so none of the card's triggers
            // are watching — a morph creature turned face down stops noticing things. Disguise's
            // ward is the exception (CR 702.168a), and it says so on the ability rather than
            // being special-cased here, so the ability is still found on the card when the
            // trigger it produces has to be resolved.
            if (hidden && !ability.FunctionsFaceDown)
                continue;

            if (obj.Zone != ability.FunctionsFrom)
                continue;

            if (!ability.Triggers(e, state, source))
                continue;

            // CR 603.1: a "only once each turn" ability stops watching once it has fired. Kept
            // per (permanent, ability) rather than per card, so two copies of the same creature
            // each get their one.
            if (ability.OncePerTurn && !_triggeredThisTurn.Add((id, ability.Id)))
                continue;

            // CR 603.2c: one declaration can be several occurrences. Asked after the predicate has
            // already said yes and gated on a type test, so nothing here runs on the hot path -
            // this is reached once per trigger that actually fired, on the two events that carry
            // a batch of creatures.
            if (ability.PerDeclaredCreature && e is BlockersDeclared or AttackersDeclared)
            {
                RecordOnePerDeclaredCreature(e, state, id, obj, ability, source);
                continue;
            }

            _triggersFound.Add(new AbilityTriggered(
                id, ability.Id, ability.Text, obj.ControllerId)
            {
                SubjectPlayer = SubjectOf(e, state),

                // CR 603.2: which object the event was about is a question about the event on
                // every trigger but one. A TargetsChosen names two - the spell that targeted and
                // the permanent it targeted - and only the condition knows which of them its own
                // sentence means, so an ability that has been told says so here.
                SubjectObject = ability.SubjectIsSource ? id : SubjectObjectOf(e),
                SubjectAmount = AmountFor(e, state, ability, source),
            });
        }
    }

    /// <summary>
    /// Records one trigger for each creature in a declaration this ability answers to (CR 603.2c).
    /// </summary>
    /// <remarks>
    /// A declaration is a batch, which is why <see cref="SubjectObjectOf"/> answers nothing for a
    /// block and answers an attack only when exactly one creature was declared: with several in it
    /// there is no one object the event was about. This is where that stops being a limitation.
    /// "Whenever this creature becomes blocked by a creature" and "whenever a creature attacks" are
    /// not asking about the declaration - they are asking about one creature in it - so the
    /// declaration is cut into single occurrences and the ability's own predicate is asked about
    /// each one alone. That is the same singleton probe <see cref="AmountFor"/> uses on an attack
    /// batch, and for the same reason: the description lives in the predicate and nowhere else, a
    /// predicate cannot be asked how many, and it can be asked once per candidate.
    /// <para>
    /// Asking the predicate again is what keeps every qualifier the condition prints - the tribe,
    /// the side, the defending player, the power - applied per creature without being restated
    /// here. This method knows how to cut a declaration up and nothing at all about what the cards
    /// say.
    /// </para>
    /// </remarks>
    private void RecordOnePerDeclaredCreature(
        GameEvent e,
        GameState state,
        ObjectId id,
        GameObject obj,
        TriggeredAbilityDefinition ability,
        TriggerSource source)
    {
        foreach (var (occurrence, subject) in OccurrencesIn(e, id))
        {
            if (!ability.Triggers(occurrence, state, source))
                continue;

            _triggersFound.Add(new AbilityTriggered(
                id, ability.Id, ability.Text, obj.ControllerId)
            {
                SubjectPlayer = SubjectOf(occurrence, state),
                SubjectObject = subject,
                SubjectAmount = AmountFor(occurrence, state, ability, source),
            });

            // CR 603.1: an ability that may fire once each turn fires once, however many creatures
            // it was about. The budget was taken before this was called, so stopping here is what
            // spends it on one occurrence rather than on all of them.
            if (ability.OncePerTurn)
                return;
        }
    }

    /// <summary>
    /// One combat declaration cut into single occurrences, each with the creature it names.
    /// </summary>
    /// <remarks>
    /// The only place the two declarations differ, and they differ in what the sentence's subject
    /// is rather than in how the batch comes apart.
    /// <list type="bullet">
    /// <item>A block is a pair, and the subject is the <em>other</em> creature in it. Resolved
    /// against the source rather than taken from the event, so it is right on either side of the
    /// declaration - the attacker's pronoun means its blocker, the blocker's means its attacker -
    /// and it is never the permanent with the ability. A source in neither half answers nothing
    /// rather than guessing: the strict subject then resolves to no object and the effect does
    /// nothing, which is the honest outcome for a pair the ability turned out not to be about.
    /// A declaration naming an attacker with no blockers yields no pairs and therefore no trigger,
    /// which is CR 509.1h - an attacker nobody blocked did not become blocked. That is stricter
    /// than the batch predicate and it is the correct answer, so there is deliberately no
    /// fall-back to a single subject-less trigger when no occurrence matches.</item>
    /// <item>An attack names one creature outright (CR 508.3a), and the subject is that attacker -
    /// which is usually not the permanent with the ability at all: Caltrops and Raking Canopy are
    /// not in the declaration they are asking about. The <see cref="AttackTarget"/> rides along so
    /// that "attacks you" and "attacks a planeswalker" are still answered per attacker.</item>
    /// </list>
    /// </remarks>
    private static IEnumerable<(GameEvent Occurrence, ObjectId? Subject)> OccurrencesIn(
        GameEvent e, ObjectId id) => e switch
        {
            BlockersDeclared declaration => BlockingPairsIn(declaration, id),
            AttackersDeclared declared => LoneAttackersIn(declared),
            _ => [],
        };

    private static IEnumerable<(GameEvent Occurrence, ObjectId? Subject)> BlockingPairsIn(
        BlockersDeclared declaration, ObjectId id)
    {
        foreach (var (attacker, blockers) in declaration.Blockers)
        {
            foreach (var blocker in blockers)
            {
                var pair = new BlockersDeclared(
                    ImmutableDictionary<ObjectId, ImmutableList<ObjectId>>.Empty
                        .Add(attacker, [blocker]));

                yield return (
                    pair,
                    attacker == id ? blocker
                    : blocker == id ? attacker
                    : (ObjectId?)null);
            }
        }
    }

    private static IEnumerable<(GameEvent Occurrence, ObjectId? Subject)> LoneAttackersIn(
        AttackersDeclared declared)
    {
        foreach (var (attacker, aimedAt) in declared.Attackers)
        {
            yield return (
                new AttackersDeclared(
                    ImmutableDictionary<ObjectId, AttackTarget>.Empty.Add(attacker, aimedAt)),
                attacker);
        }
    }

    /// <summary>
    /// How much of a batched event <em>this</em> ability was about, for "that many" (CR 603.2).
    /// </summary>
    /// <remarks>
    /// A declaration of attackers is one event carrying every attacker (CR 508.1), and the
    /// number the card wants is rarely the size of that batch: "whenever one or more Dragons you
    /// control attack, draw that many cards" means the Dragons, and an attack of two Dragons and
    /// a Goblin draws two. Four of the six corpus lines that say "that many" about an attack
    /// name a narrower group than "creatures you control" — Dragons, Dinosaurs, Birds, Treefolk
    /// — so answering with <see cref="AmountOf(GameEvent)"/> alone would print a strictly better
    /// card than the one on the table.
    /// <para>
    /// The description lives in the ability's own predicate and nowhere else, so the count is
    /// taken by asking that predicate about each attacker on its own: a declaration of one is
    /// exactly the question "does this creature answer the description". A predicate cannot be
    /// asked how many; it can be asked once per candidate.
    /// </para>
    /// <para>
    /// A predicate that needs two attackers at once to be true — training's "with another
    /// creature with greater power" — accepts no singleton and would come back with nothing, so
    /// a count of zero falls back to the whole batch rather than reporting that the event this
    /// ability just fired on was about nothing. None of those cards says "that many"; the
    /// fallback is there so that a later one cannot silently do nothing.
    /// </para>
    /// </remarks>
    private static int? AmountFor(
        GameEvent e, GameState state, TriggeredAbilityDefinition ability, TriggerSource source)
    {
        if (e is AttackersDeclared { Attackers.Count: > 1 } batch)
        {
            var mine = batch.Attackers.Count(one => ability.Triggers(
                new AttackersDeclared(
                    ImmutableDictionary<ObjectId, AttackTarget>.Empty.Add(one.Key, one.Value)),
                state,
                source));

            return mine > 0 ? mine : AmountOf(e);
        }

        // The same narrowing for a batch of cards. "Whenever you discard one or more cards,
        // create that many tokens" wants however many went; "whenever you discard one or more
        // land cards" names a narrower group, and paying that trigger for every card discarded
        // would print a strictly better card than the one on the table. Asked the same way,
        // because the description lives in the ability's own predicate and nowhere else: a batch
        // of one is exactly the question "does this card answer the description".
        if (e is CardsDiscarded { Ids.Count: > 1 } discarded)
        {
            return Narrowed(
                discarded.Ids, one => new CardsDiscarded(discarded.PlayerId, [one]));
        }

        if (e is CardsLeftGraveyard { Ids.Count: > 1 } departed)
        {
            return Narrowed(
                departed.Ids, one => new CardsLeftGraveyard(departed.PlayerId, [one]));
        }

        return AmountOf(e);

        int Narrowed(ImmutableList<ObjectId> ids, Func<ObjectId, GameEvent> alone)
        {
            var mine = ids.Count(id => ability.Triggers(alone(id), state, source));
            return mine > 0 ? mine : ids.Count;
        }
    }

    private readonly List<AbilityTriggered> _triggersFound = [];

    /// <summary>
    /// Which player an event was about, for a trigger that says "that player" (CR 603.2).
    /// </summary>
    /// <remarks>
    /// Deliberately a short list. An event that is not about one particular player has no subject
    /// at all, and saying so is the honest answer — an effect scoped to a subject that does not
    /// exist does nothing, where a guess would quietly do something to the wrong person.
    /// </remarks>
    /// <summary>
    /// How much an event was about, for a trigger that says "that many" (CR 603.2).
    /// </summary>
    /// <remarks>
    /// Only the events that carry a number worth referring back to. Anything else has none, and
    /// a "that many" on a trigger watching one of those leaves the card unread rather than
    /// drawing zero — which would look like a card that does nothing rather than one the engine
    /// cannot play.
    /// </remarks>
    /// <summary>How much a triggering event was of, for the sentences that say "that many".</summary>
    public static int? AmountOf(GameEvent e) => e switch
    {
        PlayerDamaged damaged => damaged.Amount,
        DamageMarked marked => marked.Amount,
        LifeChanged life => Math.Abs(life.Delta),
        CountersChanged counters => Math.Abs(counters.Delta),

        // How many creatures were declared. Nothing new is written down for it: attackers are
        // declared as one batch (CR 508.1) and the batch is already the event, so the size the
        // sentence asks for was there all along and only this switch could not see it. Until it
        // could, "whenever one or more creatures you control attack, add that much mana" fired
        // and added nothing, which is why the trigger family was refused rather than shipped.
        // An empty declaration is still a declaration and answers zero, not nothing.
        //
        // It also switches off the running total in RunEffects for every attack trigger, which
        // is the same trade the damage arms above already make: an ability that says how much it
        // was about says so for all of its clauses. That would matter to "whenever ~ attacks,
        // each opponent loses 2 life and you gain that much life" - and no corpus card is shaped
        // that way, checked rather than assumed.
        AttackersDeclared declared => declared.Attackers.Count,

        // How many cards went at once. The batch is already the event, the same way the
        // declaration above is, so "create that many tokens" and "deals that much damage" have a
        // number to read rather than firing and doing nothing - which is the defect this file
        // records against the attack batch, and the reason that family was refused before it.
        CardsDiscarded discarded => discarded.Ids.Count,
        CardsLeftGraveyard departed => departed.Ids.Count,

        // The number that came up. "Whenever you roll a 1 or 2, put that many +1/+1 counters on
        // this creature" and "put a number of charge counters on this artifact equal to the
        // result" are both this event's number asked for by a different name (CR 706.4).
        DiceRolled rolled => rolled.Result,

        _ => null,
    };

    public static Guid? SubjectOf(GameEvent e, GameState state) => e switch
    {
        PlayerDamaged damaged => damaged.PlayerId,
        LifeChanged life => life.PlayerId,

        // A step is somebody's step, and "at the beginning of each player's draw step, that
        // player draws" means the one whose step it is (CR 500.1). Without this the phrase had
        // no subject at all and the draw fell back to whoever controlled the enchantment.
        StepBegan => state.ActivePlayerId,
        SpellCastEvent cast => cast.PlayerId,
        PoisonCountersChanged poisoned => poisoned.PlayerId,

        // "Whenever a player taps a land for mana, ~ deals 1 damage to that player." The mana
        // went into somebody's pool and that somebody is the one who tapped the land, so the
        // event already names the player the sentence is asking about. Without this Manabarbs
        // fired on every land anyone tapped and dealt its damage to nobody at all.
        ManaAdded mana => mana.PlayerId,

        // A move is about the player whose card it was. "Whenever a player draws a card, that
        // player loses 1 life" and "whenever a creature an opponent controls dies, that player
        // loses 2 life" are the same question about two causes, and neither had an answer: the
        // phrase resolved to nobody and the effect emitted nothing, so the card compiled, the
        // trigger fired, and the game was not played.
        //
        // The old id is preferred, and that is the whole of the rule: a permanent's controller
        // is a battlefield fact (CR 109.5), and once it has moved the card is its owner's
        // (CR 108.4) — so the object as it last was is the one that knows whose it was. Both are
        // tried because a zone change carries two ids and only one exists in any given state
        // (CR 400.7); a trigger is offered the state on either side of the move.
        ObjectMoved moved =>
            state.TryGetObject(moved.OldId, out var before) ? before.ControllerId
            : state.TryGetObject(moved.NewId, out var after) ? after.OwnerId
            : null,

        // "Whenever a land enters under an opponent's control, that player loses 2 life." An
        // object that was created rather than moved carries its controller on the event, so
        // there is nothing to look up and nothing to be ambiguous about.
        ObjectCreated made => made.ControllerId,

        // A batch of cards is one player's: their graveyard, their hand. "Whenever one or more
        // players discard one or more cards, that player..." has a subject for the same reason a
        // single move does, and the batch already carries it rather than needing a lookup.
        CardsDiscarded discarded => discarded.PlayerId,
        CardsLeftGraveyard departed => departed.PlayerId,



        // The player who chose the targets is the one who controls what did the targeting, which
        // is who a ward trigger asks to pay.
        TargetsChosen aimed => state.TryGetObject(aimed.StackId, out var aiming)
            ? aiming.ControllerId
            : null,

        _ => null,
    };

    /// <summary>Which object an event was about, for a trigger that says "it" (CR 603.2).</summary>
    /// <summary>
    /// What a triggering event was <em>about</em>, for the sentences that say "it" (CR 603.1).
    /// </summary>
    /// <remarks>
    /// Only <see cref="TargetsChosen"/> answered this, so every other trigger reported nothing
    /// and "it" fell through to meaning the permanent with the ability. "Whenever a creature you
    /// control enters, put a +1/+1 counter on it" put the counter on the card that said it,
    /// every time, and nothing failed.
    /// <para>
    /// Only events with one unambiguous subject are listed. A declaration of blockers is a batch
    /// of pairs and has no single subject, so it stays absent rather than being answered with
    /// whichever one came first.
    /// </para>
    /// </remarks>
    public static ObjectId? SubjectObjectOf(GameEvent e) => e switch
    {
        TargetsChosen aimed => aimed.StackId,

        // The new id, not the old one: a zone change makes a new object (CR 400.7) and the new
        // one is what exists once the trigger resolves.
        ObjectMoved moved => moved.NewId,
        ObjectCreated made => made.Id,

        // CR 730.2c: the permanent that mutated is the same object it already was, so the id the
        // event carries is the one that still exists when the trigger resolves - which is what
        // makes "put a +1/+1 counter on it" and "put a +1/+1 counter on that creature" the same
        // question here.
        PermanentMutated mutated => mutated.Id,
        PermanentTapped tapped => tapped.Id,
        CountersChanged counted => counted.Id,

        // "Whenever a player taps a land for mana, that player adds one mana of any type
        // that land produced." The land is what the sentence is about, and the event already
        // says which permanent made the mana - so "that land" has an answer rather than
        // falling back to the permanent with the ability, which is a different card.
        ManaAdded mana => mana.SourceId,
        SpellCastEvent cast => cast.StackId,

        PlayerDamaged hit => hit.SourceId,

        // The creature that was damaged. Left out for a long time on the grounds that damage from
        // one creature to another has two objects in it and "it" cannot choose between them - but
        // the corpus settles it: every card of this shape says "that creature" and means the one
        // that was damaged, and names the source outright when it means the source. Where the
        // damaged permanent *is* the source ("whenever ~ is dealt damage") this answers the same
        // id the fallback used to, so nothing that worked before changes.
        DamageMarked marked => marked.Id,

        // "Whenever a creature you control deals combat damage to a player, put that many
        // +1/+1 counters on it." The other participant is a player rather than an object, so
        // "it" can only be the creature that dealt the damage - there is nothing else it could
        // mean. DamageMarked is deliberately absent for the opposite reason: damage from one
        // creature to another has two objects in it, and "it" picks between them by words this
        // does not see.


        // A declaration and a damage step are batches. With one creature in them "it" is that
        // creature; with several, CR 603.2 wants the ability to trigger once per creature, and
        // this engine fires a batch trigger once - so rather than pick one and be wrong about
        // the rest, it answers nothing and the sentence falls back to the permanent with the
        // ability. That is a known limitation and not a reading of the card.
        AttackersDeclared { Attackers: { Count: 1 } only } => only.Keys.First(),
        CombatDamageDealt { Dealers: [var alone] } => alone,

        _ => null,
    };

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
    private IEnumerable<(string Id, GameObject Source, Func<GameEvent, GameState, GameObject, IReadOnlyList<GameEvent>> Replace, bool Optional, Func<GameEvent, GameState, GameObject, IReadOnlyList<GameEvent>>? Decline)> Replacements(
        GameEvent e, HashSet<(ObjectId, string)> applied)
    {
        if (e is EventReplaced)
            yield break;

        // CR 614.1b: a redirection replaces the damage event with one aimed somewhere else. It
        // is asked before the shield below, because a redirected point never reaches this
        // permanent at all and so was never the shield's to soak.
        if (e is DamageMarked aimed
            && State.TryGetObject(aimed.Id, out var redirecting)
            && redirecting.Permanent is { DamageToRedirect: > 0, RedirectDamageTo: { } elsewhere }
            && State.TryGetObject(elsewhere, out _)
            && !applied.Contains((aimed.Id, "redirect")))
        {
            var moved = Math.Min(redirecting.Permanent.DamageToRedirect, aimed.Amount);
            var stays = aimed.Amount - moved;

            yield return ("redirect", redirecting, (_, _, _) => stays > 0
                ?
                [
                    new RedirectionChanged(aimed.Id, -moved, null),
                    aimed with { Id = elsewhere, Amount = moved },
                    aimed with { Amount = stays },
                ]
                :
                [
                    new RedirectionChanged(aimed.Id, -moved, null),
                    aimed with { Id = elsewhere, Amount = moved },
                ], false, null);
        }

        // CR 615.1: a prevention shield soaks up damage rather than replacing the whole event —
        // three damage into a two-point shield still marks one. That is why it is an amount, and
        // why the replacement re-emits the remainder instead of dropping it.
        if (e is DamageMarked prevented
            && State.TryGetObject(prevented.Id, out var shielded)
            && shielded.Permanent is { DamageToPrevent: > 0 } armour
            && !IsUnpreventable(prevented.IsCombat, prevented.SourceId, shielded, null)
            && !applied.Contains((prevented.Id, "prevent")))
        {
            var soaked = Math.Min(armour.DamageToPrevent, prevented.Amount);
            var remaining = prevented.Amount - soaked;

            yield return ("prevent", shielded, (_, _, _) => remaining > 0
                ?
                [
                    new PreventionChanged(prevented.Id, -soaked),
                    prevented with { Amount = remaining },
                ]
                : [new PreventionChanged(prevented.Id, -soaked)], false, null);
        }

        // The same soak, for the other kind of victim. Damage to a player is its own event and
        // never passed through the shield above, which only ever looked at objects.
        if (e is PlayerDamaged struck
            && State.Players.TryGetValue(struck.PlayerId, out var target)
            && target.DamageToPrevent > 0
            && !IsUnpreventable(struck.IsCombat, struck.SourceId, null, struck.PlayerId)
            && !applied.Contains((new ObjectId(struck.PlayerId), "prevent-player")))
        {
            var soaked = Math.Min(target.DamageToPrevent, struck.Amount);
            var remaining = struck.Amount - soaked;

            // The shield is on the player, not on any object, so the replacement has to name a
            // source object for the bookkeeping the caller does. The damaged player's own
            // permanents are nothing to do with it; the source of the damage is what is at hand.
            if (State.TryGetObject(struck.SourceId, out var dealer))
            {
                yield return ("prevent-player", dealer, (_, _, _) => remaining > 0
                    ?
                    [
                        new PlayerPreventionChanged(struck.PlayerId, -soaked),
                        struck with { Amount = remaining },
                    ]
                    : [new PlayerPreventionChanged(struck.PlayerId, -soaked)], false, null);
            }
        }

        // CR 615.1: a described prevention effect watches the same two events the shields above
        // do and takes some or all of the damage away. CR 615.10's number caps each damage event
        // and applies again to the next one, which is the whole difference between "prevent 1 of
        // that damage" and "prevent the next 1 damage" — so a described shield normally writes no
        // state back and the replacement just emits the remainder.
        //
        // CR 615.8's shield is the exception and spends itself: "the next time a source of your
        // choice would deal damage" stops one instance however large, and every later instance
        // from that same source is dealt normally. That end has to be an event, because the state
        // is a fold of the log and nothing else in the batch says the Circle has been used.
        if (e is DamageMarked hit
            && !State.Preventions.IsEmpty
            && State.TryGetObject(hit.Id, out var damaged)
            && !IsUnpreventable(hit.IsCombat, hit.SourceId, damaged, null))
        {
            foreach (var effect in State.Preventions)
            {
                var key = PreventionKey + effect.Id.ToString("N");
                if (!PreventionWatches(effect, hit.IsCombat, hit.SourceId)
                    || !PreventionCovers(effect, damaged)
                    || applied.Contains((damaged.Id, key)))
                {
                    continue;
                }

                var left = hit.Amount - Math.Min(effect.Amount ?? hit.Amount, hit.Amount);
                var spent = SpendingOf(effect, hit.Amount, left);

                yield return (
                    key,
                    damaged,
                    (_, _, _) => left > 0 ? [.. spent, hit with { Amount = left }] : spent,
                    false,
                    null);
            }
        }

        // The same effects, for the other kind of victim. Damage to a player is its own event and
        // shares none of the machinery above.
        if (e is PlayerDamaged hitPlayerBySource
            && !State.Preventions.IsEmpty
            && State.TryGetObject(hitPlayerBySource.SourceId, out var dealing)
            && !IsUnpreventable(
                hitPlayerBySource.IsCombat,
                hitPlayerBySource.SourceId,
                null,
                hitPlayerBySource.PlayerId))
        {
            foreach (var effect in State.Preventions)
            {
                var key = PreventionKey + effect.Id.ToString("N");
                if (!PreventionWatches(
                        effect, hitPlayerBySource.IsCombat, hitPlayerBySource.SourceId)
                    || !PreventionCoversPlayer(effect, hitPlayerBySource.PlayerId)
                    || applied.Contains((dealing.Id, key)))
                {
                    continue;
                }

                var left = hitPlayerBySource.Amount
                    - Math.Min(effect.Amount ?? hitPlayerBySource.Amount, hitPlayerBySource.Amount);
                var spent = SpendingOf(effect, hitPlayerBySource.Amount, left);

                yield return (
                    key,
                    dealing,
                    (_, _, _) => left > 0
                        ? [.. spent, hitPlayerBySource with { Amount = left }]
                        : spent,
                    false,
                    null);
            }
        }

        // CR 702.90b: a source with infect deals its damage as counters instead — poison on a
        // player, -1/-1 on a creature. It is a replacement of the damage event, which is why a
        // creature dealt infect damage is not "damaged" at all and cannot be healed by removing
        // damage.
        if (e is PlayerDamaged hitPlayer
            && HasInfect(hitPlayer.SourceId)
            && !applied.Contains((hitPlayer.SourceId, "infect")))
        {
            if (State.TryGetObject(hitPlayer.SourceId, out var poisoner))
            {
                yield return ("infect", poisoner, (_, _, _) =>
                    [new PoisonCountersChanged(hitPlayer.PlayerId, hitPlayer.Amount)], false, null);
            }
        }

        // CR 702.80a: wither is infect for creatures only — the damage becomes -1/-1 counters,
        // and a player hit by it loses life normally.
        if (e is DamageMarked hitCreature
            && (HasInfect(hitCreature.SourceId) || HasKeyword(hitCreature.SourceId, KeywordAbility.Wither))
            && !applied.Contains((hitCreature.SourceId, "infect")))
        {
            if (State.TryGetObject(hitCreature.SourceId, out var wither))
            {
                yield return ("infect", wither, (_, _, _) =>
                    [new CountersChanged(
                        hitCreature.Id, CounterKinds.MinusOneMinusOne, hitCreature.Amount)], false, null);
            }
        }

        // CR 122.1d: a shield counter replaces the next damage the permanent would be dealt,
        // spending itself. A rules replacement that lives on the permanent rather than on a card,
        // like regeneration below, so it is offered here rather than through ReplacementsOf.
        //
        // Offered after infect and wither, which replace the same event with counters: a creature
        // dealt infect damage is not dealt damage at all (CR 702.90b), so there is nothing left
        // for a shield to stop and spending one would cost the permanent its protection for free.
        if (e is DamageMarked shieldedHit
            && !applied.Contains((shieldedHit.Id, ShieldCounterKey))
            && State.TryGetObject(shieldedHit.Id, out var guarded)
            && guarded.Permanent?.Counters.GetValueOrDefault(CounterKinds.Shield) > 0)
        {
            yield return (ShieldCounterKey, guarded, (_, _, source) =>
                [new CountersChanged(source.Id, CounterKinds.Shield, -1)], false, null);
        }

        // The other half of the same rule. "Destroy" and "destroy, it can't be regenerated" are
        // both destruction, and a shield counter is not regeneration - reading only the first
        // would leave the commonest removal in the game going through the shield.
        if (e is ObjectMoved
            {
                To: Zone.Graveyard,
                Cause: MoveCause.Destroy or MoveCause.DestroyNoRegeneration,
            } razed
            && !applied.Contains((razed.OldId, ShieldCounterKey))
            && State.TryGetObject(razed.OldId, out var warded)
            && warded.Permanent?.Counters.GetValueOrDefault(CounterKinds.Shield) > 0)
        {
            yield return (ShieldCounterKey, warded, (_, _, source) =>
                [new CountersChanged(source.Id, CounterKinds.Shield, -1)], false, null);
        }

        // CR 614.1c: "if it would die this turn, exile it instead". A floating effect rather
        // than an ability on a card, because the spell that made it has long since resolved — and
        // it replaces the move outright rather than adding to it, which is what stops the
        // creature ever reaching a graveyard for anything to notice.
        if (e is ObjectMoved { From: Zone.Battlefield, To: Zone.Graveyard } leaving
            && !applied.Contains((leaving.OldId, ExileInsteadOfDying.FloatingId))
            && State.FloatingEffects.Any(
                f => string.Equals(
                        f.DefinitionId, ExileInsteadOfDying.FloatingId, StringComparison.Ordinal)
                    && f.AffectedIds.Contains(leaving.OldId))
            && State.TryGetObject(leaving.OldId, out var doomedToExile))
        {
            yield return (ExileInsteadOfDying.FloatingId, doomedToExile, (_, _, _) =>
            [
                leaving with { To = Zone.Exile, Cause = MoveCause.Exile },
            ], false, null);
        }

        // CR 702.35a: a discarded card with madness goes to exile instead of the graveyard, and
        // its owner is offered a cast for the madness cost. Like regeneration this is offered here
        // rather than through ReplacementsOf, because it is about the card being moved rather than
        // about a permanent watching the game.
        if (e is ObjectMoved { From: Zone.Hand, To: Zone.Graveyard, Cause: MoveCause.Discard } mad
            && !applied.Contains((mad.OldId, "madness"))
            && State.TryGetObject(mad.OldId, out var maddened)
            && _abilities.SpellOf(maddened.Card)?.MadnessCost is { } madnessCost)
        {
            yield return ("madness", maddened, (_, _, _) =>
            [
                mad with { To = Zone.Exile, Cause = MoveCause.Exile },
                new FreeCastOffered(mad.NewId, maddened.OwnerId, madnessCost),
            ], false, null);
        }

        // Regeneration is a replacement that lives on the permanent rather than on a card, so
        // it is offered here rather than through ReplacementsOf (CR 701.19b). It replaces the
        // destruction with tapping, clearing damage and removing it from combat.
        if (e is ObjectMoved { To: Zone.Graveyard, Cause: MoveCause.Destroy } dying
            && State.TryGetObject(dying.OldId, out var doomed)
            && doomed.Permanent is { RegenerationShields: > 0 }
            && !applied.Contains((dying.OldId, "regenerate")))
        {
            yield return ("regenerate", doomed, (_, _, source) =>
            [
                new RegenerationShieldsChanged(source.Id, -1),
                new PermanentTapped(source.Id),

                // This permanent's damage, not the whole board's (CR 701.19c). The global event
                // belongs to the cleanup step; using it here saved every damaged creature in
                // play alongside the one that was actually regenerated.
                new DamageRemoved(source.Id),

                // The third of the three things regeneration does (CR 701.19c), and the one the
                // comment above claimed for months without emitting: a regenerated blocker stops
                // blocking, so the attacker it was holding up becomes unblocked for the rest of
                // the combat rather than dealing its damage to a creature that is no longer
                // there. Left implicit, a creature regenerated by first-strike damage stayed in
                // the combat lists and absorbed the regular-damage step as well.
                new RemovedFromCombat(source.Id),
            ], false, null);
        }

        // Every object, not only the battlefield: "as this enters" functions from the stack
        // while the card is still a spell (CR 614.6, 614.1c), and that is the commonest
        // replacement effect there is. FunctionsFrom is what decides, so it has to be asked.
        foreach (var (id, source) in State.Objects)
        {
            // CR 613.1f: a permanent that has lost all abilities offers no replacement effects
            // either. Asked only of the battlefield, because that is the only zone the layers are
            // computed for - a card on the stack still has everything its card says.
            if (source.Zone == Zone.Battlefield
                && Characteristics.Of(State, _abilities, source).HasLostAllAbilities)
            {
                continue;
            }

            // CR 707.5: a permanent that entered as a copy has the copied card's replacement
            // effects, and they take effect as it arrives — the rule's own example is a Clone of
            // Skyshroud Behemoth entering tapped with two fade counters. Those are on the copied
            // card and nowhere near this object's own, so the card is the computed one.
            foreach (var effect in _abilities.ReplacementsOf(ReplacementCardOf(e, source)))
            {
                if (effect.FunctionsFrom is { } zone && source.Zone != zone)
                    continue;

                if (!effect.Applies(e, State, _abilities, source))
                    continue;

                // CR 615.12, for the half of the prevention family that lives out here. A
                // shield printed as a permanent's static ability is a replacement effect, so it
                // arrives through this loop rather than through the arms above — and the ban has
                // to reach both or a card would be stopped by one kind of fog and not the other.
                if (effect.IsPrevention && IsBannedFrom(e))
                    continue;

                foreach (var candidate in Branches(e, id, effect, source, applied))
                    yield return candidate;
            }
        }

        // A token is created on the battlefield and was never anywhere else (CR 111.1), so it is
        // not among the objects above when its own arrival is being replaced — and its own
        // replacements are exactly the ones that decide how it arrives. "Enters with two +1/+1
        // counters" on a token copy found nothing to ask and the token arrived without them.
        //
        // The event carries the whole card definition, which is all a replacement needs; the
        // object is built here rather than folded in early, because folding it in early would
        // make the arrival happen before the effects that replace it have run.
        if (e is not ObjectCreated made || State.Objects.ContainsKey(made.Id))
            yield break;

        var arriving = new GameObject
        {
            Id = made.Id,
            Card = made.Card,
            OwnerId = made.OwnerId,
            ControllerId = made.ControllerId,
            Zone = made.Zone,
            Timestamp = State.NextTimestamp,
        };

        // The computed card for the same reason the battlefield's is: a token created as a copy
        // carries the copied card's own "enters tapped" and "enters with counters" (CR 707.5),
        // and the copy effect naming this id has already been emitted by the time the arrival is
        // re-examined.
        foreach (var effect in _abilities.ReplacementsOf(ReplacementCardOf(e, arriving)))
        {
            if (effect.FunctionsFrom is { } zone && made.Zone != zone)
                continue;

            if (!effect.Applies(e, State, _abilities, arriving))
                continue;

            foreach (var candidate in Branches(e, made.Id, effect, arriving, applied))
                yield return candidate;
        }
    }

    /// <summary>
    /// Which card an object's replacement effects are read from (CR 707.5).
    /// </summary>
    /// <remarks>
    /// The permanent's own card until something has made it a copy, and the copy's after that —
    /// a Clone of Skyshroud Behemoth enters tapped with two fade counters, and both of those are
    /// replacement effects printed on the card it copied.
    /// <para>
    /// The id asked about is the one the object is <em>about to</em> have, not the one it has.
    /// A permanent spell is still on the stack while its own arrival is being replaced, and the
    /// copy effect its replacement created names the permanent it is becoming rather than the
    /// spell (CR 400.7). Asking about the spell's id found no copy effect at all, so a Clone
    /// cast from a hand arrived untapped and empty while one put onto the battlefield directly
    /// arrived correctly — which is exactly the kind of half-working nobody notices.
    /// </para>
    /// <para>
    /// Face-down permanents keep their own card here. Their characteristics come from the rules
    /// (CR 708.2a), but morph's own replacement is on the card underneath and reading the rules'
    /// empty stand-in would take it away.
    /// </para>
    /// </remarks>
    private CardDefinition ReplacementCardOf(GameEvent e, GameObject source) =>
        source.Permanent is { IsFaceDown: true }
            ? source.Card
            : Characteristics.CopiedAs(
                State,
                _abilities,
                Cards.CardCompiler.Arriving(e, source) ?? source.Id,
                source.Card);

    /// <summary>
    /// One candidate per way a replacement effect can be applied (CR 616.1).
    /// </summary>
    /// <remarks>
    /// Almost every replacement is one candidate: it applies, and what it does is settled. A
    /// copy effect is the exception the rules already have a question for — "you may have this
    /// creature enter as a copy of <em>any creature on the battlefield</em>" is a choice made in
    /// the middle of applying an event, which is the one place this engine could not ask
    /// anything. Expressing each creature as its own candidate hands it to the machinery that
    /// already asks which of several replacements to apply, already halts the whole game to do
    /// it, and already replays — instead of a mid-effect continuation, which a log cannot fold.
    /// <para>
    /// Each branch needs its own id, because that id is both the option the player picks and the
    /// key that stops CR 614.5 applying the same effect twice. The branch's label is what makes
    /// it distinguishable to the person answering, so it names the permanent and who controls
    /// it — a board with two Grizzly Bears on it must not offer two identical buttons.
    /// </para>
    /// </remarks>
    private IEnumerable<(string Id, GameObject Source, Func<GameEvent, GameState, GameObject, IReadOnlyList<GameEvent>> Replace, bool Optional, Func<GameEvent, GameState, GameObject, IReadOnlyList<GameEvent>>? Decline)> Branches(
        GameEvent e,
        ObjectId id,
        ReplacementEffectDefinition effect,
        GameObject source,
        HashSet<(ObjectId, string)> applied)
    {
        if (effect.Branches is not { } branching)
        {
            if (!applied.Contains((id, effect.Id)))
                yield return (effect.Id, source, effect.Replace, effect.IsOptional, effect.Decline);

            yield break;
        }

        foreach (var branch in branching(e, State, _abilities, source))
        {
            var branchId = effect.Id + ": " + branch.Label;
            if (applied.Contains((id, branchId)))
                continue;

            yield return (branchId, source, branch.Replace, effect.IsOptional, effect.Decline);
        }
    }



    private void BeginTurn()
    {
        _activationsThisTurn.Clear();
        _loyaltyUsedThisTurn.Clear();
        _triggeredThisTurn.Clear();

        // CR 500.7: an extra turn is taken before the turn order resumes, and by whoever the
        // effect named - which need not be the player whose turn just ended. The order does not
        // otherwise move on, so the player after them is still the one after whoever last took a
        // turn in sequence.
        var waiting = State.HasBegun && !State.ExtraTurns.IsEmpty
            ? State.ExtraTurns[0]
            : (Guid?)null;

        if (waiting is { } extra)
            Emit(new ExtraTurnTaken(extra));

        var next = waiting
            ?? (State.HasBegun
                ? State.NextInTurnOrderAfter(State.ActivePlayerId)
                : State.ActivePlayerId);

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

        // CR 510.4: first strike gives the phase a second combat damage step. It is the same
        // step again, not a new one in the enum.
        if (State.CurrentStep == TurnStep.CombatDamage
            && State.Combat.DamageStepsDone == 1
            && CombatRules.NeedsFirstStrikeStep(State, _abilities))
        {
            EnterStep(TurnStep.CombatDamage);
            return;
        }

        var next = State.CurrentStep.Next();
        if (next is null)
        {
            BeginTurn();
            return;
        }

        // CR 506.1: the declare blockers and combat damage steps are skipped if no creatures
        // were declared as attackers.
        if (next is TurnStep.DeclareBlockers or TurnStep.CombatDamage
            && !State.Combat.AnyAttackers)
        {
            EnterStep(TurnStep.EndOfCombat);
            return;
        }

        // CR 511.3: combat ends and everything leaves it when the phase does.
        if (next == TurnStep.PostcombatMain && State.Combat.AttackersDeclared)
            Emit(new CombatEnded());

        EnterStep(next.Value);
    }

    /// <summary>
    /// Fires any delayed triggered abilities waiting for this step (CR 603.7).
    /// </summary>
    /// <remarks>
    /// The word-based delays are done immediately rather than put on the stack, which is a
    /// simplification and is recorded as one: a delayed trigger does use the stack, and an
    /// opponent could respond to it. Everything that vocabulary can be asked to do is a zone
    /// change or a counter, and nothing in the game can profitably respond to that.
    /// <para>
    /// A delayed ability that does anything else has a route now, and the note that used to sit
    /// here asked for it: <see cref="Abilities.DelayedActions.Ability"/> names an ability on the
    /// delayed trigger's own card, and <see cref="PutDelayedAbilityOnStack"/> puts that ability
    /// on the stack rather than performing anything. A pact is why it had to exist — its payment
    /// is asked after it resolves, and mana empties as a step ends (CR 500.4), so a payment
    /// demanded at the moment the upkeep began is one no player could ever make.
    /// </para>
    /// <para>
    /// <c>TurnCreated</c> is recorded on the ability but not read here: the ordering makes it
    /// unnecessary. It stays because a delayed ability with a stated duration — "until end of
    /// turn" (CR 603.7b) — will need it, and because a log that does not carry when an ability
    /// was created cannot be asked that question later.
    /// </para>
    /// </remarks>
    private void FireDelayedTriggers(TurnStep step)
    {
        // Everything waiting when a step is *entered* was created before it began, because this
        // runs at the moment of entry and an effect resolving inside the step comes later. So
        // "the next one" needs no arithmetic: an ability created during an end step is simply not
        // in this list yet, and waits for the following turn's.
        var due = State.Delayed.Where(d => d.Step == step && IsDueNow(d)).ToList();

        foreach (var delayed in due)
        {
            // An ability rather than an instruction: it goes on the stack and is answered there,
            // which is the difference between a payment a player can make and one nobody can.
            // Above the fired event because putting it on the stack is what firing it *is* -
            // there is nothing left for the arms below to do afterwards.
            if (delayed.EffectId.StartsWith(DelayedActions.Ability, StringComparison.Ordinal))
            {
                PutDelayedAbilityOnStack(delayed);
                continue;
            }

            Emit(new DelayedTriggerFired(delayed.Id));

            // A delayed ability that does something to its controller rather than to a permanent
            // does not care what became of the permanent. "Draw a card at the beginning of the
            // next turn's upkeep" is owed whether or not the thing that promised it survived —
            // unlike the delayed sacrifice below, which is entirely about a permanent.
            if (string.Equals(delayed.EffectId, "draw", StringComparison.Ordinal))
            {
                Draw(delayed.ControllerId);
                continue;
            }

            // Rebound. The card is not where it was when this was created — it went to exile at
            // the end of the resolution that made this — so the id is followed forward the same
            // way a deferred branch follows its source (CR 400.7).
            if (string.Equals(delayed.EffectId, "rebound", StringComparison.Ordinal))
            {
                var landed = _resolvedSources.TryGetValue(delayed.SubjectId, out var moved)
                    ? moved
                    : delayed.SubjectId;

                if (State.TryGetObject(landed, out var waiting) && waiting.Zone == Zone.Exile)
                    Emit(new FreeCastOffered(landed, delayed.ControllerId));

                continue;
            }

            // The slow flicker's other half. Checked before the battlefield guard below, because
            // this one is waiting on a card in *exile* - the guard exists for the delays that act
            // on a permanent, and would throw this one away every time.
            if (string.Equals(delayed.EffectId, "return-to-battlefield", StringComparison.Ordinal))
            {
                if (State.TryGetObject(delayed.SubjectId, out var away) && away.Zone == Zone.Exile)
                    Move(delayed.SubjectId, Zone.Battlefield, MoveCause.Return, delayed.ControllerId);

                continue;
            }

            // CR 603.7c: if the object has left the zone it was expected to be in, the ability
            // does nothing at all — including to the new object that shares its name.
            if (!State.TryGetObject(delayed.SubjectId, out var subject)
                || subject.Zone != Zone.Battlefield)
            {
                continue;
            }

            // "Remove a +1/+1 counter from it at end of combat" (CR 122.1). Read here, above the
            // zone table below, because that table's default arm is a sacrifice: an id it does
            // not recognise destroys the permanent, so a counter change reaching it would be the
            // harshest possible misreading of the gentlest possible line.
            if (delayed.EffectId.StartsWith(DelaySourceCounters.Prefix, StringComparison.Ordinal))
            {
                var written = delayed.EffectId[DelaySourceCounters.Prefix.Length..];
                var split = written.LastIndexOf(':');

                if (split > 0
                    && int.TryParse(
                        written[(split + 1)..],
                        System.Globalization.NumberStyles.AllowLeadingSign,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out var delta))
                {
                    Emit(new CountersChanged(delayed.SubjectId, written[..split], delta));
                }

                continue;
            }

            // "Destroy it at end of combat" (CR 701.7a). Its own arm above the zone table
            // below, and not a fourth word in it, because destroying is not moving: a permanent
            // with indestructible is not destroyed at all (CR 702.12b) and a regeneration shield
            // replaces the destruction with a tap (CR 701.19b). The table's Move honours neither,
            // so a destroy routed through it would be the sacrifice this word exists to not be
            // (CR 701.21a).
            //
            // The shield is not checked here: Move emits an ObjectMoved with MoveCause.Destroy,
            // and regeneration is already a replacement watching for exactly that event. Only
            // indestructible has to be asked, because nothing replaces a destruction that never
            // happens.
            if (string.Equals(delayed.EffectId, DelayedActions.Destroy, StringComparison.Ordinal))
            {
                if (!Characteristics.HasKeyword(
                        State, _abilities, subject, KeywordAbility.Indestructible))
                {
                    Move(
                        delayed.SubjectId,
                        Zone.Graveyard,
                        MoveCause.Destroy,
                        delayed.ControllerId);
                }

                continue;
            }

            // Warp's exile is not an ordinary one: the card has to remember it left this way,
            // because that is what lets its owner cast it once the turn has ended.
            if (string.Equals(delayed.EffectId, "warp-exile", StringComparison.Ordinal))
            {
                var gone = Move(delayed.SubjectId, Zone.Exile, MoveCause.Exile, delayed.ControllerId);
                if (gone is { } away)
                    Emit(new CardWarpedToExile(away, State.TurnNumber));

                continue;
            }

            var (where, why) = delayed.EffectId switch
            {
                DelayedActions.Exile => (Zone.Exile, MoveCause.Exile),
                DelayedActions.ReturnToHand => (Zone.Hand, MoveCause.Return),
                _ => (Zone.Graveyard, MoveCause.Sacrifice),
            };

            Move(delayed.SubjectId, where, why, delayed.ControllerId);
        }
    }

    /// <summary>
    /// Whether a delayed ability waiting for this step is waiting for <em>this</em> one (CR 603.7).
    /// </summary>
    /// <remarks>
    /// The word-based delays name a step and mean the next one in the game — "sacrifice it at the
    /// beginning of the next end step" is whichever end step comes first, whoever's turn it is.
    /// An ability-bodied delay is printed only in the other form: every card that prints one says
    /// "at the beginning of <em>your</em> next upkeep", and a pact that came due on the opponent's
    /// upkeep would demand payment a turn early and kill its caster for it.
    /// <para>
    /// Which turn it is answers "next" completely, and no arithmetic is needed for the "next"
    /// part: an ability created inside a step is not in this list when that step is entered, so
    /// a pact cast during its caster's own upkeep waits for the following one.
    /// </para>
    /// </remarks>
    private bool IsDueNow(DelayedTrigger delayed) =>
        !delayed.EffectId.StartsWith(DelayedActions.Ability, StringComparison.Ordinal)
        || State.ActivePlayerId == delayed.ControllerId;

    /// <summary>
    /// Puts the ability a delayed trigger names onto the stack (CR 603.7, 603.3).
    /// </summary>
    /// <remarks>
    /// The body is read back off the card the delay was created by, through the same lookup that
    /// resolves every other triggered ability — so the ability is an ordinary object on the stack
    /// from the moment it arrives: an opponent may respond to it, and its controller may tap
    /// lands before it resolves.
    /// <para>
    /// CR 603.7c's counterpart for a card rather than a permanent: if nothing can be found behind
    /// the id any more, the ability does nothing. That is a real position — a pact whose card was
    /// exiled from the graveyard — and it is the wrong answer, because a pact is owed whatever
    /// became of the card that promised it. It is left this way rather than guessed at: the fix
    /// is for the delayed trigger to carry the ability's text and cost itself, which is a change
    /// to what a stored delayed ability is and wants its own pass.
    /// </para>
    /// </remarks>
    private void PutDelayedAbilityOnStack(DelayedTrigger delayed)
    {
        var abilityId = delayed.EffectId[DelayedActions.Ability.Length..];

        if (CardBehind(delayed.SubjectId) is not { } card)
            return;

        var ability = _abilities.TriggersOf(card)
            .FirstOrDefault(t => string.Equals(t.Id, abilityId, StringComparison.Ordinal));

        if (ability is null)
            return;

        Emit(new DelayedTriggerFired(delayed.Id));

        Emit(new TriggerPutOnStack(
            ObjectId.New(),
            delayed.SubjectId,
            card,
            abilityId,
            ability.Text,
            delayed.ControllerId));
    }

    private void EnterStep(TurnStep step)
    {
        if (State.IsOver)
            return;

        // CR 117.3a: the active player receives priority at the beginning of a step, so that is
        // who a question raised on the way in hands it back to.
        _priorityRecipient = State.ActivePlayerId;

        // "Until end of combat" runs out on the way into the postcombat main phase, before the
        // emptying below rather than after it - otherwise the mana would survive one step too
        // many, which is the whole of what the duration is for.
        if (step == TurnStep.PostcombatMain)
        {
            foreach (var id in State.TurnOrder)
            {
                if (State.GetPlayer(id).PersistentManaUntil == ManaPersistence.EndOfCombat)
                    Emit(new ManaPersistenceEnded(id));
            }
        }

        // CR 500.5: unspent mana empties as a step or phase ends. Emitted on entering the next
        // one, which is the same moment and the only one the engine has a hook for.
        if (State.TurnOrder.Any(id => !State.GetPlayer(id).ManaPool.IsEmpty))
            Emit(new ManaPoolsEmptied());

        Emit(new StepBegan(step));

        FireDelayedTriggers(step);

        switch (step)
        {
            case TurnStep.Untap:
                // CR 703.4a: phasing happens immediately after the untap step begins, before
                // anything untaps - so a permanent that phases in this turn is untapped by the
                // step it arrived in, and one that phases out is not.
                PhaseInAndOut();
                TurnTheSky();

                // CR 611.2b: "until your next turn" runs out as that player's next turn begins,
                // which is here. Compared against the active player rather than a stored turn
                // number, because an extra turn moves the number and not the player.
                foreach (var lapsed in State.FloatingEffects
                    .Where(f => f.UntilTurnOf == State.ActivePlayerId)
                    .ToList())
                {
                    Emit(new ContinuousEffectEnded(lapsed.Id));
                }

                Untap();
                // CR 500.3: a step in which no player receives priority ends once its actions
                // are done.
                AdvanceStep();
                return;

            case TurnStep.Draw:
                DrawForTurn();
                break;

            case TurnStep.PrecombatMain:
                // CR 714.3c: as a player's precombat main phase begins, that player puts a lore
                // counter on each Saga they control. A turn-based action, so it happens before
                // anyone has priority and does not use the stack - the chapter ability it sets
                // off does.
                AdvanceSagas();

                // CR 505.5, 717.4: third, if the active player controls one or more Attractions
                // and it is their precombat main phase, they roll to visit them. A turn-based
                // action beside the Saga's lore counter, and like it, it does not use the stack -
                // the visit abilities it sets off do.
                RollToVisitAttractions();
                break;

            case TurnStep.DeclareAttackers:
                // CR 508.1: declaring attackers is a turn-based action that happens before
                // anyone gets priority, so the game waits here for the active player's
                // declaration rather than granting priority first.
                SettleBeforePriority();
                return;

            case TurnStep.DeclareBlockers:
                SettleBeforePriority();
                return;

            case TurnStep.CombatDamage:
                DealCombatDamage();
                break;

            case TurnStep.EndOfCombat:
                // CR 511.3: everything is removed from combat as the step ends. Doing it as the
                // step begins would be wrong for "at end of combat" triggers, but nothing in the
                // engine reads combat after this point, and the phase is over either way.
                break;

            case TurnStep.End:
                // CR 725.2, the first of the monarch's two inherent abilities: "at the beginning
                // of the monarch's end step, that player draws a card."
                //
                // **A simplification, stated rather than hidden.** The rules make both of these
                // triggered abilities with no source, which means they use the stack and can be
                // responded to. This engine's pending triggers are keyed to the permanent that
                // produced them, so a sourceless one has nothing to look its ability up by; both
                // are done here instead. What that costs is the window between the trigger and
                // the draw - nothing else about the mechanic differs.
                if (State.MonarchId == State.ActivePlayerId)
                    Draw(State.ActivePlayerId);

                break;

            case TurnStep.Upkeep:
                // CR 702.62a: the countdown runs at the beginning of the active player's upkeep,
                // before anyone receives priority.
                TickSuspendedCards(State.ActivePlayerId);

                // CR 726.2, the first of the initiative's three inherent abilities: "at the
                // beginning of the upkeep of the player who has the initiative, that player
                // ventures into Undercity." The same stated simplification the monarch's end
                // step draw makes: the rules give this trigger no source, this engine keys
                // pending triggers to a permanent, so the venture happens directly. The room the
                // marker enters is a real triggered ability on the dungeon card and uses the
                // stack as normal — only the venture itself skips it.
                if (State.InitiativeId == State.ActivePlayerId)
                {
                    foreach (var owed in Dungeons.VentureEvents(
                        State, State.ActivePlayerId, Dungeons.Undercity))
                    {
                        Emit(owed);
                    }
                }

                break;

            case TurnStep.Cleanup:
                Emit(new PriorityWithdrawn());
                // CR 514.2: damage is removed and "until end of turn" effects end, at the same
                // time, as a turn-based action.
                Emit(new DamageCleared());

                // Two durations end in this step and they are told apart by which fields are set,
                // not by the turn number: an effect carrying UntilEndOfTurnOf reaches the cleanup
                // of the turn that made it with its number already satisfied, and the plain arm
                // would take it a whole turn early. Asked here rather than by widening the
                // comparison, because "the end of your next turn" is a different sentence from
                // "until end of turn" and not a longer reading of it (CR 611.2b).
                foreach (var expiring in State.FloatingEffects
                    .Where(f => f.UntilEndOfTurnOf is null
                        && f.UntilEndOfTurn is not null
                        && f.UntilEndOfTurn <= State.TurnNumber)
                    .ToList())
                {
                    Emit(new ContinuousEffectEnded(expiring.Id));
                }

                // "Until the end of your next turn" — the cleanup of a turn that player took
                // after the one that granted it. Strictly later, so the granting turn does not
                // count even when it was already theirs; compared against the active player
                // rather than an arithmetic deadline, so an extra turn taken in between is
                // harmless. The same shape the exile play-window uses in FinishCleanup.
                //
                // A missing start stamp ends it here rather than never: the two fields are set
                // together by the only effect that makes one of these, and if that ever came
                // apart the shorter window is the safe half to be wrong on. `null < int` is
                // false, so leaving the comparison bare would have made the theft permanent.
                foreach (var lingering in State.FloatingEffects
                    .Where(f => f.UntilEndOfTurnOf == State.ActivePlayerId
                        && (f.UntilEndOfTurn is not { } began || began < State.TurnNumber))
                    .ToList())
                {
                    Emit(new ContinuousEffectEnded(lingering.Id));
                }

                if (!AskDiscardIfNeeded())
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

    /// <summary>
    /// Phases permanents out and in, before the active player untaps (CR 703.4a).
    /// </summary>
    /// <remarks>
    /// One turn-based action, not two: everything phases at once (CR 702.26a), so both lists are
    /// worked out against the board as it stands and only then written. Reading the second list
    /// after the first had been applied would phase a permanent out and straight back in.
    /// <para>
    /// CR 702.26g: an Aura, Equipment or Fortification attached to something that phases out goes
    /// with it, and comes back with it - which is why what is written down is the player whose
    /// untap step returns each permanent rather than its own controller. An opponent's Aura on
    /// your phasing creature returns on <em>your</em> untap step, along with the creature.
    /// </para>
    /// </remarks>
    private void PhaseInAndOut()
    {
        var returning = State.PhasedOut
            .Where(pair => pair.Value == State.ActivePlayerId)
            .Select(pair => pair.Key)
            .ToList();

        var leaving = new Dictionary<ObjectId, Guid>();

        foreach (var id in State.Battlefield)
        {
            var obj = State.GetObject(id);
            if (ControllerOf(obj) != State.ActivePlayerId)
                continue;

            if (Characteristics.Of(State, _abilities, obj).Has(KeywordAbility.Phasing))
                leaving[id] = State.ActivePlayerId;
        }

        if (leaving.Count == 0 && returning.Count == 0)
            return;

        // CR 702.26g, 702.26h: anything attached to a departing permanent leaves with it, under
        // the departing permanent's schedule rather than its own. Shared with PhaseOutPermanent,
        // which needs exactly the same fan-out for exactly the same reason.
        foreach (var going in Phasing.Out(State, leaving))
            Emit(going);

        foreach (var id in returning)
            Emit(new PermanentPhasedIn(id));
    }

    private void Untap()
    {
        Emit(new PriorityWithdrawn());

        var mine = State.Battlefield
            .Where(id => ControllerOf(State.GetObject(id)) == State.ActivePlayerId)
            .ToList();

        // CR 302.6: a permanent its controller has controlled since their turn began is no
        // longer summoning sick. That is decided as the turn starts, before anything untaps.
        var sick = mine
            .Where(id => State.GetObject(id).Permanent?.HasSummoningSickness == true)
            .ToImmutableList();
        if (!sick.IsEmpty)
            Emit(new SummoningSicknessCleared(sick));

        // CR 502.3: the active player's permanents untap, simultaneously — except those a
        // continuous effect says do not. The restriction is computed rather than stored because
        // it usually comes from somewhere else: the Aura holding the creature down is a different
        // permanent, and it can be removed between one untap step and the next.
        // CR 502.3: and except those told once to sit this one out. That flag is spent by
        // being honoured, so it is cleared here whether or not the permanent was tapped - a
        // creature that untapped some other way still used up the effect.
        var held = mine
            .Where(id => State.GetObject(id).Permanent?.SkipsNextUntap == true)
            .ToList();

        var tapped = mine
            .Where(id => State.GetObject(id).Permanent?.IsTapped == true)
            .Where(id => State.GetObject(id).Permanent?.SkipsNextUntap != true)
            .Where(id => !Characteristics.Of(State, _abilities, State.GetObject(id)).DoesNotUntap)
            .ToImmutableList();

        // CR 502.3: "you may choose not to untap this" is a decision the untap step makes, not a
        // restriction on it. The ones that offer it are held back and asked about at the settle
        // that follows; everything else untaps now, because untapping is simultaneous and waiting
        // for an answer would split it.
        var optional = tapped
            .Where(id => _abilities.MayDeclineUntap(State.GetObject(id).Card))
            .ToImmutableList();

        // CR 122.1c: a permanent with a stun counter does not untap here either - it spends a
        // counter instead, and the untap step is where nearly every one of them is spent.
        var automatic = tapped.RemoveRange(optional);
        foreach (var e in StunCounters.Untapping(State, automatic))
            Emit(e);

        _optionalUntaps = optional;

        foreach (var id in held)
            Emit(new UntapSkipped(id, Skipping: false));
    }

    private void DrawForTurn()
    {
        // CR 103.8a: in a two-player game, the player who plays first skips the draw step of
        // their first turn. With more players everyone draws every turn.
        var skips = State.TurnOrder.Count == 2
            && State.TurnNumber == 1
            && State.ActivePlayerId == FirstPlayerId;

        // CR 504.1: and whatever the active player controls that says to skip it. Asked of the
        // board rather than held as a flag on the player, for the same reason the hand limit and
        // the land drop are: the permanent saying so can leave between one turn and the next.
        skips |= State.Battlefield
            .Select(State.GetObject)
            .Any(o => ControllerOf(o) == State.ActivePlayerId
                && _abilities.SkipsDrawStep(o.Card));

        if (!skips)
            Draw(State.ActivePlayerId);
    }

    /// <summary>
    /// Assigns and deals combat damage as one simultaneous event (CR 510.1, 510.2).
    /// </summary>
    /// <remarks>
    /// Nobody gets priority between assignment and dealing (CR 510.2), which is what makes two
    /// creatures that kill each other both die: neither is destroyed before the other assigns.
    /// </remarks>
    private void DealCombatDamage()
    {
        Emit(new PriorityWithdrawn());

        // Asked before the division, because taking it means there is no division to make.
        if (AskAssignAsThoughUnblockedIfNeeded())
            return;

        // CR 510.1c: an attacker blocked by more than one creature has its damage divided as
        // its controller chooses. The engine used the order the blocks were declared in, which
        // is the defending player's order — the wrong player's — so it is asked for.
        if (AskDamageDivisionIfNeeded())
            return;

        DealCombatDamageNow();
    }

    private void DealCombatDamageNow()
    {
        // CR 615.1: a fog prevents the damage, so none is dealt. The step still finishes —
        // combat carries on to the end of combat step, it just does nothing on the way.
        var fogged = State.FloatingEffects.Any(f =>
            string.Equals(f.DefinitionId, PreventAllCombatDamage.FloatingId, StringComparison.Ordinal));

        // CR 615.12: with a ban in play the fog is no longer all-or-nothing — it prevents the
        // events the ban does not cover and none of the ones it does, so the step has to be
        // walked event by event. The shortcut stays for the case that is still wholesale, which
        // is every game with no ban in it.
        if (fogged && !AnyPreventionBan())
        {
            _damageDivision.Clear();
            _damageDivided.Clear();
            Emit(new CombatDamageStepDone());
            return;
        }

        var firstStrikeStep = State.Combat.DamageStepsDone == 0
            && CombatRules.NeedsFirstStrikeStep(State, _abilities);

        foreach (var damage in CombatRules.AssignCombatDamage(
            State, _abilities, firstStrikeStep, _damageDivision, _assigningAsThoughUnblocked))
        {
            // CR 615.12: under a fog, only the damage the ban covers is dealt. Asked here rather
            // than in the replacement pass because the fog is not a replacement effect at all -
            // it is a floating effect the step reads - so there is nowhere else to ask it.
            if (fogged && !IsBannedFrom(damage))
                continue;

            Emit(damage);
        }

        _damageDivision.Clear();
        _damageDivided.Clear();
        _assigningAsThoughUnblocked.Clear();
        _askedAboutUnblocked.Clear();
        Emit(new CombatDamageStepDone());
    }

    /// <summary>
    /// Asks an attacking player how to divide damage among multiple blockers (CR 510.1c).
    /// </summary>
    /// <remarks>
    /// The answer is an amount per blocker, given as one pick per point of damage — pick a
    /// blocker three times and it takes three. That expresses any legal division, including the
    /// one an ordering cannot: two damage to each of two three-toughness blockers, killing
    /// neither.
    /// <para>
    /// The rules constrain it (CR 510.1c with CR 702.19b): a creature may only be assigned
    /// damage beyond lethal, or damage trampling through to the player, once every blocker
    /// ahead of it has lethal. That is checked when the answer comes back rather than trusted.
    /// </para>
    /// </remarks>
    /// <summary>
    /// Offers "you may have this assign its combat damage as though it weren't blocked"
    /// (CR 510.1a).
    /// </summary>
    /// <remarks>
    /// Asked once per attacker per damage step, and only where the answer could matter: an
    /// attacker nothing is blocking already assigns everything to what it was attacking, so
    /// there is nothing to offer.
    /// </remarks>
    private bool AskAssignAsThoughUnblockedIfNeeded()
    {
        foreach (var (attackerId, _) in State.Combat.Attackers)
        {
            if (_askedAboutUnblocked.Contains(attackerId)
                || !State.Combat.Blocked.Contains(attackerId)
                || !State.TryGetObject(attackerId, out var attacker))
            {
                continue;
            }

            var computed = Characteristics.Of(State, _abilities, attacker);
            if (!computed.MayAssignAsThoughUnblocked || (computed.Power ?? 0) <= 0)
                continue;

            if (!AssignsThisStepNow(computed))
                continue;

            _askedAboutUnblocked.Add(attackerId);

            Ask(new PendingChoice
            {
                Id = "unblocked:" + attackerId,
                PlayerId = attacker.ControllerId,
                Kind = ChoiceKind.AssignAsThoughUnblocked,
                Prompt = $"Have {attacker.Card.Name} assign its combat damage as though it "
                    + "weren't blocked?",
                Options =
                [
                    new ChoiceOption("yes", "Assign as though unblocked"),
                    new ChoiceOption("no", "Assign to the blockers"),
                ],
                MinPicks = 1,
                MaxPicks = 1,
                Context = [attackerId.Value.ToString("N")],
            });

            return true;
        }

        return false;
    }

    /// <summary>Whether this creature assigns damage in the step the game is in now.</summary>
    private bool AssignsThisStepNow(ComputedCharacteristics computed) =>
        CombatRules.AssignsThisStep(
            computed,
            State.Combat.DamageStepsDone == 0
                && CombatRules.NeedsFirstStrikeStep(State, _abilities));

    /// <summary>Records the answer and carries on into the division, if one is still needed.</summary>
    private void ResolveAssignAsThoughUnblocked(PendingChoice choice, IReadOnlyList<string> picks)
    {
        var attackerId = new ObjectId(Guid.ParseExact(choice.Context[0], "N"));

        if (picks.Count > 0 && string.Equals(picks[0], "yes", StringComparison.Ordinal))
        {
            _assigningAsThoughUnblocked.Add(attackerId);

            // Nothing left to divide for this attacker, so it must not be asked about below.
            _damageDivided.Add(attackerId);
        }

        if (AskAssignAsThoughUnblockedIfNeeded())
            return;

        if (AskDamageDivisionIfNeeded())
            return;

        DealCombatDamageNow();
    }

    private bool AskDamageDivisionIfNeeded()
    {
        foreach (var (attackerId, _) in State.Combat.Attackers)
        {
            var blockers = State.Combat.BlockersOf(attackerId);
            if (blockers.Count < 2 || _damageDivided.Contains(attackerId))
                continue;

            if (!State.TryGetObject(attackerId, out var attacker))
                continue;

            var power = Characteristics.Of(State, _abilities, attacker).Power ?? 0;
            if (power <= 0)
                continue;

            var living = blockers.Where(id => State.TryGetObject(id, out _)).ToList();
            if (living.Count < 2)
                continue;

            // CR 702.22j: a creature blocked by a creature with banding has its damage divided
            // by the *defending* player, not by the player who attacked with it. It is the one
            // exception to CR 510.1c, and the only half of banding a two-player game reaches
            // without declaring attacking bands.
            var bandedBlocker = living.Any(id =>
                State.TryGetObject(id, out var blocker)
                && Characteristics.Of(State, _abilities, blocker).Has(KeywordAbility.Banding));

            var divider = bandedBlocker && State.Combat.Attackers.TryGetValue(attackerId, out var at)
                ? at.DefendingPlayer
                : attacker.ControllerId;

            Ask(new PendingChoice
            {
                Id = "divide:" + attackerId,
                PlayerId = divider,
                Kind = ChoiceKind.DivideCombatDamage,
                Prompt = $"Divide {attacker.Card.Name}'s {power} damage among the "
                    + $"{living.Count} creatures blocking it. Pick a creature once per point; "
                    + "a creature can only take more than lethal once the others have lethal.",
                Options = [.. living.Select(id => new ChoiceOption(
                    id.Value.ToString("N"), State.GetObject(id).Card.Name))],
                // One pick per point of damage, which is what makes any legal division sayable.
                MinPicks = power,
                MaxPicks = power,
                TotalToDivide = power,
                Context = [attackerId.Value.ToString("N")],
            });

            return true;
        }

        return false;
    }

    /// <summary>The attacker a division choice is about.</summary>
    private ObjectId DivisionAttacker(PendingChoice choice) =>
        State.Combat.Attackers.Keys.First(
            id => string.Equals(id.Value.ToString("N"), choice.Context[0], StringComparison.Ordinal));

    /// <summary>Turns one-pick-per-point into an amount per blocker.</summary>
    private Dictionary<ObjectId, int> DivisionAmounts(
        PendingChoice choice, IReadOnlyList<string> picks)
    {
        var attackerId = DivisionAttacker(choice);
        var amounts = new Dictionary<ObjectId, int>();

        foreach (var pick in picks)
        {
            var blocker = State.Combat.BlockersOf(attackerId).First(
                b => string.Equals(b.Value.ToString("N"), pick, StringComparison.Ordinal));
            amounts[blocker] = amounts.GetValueOrDefault(blocker) + 1;
        }

        return amounts;
    }

    private void RecordDamageDivision(PendingChoice choice, IReadOnlyList<string> picks)
    {
        var attackerId = DivisionAttacker(choice);
        var amounts = DivisionAmounts(choice, picks);

        _damageDivision[attackerId] = amounts;
        _damageDivided.Add(attackerId);

        if (AskDamageDivisionIfNeeded())
            return;

        DealCombatDamageNow();
        _priorityRecipient = choice.ResumePriorityTo;
        SettleBeforePriority();
        GrantPriorityAfterSettle(choice.ResumePriorityTo);
    }

    /// <summary>
    /// Refuses a division the rules do not allow (CR 510.1c).
    /// </summary>
    /// <remarks>
    /// The constraint is not "lethal to everything": it is that a creature may only be assigned
    /// damage <em>beyond</em> lethal once every other blocker already has lethal. Assigning two
    /// each to two 3/3s is legal; assigning four to one and none to the other is not, because
    /// the fourth point went past lethal while a blocker still had none.
    /// </remarks>
    private void RequireLegalDivision(ObjectId attackerId, Dictionary<ObjectId, int> amounts)
    {
        var deathtouch = Characteristics
            .Of(State, _abilities, State.GetObject(attackerId))
            .Has(KeywordAbility.Deathtouch);

        var anyBelowLethal = false;
        var anyAboveLethal = false;

        foreach (var blockerId in State.Combat.BlockersOf(attackerId))
        {
            if (!State.TryGetObject(blockerId, out var blocker))
                continue;

            var lethal = deathtouch
                ? 1
                : Math.Max(
                    0,
                    (Characteristics.ToughnessOf(State, _abilities, blocker) ?? 0)
                        - (blocker.Permanent?.DamageMarked ?? 0));

            var assigned = amounts.GetValueOrDefault(blockerId);
            if (assigned < lethal)
                anyBelowLethal = true;

            if (assigned > lethal)
                anyAboveLethal = true;
        }

        if (anyAboveLethal && anyBelowLethal)
        {
            throw new InvalidOperationException(
                "A blocker can only be assigned more than lethal damage once every other "
                + "blocker has lethal (CR 510.1c).");
        }
    }

    private readonly Dictionary<ObjectId, Dictionary<ObjectId, int>> _damageDivision = [];

    /// <summary>Attackers whose controller took the "as though it weren't blocked" option.</summary>
    private readonly HashSet<ObjectId> _assigningAsThoughUnblocked = [];

    /// <summary>Attackers already asked this step, so nobody is asked the same thing twice.</summary>
    private readonly HashSet<ObjectId> _askedAboutUnblocked = [];
    private readonly HashSet<ObjectId> _damageDivided = [];

    private void FinishCleanup()
    {
        // CR 601.3e: "until the end of your next turn" runs out here, at the end of a turn the
        // card's owner took after the one that granted it. Revoking it by event rather than by
        // arithmetic is what makes an extra turn taken in between harmless - the window ends when
        // that player's turn actually ends, whichever number it turned out to carry.
        foreach (var id in State.Exile)
        {
            var card = State.GetObject(id);

            if (card.MayPlayThroughOwnersNextTurn is { } granted
                && State.ActivePlayerId == card.OwnerId
                && State.TurnNumber > granted)
            {
                Emit(new PlayWindowClosed(id));
            }
        }

        // CR 514.3: normally no player receives priority during cleanup, and the turn ends.
        BeginTurn();
    }

    private void ResolveTop()
    {
        var stackId = State.Stack[0];
        var spell = State.GetObject(stackId);

        Emit(new PriorityWithdrawn());

        // CR 608.3b and 702.140b: a mutating creature spell is the exception to the rule below.
        // With an illegal target it does not fail to resolve — it stops being a mutating creature
        // spell, continues resolving as an ordinary creature spell, and the creature arrives on
        // the battlefield on its own. Left to fizzle it would go to the graveyard instead, which
        // is a card its controller has lost rather than one that merged with nothing.
        var mergingCreature = spell.WasMutated && spell.Ability is null;
        if (mergingCreature && !MutateTargetStillLegal(spell))
        {
            Emit(new SpellMutationLapsed(stackId));
            spell = State.GetObject(stackId);
            mergingCreature = false;
        }

        // CR 608.2b: if every target is now illegal, it does not resolve at all — none of its
        // effects happen, including the ones that had nothing to do with the target.
        if (!TargetsStillLegal(spell))
        {
            var description = spell.Ability?.Text ?? spell.Card.Name;
            Emit(new FizzledForIllegalTargets(stackId, description));

            if (spell.Ability is not null)
                Emit(new ObjectCeasedToExist(stackId, Zone.Stack));
            else
                Move(stackId, Zone.Graveyard, MoveCause.Other, spell.ControllerId);

            return;
        }

        if (spell.IsCopy)
        {
            // CR 707.10: a copy resolves like the spell it copies and then stops existing, having
            // never been a card.
            RunEffects(SpellBeingCast(spell)?.Effects ?? [], spell);
            Emit(new StackObjectResolved(stackId, spell.Card.Name));
            Emit(new ObjectCeasedToExist(stackId, Zone.Stack));
            _castAs.Remove(stackId);
            return;
        }

        if (spell.Ability is not null)
        {
            if (!spell.ChosenModes.IsEmpty
                && TriggerBehind(spell.Card, spell.Ability.AbilityId) is { } modal)
            {
                // Each mode's effects index into that mode's own targets, so each runs against
                // its own slice of what was chosen - the same arithmetic the spell path does,
                // and wrong in the same way if it is skipped.
                var modeOffset = 0;
                foreach (var index in spell.ChosenModes)
                {
                    var mode = modal.Modes[index];
                    RunEffects(
                        mode.Effects,
                        spell with
                        {
                            Targets =
                                [.. spell.Targets.Skip(modeOffset).Take(mode.Targets.Count)],
                        });

                    modeOffset += mode.Targets.Count;
                }

                Emit(new StackObjectResolved(stackId, spell.Ability.Text));
                Emit(new ObjectCeasedToExist(stackId, Zone.Stack));
                return;
            }

            RunEffects(
                EffectsOfAbility(spell.Card, spell.Ability.AbilityId, spell.Ability.SourceId),
                spell);

            // CR 608.2m applies to cards. An ability was never a card and has no graveyard to go
            // to: it simply leaves the stack and stops existing.
            Emit(new StackObjectResolved(stackId, spell.Ability.Text));
            Emit(new ObjectCeasedToExist(stackId, Zone.Stack));
            return;
        }

        var spellDefinition = SpellBeingCast(spell);

        // CR 702.47a: the spliced text is added to the spell, and the rule puts it after what the
        // card itself says. Its targets were chosen after the modes', so its slice starts where
        // theirs ended - the arithmetic modes already needed, one more list along.
        var splicedOffset = (spellDefinition?.Targets.Count ?? 0)
            + spell.ChosenModes.Sum(i => spellDefinition!.Modes[i].Targets.Count);

        void RunSpliced()
        {
            foreach (var onto in spell.Spliced)
            {
                var text = _abilities.SpellOf(onto);
                if (text is null)
                    continue;

                RunEffects(
                    text.Effects,
                    spell with
                    {
                        Targets = [.. spell.Targets.Skip(splicedOffset).Take(text.Targets.Count)],
                    });

                splicedOffset += text.Targets.Count;
            }
        }

        if (spell.ChosenModes.IsEmpty)
        {
            RunEffects(
                spell.WasOverloaded && spellDefinition is { OverloadEffects.IsEmpty: false }
                    ? spellDefinition.OverloadEffects
                    : spellDefinition?.Effects ?? [],
                spell);

            RunSpliced();
        }
        else
        {
            // Each mode's effects index into that mode's own targets, so each is run against its
            // own slice of what was chosen — in the order the card lists the modes (CR 700.2e).
            var offset = spellDefinition!.Targets.Count;
            foreach (var index in spell.ChosenModes)
            {
                var mode = spellDefinition.Modes[index];
                RunEffects(
                    mode.Effects,
                    spell with { Targets = [.. spell.Targets.Skip(offset).Take(mode.Targets.Count)] });

                offset += mode.Targets.Count;
            }

            // Whatever the card says outside its modes still happens. Skipping it lost the
            // kicked half of every modal card with a kicker — the modes resolved and the rider
            // silently did not.
            RunEffects(
                spellDefinition.Effects,
                spell with { Targets = [.. spell.Targets.Take(spellDefinition.Targets.Count)] });

            RunSpliced();
        }

        // CR 702.113a: the awaken half is a second spell ability that happens on top of
        // everything the card already said - not instead of it, which is the difference from
        // overload. Its one target was added last as the spell was cast, so it is the final
        // entry in whatever the card, its modes and its splices between them chose.
        if (spell.WasAwakened && spellDefinition is { AwakenEffects.IsEmpty: false })
        {
            RunEffects(
                spellDefinition.AwakenEffects,
                spell with { Targets = [.. spell.Targets.Skip(spell.Targets.Count - 1)] });
        }

        // CR 608.3: a permanent spell becomes a permanent. CR 608.2m: an instant or sorcery is
        // put into its owner's graveyard as the final part of its resolution — unless it was cast
        // with flashback, which exiles it instead (CR 702.34a).
        // CR 707.2: a spell cast face down resolves into a face-down permanent, and it is a
        // creature spell whatever the card underneath happens to be — a face-down land card cast
        // with morph becomes a 2/2 creature, not a land.
        var wasFaceDown = _castFaceDown.Remove(stackId);

        // CR 715.3b: a spell cast as an Adventure "has only its alternative characteristics", and
        // those are a sorcery or an instant. Asking the card instead put the creature onto the
        // battlefield the moment its Adventure resolved - the Adventure did happen, and then the
        // card arrived as well, which is two cards' worth of value from one.
        var chosenHalf = _castAs.TryGetValue(stackId, out var half) ? half : (CastAs?)null;
        var castHalf = _halfCast.TryGetValue(stackId, out var which) ? which : (int?)null;
        _halfCast.Remove(stackId);

        var becomesPermanent = chosenHalf?.IsPermanent ?? IsPermanentCard(spell.Card);

        var destination = wasFaceDown || becomesPermanent
            ? Zone.Battlefield
            : Zone.Graveyard;

        // CR 702.27a: a bought-back spell goes to its owner's hand instead of the graveyard, as
        // the last part of its resolution. Everything it did has already happened.
        if (spell.WasBoughtBack && destination == Zone.Graveyard)
            destination = Zone.Hand;
        if (_exileOnLeavingStack.Remove(stackId) && destination == Zone.Graveyard)
            destination = Zone.Exile;

        // CR 715.3d: "instead of putting a spell that was cast as an Adventure into its owner's
        // graveyard as it resolves, its controller exiles it. For as long as that card remains
        // exiled, that player may play it."
        _castAs.Remove(stackId);
        if (chosenHalf?.ExileOnResolve == true && destination == Zone.Graveyard)
            destination = Zone.Exile;

        var wentAdventuring = chosenHalf?.OnAdventure == true;

        // CR 702.140c: "it doesn't enter the battlefield. Rather, it merges with the target
        // creature and becomes one object represented by more than one card." So the move that
        // every other permanent spell makes is the one thing that must not happen here — the
        // card does not arrive anywhere, it joins something already on the battlefield, and the
        // permanent that results is the one that was already there (CR 730.2c).
        //
        // Everything the spell itself said has already run above, which is CR 702.140f working
        // out on its own: by the time anything could refer to the mutating creature spell, what
        // is left of it is the permanent it merged with.
        if (mergingCreature
            && destination == Zone.Battlefield
            && spell.Targets.LastOrDefault() is { Kind: TargetKind.Permanent } merging)
        {
            Emit(new PermanentMutated(merging.Subject, stackId, spell.MutatesOnTop));
            Emit(new StackObjectResolved(stackId, spell.Card.Name));
            return;
        }

        // CR 608.2m: the last thing a resolving spell does is go to its owner's graveyard - but
        // only if it is still on the stack to go. A spell whose own text moves it has already
        // left, and "Exile Blood for the Blood God!" is the printed shape that does it: the
        // effects above exile the card, and this move then asked the game for an object that had
        // stopped existing. Nothing to move is not an error, it is the rule having nothing left
        // to do. Found by the soak, which is the only thing that plays a card like this.
        if (!State.TryGetObject(stackId, out _))
        {
            Emit(new StackObjectResolved(stackId, spell.Card.Name));
            return;
        }

        var landed = Move(stackId, destination, MoveCause.Resolve, spell.ControllerId);

        if (wentAdventuring && landed is { } exiled)
            Emit(new WentOnAdventure(exiled));

        // Now the object exists, so the counters have something to sit on. Suspended rather than
        // merely countered-up: the card's own suspend ability is what removes them and casts it,
        // and it reads the same state a suspend from hand sets.
        if (_suspendOnResolve.Remove(stackId, out var waiting)
            && destination == Zone.Exile
            && landed is { } waitingId)
        {
            Emit(new CardSuspended(waitingId, spell.ControllerId, waiting));
        }

        // Now the exiled card exists, so there is something to encode. Asked at the next settle
        // rather than here, because choosing a creature is a choice and this is mid-resolution.
        if (_cipherOnResolve.Remove(stackId, out var encoder)
            && destination == Zone.Exile
            && landed is { } encodedId)
        {
            _encodingsOwed.Add((encodedId, encoder));
        }

        // CR 709.5d: the permanent "is given the 'left half unlocked' designation as it enters the
        // battlefield if its left half was cast as a spell". Which half was cast is the same fact
        // that decided what resolved, so it is read from the same place.
        if (castHalf is { } opened && destination == Zone.Battlefield && landed is { } arrived)
            Emit(new HalfUnlocked(arrived, opened));

        // CR 702.190b: "a permanent spell whose sneak cost was paid enters the battlefield tapped
        // and attacking ... the same player, planeswalker, or battle as the creature that was
        // returned". It arrives attacking without ever having been declared, so no "whenever this
        // attacks" ability of its own triggers - the same thing ninjutsu's JoinedCombat says.
        if (spell.JoiningAgainst is { } sneakingInto
            && destination == Zone.Battlefield
            && landed is { } snuckIn)
        {
            Emit(new PermanentTapped(snuckIn));
            Emit(new JoinedCombat(snuckIn, sneakingInto));
        }

        // Emitted straight after the move rather than as part of it, because turning face down is
        // its own thing that happens to a permanent and the move is not where that knowledge is.
        if (wasFaceDown)
            Emit(new PermanentTurned(landed, FaceDown: true));

        // CR 702.185a: "if this spell's warp cost was paid, exile the permanent this spell
        // becomes at the beginning of the next end step". A delayed trigger rather than anything
        // done now - the permanent gets the turn it was cast on, which is the whole of what warp
        // buys - and its own effect id, because it has to record the exile as a warp so the card
        // can be cast again afterwards.
        if (spell.WasWarped && destination == Zone.Battlefield)
        {
            Emit(new DelayedTriggerCreated(
                Guid.NewGuid(),
                spell.ControllerId,
                landed,
                TurnStep.End,
                "warp-exile",
                State.TurnNumber));
        }

        // CR 702.171a: a paid-for offspring arrives beside its parent as a 1/1 copy. The rule
        // makes this an enters trigger and this creates the token directly, so there is no window
        // between the creature arriving and its copy - the same deviation evoke's sacrifice makes,
        // and for the same reason.
        // CR 702.157a: one token copy per payment, created as the creature enters. Copies of
        // the card rather than of the permanent, which is the same thing here and the only one
        // available - the permanent's own characteristics are computed, never stored.
        if (spell.SquadPaid > 0 && destination == Zone.Battlefield)
        {
            var copy = Abilities.TokenCards.AsToken(
                spell.Card, oracleId: "token-squad-" + spell.Card.OracleId);

            for (var i = 0; i < spell.SquadPaid; i++)
            {
                Emit(new ObjectCreated(
                    ObjectId.New(), copy, spell.ControllerId, spell.ControllerId,
                    Zone.Battlefield));
            }
        }

        if (spell.WasOffspring && destination == Zone.Battlefield)
        {
            var small = Abilities.TokenCards.AsToken(
                spell.Card,
                power: 1,
                toughness: 1,
                oracleId: "token-offspring-" + spell.Card.OracleId);

            Emit(new ObjectCreated(
                ObjectId.New(), small, spell.ControllerId, spell.ControllerId,
                Zone.Battlefield));
        }

        // CR 702.74a: an evoked creature is sacrificed as it enters. The enters trigger it was
        // cast for has already fired by now and is its own object on the stack, so it resolves
        // whether or not the creature that raised it is still there (CR 603.6).
        //
        // The rule makes the sacrifice a triggered ability and this does it directly, so there is
        // no window to respond to it. The difference is visible only to a card that wants to
        // sacrifice the creature to something else in that window, which is the trick evoke is
        // famous for; it is a known deviation rather than an oversight.
        if (spell.WasEvoked && destination == Zone.Battlefield)
            Move(landed, Zone.Graveyard, MoveCause.Sacrifice, spell.ControllerId);

        // CR 702.109a: a dashed permanent has haste and goes home at the beginning of the next
        // end step. Both belong to the permanent rather than to the spell, so they are granted
        // here, to the object the spell just became — the spell's own effects ran while it was
        // still on the stack, when this permanent did not exist.
        if (spell.WasDashed && destination == Zone.Battlefield)
        {
            Emit(new ContinuousEffectCreated(
                Guid.NewGuid(),
                Cards.GenerativeEffects.GrantId(KeywordAbility.Haste),
                [landed],
                State.TurnNumber));

            Emit(new DelayedTriggerCreated(
                Guid.NewGuid(),
                spell.ControllerId,
                landed,
                TurnStep.End,
                "return-to-hand",
                State.TurnNumber));
        }

        // CR 702.152a: blitz hands the permanent the same haste and the same clock, and takes it
        // to the graveyard instead of the hand. The permanent is marked as well as the spell,
        // because the ability that draws a card when it dies is a static ability of the
        // *permanent* - "as long as this permanent's blitz cost was paid" - and the spell it came
        // from has stopped existing by then (CR 400.7).
        if (spell.WasBlitzed && destination == Zone.Battlefield)
        {
            Emit(new SpellBlitzed(landed));

            Emit(new ContinuousEffectCreated(
                Guid.NewGuid(),
                Cards.GenerativeEffects.GrantId(KeywordAbility.Haste),
                [landed],
                State.TurnNumber));

            Emit(new DelayedTriggerCreated(
                Guid.NewGuid(),
                spell.ControllerId,
                landed,
                TurnStep.End,
                "sacrifice",
                State.TurnNumber));
        }

        // CR 303.4c: an Aura spell does not choose what it enchants on resolution — it entered
        // the stack aimed at something, and it arrives already attached to it. The attach has to
        // happen here rather than in an effect, because a spell's effects run while it is still
        // on the stack and the permanent it becomes does not exist yet.
        if (destination == Zone.Battlefield && IsAura(spell.Card)
            && spell.Targets.FirstOrDefault() is { } enchanted)
        {
            // A player is a legal thing to enchant and is not an object (CR 303.4a), so which of
            // the two it was decides which half of the attachment is filled in.
            Emit(enchanted.Kind == TargetKind.Player
                ? new PermanentAttached(landed, null, enchanted.Player)
                : new PermanentAttached(landed, enchanted.Subject));
        }

        // CR 702.103a: a bestowed permanent arrives attached to what the spell enchanted, and
        // goes on being an Aura until it falls off. The flag is put on the permanent as well as
        // on the spell, because they are different objects (CR 400.7) and it is the permanent
        // that has to remember.
        if (destination == Zone.Battlefield && spell.WasBestowed
            && spell.Targets.LastOrDefault() is { Kind: TargetKind.Permanent } bestowedOn)
        {
            Emit(new SpellBestowed(landed));
            Emit(new PermanentAttached(landed, bestowedOn.Subject));
        }

        // CR 702.146a: a card cast for its disturb cost arrives with its back face up. Derived
        // rather than remembered - the object already records the zone it was cast from, and the
        // card already says which zone turns it over - so a replay reaches the same permanent
        // without an event whose only job is to carry a flag.
        if (destination == Zone.Battlefield
            && spell.CastFromZone is { } cameFrom
            && _abilities.SpellOf(spell.Card)?.CastFrom is { Transformed: true } turning
            && turning.Zone == cameFrom
            && spell.Card.Faces.Count > 1)
        {
            Emit(new PermanentTransformed(landed, 1));
        }

        Emit(new StackObjectResolved(stackId, spell.Card.Name));
    }

    /// <summary>Whether a damage source has infect (CR 702.90b).</summary>
    private bool HasInfect(ObjectId sourceId) => HasKeyword(sourceId, KeywordAbility.Infect);

    /// <summary>Whether an object has a keyword right now, computed (CR 613).</summary>
    private bool HasKeyword(ObjectId id, KeywordAbility keyword) =>
        State.TryGetObject(id, out var source)
        && Characteristics.Of(State, _abilities, source).Has(keyword);

    /// <summary>
    /// How many lands a player may play this turn (CR 305.2).
    /// </summary>
    /// <remarks>
    /// One, plus whatever the battlefield adds, and asked rather than assumed for the same reason
    /// the hand limit is: the answer changes when a permanent enters or leaves, and a number
    /// written into the land drop goes on being one while the card that says otherwise sits there
    /// doing nothing.
    /// </remarks>
    private int LandDropsFor(Guid playerId) =>
        1 + State.GetPlayer(playerId).ExtraLandDropsThisTurn
        + State.Battlefield
            .Select(State.GetObject)
            .Where(o => ControllerOf(o) == playerId)
            .Sum(o => _abilities.ExtraLandDrops(o.Card));

    /// <summary>The colour a printed word names, or null if it is not one (CR 105.1).</summary>
    private static ManaColor? ColorNamed(string? word) => word?.ToLowerInvariant() switch
    {
        "white" => ManaColor.White,
        "blue" => ManaColor.Blue,
        "black" => ManaColor.Black,
        "red" => ManaColor.Red,
        "green" => ManaColor.Green,
        _ => null,
    };

    /// <summary>Whether the card is an Aura, and so enters attached (CR 303.4).</summary>
    private static bool IsAura(CardDefinition card) =>
        card.Subtypes.Contains("Aura", StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Whether the creature a mutating creature spell is merging with is still legal
    /// (CR 702.140b).
    /// </summary>
    /// <remarks>
    /// Asked of the mutate target alone rather than through
    /// <see cref="TargetsStillLegal(GameObject)"/>, which answers "is <em>any</em> target still
    /// legal" (CR 608.2b). The two agree on every printed mutate card, because none of them
    /// targets anything else; they would disagree on one that did, and the mutate target is the
    /// only one this question is about — a spell whose creature has died merges with nothing
    /// however legal the rest of its targets are.
    /// <para>
    /// Mutate's target is appended after whatever the card itself targets, so it is the last one.
    /// </para>
    /// </remarks>
    private bool MutateTargetStillLegal(GameObject spell)
    {
        var specs = SpellBeingCast(spell)?.Targets ?? [];
        if (_abilities.SpellOf(spell.Card)?.MutateTarget is not { } merging)
            return false;

        var index = specs.Count;
        return index < spell.Targets.Count
            && merging.IsLegal(
                State,
                _abilities,
                spell.Targets[index],
                spell.ControllerId,
                spell,
                announced: spell.VariableValue);
    }

    /// <summary>
    /// Whether at least one of the object's targets is still legal (CR 608.2b).
    /// </summary>
    /// <remarks>
    /// One legal target is enough: a spell with several targets resolves and does as much as it
    /// can, and only one with <em>no</em> legal targets left does nothing at all.
    /// </remarks>
    private bool TargetsStillLegal(GameObject spell)
    {
        if (spell.Targets.IsEmpty)
            return true;

        var specs = spell.Ability is not null
            ? TargetsOfAbility(spell.Card, spell.Ability.AbilityId, spell.Ability.SourceId)
            : SpellBeingCast(spell)?.Targets;

        if (specs is null || specs.Count == 0)
            return true;

        for (var i = 0; i < spell.Targets.Count && i < specs.Count; i++)
        {
            // CR 608.2b: a spell fizzles only if *every* target is illegal by the time it
            // resolves, and protection acquired in between is one of the ways that happens.
            // The X it was cast for rides on the object (CR 601.2b), so a filter written around
            // one asks the same question here that it was asked as the target was chosen.
            if (specs[i].IsLegal(
                    State,
                    _abilities,
                    spell.Targets[i],
                    spell.ControllerId,
                    spell,
                    PeersOf(specs, spell.Targets, i),
                    spell.VariableValue))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// An ability's effects, whether it was activated or triggered (CR 602, 603).
    /// </summary>
    /// <remarks>
    /// Both kinds sit on the stack as the same thing (CR 113.7a), so resolution asks one question
    /// and looks in both places rather than caring which it was.
    /// </remarks>
    /// <summary>
    /// Every activated ability a permanent has right now: printed, plus granted (CR 613.1f).
    /// </summary>
    /// <remarks>
    /// Asking the card alone is not enough once layer 6 can add abilities. An Aura that reads
    /// <c>Enchanted land has "{T}: Add {B}"</c> gives that ability to one permanent and not to
    /// the card, so a lookup by card would report it has no such ability and refuse to activate
    /// it. Public because the board asks the same question through the view.
    /// </remarks>
    public static IReadOnlyList<ActivatedAbilityDefinition> ActivatedAbilitiesOf(
        GameState state, IAbilitySource abilities, GameObject obj)
    {
        ArgumentNullException.ThrowIfNull(abilities);
        ArgumentNullException.ThrowIfNull(obj);

        // CR 707.2: a face-down permanent has no abilities at all. Its own are gone with the
        // rest of the card; anything granted to it in layer 6 still applies, which is why this
        // asks the computed characteristics rather than returning nothing outright.
        var printed = obj.Permanent is { IsFaceDown: true } ? [] : abilities.ActivatedOf(obj.Card);

        // Only a permanent can be granted anything: the layers are computed for objects on the
        // battlefield, and a card in hand has whatever its card says and nothing more.
        if (obj.Zone != Zone.Battlefield)
            return printed;

        var now = Characteristics.Of(state, abilities, obj);

        // CR 707.2a: a copy has the copied card's abilities and *not* its own as well — "it
        // doesn't wind up with two values of each ability". Which card those come from is layer
        // 1's answer and nothing else's, so this asks the computed characteristics rather than
        // the object's own card. Asking the object's card left a Clone of a Wall of Omens
        // offering both cards' abilities at once.
        if (obj.Permanent is not { IsFaceDown: true })
            printed = abilities.ActivatedOf(now.Card);

        // CR 613.1f: an effect that removes all abilities takes the printed ones with it. The
        // granted ones are not cleared here - the layers have already settled which of those
        // survive, because a grant that applied after the removal still applies and one that
        // applied before does not (CR 613.7). Same shape as the face-down rule above.
        // CR 305.7 does the same thing to a land whose types were set to a basic land type, and
        // only to its printed half: "it loses all abilities generated from its rules text ...
        // this doesn't remove any abilities that were granted to the land by other effects". The
        // mana ability the retyping itself grants is one of those, and it is already in the
        // granted list below, so it survives exactly as the rule says it should.
        if (now.HasLostAllAbilities || now.HasLostPrintedAbilities)
            printed = [];

        var granted = now.GrantedActivated;
        return granted.IsEmpty ? printed : [.. printed, .. granted];
    }

    /// <summary>
    /// Every triggered ability watching on behalf of one object, granted ones included.
    /// </summary>
    /// <remarks>
    /// A card-keyed lookup cannot see an ability the card does not have, which is the same trap
    /// the activated abilities were in: an Aura reading <c>enchanted creature has "when this
    /// creature dies, draw a card"</c> puts the trigger on the creature, and nothing about the
    /// creature's card says so.
    /// <para>
    /// Only a permanent can have been granted anything, for the reason
    /// <see cref="ActivatedAbilitiesOf"/> gives: the layers are computed for the battlefield, and
    /// a card in hand has what its card says and nothing more.
    /// </para>
    /// </remarks>
    private IReadOnlyList<TriggeredAbilityDefinition> TriggersWatching(
        GameState state, GameObject obj)
    {
        // A dungeon in the command zone (CR 309.2b) has no card the compiler ever read - dungeon
        // cards are legal in no format and never enter the corpus - so its rooms come from the
        // rules, like the Ring's abilities. They are written down the way a granted trigger is,
        // because the ability goes on the stack as an id and the card it will be looked up on
        // does not have it.
        if (Dungeons.RoomAbilitiesOf(obj) is { Count: > 0 } rooms)
        {
            foreach (var room in rooms)
                _grantedTriggersOnStack[GrantedKey(obj.Id, room.Id)] = room;

            return rooms;
        }

        // An emblem's abilities function in the command zone (CR 114.4), and the compiler has no
        // way to know that: it read the quoted text as a card's text and wrote the battlefield on
        // it, because that is where all but a handful of printed abilities work. Re-keyed rather
        // than compiled apart, so the sentence stays one sentence - see Emblems.TriggersOf.
        if (Emblems.IsEmblem(obj))
            return Emblems.TriggersOf(obj, _abilities);

        var printed = _abilities.TriggersOf(obj.Card);

        if (obj.Zone != Zone.Battlefield)
            return printed;

        var now = Characteristics.Of(state, _abilities, obj);

        // CR 707.2a: the copied card's triggers are the permanent's triggers, in place of its
        // own rather than beside them. The same one-card question the activated abilities ask.
        //
        // Not while it is face down. CR 708.10 says a face-down permanent that becomes a copy
        // keeps the characteristics its face-down status gives it, so there is nothing to read
        // from the copy - and the card underneath still has to be offered here, because
        // disguise's ward is found on it and thrown out by the FunctionsFaceDown test in
        // Consider rather than by this lookup.
        var copied = obj.Permanent is not { IsFaceDown: true }
            && !ReferenceEquals(now.Card, obj.Card);
        if (copied)
            printed = _abilities.TriggersOf(now.Card);

        // CR 613.1f: an effect that removes all abilities takes the printed ones with it. The
        // granted ones are not cleared here - the layers have already settled which of those
        // survive, because a grant that applied after the removal still applies and one that
        // applied before does not (CR 613.7). Same shape as the face-down rule above.
        // CR 305.7, the same way: a Dark Depths that Blood Moon has turned into a Mountain stops
        // watching for its own counters, because that trigger is generated from its rules text.
        if (now.HasLostAllAbilities || now.HasLostPrintedAbilities)
            printed = [];

        // A copied trigger is on the stack as an id against a permanent whose own card has never
        // heard of it, so it has to be written down here exactly as a granted one is - the copy
        // effect could have ended by the time the ability resolves, and CR 707.11 says the
        // ability still does what it said.
        if (copied)
        {
            foreach (var trigger in printed)
                _grantedTriggersOnStack[GrantedKey(obj.Id, trigger.Id)] = trigger;
        }

        // CR 603.2b gives this as its own example: with "all creatures lose all abilities" on
        // the battlefield, a creature entering does not trigger its own enters ability.
        var granted = now.GrantedTriggers;
        if (granted.IsEmpty)
            return printed;

        // Written down for the same reason an activated one is: the ability goes on the stack as
        // an id, and the card it will be looked up on does not have it.
        foreach (var trigger in granted)
            _grantedTriggersOnStack[GrantedKey(obj.Id, trigger.Id)] = trigger;

        return [.. printed, .. granted];
    }

    /// <remarks>
    /// The ability recorded at activation is asked <em>first</em>, and that order is the fix for
    /// a copy whose ability shares an id with the copying card's own. Ids are generated per card
    /// — "a", "a1" — so two cards routinely produce the same one, and a lookup by id on a card
    /// cannot tell which of them was activated. Activated and triggered ids never collide with
    /// each other ("a" against "t"), so the two recorded lookups stay on their own side of the
    /// card lookups rather than shadowing them both.
    /// </remarks>
    private ImmutableList<IEffect> EffectsOfAbility(
        CardDefinition card, string abilityId, ObjectId? sourceId = null) =>
        GrantedOnStack(sourceId, abilityId)?.Effects
        ?? _abilities.ActivatedOf(card)
            .FirstOrDefault(a => string.Equals(a.Id, abilityId, StringComparison.Ordinal))
            ?.Effects
        ?? _abilities.TriggersOf(card)
            .FirstOrDefault(t => string.Equals(t.Id, abilityId, StringComparison.Ordinal))
            ?.Effects
        ?? GrantedTriggerOnStack(sourceId, abilityId)?.Effects
        ?? [];

    /// <summary>The granted ability behind a stack object, if that is what it is.</summary>
    private ActivatedAbilityDefinition? GrantedOnStack(ObjectId? sourceId, string abilityId) =>
        sourceId is { } id
        && _grantedOnStack.TryGetValue(GrantedKey(id, abilityId), out var granted)
            ? granted
            : null;

    /// <summary>The granted trigger behind a stack object, if that is what it is.</summary>
    private TriggeredAbilityDefinition? GrantedTriggerOnStack(
        ObjectId? sourceId, string abilityId) =>
        sourceId is { } id
        && _grantedTriggersOnStack.TryGetValue(GrantedKey(id, abilityId), out var granted)
            ? granted
            : null;

    /// <summary>
    /// The spell a stack object actually is - its Adventure, if it was cast as one (CR 715.3b).
    /// </summary>
    /// <remarks>
    /// The table answers first; the two facts after it are the same answer read from the state,
    /// so a game resumed with a cleaved or promised spell still on the stack resolves the
    /// reading that was paid for. The adventure and split-card choices have no such fact yet,
    /// which is a recorded gap and not a licence to guess here.
    /// <para>
    /// An "any number of target ..." block is expanded here and nowhere else, against the number
    /// of targets on the stack object. That is the count the caster announced (CR 601.2c) and it
    /// cannot have changed since, so every caller below — the effects that run, the specs the
    /// fizzle check reads — sees one ordinary counted spell. Doing it in the lookup rather than
    /// at each call site is what stops the two disagreeing: a spell whose effects were expanded
    /// to three and whose specs were not would deal its third pump to nothing.
    /// </para>
    /// </remarks>
    private SpellDefinition? SpellBeingCast(GameObject spell) =>
        Announced(
            _castAs.TryGetValue(spell.Id, out var chosen)
                ? chosen.Spell
                : spell.WasCleaved && _abilities.CleaveSpellOf(spell.Card) is { } cloven
                ? cloven
                : spell.GiftedTo is not null && _abilities.GiftSpellOf(spell.Card) is { } promised
                ? promised
                : _abilities.SpellOf(spell.Card),
            spell.Targets.Count);

    /// <summary>The spell as it was cast, once a variable target block is counted out.</summary>
    private static SpellDefinition? Announced(SpellDefinition? definition, int targets)
    {
        if (definition is null || !VariableTargets.Present(definition.Targets))
            return definition;

        var (specs, effects) =
            VariableTargets.Expand(definition.Targets, definition.Effects, targets);

        return definition with { Targets = specs, Effects = effects };
    }

    /// <summary>
    /// Pays a locked half's mana cost to open that door (CR 116.2m, 709.5e).
    /// </summary>
    /// <remarks>
    /// A special action, not an ability: it does not use the stack and nobody may respond to it.
    /// The timing is written into the rule rather than into a keyword - "any time they have
    /// priority and the stack is empty during a main phase of their turn" - which is sorcery
    /// speed said the long way.
    /// </remarks>
    public void UnlockDoor(Guid playerId, ObjectId permanentId, int half)
    {
        RequirePriority(playerId);

        var room = State.GetObject(permanentId);

        if (room.Zone != Zone.Battlefield || room.ControllerId != playerId)
        {
            throw new InvalidOperationException(
                "Only the controller of a permanent may unlock its doors (CR 709.5e).");
        }

        if (!State.CurrentStep.IsMainPhase()
            || !State.Stack.IsEmpty
            || State.ActivePlayerId != playerId)
        {
            throw new InvalidOperationException(
                "A door is unlocked at sorcery speed (CR 116.2m).");
        }

        var door = _abilities.HalvesOf(room.Card).FirstOrDefault(h => h.Index == half)
            ?? throw new InvalidOperationException(
                $"{room.Card.Name} has no door {half}.");

        if (room.Permanent?.UnlockedHalves.Contains(half) == true)
            throw new InvalidOperationException("That door is already unlocked (CR 709.5c).");

        PayMana(playerId, ManaCostSpec.Parse(door.ManaCostRaw));
        Emit(new HalfUnlocked(permanentId, half));

        SettleBeforePriority();
        Emit(new PriorityGranted(playerId));
    }

    /// <summary>The triggered ability a stack object came from, or null.</summary>
    private TriggeredAbilityDefinition? TriggerBehind(CardDefinition card, string abilityId) =>
        _abilities.TriggersOf(card)
            .FirstOrDefault(t => string.Equals(t.Id, abilityId, StringComparison.Ordinal));

    /// <remarks>The order <see cref="EffectsOfAbility"/> uses, and for the same reason.</remarks>
    private ImmutableList<TargetSpec>? TargetsOfAbility(
        CardDefinition card, string abilityId, ObjectId? sourceId = null) =>
        GrantedOnStack(sourceId, abilityId)?.Targets
        ?? _abilities.ActivatedOf(card)
            .FirstOrDefault(a => string.Equals(a.Id, abilityId, StringComparison.Ordinal))
            ?.Targets
        ?? _abilities.TriggersOf(card)
            .FirstOrDefault(t => string.Equals(t.Id, abilityId, StringComparison.Ordinal))
            ?.Targets
        ?? GrantedTriggerOnStack(sourceId, abilityId)?.Targets;

    /// <summary>
    /// What a cost is about to take, for the sentence that names it afterwards (CR 608.2k).
    /// </summary>
    /// <remarks>
    /// Read before anything is paid and not after, which is the whole of the difficulty. "Where X
    /// is the sacrificed creature's power" is answered while the ability resolves, by which time
    /// the creature is a card in a graveyard under a new id (CR 400.7) whose power is the printed
    /// one — so a 2/2 sacrificed under an anthem would answer 2 where the game was looking at a
    /// 4/4. CR 608.2h asks for the object's last known information, and this is the last moment
    /// it exists.
    /// <para>
    /// <b>Exactly one, or nothing.</b> Every printed sentence in this family is singular — "the
    /// sacrificed creature", "the exiled card" — and a cost that took two has not said which one
    /// is meant. Answering with either would be a guess, and answering with the sum would be a
    /// number no card prints, so the payment is not recorded at all and the reader that wanted it
    /// falls back to nought. That is the same singular discipline
    /// <see cref="Abilities.TouchFilter.StatIn"/> keeps for the objects a resolution moves.
    /// </para>
    /// <para>
    /// Off the layers for a permanent and off the card for anything else: a card exiled from a
    /// graveyard to pay for an ability has never been subject to a continuous effect, and its
    /// printed characteristics are its real ones (CR 108.3).
    /// </para>
    /// </remarks>
    private CostPaid? WhatTheCostTakes(IReadOnlyList<ObjectId> taking)
    {
        if (taking.Count != 1 || !State.TryGetObject(taking[0], out var paying))
            return null;

        if (paying.Zone != Zone.Battlefield)
            return new CostPaid(paying.Card.Power, paying.Card.Toughness, paying.Card.Cmc);

        var now = Characteristics.Of(State, _abilities, paying);

        // The card the layers settled on rather than the printed one, for the mana value as well
        // as the size: a permanent that has become a copy is the card it copied (CR 613.2c), and
        // sacrificing a Clone of a Dragon pays with the Dragon's mana value.
        return new CostPaid(now.Power, now.Toughness, now.Card.Cmc);
    }

    /// <summary>Runs a resolving object's effects in order (CR 608.2c).</summary>
    /// <summary>
    /// Runs a set of effects as though they were the rest of a resolution.
    /// </summary>
    /// <remarks>
    /// The subject is passed rather than read off the source, because a deferred branch runs
    /// against the permanent behind the ability and a permanent carries no ability — so anything
    /// the trigger knew about the event that fired it has to be handed back in.
    /// </remarks>
    private void RunEffects(
        ImmutableList<IEffect> effects,
        GameObject source,
        ObjectId? subjectObject = null,
        int? subjectAmount = null,
        Guid? subjectPlayer = null)
    {
        if (effects.Count == 0)
            return;

        // A deferred branch can know how much its question was about — the number a die roll
        // came up — when the object it runs against remembers nothing, having never been the
        // ability that asked. Handed in for the same reason the subject object is.
        var about = subjectAmount ?? source.Ability?.SubjectAmount;

        var context = new ResolutionContext
        {
            State = State,
            Abilities = _abilities,
            ControllerId = source.ControllerId,
            SourceId = source.Id,
            AbilityId = source.Ability?.AbilityId,
            Targets = source.Targets,
            VariableValue = source.VariableValue,

            // What the cost took, carried the same way the announced X is and for the same
            // reason: both were settled while this object was being put on the stack, and by the
            // time it resolves nothing else in the game remembers either (CR 608.2k).
            CostPaid = source.CostPaid,
            Division = source.Division,
            SubjectPlayer = subjectPlayer ?? source.Ability?.SubjectPlayer,
            SubjectAmount = about,
            SubjectObject = subjectObject ?? source.Ability?.SubjectObject,
            ControllerBehind = ControllerBehind,
            ObjectBehind = ObjectBehind,
        };

        // "Each opponent loses 2 life and you gain that much life." The amount is not the
        // triggering event's — a spell has no triggering event — it is what the sentence before
        // this one just did. Carried forward here rather than looked up afterwards, because by
        // then the life totals have moved on and the number is gone.
        var produced = about;

        // What the effects so far have done, for a later sentence that says "this way"
        // (CR 608.2). The magnitude beside it has been carried forward since the day the loop was
        // written; this is the things themselves, which is what the other four printed
        // back-references ask about.
        var record = ResolutionRecord.Empty;

        // How much of the damage this resolution has dealt was excess (CR 120.4a) - the third
        // back-reference, beside the magnitude and the record. It is derived from the events the
        // way both of those are, so nothing about it is stored and nothing about it is logged:
        // whatever the next sentence does with the number lands in the log as its own event.
        var excess = (Any: 0, ToCreature: 0);

        foreach (var effect in effects)
        {
            // Each effect sees the state the previous one left behind (CR 608.2c), so the
            // context is rebuilt rather than captured once.
            var running = context with
            {
                State = State,
                SubjectAmount = produced,
                Record = record,
                ExcessDealt = excess.Any,
                ExcessDealtToCreature = excess.ToCreature,
            };

            var emitted = effect.Resolve(running);

            // Measured against the state from before the batch is applied, which is the state
            // CR 120.4a asks "what would be lethal" about. An effect that marked no damage at
            // all leaves the number where it was; one that marked damage replaces it, because
            // "excess damage dealt this way" points at the sentence that dealt the damage and
            // not at the sum of every sentence that ever did.
            if (ExcessDamage.InBatch(running, emitted) is { } over)
                excess = over;

            // Read before the batch is applied, because the record keeps each object's card and
            // an id that has moved names nothing afterwards (CR 400.7). The id it *stores* is
            // the new one, which is the object that will exist once these events have landed.
            record = record.Following(emitted, CardMoving);

            // One effect's events happen together (CR 608.2c), which is the batch a "one or
            // more" trigger is written about: "exile three cards from your graveyard" is one
            // occurrence of cards leaving it, not three.
            AsOneBatch(() =>
            {
                foreach (var e in emitted)
                    Emit(e);
            });

            // Only when the ability had no amount of its own: a trigger that says how much it
            // was about keeps saying so for every clause, and a running total would quietly
            // replace it partway down the card.
            if (about is not null)
                continue;

            // Life lost and damage dealt, which is what "that much" is written about. A life
            // *gain* is deliberately not counted: it is usually the clause doing the asking, and
            // feeding it back in would make the number grow down the card.
            var magnitude = emitted.Sum(e => e switch
            {
                LifeChanged { Delta: < 0 } lost => -lost.Delta,
                PlayerDamaged hit => hit.Amount,
                DamageMarked marked => marked.Amount,
                _ => 0,
            });

            if (magnitude > 0)
                produced = magnitude;
        }
    }

    /// <summary>
    /// The card an object about to change zones is, for the resolution's own record (CR 608.2).
    /// </summary>
    /// <remarks>
    /// Through the computed characteristics rather than off <c>obj.Card</c>, because a permanent
    /// that has become a copy is the card it copied (CR 613.2c) — a Clone of a Grizzly Bears that
    /// gets swept away is a creature card destroyed this way, and reading the printed card would
    /// answer with whatever the Clone itself is. That is the same mistake this file records
    /// against nine other readers.
    /// <para>
    /// Falls back to the card behind an id the state no longer knows, which is how a token that
    /// has already ceased to exist is still named by the sweep that killed it.
    /// </para>
    /// <para>
    /// The size comes off the <em>computed</em> characteristics while the object is on a
    /// battlefield and off the card everywhere else, which is what CR 608.2h means by last known
    /// information: a 2/2 with three +1/+1 counters on it that gets exiled was a 5/5, and a card
    /// sitting in a graveyard has no layers applied to it at all.
    /// </para>
    /// </remarks>
    private LastKnown? CardMoving(ObjectId id)
    {
        if (!State.TryGetObject(id, out var live))
            return CardBehind(id) is { } gone ? new LastKnown(gone, gone.Power, gone.Toughness) : null;

        var card = Characteristics.CardOf(State, _abilities, live);

        if (live.Zone != Zone.Battlefield)
            return new LastKnown(card, card.Power, card.Toughness);

        var now = Characteristics.Of(State, _abilities, live);
        return new LastKnown(card, now.Power, now.Toughness);
    }

    /// <summary>Notes a scry or surveil the moment its effect asks for it.</summary>
    private void NoteLook(GameEvent e)
    {
        if (e is LookAtTopRequested look)
            _looksOwed.Add(look);

        if (e is DiscardRequested discard)
            _discardsOwed.Add(discard);

        if (e is LookAndTakeRequested lookAndTake)
            _looksAndTakesOwed.Add(lookAndTake);

        if (e is LibrarySearchRequested search)
            _searchesOwed.Add(search);

        if (e is SeekRequested seeking)
            _seeksOwed.Add(seeking);

        if (e is ProliferateRequested proliferate)
            _proliferationsOwed.Add(proliferate);

        if (e is ShuffleRequested shuffling)
            _shufflesOwed.Add(shuffling);

        if (e is DiscoverRequested discovering)
            _discoveriesOwed.Add(discovering);

        if (e is CascadeRequested cascading)
            _cascadesOwed.Add(cascading);

        if (e is RippleRequested rippling)
            _ripplesOwed.Add(rippling);

        // Rebound exiles the spell instead of letting it reach the graveyard, and the moment to
        // say so is now: the trigger resolves while the spell is still on the stack underneath
        // it, which is the only window in which the spell has an id to mark.
        if (e is DelayedTriggerCreated { EffectId: "rebound" } rebounding)
            _exileOnLeavingStack.Add(rebounding.SubjectId);

        // The same window, for the same reason: the spell is on the stack as its own effect asks
        // for this, and marking it now is what lets the move that follows put the counters on
        // whatever it turns into.
        if (e is SuspendOnResolveRequested suspending)
        {
            _exileOnLeavingStack.Add(suspending.SubjectId);
            _suspendOnResolve[suspending.SubjectId] = suspending.Counters;
        }

        // The same window again: the spell is on the stack while its own cipher clause resolves,
        // so the exile is marked now and the encoding is asked for once the card has landed.
        if (e is CipherRequested ciphering)
        {
            _exileOnLeavingStack.Add(ciphering.SubjectId);
            _cipherOnResolve[ciphering.SubjectId] = ciphering.PlayerId;
        }

        if (e is SacrificeUnlessPaidRequested ransom)
            _sacrificeUnlessOwed.Add(ransom);

        if (e is LibraryEndChoiceRequested filed)
            _libraryEndsOwed.Add(filed);

        // CR 702.94a: revealed as it is drawn, and only for the first card drawn this turn.
        //
        // Read off the *new* id and after the count has moved: this hook sees events that have
        // already been applied, so the card that left the library no longer exists under the id
        // it had there (CR 400.7), and this draw is already counted - which is why "the first
        // card drawn this turn" is a count of one rather than of none.
        if (e is ObjectMoved { To: Zone.Hand, Cause: MoveCause.Draw } drawn
            && State.TryGetObject(drawn.NewId, out var pulled)
            && State.GetPlayer(pulled.OwnerId).CardsDrawnThisTurn == 1
            && _abilities.MiracleCostOf(pulled.Card) is { } miracle)
        {
            _miraclesOwed.Add((drawn.NewId, pulled.OwnerId, miracle.ToString()));
        }

        if (e is HandChoiceRequested rummaging)
            _handChoicesOwed.Add(rummaging);

        if (e is ManaColorChoiceRequested manaColour)
            _manaColorChoicesOwed.Add(manaColour);

        if (e is DamageSourceChoiceRequested shielding)
            _damageSourceChoicesOwed.Add(shielding);

        if (e is ColorChoiceRequested naming)
            _colorChoicesOwed.Add(naming);

        if (e is CardNameChoiceRequested namingACard)
            _cardNameChoicesOwed.Add(namingACard);

        if (e is CreatureTypeChoiceRequested retyping)
            _creatureTypeChoicesOwed.Add(retyping);

        if (e is LandTypeChoiceRequested reshaping)
            _landTypeChoicesOwed.Add(reshaping);

        if (e is ConniveRequested conniving)
            _connivesOwed.Add(conniving);

        if (e is ManifestDreadRequested dreading)
            _manifestDreadsOwed.Add(dreading);

        if (e is TouchedChoiceRequested taking)
            _touchedChoicesOwed.Add(taking);

        if (e is PopulateRequested populating)
            _populatesOwed.Add(populating);

        if (e is ExploitRequested exploiting)
            _exploitsOwed.Add(exploiting);

        if (e is SoulbondPairRequested pairing)
            _soulbondsOwed.Add(pairing);

        if (e is UntapChoiceRequested untapping)
            _untapChoicesOwed.Add(untapping);

        if (e is CounterChoiceRequested growing)
            _counterChoicesOwed.Add(growing);

        if (e is LibraryOrderRequested arranging)
            _libraryOrdersOwed.Add(arranging);

        if (e is ChoosePermanentRequested choosing && FindPermanentChoice(choosing) is { } chooser)
            _choicesOwed.Add((choosing, chooser));

        if (e is RingBearerRequested tempted)
            _ringBearersOwed.Add(tempted);

        if (e is VentureRoomRequested venturing)
            _venturesOwed.Add(venturing);

        if (e is ClashRequested clashing && FindClash(clashing) is { } clash)
        {
            var aimedAtClash = State.TryGetObject(clashing.SourceId, out var clasher)
                ? clasher.Targets
                : [];

            _clashesOwed.Add((clashing, clash, aimedAtClash));
        }

        if (e is CoinFlipRequested flip && FindFlip(flip) is { } coin)
        {
            var aimed = State.TryGetObject(flip.SourceId, out var flipper) ? flipper.Targets : [];
            _flipsOwed.Add((flip, coin, aimed));
        }

        // Off the request and not off the source: the source named here is the permanent whose
        // ability rolled, and the targets were chosen by the ability on the stack (CR 601.2c).
        if (e is DiceRollRequested roll && FindRoll(roll) is { } dice)
            _rollsOwed.Add((roll, dice, roll.Targets));

        // Resolved here, while the source is still where the locator says it is — and its
        // targets are taken with it, because a spell loses them when it leaves the stack and the
        // "if you don't" branch is usually about the very thing it was aimed at.
        if (e is OptionalPaymentRequested payment && FindOptionalPayment(payment) is { } offer)
            _paymentsOwed.Add((payment, offer, payment.Targets));
    }

    /// <summary>
    /// Gains life for the controller of a source with lifelink (CR 702.15b).
    /// </summary>
    /// <remarks>
    /// Lifelink is not a trigger and does not use the stack: the life gain happens at the same
    /// time as the damage, as part of the same event. So it is done here, as the damage lands,
    /// rather than by watching for it afterwards.
    /// <para>
    /// It applies to damage of every kind, not only combat — a lifelinked creature that pings
    /// gains life too.
    /// </para>
    /// </remarks>
    private void GainForLifelink(ObjectId sourceId, int amount)
    {
        if (amount <= 0 || sourceId == default || !State.TryGetObject(sourceId, out var source))
            return;

        if (!Characteristics.Of(State, _abilities, source).Has(KeywordAbility.Lifelink))
            return;

        var controller = source.ControllerId;
        Emit(new LifeChanged(controller, amount, State.GetPlayer(controller).Life + amount));
    }

    /// <summary>
    /// Who controls a permanent right now, after any control-changing effect (CR 613.1b).
    /// </summary>
    /// <remarks>
    /// Control is layer 2, so the controller stored on the object is only where it started. Every
    /// question of the form "is this yours" has to ask this instead, and two of them did not: a
    /// lord's filter and the target grammar's ownership clause both read the stored value, which
    /// made a stolen creature invisible to its new controller's static abilities and still
    /// vulnerable to sweepers aimed at its old one.
    /// </remarks>
    private Guid ControllerOf(GameObject obj) =>
        obj.Zone == Zone.Battlefield
            ? Characteristics.Of(State, _abilities, obj).ControllerId
            : obj.ControllerId;

    /// <summary>Whether this object is the given player's commander (CR 903.3).</summary>
    /// <remarks>
    /// Compared by oracle id, because being a commander belongs to the card and survives every
    /// zone change (CR 903.3) while an object's identity deliberately does not (CR 400.7).
    /// </remarks>
    private bool IsCommanderOf(Guid playerId, GameObject obj) =>
        State.GetPlayer(playerId).CommanderOracleId is { } oracleId
        && string.Equals(obj.Card.OracleId, oracleId, StringComparison.Ordinal);

    /// <summary>
    /// Records combat damage a commander dealt to a player (CR 903.10a).
    /// </summary>
    /// <remarks>
    /// Twenty-one from the same commander over the whole game is a loss, so the running total is
    /// kept per commander rather than per creature — a commander that dies and comes back is a
    /// new object each time, and the damage still accumulates.
    /// </remarks>
    private void TrackCommanderDamage(PlayerDamaged damage)
    {
        if (!damage.IsCombat || !State.TryGetObject(damage.SourceId, out var source))
            return;

        var owner = source.OwnerId;
        if (State.GetPlayer(owner).CommanderOracleId is not { } oracleId)
            return;

        if (!string.Equals(source.Card.OracleId, oracleId, StringComparison.Ordinal))
            return;

        var already = State.GetPlayer(damage.PlayerId).CommanderDamage.GetValueOrDefault(oracleId);
        Emit(new CommanderDamageDealt(
            damage.PlayerId, oracleId, damage.Amount, already + damage.Amount));
    }

    /// <summary>Card types that exist on the battlefield (CR 110.4, 205.2a).</summary>
    private static bool IsPermanentCard(CardDefinition card) =>
        IsPermanentType(card.CardTypes);

    /// <summary>Whether these types make a permanent (CR 110.1).</summary>
    /// <remarks>
    /// Asked of types rather than of a card, because half of a split card has its own types and
    /// no card of its own: the question at resolution is what is on the stack, not what is
    /// printed across the whole piece of cardboard.
    /// </remarks>
    private static bool IsPermanentType(CardType types) =>
        (types & (CardType.Creature | CardType.Artifact | CardType.Enchantment
            | CardType.Land | CardType.Planeswalker | CardType.Battle)) != 0;

    /// <summary>Who started the game, for CR 103.8a. Read from the log, which cannot drift.</summary>
    private Guid FirstPlayerId => ((GameStarted)_log[0]).StartingPlayerId;

    /// <summary>What the given player may see (CR 400.2).</summary>
    public GameView ViewFor(Guid playerId) =>
        PlayerViewProjector.Project(State, playerId, _abilities);

    private void Emit(GameEvent e) => Emit(e, []);

    /// <summary>The answer that declines every optional replacement on the held event.</summary>
    private const string DeclineReplacement = "decline";

    /// <summary>The event held while its controller decides which replacement applies first.</summary>
    private GameEvent? _heldEvent;
    private HashSet<(ObjectId, string)> _heldApplied = [];
    private string? _replacementOrder;

    /// <summary>
    /// Re-emits the event that was held while a replacement-order question was outstanding.
    /// </summary>
    private void ReplayHeldEvent()
    {
        var held = _heldEvent;
        var applied = _heldApplied;
        _heldEvent = null;
        _heldApplied = [];

        if (held is not null)
            Emit(held, applied);
    }

    /// <param name="applied">
    /// Replacement effects already used on this event. CR 614.5: each applies only once to a
    /// given event, and that carries down to whatever replaced it — otherwise an effect that
    /// replaces damage with damage would replace its own output forever.
    /// </param>
    private void Emit(GameEvent e, HashSet<(ObjectId, string)> applied)
    {
        // CR 119.7: a player who can't gain life doesn't, and this is not a replacement effect -
        // the rule says outright that a replacement effect which would replace the life gain
        // "won't do anything", so the ban sits above the pass rather than inside it. Modelled as
        // a candidate it would be handed to CR 616.1's ordering question, which is a question
        // about which effect to apply first and not one anybody may answer about a prohibition.
        // Zero is not a life gain event at all (CR 119.9), so the guard asks for more than none.
        if (e is LifeChanged { Delta: > 0 } gain && CannotGainLife(gain.PlayerId))
            return;

        var candidates = Replacements(e, applied).ToList();

        // CR 616.1: when more than one replacement effect applies, the affected object's
        // controller chooses which to apply first, and the rest are re-examined afterwards.
        // The engine used to take them in timestamp order, which is a legal order and the
        // wrong one whenever the two effects do different things — the rules' own example is
        // "exile it instead" against "shuffle it into its library instead", where the choice
        // decides where the card ends up.
        // CR 614.1b: an optional replacement is a question even when it is the only one that
        // applies, which is the difference from the ordering question below - there, something is
        // going to be applied and the only issue is which first.
        var optional = candidates.Any(c => c.Optional);

        if ((candidates.Count > 1 || optional) && _replacementOrder is null)
        {
            _heldEvent = e;
            _heldApplied = applied;

            var options = candidates
                .Select(c => new ChoiceOption(
                    c.Source.Id.Value.ToString("N") + "|" + c.Id,
                    c.Source.Card.Name + ": " + c.Id))
                .ToList();

            // Declining is only offered when every candidate may be declined. A mandatory
            // replacement still has to happen, and taking it first re-examines the rest - which
            // is how "decline the optional one but not the other" is said here.
            if (candidates.All(c => c.Optional))
                options.Add(new ChoiceOption(DeclineReplacement, "Let the event happen instead."));

            Ask(new PendingChoice
            {
                Id = "replace:" + candidates.Count + ":" + e.GetType().Name,
                PlayerId = AffectedPlayer(e),
                Kind = ChoiceKind.OrderReplacements,
                Prompt = optional
                    ? "A replacement effect may apply. Choose whether to apply it."
                    : "More than one replacement effect applies. Choose which to apply first.",
                Options = [.. options],
            });
            return;
        }

        if (_replacementOrder is not null)
        {
            var wanted = _replacementOrder;
            _replacementOrder = null;

            if (string.Equals(wanted, DeclineReplacement, StringComparison.Ordinal))
            {
                // CR 614.1b: declining means the event happens as it was - unless the effect
                // says what happens instead when it is declined, which a shockland does. Only
                // read with a single candidate: with several, "declined" is a question about all
                // of them and one effect's answer is not the answer.
                if (candidates is [{ Decline: not null } only])
                {
                    _log.Add(new EventReplaced(only.Id + ":declined", e.Describe()));
                    applied.Add((only.Source.Id, only.Id));

                    foreach (var instead in only.Decline(e, State, only.Source))
                        Emit(instead, applied);

                    return;
                }

                candidates = [];
            }
            else
            {
                candidates = [.. candidates.Where(c =>
                    string.Equals(c.Source.Id.Value.ToString("N") + "|" + c.Id, wanted, StringComparison.Ordinal))];
            }
        }

        foreach (var replacement in candidates.Take(1))
        {
            // CR 614.1: the event never happens. What happens instead is emitted in its place,
            // and because the original did not occur, nothing triggers off it (CR 603.2g).
            _log.Add(new EventReplaced(replacement.Id, e.Describe()));
            applied.Add((replacement.Source.Id, replacement.Id));
            foreach (var instead in replacement.Replace(e, State, replacement.Source))
                Emit(instead, applied);

            return;
        }

        // CR 613.1b: control is layer 2, so the controller stored on the departing object is only
        // where its control started - a stolen permanent that dies would be filed against the
        // player it was taken from. The fold cannot work the current one out, because it has no
        // ability source; this does, and this is the one path every event takes on its way into
        // both the state and the log, so a replay reads the same answer rather than recomputing
        // it against a board that has moved on.
        if (e is ObjectMoved { From: Zone.Battlefield, LeavingControllerId: null } leaving)
            e = leaving with { LeavingControllerId = ControllerOf(State.GetObject(leaving.OldId)) };

        // CR 717.6: a card with an Astrotorium back that would be put into any zone other than
        // the battlefield, exile or the command zone goes to the command zone instead - the pile
        // the reminder text on Draconian Gate-Bot and Down for Repairs calls a "junkyard", which
        // CR 717.6a is explicit is not a zone of its own. Without it a destroyed Attraction lands
        // in a graveyard, where it can be counted, recurred and targeted by cards that have never
        // been able to see one, and the reminder text printed on the card destroying it would be
        // describing something the engine does not do.
        //
        // A rewrite of the destination rather than a replacement candidate, and that is the whole
        // difference: the move still happens and everything watching a permanent leave the
        // battlefield still sees it leave. Only where it lands changes, which is what the rule
        // says. It sits on the one path every event takes, because a removal effect builds its
        // own ObjectMoved and there is no single caller to correct.
        if (e is ObjectMoved astrotorium
            && astrotorium.To is not (Zone.Battlefield or Zone.Exile or Zone.Command)
            && State.TryGetObject(astrotorium.OldId, out var junked)
            && Attractions.Is(junked.Card))
        {
            e = astrotorium with { To = Zone.Command, ControllerId = junked.OwnerId };
        }

        // CR 730.3: "if a merged permanent leaves the battlefield, one permanent leaves the
        // battlefield and each of the individual components are put into the appropriate zone."
        // The topmost card travels as the permanent, in the move about to be applied; the cards
        // under it have no move of their own to make, because they were never separate objects
        // while the permanent stood. Without this the whole stack becomes one card the moment it
        // dies, and every card ever mutated onto something is quietly gone from the game.
        //
        // Here rather than in Move, because Move is not the only way a permanent leaves the
        // battlefield: every removal effect builds its own ObjectMoved and emits it. This is the
        // one path all of them come down.
        if (e is ObjectMoved { From: Zone.Battlefield } departing
            && departing.To != Zone.Battlefield
            && State.TryGetObject(departing.OldId, out var coming)
            && !coming.MergedComponents.IsEmpty)
        {
            // The ids are made once, here, and go into the log with the event - a fold decides
            // nothing, and two replays of one game have to produce the same objects.
            Emit(
                new MergedPermanentSeparated(
                    departing.OldId,
                    departing.To,
                    [.. coming.MergedComponents.Select(_ => ObjectId.New())]),
                applied);
        }

        var before = State;
        State = GameReducer.Apply(State, e);
        _log.Add(e);

        if (e is ObjectMoved forwarded)
            _resolvedSources[forwarded.OldId] = forwarded.NewId;

        // A move a "one or more" trigger watches, held back until the batch it belongs to has
        // finished (CR 603.2c). Outside a batch it is a batch of one and is summarised as soon as
        // its own triggers have been collected: a lone discard has to fire the trigger too, and a
        // summary that only appeared inside an explicit scope would leave every route nobody
        // remembered to wrap silently doing nothing.
        ObjectMoved? loneMove = null;
        if (e is ObjectMoved batched && Summarised(batched))
        {
            if (_batchOpen)
                (_movesInBatch ??= []).Add(batched);
            else
                loneMove = batched;
        }

        // CR 903.10a: commander damage accumulates over the whole game, so it is noted as the
        // damage lands rather than reconstructed later from the log.
        if (e is PlayerDamaged damaged)
        {
            TrackCommanderDamage(damaged);
            GainForLifelink(damaged.SourceId, damaged.Amount);
            StealTheCrown(damaged);
            TakeTheInitiativeFromCombat(damaged);
            OfferCipheredCopies(damaged);
            NoteFreerunning(damaged);
        }

        if (e is DamageMarked marked)
            GainForLifelink(marked.SourceId, marked.Amount);

        NoteLook(e);

        // CR 603.2: an ability triggers the moment its event happens, even mid-resolution.
        // Nothing happens yet — the trigger waits (CR 117.2a) — so this only records them.
        CollectTriggers(e, before);

        if (_triggersFound.Count > 0)
        {
            var found = _triggersFound.ToList();
            _triggersFound.Clear();
            foreach (var trigger in found)
            {
                State = GameReducer.Apply(State, trigger);
                _log.Add(trigger);
            }
        }

        // After this event's own triggers, so the summary reaches the game in the order the
        // rules put it: the move happened, then the batch it was part of is over.
        if (loneMove is { } single)
            EmitBatchSummaries([single]);
    }

    // ---- Batched moves (CR 603.2c) ---------------------------------------------------------

    /// <summary>Whether a batch is open, whether or not anything has landed in it yet.</summary>
    /// <remarks>
    /// A flag beside the list rather than the list alone, because the list is allocated lazily:
    /// every effect in the game opens one of these and almost none of them move a card out of a
    /// graveyard or into one, and the corpus soak resolves millions of them.
    /// </remarks>
    private bool _batchOpen;

    /// <summary>Moves collected into the open batch, allocated on the first one that lands.</summary>
    private List<ObjectMoved>? _movesInBatch;

    /// <summary>
    /// Runs work whose moves all happen at once, and says so once when it is done (CR 603.2c).
    /// </summary>
    /// <remarks>
    /// The one place a batch is derived, so the summary and the moves it summarises cannot
    /// disagree - the same discipline <see cref="CombatDamageDealt"/> is built with, one zone
    /// change along. Nested scopes join the outer one rather than opening a second: an effect
    /// that moves cards while another is resolving is part of the same simultaneous event, and
    /// two scopes would fire the trigger twice for one batch, which prints a strictly better card
    /// than the one on the table.
    /// </remarks>
    private void AsOneBatch(Action work)
    {
        ArgumentNullException.ThrowIfNull(work);

        if (_batchOpen)
        {
            work();
            return;
        }

        _batchOpen = true;
        List<ObjectMoved>? batch;

        try
        {
            work();
        }
        finally
        {
            batch = _movesInBatch;
            _batchOpen = false;
            _movesInBatch = null;
        }

        if (batch is not null)
            EmitBatchSummaries(batch);
    }

    /// <summary>Whether a move is one a batched trigger watches.</summary>
    /// <remarks>
    /// Asked here rather than at each scope, so a batch collects only what something is watching
    /// and an ordinary draw never allocates a summary nobody reads.
    /// </remarks>
    private static bool Summarised(ObjectMoved moved) =>
        moved.From == Zone.Graveyard
        || (moved.To == Zone.Graveyard && moved.Cause == MoveCause.Discard);

    /// <summary>
    /// One summary per player per kind for a batch of moves that happened together.
    /// </summary>
    /// <remarks>
    /// Grouped by the card's owner, because both sentences are possessive - "leave <em>your</em>
    /// graveyard", "<em>you</em> discard" - and a hand is its owner's wherever the effect came
    /// from (CR 400.3). Not grouped by destination: "one or more cards leave your graveyard" is
    /// one trigger whether they went to a hand, to exile or to the battlefield, and splitting the
    /// batch by where each went would fire it once per destination.
    /// </remarks>
    private void EmitBatchSummaries(List<ObjectMoved> batch)
    {
        if (batch.Count == 0)
            return;

        foreach (var (owner, ids) in ByOwner(batch, m => m.From == Zone.Graveyard))
            Emit(new CardsLeftGraveyard(owner, ids));

        foreach (var (owner, ids) in ByOwner(
            batch, m => m.To == Zone.Graveyard && m.Cause == MoveCause.Discard))
        {
            Emit(new CardsDiscarded(owner, ids));
        }
    }

    /// <summary>The batch's matching moves, as one list of new ids per owner.</summary>
    /// <remarks>
    /// The new id, not the old one: a zone change makes a new object (CR 400.7), and the new one
    /// is what still exists when a trigger asks what kind of card it was. A move whose ends have
    /// both gone is dropped rather than filed under nobody.
    /// </remarks>
    private List<(Guid Owner, ImmutableList<ObjectId> Ids)> ByOwner(
        List<ObjectMoved> batch, Func<ObjectMoved, bool> wanted)
    {
        var byOwner = new Dictionary<Guid, List<ObjectId>>();

        foreach (var moved in batch)
        {
            if (!wanted(moved))
                continue;

            var owner =
                State.TryGetObject(moved.NewId, out var arrived) ? arrived.OwnerId
                : State.TryGetObject(moved.OldId, out var departed) ? departed.OwnerId
                : (Guid?)null;

            if (owner is not { } whose)
                continue;

            if (!byOwner.TryGetValue(whose, out var ids))
                byOwner[whose] = ids = [];

            ids.Add(moved.NewId);
        }

        return [.. byOwner.Select(pair => (pair.Key, ImmutableList.CreateRange(pair.Value)))];
    }
}
