using MtgEngine.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
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
public sealed class ZoneSystem : IZoneSystem
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
            foreach (var oracleId in start.DeckOracleIds)
                tape.Add(Born(tape.State, Definition(store, oracleId), player, Cards.Zone.Library));

            // CR 103.2c / 903.6: in a Commander game the commander starts face up in the command zone,
            // and stays a commander through every zone it visits, CR 903.3.
            foreach (var oracleId in start.CommandZoneOracleIds)
            {
                var born = Born(tape.State, Definition(store, oracleId), player, Cards.Zone.Command);
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
    public Outcome Move(GameState state, ObjectId what, ZoneKey to, Placement where = Placement.Default, bool tapped = false, PlayerId? controller = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (!state.Objects.TryGetValue(what, out var moving))
            return Outcome.Unchanged(state);

        // CR 400.4a: an instant or sorcery card that would enter the battlefield stays where it is.
        if (to.Zone == Cards.Zone.Battlefield && (PrintedTypes(moving) & (CardType.Instant | CardType.Sorcery)) != 0)
            return Outcome.Unchanged(state);

        // CR 400.4b: a conspiracy, phenomenon, plane, scheme or vanguard card never leaves the command zone.
        const CardType StaysInCommand = CardType.Conspiracy | CardType.Phenomenon | CardType.Plane | CardType.Scheme | CardType.Vanguard;
        if (moving.Zone.Zone == Cards.Zone.Command && to.Zone != Cards.Zone.Command && (PrintedTypes(moving) & StaysInCommand) != 0)
            return Outcome.Unchanged(state);

        // CR 400.3: an object headed for a library, graveyard or hand that is not its owner's goes
        // to its owner's instead; and the shared zones take no player in their key, CR 400.1.
        var destination = KeyFor(to.Zone, moving.Owner);

        var tape = new Tape(state);
        // CR 603.10: the object as it last existed travels with the move, for the abilities that look back.
        tape.Add(new ObjectMoved(what, Reborn(state, moving, destination, tapped, controller), PositionIn(state, destination, where), Departed: moving));
        return tape.Done();
    }

    /// <summary>
    /// Draw, CR 121.1: the top card of the library goes to its owner's hand. A draw attempted from
    /// an empty library is recorded rather than performed, CR 121.4; the state-based action in
    /// CR 704.5b is what loses the game for it.
    /// </summary>
    [RuleRef("121")]
    public Outcome Draw(GameState state, PlayerId player, int count)
    {
        ArgumentNullException.ThrowIfNull(state);
        var library = ZoneKey.Of(Cards.Zone.Library, player);
        var hand = ZoneKey.Of(Cards.Zone.Hand, player);
        var tape = new Tape(state);

        for (var i = 0; i < count; i++)
        {
            var cards = tape.State.In(library);
            if (cards.IsEmpty)
            {
                // CR 121.4: the attempt is what matters; each attempt is recorded.
                tape.Add(new DrawFromEmptyLibraryAttempted(player));
                continue;
            }

            // Libraries are top-first, so the drawn card is index 0; a drawn card goes to the end
            // of the hand, which is unordered anyway, CR 400.5.
            var top = cards[0];
            var drawn = tape.State.Objects[top];
            var reborn = Reborn(tape.State, drawn, hand, tapped: false, controller: null);
            tape.Add(new ObjectMoved(top, reborn, -1, Departed: drawn));
            // CR 121.1: the draw is its own event, distinct from a card put into a hand, CR 121.2.
            tape.Add(new ObjectDrawn(player, reborn.Id));
        }

        return tape.Done();
    }

    /// <summary>
    /// Shuffle a library, CR 701.24a: randomize it so no player knows the order. The randomness is
    /// the seeded generator in the state, so the same log replays the same shuffle. A library of
    /// zero or one cards is still shuffled, CR 701.24e — the reorder is emitted either way.
    /// </summary>
    [RuleRef("701.24")]
    public Outcome Shuffle(GameState state, PlayerId player)
    {
        ArgumentNullException.ThrowIfNull(state);
        var library = ZoneKey.Of(Cards.Zone.Library, player);
        var (order, next) = Rng.Shuffle(state.Rng, state.In(library));

        var tape = new Tape(state);

        // The generator was consumed before the result is known, so the advance is logged first;
        // with fewer than two cards Rng.Shuffle draws nothing and there is nothing to log.
        if (next != state.Rng)
            tape.Add(new RngAdvanced(next));

        tape.Add(new ZoneReordered(library, order, Shuffle: true));
        return tape.Done();
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
        var id = new ObjectId(state.NextObjectId);
        // CR 111.2: the token's owner is the player who controls the effect that created it.
        var created = new GameObject(id, DefinitionOf(token), 0, controller, controller, battlefield, state.NextTimestamp)
        {
            IsToken = true,
            Tapped = tapped,
        };

        var tape = new Tape(state);
        tape.Add(new ObjectCreated(created, -1));

        // CR 506.3c: an effect can put a token onto the battlefield attacking. It joins the combat
        // already happening; with no combat there is nothing to attack and the flag is moot.
        if (attacking && tape.State.Combat is { } combat)
            tape.Add(new AttackersDeclared(combat.AttackingPlayer, combat.Attackers.SetItem(id, DefenderIn(tape.State, combat))));

        return tape.Done();
    }

    /// <summary>The printed types of the face an object is showing, CR 109.3. What a layer added is E7's business; CR 400.4 is about the card.</summary>
    [RuleRef("109.3")]
    private static CardType PrintedTypes(GameObject obj) => obj.Face.Types.Types;

    private static CardDefinition Definition(IDefinitionStore store, string oracleId) =>
        store.ByOracleId(oracleId) ?? throw new InvalidOperationException($"No definition for oracle id {oracleId}.");

    /// <summary>A card entering the game for the first time, CR 103: owner and controller are the player who started with it.</summary>
    [RuleRef("108.3")]
    private static ObjectCreated Born(GameState state, CardDefinition definition, PlayerId owner, Cards.Zone zone)
    {
        var key = KeyFor(zone, owner);
        var obj = new GameObject(new ObjectId(state.NextObjectId), definition, 0, owner, owner, key, state.NextTimestamp);
        return new ObjectCreated(obj, -1);
    }

    /// <summary>
    /// The new object a moved object becomes, CR 400.7. Everything that was true of the old object
    /// and is not printed on the card is dropped: counters, damage, attachment, tap and face-down
    /// status, phasing, and anything the stack was holding for it.
    /// </summary>
    [RuleRef("400.7")]
    private static GameObject Reborn(GameState state, GameObject moving, ZoneKey destination, bool tapped, PlayerId? controller) =>
        new(new ObjectId(state.NextObjectId), moving.Definition, moving.FaceIndex, moving.Owner, ControllerFor(destination, moving, controller), destination, state.NextTimestamp)
        {
            IsToken = moving.IsToken,
            IsCommander = moving.IsCommander,
            Tapped = tapped,

            // CR 302.6: a creature entering the battlefield has not been controlled since the turn
            // began. In every other zone the flag has nothing to say.
            SummoningSick = true,
        };

    /// <summary>
    /// Who controls the new object. A library, hand or graveyard is its owner's and has no
    /// controller to speak of, CR 108.4; on the battlefield the mover says, and says nothing when
    /// the object simply carries on under the same control.
    /// </summary>
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

        public GameState State { get; private set; } = state;

        public void Add(GameEvent e)
        {
            State = e.Apply(State);
            _events.Add(e);
        }

        public void AddAll(IEnumerable<GameEvent> more)
        {
            foreach (var e in more)
                Add(e);
        }

        public Outcome Done() => new(State, _events.ToImmutable());
    }
}
