using MtgEngine.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// A board built by hand, with made-up definitions. Nothing here reads a card file: every
/// object is whatever the test says it is, so a state-based action is exercised on the state
/// alone and not on a card's translation.
/// </summary>
internal sealed class Board
{
    public Board(int players = 2, int life = 20) =>
        State = GameReducer.Replay(
        [
            new GameStarted(
                [.. Enumerable.Range(0, players).Select(i => new PlayerStart($"seat {i}", [], []))],
                7UL,
                life,
                "commander"),
        ]);

    public GameState State { get; private set; }

    public static CardDefinition Definition(
        string name,
        CardType types,
        int? power = null,
        int? toughness = null,
        Supertype supertypes = Supertype.None,
        string[]? subtypes = null,
        Ability[]? abilities = null) =>
        new(
            name,
            name,
            Layout.Normal,
            [
                new Face(
                    name,
                    FaceRole.Main,
                    new TypeLine(types, supertypes, subtypes),
                    Power: power is null ? null : new PrintedStat(power.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                    Toughness: toughness is null ? null : new PrintedStat(toughness.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                    Abilities: abilities),
            ]);

    public static CardDefinition Creature(int power, int toughness, string name = "sba beast", Ability[]? abilities = null, Supertype supertypes = Supertype.None) =>
        Definition(name, CardType.Creature, power, toughness, supertypes, ["Beast"], abilities);

    public ObjectId Put(
        CardDefinition definition,
        Zone zone = Zone.Battlefield,
        int owner = 0,
        int damage = 0,
        bool token = false,
        ObjectId? attachedTo = null,
        StackEntry? stack = null,
        int? attachedToPlayer = null,

        // CR 108.4: who controls it, when that is not its owner — a creature taken with Bribery,
        // or an ability on the stack of a permanent somebody else controls. The zone is still the
        // OWNER's (CR 400.3), which is the pairing CR 800.4a's last two steps turn on.
        int? controller = null,
        params (string Kind, int Count)[] counters)
    {
        var seat = new PlayerId(owner);
        var held = controller is { } holder ? new PlayerId(holder) : seat;
        var id = new ObjectId(State.NextObjectId);
        var created = new ObjectCreated(
            new GameObject(id, definition, 0, seat, held, Key(zone, seat), State.NextTimestamp)
            {
                Damage = damage,
                IsToken = token,
                AttachedTo = attachedTo,

                // CR 303.4f: the other half of an attachment. A seat number rather than a
                // PlayerId so a test names it the way it names an owner.
                AttachedToPlayer = attachedToPlayer is { } seated ? new PlayerId(seated) : null,
                Stack = stack,
                SummoningSick = false,
                Counters = counters.ToImmutableDictionary(c => c.Kind, c => c.Count, StringComparer.Ordinal),
            },
            -1);
        State = created.Apply(State);
        return id;
    }

    public Board Fold(params GameEvent[] events)
    {
        State = GameReducer.Apply(State, events);
        return this;
    }

    public Board Life(int player, int life) => Fold(new LifeChanged(new PlayerId(player), life - State.Player(new PlayerId(player)).Life, "test"));

    public Board Poison(int player, int count) => Fold(new PlayerCountersChanged(new PlayerId(player), Counters.Poison, count));

    public Board DrewFromEmptyLibrary(int player) => Fold(new DrawFromEmptyLibraryAttempted(new PlayerId(player)));

    /// <summary>
    /// Deals commander damage from a real commander on the board. The commander has to exist,
    /// because the running total is keyed by WHICH commander (owner and card) rather than by the
    /// object it is at this moment - CR 400.7 gives it a new object on every zone change.
    /// </summary>
    public Board CommanderDamage(int player, string commander, int amount, int owner = 0)
    {
        var id = Put(Definition(commander, CardType.Creature, 3, 3, Supertype.Legendary), owner: owner);
        return Fold(new DamageDealt(id, null, new PlayerId(player), amount, Combat: true, FromCommander: true));
    }

    public Board Lost(int player) => Fold(new PlayerLost(new PlayerId(player), "test"));

    private static ZoneKey Key(Zone zone, PlayerId owner) => zone switch
    {
        Zone.Battlefield or Zone.Stack or Zone.Exile => ZoneKey.Shared(zone),
        _ => ZoneKey.Of(zone, owner),
    };
}

/// <summary>
/// Layer 7d and nothing else: printed power and toughness moved by +1/+1 and -1/-1 counters,
/// CR 613.4c. That is the whole reason a check has to repeat — the toughness a state-based
/// action reads is computed, so removing counters changes it.
/// <para>
/// Layer 4 gets the one arm 704.5m and 704.5n care about: a self-applied
/// <see cref="AddTypes"/> with <c>AllCreatureTypes</c> (changeling, CR 702.72a and 205.3d) sets
/// the flag, and a self-applied <see cref="RemoveTypes"/> with it takes both the flag and the
/// printed creature types away (CR 205.3m). The real layer system does this for real; here it
/// keeps the state-based actions reading a computed characteristic and not a printed one.
/// </para>
/// </summary>
internal sealed class FakeCharacteristics : ICharacteristics
{
    public string Name => "fake layers";

    public Characteristics Of(GameState state, ObjectId id)
    {
        var obj = state.Objects[id];
        var face = obj.Face;
        var shift = obj.Counters.GetValueOrDefault(Counters.PlusOnePlusOne) - obj.Counters.GetValueOrDefault(Counters.MinusOneMinusOne);
        var effects = face.AbilityList.OfType<StaticAbility>().Where(s => s.To is Self).Select(s => s.Effect).ToList();
        var lost = effects.OfType<RemoveTypes>().Any(r => r.AllCreatureTypes);
        var every = !lost && effects.OfType<AddTypes>().Any(a => a.AllCreatureTypes);
        var subtypes = lost
            ? face.Types.SubtypeList.Where(s => !CreatureTypes.IsCreatureType(s))
            : face.Types.SubtypeList;
        return new Characteristics(
            face.Name,
            face.Types.Types,
            face.Types.Supertypes,
            [.. subtypes],
            Color.Colorless,
            face.Power?.Number is { } power ? power + shift : null,
            face.Toughness?.Number is { } toughness ? toughness + shift : null,
            obj.Counters.GetValueOrDefault(Counters.Loyalty),
            face.Defense,
            face.ManaCost,
            [.. face.AbilityList],
            obj.InitialController,
            every);
    }

    public PlayerId ControllerOf(GameState state, ObjectId id) => state.Objects[id].InitialController;

    /// <summary>
    /// CR 613.10, as a table a test writes rather than a computation over the battlefield: which
    /// continuous effects put a limit on a player is the layer system's question, and a
    /// state-based action's question is only what the limit then forbids. A player with no entry
    /// is unrestricted, and <see cref="Ends"/> is how a test takes a limit away mid-game.
    /// </summary>
    [RuleRef("613.10")]
    public PlayerLimits PlayerRestrictions(GameState state, PlayerId player) =>
        Limits.TryGetValue(player, out var limits) ? limits : PlayerLimits.None;

    /// <summary>The limits in force, by player. Written by a test before the check that reads them.</summary>
    public Dictionary<PlayerId, PlayerLimits> Limits { get; } = [];

    /// <summary>Every limit on this player ends, the way the effect that granted it leaving the battlefield would end it.</summary>
    public void Ends(PlayerId player) => Limits.Remove(player);
}

/// <summary>Only <see cref="Move"/>: the state-based actions never draw, shuffle or make a token.</summary>
internal sealed class FakeZones : IZoneSystem
{
    public string Name => "fake zones";

    public Outcome Move(GameState state, ObjectId what, ZoneKey to, Placement where = Placement.Default, bool tapped = false, PlayerId? controller = null)
    {
        var obj = state.Objects[what];
        var moved = new ObjectMoved(
            what,
            obj with
            {
                Id = new ObjectId(state.NextObjectId),
                Zone = to,
                Timestamp = state.NextTimestamp,
                Tapped = tapped,
                Damage = 0,
                Counters = ImmutableDictionary<string, int>.Empty,
                AttachedTo = null,
            },
            -1);
        return new Outcome(moved.Apply(state), [moved]);
    }

    public Outcome Create(GameSetup setup, IDefinitionStore store) => throw new NotSupportedException();

    public Outcome Draw(GameState state, PlayerId player, int count, IEnumerable<string>? usedReplacements = null) => throw new NotSupportedException();

    public Outcome Shuffle(GameState state, PlayerId player, IEnumerable<string>? usedReplacements = null) => throw new NotSupportedException();

    public Outcome CreateToken(GameState state, TokenSpec token, PlayerId controller, bool tapped, bool attacking) => throw new NotSupportedException();
}

/// <summary>Enough filter matching for "enchant creature" to be right or wrong.</summary>
internal sealed class FakeQuery(ICharacteristics characteristics) : IQuery
{
    public string Name => "fake query";

    public bool Matches(GameState state, ObjectFilter filter, ObjectId candidate, EffectContext context)
    {
        var chars = characteristics.Of(state, candidate);
        if (filter.Types is { } types && (chars.Types & types) != types)
            return false;
        if (filter.NotTypes is { } notTypes && (chars.Types & notTypes) != 0)
            return false;
        // CR 205.3d: an object that is every creature type carries none of the names, so the
        // flag is read beside the list, as the real query layer does.
        return filter.Subtypes is not { Count: > 0 } subtypes
            || subtypes.Any(s => chars.Subtypes.Contains(s, StringComparer.OrdinalIgnoreCase)
                || (chars.EveryCreatureType && CreatureTypes.IsCreatureType(s)));
    }

    /// <summary>
    /// One arm, <see cref="IsYourTurn"/> (CR 500.1 with 102.1): it is the cheapest condition that
    /// tells the two seats apart, so it is what proves which player a
    /// <see cref="PlayerFilter.Also"/> is asked about. Anything else still throws — a fake that
    /// answered every condition would stop being evidence.
    /// </summary>
    public bool Holds(GameState state, Condition condition, EffectContext context) => condition switch
    {
        IsYourTurn wanted => wanted.Value == (context.Controller == state.Turn.ActivePlayer),
        _ => throw new NotSupportedException(),
    };

    /// <summary>
    /// The seam of CR 702.5b, which the state-based actions ask rather than answer since wave 10.
    /// "You" and "opponent" are read from the context's controller (CR 601.2c); <c>Also</c> is
    /// asked <em>as the candidate</em>, because the only <c>PlayerRef</c> a condition has for the
    /// player being filtered is <c>You</c>. The real one is the query system's; this is the fake
    /// that proves the rule delegates, and <see cref="PlayersAsked"/> records that it was asked.
    /// </summary>
    public bool PlayerMatches(GameState state, PlayerFilter filter, PlayerId candidate, EffectContext context)
    {
        PlayersAsked.Add((filter, candidate));
        if (filter.IsYou is { } you && you != (candidate == context.Controller))
            return false;
        if (filter.Opponent is { } opponent && opponent != (candidate != context.Controller))
            return false;
        return filter.Also is null || Holds(state, filter.Also, context with { Controller = candidate });
    }

    /// <summary>Every filter this fake was asked about, in order, so a test can prove the call happened at all.</summary>
    public List<(PlayerFilter Filter, PlayerId Candidate)> PlayersAsked { get; } = [];

    public int Evaluate(GameState state, Amount amount, EffectContext context) => throw new NotSupportedException();

    public ImmutableList<ObjectId> Resolve(GameState state, Selector selector, EffectContext context) => throw new NotSupportedException();

    public ImmutableList<PlayerId> Players(GameState state, PlayerRef player, EffectContext context) => throw new NotSupportedException();
}
