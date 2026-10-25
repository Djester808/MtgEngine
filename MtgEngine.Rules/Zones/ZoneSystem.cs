using MtgEngine.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.Faces;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Zones;

/// <summary>
/// Zones and the objects in them, CR 400: starting the game, moving an object from one zone to
/// another, drawing, shuffling and creating tokens.
/// <para>
/// Every method builds its state by applying the events it emits and nothing else, so
/// <c>GameReducer.Apply(previous, outcome.Events)</c> equals <c>outcome.State</c> for every
/// outcome this subsystem returns. That is the invariant the whole log-is-the-game design rests
/// on, and <see cref="Tape"/> is what makes it structural rather than remembered.
/// </para>
/// </summary>
[RuleRef("400")]
public sealed partial class ZoneSystem : IZoneSystem
{
    public string Name => "zones";

    /// <summary>Zones a single player owns, CR 400.1. The rest are shared, so their key carries no player.</summary>
    [RuleRef("400.1")]
    public static bool IsOwnerZone(Cards.Zone zone) =>
        zone is Cards.Zone.Library or Cards.Zone.Hand or Cards.Zone.Graveyard;

    /// <summary>The key an object of this owner belongs in, CR 400.1 and 400.3.</summary>
    [RuleRef("400.3")]
    public static ZoneKey KeyFor(Cards.Zone zone, PlayerId owner) =>
        IsOwnerZone(zone) ? ZoneKey.Of(zone, owner) : ZoneKey.Shared(zone);

    /// <summary>
    /// Start the game, CR 103. The deck becomes the library (CR 401.1), the commander goes to the
    /// command zone (CR 103.2c), each library is shuffled (CR 103.3) and each player draws their
    /// opening hand (CR 103.4, CR 402.1).
    /// </summary>
    [RuleRef("103")]
    public Outcome Create(GameSetup setup, IDefinitionStore store)
    {
        ArgumentNullException.ThrowIfNull(setup);
        ArgumentNullException.ThrowIfNull(store);

        var tape = new Tape(GameStarted.Empty);
        tape.Add(new GameStarted(setup.Players, setup.Seed, setup.StartingLife, setup.Format, setup.StartingPlayer));

        for (var seat = 0; seat < setup.Players.Count; seat++)
        {
            var player = new PlayerId(seat);
            var start = setup.Players[seat];

            // CR 401.1: when a game begins, each player's deck becomes their library.
            foreach (var card in start.DeckCards)
                tape.Add(Born(tape.State, Definition(store, card.OracleId), player, Cards.Zone.Library, card));

            // CR 103.2c / 903.6: in a Commander game the commander starts face up in the command zone,
            // and stays a commander through every zone it visits, CR 903.3.
            foreach (var card in start.CommandCards)
            {
                var born = Born(tape.State, Definition(store, card.OracleId), player, Cards.Zone.Command, card);
                tape.Add(born with { Object = born.Object with { IsCommander = true } });
            }
        }

        // CR 103.3: each player shuffles their deck so that the cards are in a random order.
        for (var seat = 0; seat < setup.Players.Count; seat++)
            tape.AddAll(Shuffle(tape.State, new PlayerId(seat)).Events);

        // CR 103.4: each player draws a number of cards equal to their starting hand size.
        for (var seat = 0; seat < setup.Players.Count; seat++)
            tape.AddAll(Draw(tape.State, new PlayerId(seat), setup.OpeningHandSize).Events);

        return tape.Done();
    }

    /// <summary>
    /// Move an object to another zone, CR 400.7: it becomes a new object with a new id and a new
    /// timestamp and no memory of its previous existence, so counters, damage, attachment, status
    /// and any stack entry are gone.
    /// </summary>
    [RuleRef("400.7")]
    public Outcome Move(GameState state, ObjectId what, ZoneKey to, Placement where = Placement.Default, bool tapped = false, PlayerId? controller = null) =>
        Move(state, what, to, where, tapped, controller, null);

    /// <summary>
    /// The same move, showing a chosen face, CR 712.12: a modal double-faced card's land face is
    /// played rather than cast, and only the move can say which face the new object has, because
    /// the move is what builds it (CR 400.7). A null index keeps the face the object had.
    /// </summary>
    [RuleRef("712.12")]
    public Outcome Move(GameState state, ObjectId what, ZoneKey to, Placement where, bool tapped, PlayerId? controller, int? faceIndex) =>
        Move(state, what, to, where, tapped, controller, faceIndex, faceDown: false);

    /// <summary>
    /// Several objects change zones at once, CR 400.7 and 704.5m. Every move is read against the
    /// one board — the state before any of them happened — so an effect watching one object leave
    /// still sees the others; ids and timestamps come from the running state, so no two of them
    /// collide. A move whose replacement stops to ask (CR 616.1) stops the rest of the batch.
    /// </summary>
    [RuleRef("400.7")]
    public Outcome MoveAll(GameState state, IReadOnlyList<MoveRequest> moves) => MoveAll(state, moves, null);

    public Outcome MoveAll(GameState state, IReadOnlyList<MoveRequest> moves, IEnumerable<string>? usedReplacements)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(moves);

        var tape = new Tape(state);
        for (var index = 0; index < moves.Count; index++)
        {
            var m = moves[index];
            if (Proposal(state, tape.State, m.What, m.To, m.Where, m.Tapped, m.Controller, null, faceDown: false) is not { } proposed)
                continue;

            tape.AddAll(ApplyMove(state, tape.State, tape.Resolve(proposed), usedReplacements));
            if (tape.State.Pending is not null)
            {
                var remaining = ImmutableList.CreateBuilder<ObjectMoved>();
                var counters = tape.State;
                foreach (var later in moves.Skip(index + 1))
                {
                    if (Proposal(state, counters, later.What, later.To, later.Where, later.Tapped, later.Controller, null, faceDown: false) is not { } next)
                        continue;
                    remaining.Add(tape.Resolve(next));
                    if (!IsLibraryReposition(next))
                        counters = counters with { NextObjectId = counters.NextObjectId + 1, NextTimestamp = counters.NextTimestamp + 1 };
                }
                tape.Add(new ChoiceRequested(tape.State.Pending with
                {
                    Context = tape.State.Pending.Context with
                    {
                        DeferredZoneMoves = remaining.ToImmutable(),
                        DeferredMoveHistory = usedReplacements is null
                            ? ImmutableDictionary<int, ImmutableHashSet<string>>.Empty
                            : remaining.ToImmutableDictionary(move => move.From.Value,
                                _ => usedReplacements.ToImmutableHashSet(StringComparer.Ordinal)),
                        ZoneMoveSnapshot = ZoneMoveSnapshot.Capture(state),
                    },
                }));
                break;
            }
        }

        return tape.Done();
    }

    /// <summary>
    /// The move as it would happen with nothing to change it, or none when the rules keep the
    /// object where it is. <paramref name="board"/> is the board the move is read against — for a
    /// batch, the one board of CR 400.7 — and <paramref name="counters"/> is where the new
    /// object's id and timestamp come from, which for a batch is the running state.
    /// <paramref name="faceDown"/> is on the proposal and not applied after it, CR 701.40a: the
    /// card is turned face down before it is put onto the battlefield, so what the replacement
    /// effects are offered is already the 2/2 with no name.
    /// </summary>
    [RuleRef("400.7")]
    private static ObjectMoved? Proposal(GameState board, GameState counters, ObjectId what, ZoneKey to, Placement where, bool tapped, PlayerId? controller, int? faceIndex, bool faceDown)
    {
        if (!board.Objects.TryGetValue(what, out var moving))
            return null;

        // CR 400.4a: an instant or sorcery card that would enter the battlefield stays where it is.
        if (to.Zone == Cards.Zone.Battlefield && (PrintedTypes(moving) & (CardType.Instant | CardType.Sorcery)) != 0)
            return null;

        // CR 400.4b: a conspiracy, phenomenon, plane, scheme or vanguard card never leaves the command zone.
        const CardType StaysInCommand = CardType.Conspiracy | CardType.Phenomenon | CardType.Plane | CardType.Scheme | CardType.Vanguard;
        if (moving.Zone.Zone == Cards.Zone.Command && to.Zone != Cards.Zone.Command && (PrintedTypes(moving) & StaysInCommand) != 0)
            return null;

        // CR 400.3: an object headed for a library, graveyard or hand that is not its owner's goes
        // to its owner's instead; and the shared zones take no player in their key, CR 400.1.
        var destination = KeyFor(to.Zone, moving.Owner);

        // A graveyard is an ordered pile, not a freely reorderable library. A request
        // to put a member back in that same pile is not a zone change (CR 400.7, 404.2).
        if (destination.Zone == Cards.Zone.Graveyard && moving.Zone == destination)
            return null;

        // CR 402.3: arranging a hand is presentation, not a zone change or new object.
        if (destination.Zone == Cards.Zone.Hand && moving.Zone == destination)
            return null;

        // Retain the existing object for an order instruction, including when stored in a
        // deferred batch. ApplyMove translates this instruction into ZoneReordered.
        if (destination.Zone == Cards.Zone.Library && moving.Zone == destination)
            return new ObjectMoved(what, moving, where == Placement.Bottom ? -1 : 0, Departed: moving);

        // CR 603.10: the object as it last existed travels with the move, for the abilities that
        // look back. CR 306.5b: what the rules give the object as it enters is on the proposal, so
        // the replacement effects are offered a planeswalker that already has its printed loyalty.
        var arriving = Entering(Reborn(counters, moving, destination, tapped, controller, faceIndex, faceDown));
        return new ObjectMoved(what, arriving, PositionIn(board, destination, where), Departed: moving);
    }

    private static bool IsLibraryReposition(ObjectMoved move) =>
        move.To.Zone.Zone == Cards.Zone.Library && move.LeftFrom == move.To.Zone;

    private ImmutableList<GameEvent> ApplyMove(GameState evaluation, GameState current, ObjectMoved move, IEnumerable<string>? usedReplacements = null)
    {
        if (!IsLibraryReposition(move))
            return Offer(ZoneMoveSnapshot.WithCurrentAllocation(evaluation, current), move, AffectedBy(evaluation, move), usedReplacements);
        var cards = current.In(move.To.Zone);
        var order = Library.LibraryOrder.Place(cards, move.From, move.Position);
        return cards.SequenceEqual(order) ? [] : [new ZoneReordered(move.To.Zone, order)];
    }

    /// <summary>
    /// Draw, CR 121.1: the top card of the library goes to its owner's hand. A draw attempted from
    /// an empty library is recorded rather than performed, CR 121.4; the state-based action in
    /// CR 704.5b is what loses the game for it.
    /// </summary>
    [RuleRef("121")]
    public Outcome Draw(GameState state, PlayerId player, int count, IEnumerable<string>? usedReplacements = null) =>
        new Library.LibraryDrawCoordinator(Replacements).Draw(state, player, count, usedReplacements);

    internal static ObjectMoved? PrepareDraw(GameState state, PlayerId player)
    {
        var library = state.In(ZoneKey.Of(Cards.Zone.Library, player));
        return library.IsEmpty ? null : Proposal(state, state, library[0], ZoneKey.Of(Cards.Zone.Hand, player),
            Placement.Default, tapped: false, controller: null, faceIndex: null, faceDown: false);
    }
    /// <summary>
    /// Shuffle a library, CR 701.24a: randomize it so no player knows the order. The randomness is
    /// the seeded generator in the state, so the same log replays the same shuffle. A library of
    /// zero or one cards is still shuffled, CR 701.24e — the reorder is emitted either way.
    /// </summary>
    [RuleRef("701.24")]
    public Outcome Shuffle(GameState state, PlayerId player, IEnumerable<string>? usedReplacements = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        return new Library.LibraryShuffleCoordinator(Replacements).Shuffle(state, player, usedReplacements);
    }

    /// <summary>
    /// Create a token, CR 111.1: an object that is not a card, on the battlefield, with only the
    /// characteristics the effect gave it. The spec becomes a one-face definition so the rest of
    /// the engine reads a token exactly as it reads a card.
    /// </summary>
    [RuleRef("111")]
    public Outcome CreateToken(GameState state, TokenSpec token, PlayerId controller, bool tapped, bool attacking)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(token);

        var battlefield = ZoneKey.Shared(Cards.Zone.Battlefield);
        // CR 111.2: the token's owner is the player who controls the effect that created it.
        var created = new GameObject(new ObjectId(state.NextObjectId), DefinitionOf(token), 0, controller, controller, battlefield, state.NextTimestamp)
        {
            IsToken = true,
            Tapped = tapped,
        };

        return Arrive(state, created, attacking);
    }

    /// <summary>
    /// A token that is a copy of something, CR 707.2 and 111.4: it has the copiable values of what
    /// it copies and nothing else — no counters, no damage, no status but the tap the effect gave
    /// it — so it arrives through the same door as every other token rather than being built
    /// somewhere else and put on the battlefield by hand.
    /// </summary>
    [RuleRef("707.2")]
    public Outcome CreateToken(GameState state, CopiableValues values, PlayerId controller, bool tapped, bool attacking)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(values);

        var battlefield = ZoneKey.Shared(Cards.Zone.Battlefield);
        var created = CopyRules.NewCopy(values, new ObjectId(state.NextObjectId), controller, controller, battlefield, state.NextTimestamp) with
        {
            Tapped = tapped,
        };

        return Arrive(state, created, attacking);
    }

    /// <summary>
    /// A new object arriving on the battlefield, CR 111.1. The creation is proposed like every
    /// other event (CR 614.1c), so "if you would create a token, create two instead" and the rest
    /// of CR 614 reach a token; a creation replaced away creates nothing to attack with.
    /// </summary>
    [RuleRef("111.1")]
    private Outcome Arrive(GameState state, GameObject created, bool attacking)
    {
        // CR 306.5b: a token that is a copy of a planeswalker enters with its loyalty like any
        // other permanent arriving on the battlefield, and before anything is offered.
        var entering = Entering(created);
        var tape = new Tape(state);
        var happened = Offer(state, new ObjectCreated(entering, -1), entering.InitialController);
        tape.AddAll(happened);

        // CR 506.3c: an effect can put a token onto the battlefield attacking. It joins the combat
        // already happening; with no combat there is nothing to attack and the flag is moot.
        if (attacking
            && happened.Any(e => e is ObjectCreated c && c.Object.Id == entering.Id)
            && tape.State.Combat is { } combat)
        {
            tape.Add(new AttackersDeclared(combat.AttackingPlayer, combat.Attackers.SetItem(entering.Id, DefenderIn(tape.State, combat))));
        }

        return tape.Done();
    }

    /// <summary>The printed types of the face an object is showing, CR 109.3. What a layer added is E7's business; CR 400.4 is about the card.</summary>
    [RuleRef("109.3")]
    private static CardType PrintedTypes(GameObject obj) => obj.Face.Types.Types;

    private static CardDefinition Definition(IDefinitionStore store, string oracleId) =>
        store.ByOracleId(oracleId) ?? throw new InvalidOperationException($"No definition for oracle id {oracleId}.");

    /// <summary>A card entering the game for the first time, CR 103: owner and controller are the player who started with it.</summary>
    [RuleRef("108.3")]
    private static ObjectCreated Born(GameState state, CardDefinition definition, PlayerId owner, Cards.Zone zone, StartingCard card)
    {
        var key = KeyFor(zone, owner);
        var obj = GameObject.CreateCard(new ObjectId(state.NextObjectId), definition, card, owner, key, state.NextTimestamp);
        return new ObjectCreated(obj, -1);
    }

    /// <summary>
    /// The new object a moved object becomes, CR 400.7. Everything that was true of the old object
    /// and is not printed on the card is dropped: counters, damage, attachment, tap and face-down
    /// status, phasing, and anything the stack was holding for it. Face-down status is therefore
    /// the mover's to ask for (CR 701.40a, 702.37c) and is never carried over by itself.
    /// </summary>
    [RuleRef("400.7")]
    internal static GameObject Reborn(GameState state, GameObject moving, ZoneKey destination, bool tapped, PlayerId? controller, int? faceIndex, bool faceDown) =>
        new(new ObjectId(state.NextObjectId), moving.Definition,
            destination.Zone == Cards.Zone.Hand ? FaceRules.FrontFace(moving.Definition)
                : destination.Zone == Cards.Zone.Graveyard ? 0 : FaceFor(moving, faceIndex),
            moving.Owner, ControllerFor(destination, moving, controller), destination, state.NextTimestamp)
        {
            IsToken = moving.IsToken,
            IsCommander = moving.IsCommander,
            DeckSelection = moving.DeckSelection,
            CardInstanceId = moving.CardInstanceId,
            Tapped = tapped,
            // CR 404.2: graveyards are face up. CR 712.8a: a double-faced card there
            // uses its front face; an entry instruction cannot preserve its back face.
            // A hidden hand still lets its owner inspect the card (CR 402.3).
            FaceDown = destination.Zone is not (Cards.Zone.Graveyard or Cards.Zone.Hand) && faceDown,

            // CR 302.6: a creature entering the battlefield has not been controlled since the turn
            // began. In every other zone the flag has nothing to say.
            SummoningSick = true,
        };

    /// <summary>
    /// Who controls the new object. A library, hand or graveyard is its owner's and has no
    /// controller to speak of, CR 108.4; on the battlefield the mover says, and says nothing when
    /// the object simply carries on under the same control.
    /// </summary>
    /// <summary>
    /// Which face the new object shows, CR 712.12. A null index means the face the object already
    /// had, which is what every zone change other than a chosen modal face wants; an index that
    /// names no face on the card is a bug at the seam, not a rules situation.
    /// </summary>
    [RuleRef("712.12")]
    private static int FaceFor(GameObject moving, int? faceIndex)
    {
        if (faceIndex is not { } index)
            return moving.FaceIndex;
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, moving.Definition.Faces.Count);
        return index;
    }

    [RuleRef("108.4")]
    private static PlayerId ControllerFor(ZoneKey destination, GameObject moving, PlayerId? controller) =>
        IsOwnerZone(destination.Zone) ? moving.Owner : controller ?? moving.InitialController;

    /// <summary>
    /// Where in the destination the object lands. Libraries and graveyards are top-first, so the
    /// top is index 0 and CR 404.1's "on top of its owner's graveyard" is the default; the stack is
    /// bottom-first, so its top is the end. Ordering several objects at once is a choice, CR 401.4,
    /// and belongs to the choice system, so an order placement lands one object at the default.
    /// </summary>
    [RuleRef("400.5")]
    private static int PositionIn(GameState state, ZoneKey destination, Placement where)
    {
        var topIsFirst = destination.Zone is Cards.Zone.Library or Cards.Zone.Graveyard;
        return where switch
        {
            Placement.Top => topIsFirst ? 0 : state.In(destination).Count,
            Placement.Bottom => topIsFirst ? state.In(destination).Count : 0,
            _ => topIsFirst ? 0 : -1,
        };
    }

    /// <summary>
    /// What a token put onto the battlefield attacking is attacking. The seam says only "attacking",
    /// so it joins what the rest of the attackers are attacking, CR 508.1; with none to copy, in a
    /// two-player game there is exactly one player it could be.
    /// </summary>
    [RuleRef("508.1")]
    private static CombatTarget DefenderIn(GameState state, CombatState combat)
    {
        foreach (var declared in combat.Attackers.Values)
            return declared;

        var defender = state.Players.FirstOrDefault(p => p.Id != combat.AttackingPlayer && !p.HasLost);
        return new CombatTarget(defender?.Id, null);
    }

    /// <summary>
    /// A token spec as a one-face definition, CR 111.2: the token has the characteristics the
    /// effect that created it gave it and no others. Power and toughness go back through the
    /// printed-stat form so a token reads like any other face.
    /// </summary>
    [RuleRef("111.2")]
    private static CardDefinition DefinitionOf(TokenSpec token)
    {
        var face = new Face(
            token.Name,
            FaceRole.Main,
            new TypeLine(token.Types, token.Supertypes, token.Subtypes),
            Power: token.Power is { } p ? new PrintedStat(p.ToString(System.Globalization.CultureInfo.InvariantCulture)) : null,
            Toughness: token.Toughness is { } t ? new PrintedStat(t.ToString(System.Globalization.CultureInfo.InvariantCulture)) : null,
            ColorIndicator: token.Colors,
            Abilities: token.Abilities);

        return new CardDefinition(token.ScryfallOracleId ?? string.Empty, token.Name, Layout.Normal, [face]);
    }

    /// <summary>
    /// An outcome under construction. Every event is applied as it is added, so the state a method
    /// returns is by construction the fold of the events it returns and cannot drift from them.
    /// </summary>
    private sealed class Tape(GameState state)
    {
        private readonly ImmutableList<GameEvent>.Builder _events = ImmutableList.CreateBuilder<GameEvent>();
        private readonly Dictionary<ObjectId, ObjectId> _identities = [];

        public GameState State { get; private set; } = state;

        public void Add(GameEvent e)
        {
            State = e.Apply(State);
            if (e is ZoneReordered reordered)
                foreach (var (previous, current) in Library.LibraryIdentity.AfterReorder(reordered.Order, State.In(reordered.Zone)))
                    _identities[previous] = current;
            _events.Add(e);
        }

        public ObjectMoved Resolve(ObjectMoved move)
        {
            var id = move.From;
            while (_identities.TryGetValue(id, out var current))
                id = current;
            if (id == move.From || !State.Objects.TryGetValue(id, out var card))
                return move;
            return move with
            {
                From = id,
                Departed = card,
                To = IsLibraryReposition(move) ? card : move.To,
            };
        }

        public void AddAll(IEnumerable<GameEvent> more)
        {
            foreach (var e in more)
                Add(e);
        }

        public Outcome Done() => new(State, _events.ToImmutable());
    }
}
