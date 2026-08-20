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
            BeginTurn();
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
        BeginTurn();
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
    public ObjectId CastSpell(
        Guid playerId,
        ObjectId cardId,
        IReadOnlyList<Target>? targets = null,
        int variableValue = 0)
    {
        RequirePriority(playerId);

        var card = State.GetObject(cardId);
        if (card.Zone != Zone.Hand)
            throw new InvalidOperationException("A spell is cast from hand.");

        if (card.Card.CardTypes.HasFlag(CardType.Land))
            throw new InvalidOperationException("A land is played, not cast (CR 305.1).");

        // CR 117.1a: an instant any time you have priority; anything else only at sorcery speed.
        var isInstant = card.Card.CardTypes.HasFlag(CardType.Instant)
            || Characteristics.Of(State, _abilities, card).Has(KeywordAbility.Flash);
        if (!isInstant && !State.IsSorcerySpeedFor(playerId))
            throw new InvalidOperationException(
                $"{card.Card.Name} can only be cast during your main phase with an empty stack (CR 505.6a).");

        var definition = _abilities.SpellOf(card.Card);
        var chosen = (targets ?? []).ToImmutableList();

        // CR 601.2c: targets are chosen as the spell is cast, and they have to be legal now.
        RequireLegalTargets(definition?.Targets ?? [], chosen, playerId, card.Card.Name);

        // CR 601.2h: the cost is paid last, and a spell whose cost cannot be paid is not cast at
        // all — the game rewinds rather than leaving it half-cast (CR 601.2i, 733).
        var cost = definition?.AlternateCost ?? ManaCostSpec.Parse(card.Card.ManaCostRaw);
        PayMana(playerId, cost, variableValue);

        var stackId = Move(cardId, Zone.Stack, MoveCause.Cast, playerId);
        if (!chosen.IsEmpty || variableValue > 0)
            Emit(new TargetsChosen(stackId, chosen, variableValue));

        Emit(new SpellCastEvent(playerId, stackId, card.Card.Name));
        SettleBeforePriority();
        // CR 117.3c: the caster receives priority again, and the run of passes is broken.
        Emit(new PriorityGranted(playerId));

        return stackId;
    }

    /// <summary>
    /// Activates an ability (CR 602.2). A mana ability resolves immediately and does not use the
    /// stack (CR 605.3b); everything else goes on the stack like a spell.
    /// </summary>
    public ObjectId? ActivateAbility(
        Guid playerId,
        ObjectId sourceId,
        string abilityId,
        IReadOnlyList<Target>? targets = null)
    {
        var source = State.GetObject(sourceId);
        var ability = _abilities.ActivatedOf(source.Card)
            .FirstOrDefault(a => string.Equals(a.Id, abilityId, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"{source.Card.Name} has no ability {abilityId}.");

        // CR 117.1d: a mana ability may be activated whenever a player has priority, and also
        // while they are paying a cost — which is the only reason mana is ever available.
        if (!ability.IsManaAbility)
            RequirePriority(playerId);
        else if (State.IsOver)
            throw new InvalidOperationException("The game is over (CR 104.2).");

        if (source.ControllerId != playerId)
            throw new InvalidOperationException("You do not control that permanent.");

        if (source.Zone != ability.FunctionsFrom)
            throw new InvalidOperationException("That ability does not function from there (CR 602.5).");

        if (ability.RequiresTap)
        {
            var permanent = source.Permanent
                ?? throw new InvalidOperationException("Only a permanent can be tapped for a cost.");

            if (permanent.IsTapped)
                throw new InvalidOperationException("It is already tapped (CR 602.5b).");

            // CR 302.6: a creature's {T} ability needs it to have been around since the turn
            // began. A noncreature permanent has no such restriction.
            if (permanent.HasSummoningSickness
                && Characteristics.Of(State, _abilities, source).IsCreature)
            {
                throw new InvalidOperationException("It has summoning sickness (CR 302.6).");
            }
        }

        var chosen = (targets ?? []).ToImmutableList();
        RequireLegalTargets(ability.Targets, chosen, playerId, ability.Text);

        PayMana(playerId, ability.ManaCost);
        if (ability.RequiresTap)
            Emit(new PermanentTapped(sourceId));

        Emit(new AbilityActivated(playerId, sourceId, ability.Id, ability.Text));

        if (ability.IsManaAbility)
        {
            // CR 605.3b: it resolves immediately, and nobody gets a chance to respond.
            foreach (var production in ability.Produces)
                Emit(new ManaAdded(playerId, production.Color, production.Amount));

            return null;
        }

        var stackId = ObjectId.New();
        Emit(new TriggerPutOnStack(
            stackId, sourceId, source.Card, ability.Id, ability.Text, playerId));

        if (!chosen.IsEmpty)
            Emit(new TargetsChosen(stackId, chosen, 0));

        SettleBeforePriority();
        Emit(new PriorityGranted(playerId));

        return stackId;
    }

    /// <summary>Checks that targets match the specs and are legal right now (CR 601.2c).</summary>
    private void RequireLegalTargets(
        ImmutableList<TargetSpec> specs,
        ImmutableList<Target> chosen,
        Guid playerId,
        string what)
    {
        if (chosen.Count != specs.Count)
        {
            throw new InvalidOperationException(
                $"{what} needs {specs.Count} target(s) and was given {chosen.Count} (CR 601.2c).");
        }

        for (var i = 0; i < specs.Count; i++)
        {
            if (!specs[i].IsLegal(State, _abilities, chosen[i], playerId))
                throw new InvalidOperationException($"Illegal target: {specs[i].Description}.");
        }
    }

    /// <summary>
    /// Pays a mana cost from the player's pool (CR 601.2h), or refuses if it cannot be paid.
    /// </summary>
    private void PayMana(Guid playerId, ManaCostSpec cost, int variableValue = 0)
    {
        if (cost.Symbols.IsEmpty && variableValue == 0)
            return;

        var pool = State.GetPlayer(playerId).ManaPool;
        var remaining = ManaPayment.Pay(pool, cost, variableValue)
            ?? throw new InvalidOperationException(
                $"Not enough mana: {cost} needs more than {pool} (CR 601.2h).");

        Emit(new ManaSpent(playerId, remaining));
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

    /// <summary>
    /// Declares attackers (CR 508.1). Declaring none is a declaration and moves the step along.
    /// </summary>
    /// <param name="attackers">Each attacking creature, and the player it is attacking.</param>
    public void DeclareAttackers(Guid playerId, IReadOnlyDictionary<ObjectId, Guid> attackers)
    {
        ArgumentNullException.ThrowIfNull(attackers);

        if (State.CurrentStep != TurnStep.DeclareAttackers)
            throw new InvalidOperationException("Attackers are declared in the declare attackers step.");

        if (playerId != State.ActivePlayerId)
            throw new InvalidOperationException("Only the active player declares attackers (CR 508.1).");

        if (State.Combat.AttackersDeclared)
            throw new InvalidOperationException("Attackers have already been declared this combat.");

        foreach (var (attackerId, defender) in attackers)
        {
            var reason = CombatRules.CannotAttack(State, _abilities, State.GetObject(attackerId), playerId);
            if (reason is not null)
                throw new InvalidOperationException($"That creature cannot attack: {reason}.");

            if (defender == playerId || State.GetPlayer(defender).HasLost)
                throw new InvalidOperationException("That player cannot be attacked.");
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
            if (!State.Combat.Attackers.TryGetValue(attackerId, out var defender))
                throw new InvalidOperationException("That creature is not attacking.");

            if (defender != playerId)
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

        if (picks.Distinct(StringComparer.Ordinal).Count() != picks.Count)
            throw new InvalidOperationException("The same option was picked twice.");

        Emit(new ChoiceMade(choice.Id, [.. picks]));
        Resume(choice, picks);
    }

    /// <summary>Stops the game and asks (CR 103.5 and friends).</summary>
    private void Ask(PendingChoice choice) => Emit(new ChoiceRequested(choice));

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

            case ChoiceKind.LegendRule:
                KeepLegend(choice, picks[0]);
                break;

            case ChoiceKind.OrderTriggers:
                _triggerOrder[choice.PlayerId] = [.. picks];
                SettleBeforePriority();
                GrantPriorityAfterSettle();
                break;

            case ChoiceKind.OrderReplacements:
                _replacementOrder = picks[0];
                ReplayHeldEvent();
                break;

            case ChoiceKind.DivideCombatDamage:
                RecordDamageDivision(choice, picks);
                break;

            default:
                throw new InvalidOperationException($"No resumption for {choice.Kind}.");
        }
    }

    /// <summary>Gives priority back once a settle that was interrupted has finished.</summary>
    private void GrantPriorityAfterSettle()
    {
        if (State.IsOver || State.IsWaitingForChoice)
            return;

        Emit(new PriorityGranted(State.ActivePlayerId));
    }

    /// <summary>Orders a player's triggers, until they have answered (CR 603.3b).</summary>
    private readonly Dictionary<Guid, List<string>> _triggerOrder = [];

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

    /// <summary>
    /// Asks a player which duplicate legendary permanent to keep, if they have any (CR 704.5j).
    /// </summary>
    /// <returns>True when a question was asked and the settle has to stop.</returns>
    private bool AskLegendRuleIfNeeded()
    {
        var groups = State.Battlefield
            .Select(State.GetObject)
            .Where(o => o.Card.Supertypes.Contains("Legendary", StringComparer.OrdinalIgnoreCase))
            .GroupBy(o => (o.ControllerId, o.Card.Name))
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
            Options = [.. group.Select(o => new ChoiceOption(
                o.Id.Value.ToString("N"), $"{o.Card.Name} ({o.Card.Power}/{o.Card.Toughness})"))],
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

        SettleBeforePriority();
        GrantPriorityAfterSettle();
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
        LifeChanged life => life.PlayerId,
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
            if (mine.Count > 1 && !_triggerOrder.TryGetValue(playerId, out _))
            {
                Ask(new PendingChoice
                {
                    Id = "triggers:" + playerId.ToString("N"),
                    PlayerId = playerId,
                    Kind = ChoiceKind.OrderTriggers,
                    Prompt = "Choose the order your triggered abilities go on the stack. "
                        + "The last one you pick resolves first.",
                    Options = [.. mine.Select(t => new ChoiceOption(t.AbilityId, t.Text))],
                    MinPicks = mine.Count,
                    MaxPicks = mine.Count,
                });
                return;
            }

            if (_triggerOrder.TryGetValue(playerId, out var order))
            {
                mine = [.. order
                    .Select(id => mine.FirstOrDefault(t =>
                        string.Equals(t.AbilityId, id, StringComparison.Ordinal)))
                    .Where(t => t is not null)
                    .Select(t => t!)];
                _triggerOrder.Remove(playerId);
            }

            foreach (var trigger in mine)
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

        // Which state an ability looks at depends on which side of the event its source is on
        // (CR 603.6). An enters-the-battlefield ability triggers on the game as it is *after* the
        // permanent arrived — the object it is on did not exist a moment earlier — while a
        // leaves-the-battlefield ability triggers on the game as it was *before*, because by the
        // time the event has happened the permanent is gone. Looking only at the state before the
        // event, as this did at first, silently loses every ETB trigger there is.
        foreach (var (id, obj) in before.Objects)
            Consider(e, before, id, obj);

        foreach (var (id, obj) in State.Objects)
        {
            if (!before.Objects.ContainsKey(id))
                Consider(e, State, id, obj);
        }
    }

    private void Consider(GameEvent e, GameState state, ObjectId id, GameObject obj)
    {
        foreach (var ability in _abilities.TriggersOf(obj.Card))
        {
            if (obj.Zone != ability.FunctionsFrom)
                continue;

            if (ability.Triggers(e, state, obj))
            {
                _triggersFound.Add(new AbilityTriggered(
                    id, ability.Id, ability.Text, obj.ControllerId));
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

                yield return (effect.Id, source, effect.Replace);
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

    private void EnterStep(TurnStep step)
    {
        if (State.IsOver)
            return;

        // CR 500.5: unspent mana empties as a step or phase ends. Emitted on entering the next
        // one, which is the same moment and the only one the engine has a hook for.
        if (State.TurnOrder.Any(id => !State.GetPlayer(id).ManaPool.IsEmpty))
            Emit(new ManaPoolsEmptied());

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

        // CR 510.1c: an attacker blocked by more than one creature has its damage divided as
        // its controller chooses. The engine used the order the blocks were declared in, which
        // is the defending player's order — the wrong player's — so it is asked for.
        if (AskDamageDivisionIfNeeded())
            return;

        DealCombatDamageNow();
    }

    private void DealCombatDamageNow()
    {
        var firstStrikeStep = State.Combat.DamageStepsDone == 0
            && CombatRules.NeedsFirstStrikeStep(State, _abilities);

        foreach (var damage in CombatRules.AssignCombatDamage(
            State, _abilities, firstStrikeStep, _damageOrder))
        {
            Emit(damage);
        }

        _damageOrder.Clear();
        _damageDivided.Clear();
        Emit(new CombatDamageStepDone());
    }

    /// <summary>
    /// Asks an attacking player how to divide damage among multiple blockers (CR 510.1c).
    /// </summary>
    /// <remarks>
    /// The answer is the blockers in the order damage is assigned to them, each taking lethal
    /// before the next takes any — which is the division the rules require of a trampler
    /// (CR 702.19b) and the one that decides which chump blocker dies. An arbitrary split (two
    /// damage each to two three-toughness blockers, killing neither) is legal and is <em>not</em>
    /// expressible here; that needs an amount per option, and is called out rather than quietly
    /// missing.
    /// </remarks>
    private bool AskDamageDivisionIfNeeded()
    {
        foreach (var (attackerId, _) in State.Combat.Attackers)
        {
            var blockers = State.Combat.BlockersOf(attackerId);
            if (blockers.Count < 2 || _damageDivided.Contains(attackerId))
                continue;

            if (!State.TryGetObject(attackerId, out var attacker))
                continue;

            Ask(new PendingChoice
            {
                Id = "divide:" + attackerId,
                PlayerId = attacker.ControllerId,
                Kind = ChoiceKind.DivideCombatDamage,
                Prompt = $"{attacker.Card.Name} is blocked by {blockers.Count} creatures. "
                    + "Choose the order to assign its damage; each takes lethal before the next.",
                Options = [.. blockers
                    .Where(id => State.TryGetObject(id, out _))
                    .Select(id => new ChoiceOption(
                        id.Value.ToString("N"), State.GetObject(id).Card.Name))],
                MinPicks = blockers.Count(id => State.TryGetObject(id, out _)),
                MaxPicks = blockers.Count(id => State.TryGetObject(id, out _)),
                Context = [attackerId.Value.ToString("N")],
            });

            return true;
        }

        return false;
    }

    private void RecordDamageDivision(PendingChoice choice, IReadOnlyList<string> picks)
    {
        var attackerId = State.Combat.Attackers.Keys.First(
            id => string.Equals(id.Value.ToString("N"), choice.Context[0], StringComparison.Ordinal));

        _damageOrder[attackerId] =
        [
            .. picks.Select(p => State.Combat.BlockersOf(attackerId).First(
                b => string.Equals(b.Value.ToString("N"), p, StringComparison.Ordinal))),
        ];
        _damageDivided.Add(attackerId);

        if (AskDamageDivisionIfNeeded())
            return;

        DealCombatDamageNow();
        SettleBeforePriority();
        GrantPriorityAfterSettle();
    }

    private readonly Dictionary<ObjectId, List<ObjectId>> _damageOrder = [];
    private readonly HashSet<ObjectId> _damageDivided = [];

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

        if (spell.Ability is not null)
        {
            RunEffects(EffectsOfAbility(spell.Card, spell.Ability.AbilityId), spell);

            // CR 608.2m applies to cards. An ability was never a card and has no graveyard to go
            // to: it simply leaves the stack and stops existing.
            Emit(new StackObjectResolved(stackId, spell.Ability.Text));
            Emit(new ObjectCeasedToExist(stackId, Zone.Stack));
            return;
        }

        RunEffects(_abilities.SpellOf(spell.Card)?.Effects ?? [], spell);

        // CR 608.3: a permanent spell becomes a permanent. CR 608.2m: an instant or sorcery is
        // put into its owner's graveyard as the final part of its resolution.
        var destination = IsPermanentCard(spell.Card) ? Zone.Battlefield : Zone.Graveyard;
        Move(stackId, destination, MoveCause.Resolve, spell.ControllerId);

        Emit(new StackObjectResolved(stackId, spell.Card.Name));
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
            ? TargetsOfAbility(spell.Card, spell.Ability.AbilityId)
            : _abilities.SpellOf(spell.Card)?.Targets;

        if (specs is null || specs.Count == 0)
            return true;

        for (var i = 0; i < spell.Targets.Count && i < specs.Count; i++)
        {
            if (specs[i].IsLegal(State, _abilities, spell.Targets[i], spell.ControllerId))
                return true;
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
    private ImmutableList<IEffect> EffectsOfAbility(CardDefinition card, string abilityId) =>
        _abilities.ActivatedOf(card)
            .FirstOrDefault(a => string.Equals(a.Id, abilityId, StringComparison.Ordinal))
            ?.Effects
        ?? _abilities.TriggersOf(card)
            .FirstOrDefault(t => string.Equals(t.Id, abilityId, StringComparison.Ordinal))
            ?.Effects
        ?? [];

    private ImmutableList<TargetSpec>? TargetsOfAbility(CardDefinition card, string abilityId) =>
        _abilities.ActivatedOf(card)
            .FirstOrDefault(a => string.Equals(a.Id, abilityId, StringComparison.Ordinal))
            ?.Targets
        ?? _abilities.TriggersOf(card)
            .FirstOrDefault(t => string.Equals(t.Id, abilityId, StringComparison.Ordinal))
            ?.Targets;

    /// <summary>Runs a resolving object's effects in order (CR 608.2c).</summary>
    private void RunEffects(ImmutableList<IEffect> effects, GameObject source)
    {
        if (effects.Count == 0)
            return;

        var context = new ResolutionContext
        {
            State = State,
            Abilities = _abilities,
            ControllerId = source.ControllerId,
            SourceId = source.Id,
            Targets = source.Targets,
            VariableValue = source.VariableValue,
        };

        foreach (var effect in effects)
        {
            // Each effect sees the state the previous one left behind (CR 608.2c), so the
            // context is rebuilt rather than captured once.
            foreach (var e in effect.Resolve(context with { State = State }))
                Emit(e);
        }
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
        var candidates = Replacements(e, applied).ToList();

        // CR 616.1: when more than one replacement effect applies, the affected object's
        // controller chooses which to apply first, and the rest are re-examined afterwards.
        // The engine used to take them in timestamp order, which is a legal order and the
        // wrong one whenever the two effects do different things — the rules' own example is
        // "exile it instead" against "shuffle it into its library instead", where the choice
        // decides where the card ends up.
        if (candidates.Count > 1 && _replacementOrder is null)
        {
            _heldEvent = e;
            _heldApplied = applied;
            Ask(new PendingChoice
            {
                Id = "replace:" + candidates.Count + ":" + e.GetType().Name,
                PlayerId = AffectedPlayer(e),
                Kind = ChoiceKind.OrderReplacements,
                Prompt = "More than one replacement effect applies. Choose which to apply first.",
                Options = [.. candidates.Select(c => new ChoiceOption(
                    c.Source.Id.Value.ToString("N") + "|" + c.Id, c.Source.Card.Name + ": " + c.Id))],
            });
            return;
        }

        if (_replacementOrder is not null)
        {
            var wanted = _replacementOrder;
            _replacementOrder = null;
            candidates = [.. candidates.Where(c =>
                string.Equals(c.Source.Id.Value.ToString("N") + "|" + c.Id, wanted, StringComparison.Ordinal))];
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
