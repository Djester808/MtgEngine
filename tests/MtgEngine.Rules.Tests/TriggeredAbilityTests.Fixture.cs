using MtgEngine.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// A board for trigger tests, built by hand from made-up definitions. Nothing here reads a card
/// file: every ability is whatever the test says it is, so what is under test is the matching and
/// the ordering and never a card's translation.
/// </summary>
internal sealed class TriggeredAbilityBoard
{
    public TriggeredAbilityBoard(int players = 2)
    {
        var started = new GameStarted([.. Enumerable.Range(0, players).Select(i => new PlayerStart($"seat {i}", [], []))], 11UL, 20, "commander");
        Recorded.Add(started);
        State = GameReducer.Replay(Recorded);
    }

    public GameState State { get; private set; }

    /// <summary>
    /// Every event the board folded, in order, so a test that cares about the log being the game
    /// can replay the board and not only the events the engine produced afterwards.
    /// </summary>
    public List<GameEvent> Recorded { get; } = [];

    public static ICharacteristics Layers { get; } = new Layers.LayerSystem();

    public static Triggers.TriggerSystem Collector { get; } = new(new Query.Query(Layers), Layers);

    public static CardDefinition Definition(string name, CardType types, Ability[]? abilities = null, int? power = null, int? toughness = null) =>
        new(
            name,
            name,
            Layout.Normal,
            [
                new Face(
                    name,
                    FaceRole.Main,
                    new TypeLine(types, Supertype.None, null),
                    Power: power is null ? null : new PrintedStat(power.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                    Toughness: toughness is null ? null : new PrintedStat(toughness.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                    Abilities: abilities),
            ]);

    public static CardDefinition Creature(string name, Ability[]? abilities = null) =>
        Definition(name, CardType.Creature, abilities, 2, 2);

    /// <summary>
    /// Puts an object into a zone and hands back the event that put it there, which is what a
    /// trigger sees. An <paramref name="owner"/> other than the controller is a permanent someone
    /// has taken control of, which is the only way the look-back's controller differs from the new
    /// object's (CR 108.4: a card in a graveyard is its owner's).
    /// </summary>
    public ObjectCreated Put(
        CardDefinition definition,
        int controller = 0,
        Zone zone = Zone.Battlefield,
        ObjectId? attachedTo = null,
        bool token = false,
        int? owner = null,
        string? counter = null)
    {
        var seat = new PlayerId(controller);
        var created = new ObjectCreated(
            new GameObject(new ObjectId(State.NextObjectId), definition, 0, new PlayerId(owner ?? controller), seat, Key(zone, seat), State.NextTimestamp)
            {
                SummoningSick = false,
                AttachedTo = attachedTo,
                IsToken = token,
                Counters = counter is null
                    ? ImmutableDictionary<string, int>.Empty
                    : ImmutableDictionary<string, int>.Empty.SetItem(counter, 1),
            },
            -1);
        Recorded.Add(created);
        State = created.Apply(State);
        return created;
    }

    public ObjectId Id(ObjectCreated created) => created.Object.Id;

    /// <summary>
    /// The battlefield to the graveyard, CR 700.4: the object that left is gone from the state by
    /// the time triggers are collected. The card in the graveyard is a new object (CR 400.7) that
    /// kept none of the permanent's counters, damage or control, exactly as
    /// <c>ZoneSystem.Reborn</c> makes it, and the move carries the permanent as it last existed so
    /// the look-back of CR 603.10 has something true to read. <paramref name="carryDeparted"/>
    /// false is the older shape of the event, for the arm that still has to cope with it.
    /// </summary>
    public ObjectMoved Kill(ObjectId id, bool carryDeparted = true)
    {
        var obj = State.Objects[id];
        var dead = new GameObject(
            new ObjectId(State.NextObjectId),
            obj.Definition,
            obj.FaceIndex,
            obj.Owner,
            obj.Owner,
            ZoneKey.Of(Zone.Graveyard, obj.Owner),
            State.NextTimestamp)
        {
            IsToken = obj.IsToken,
            IsCommander = obj.IsCommander,
        };
        var moved = new ObjectMoved(id, dead, -1, carryDeparted ? obj : null);
        State = moved.Apply(State);
        return moved;
    }

    public TriggeredAbilityBoard Fold(params GameEvent[] events)
    {
        Recorded.AddRange(events);
        State = GameReducer.Apply(State, events);
        return this;
    }

    /// <summary>Whose turn it is, which is what APNAP and "at the beginning of your upkeep" turn on.</summary>
    public TriggeredAbilityBoard ActivePlayer(int seat) => Fold(new TurnBegan(1, new PlayerId(seat), false));

    /// <summary>
    /// Move an object to another of its owner's zones, CR 400.7: a new object arrives there and the
    /// old one ceases to exist. The move carries the object as it last existed, exactly as
    /// <c>ZoneSystem.Proposal</c> does, so the zone it left is on the event (CR 603.6).
    /// <paramref name="carryDeparted"/> false is a move that never said which zone it left, which
    /// the matcher has to refuse rather than guess.
    /// </summary>
    public ObjectMoved MoveTo(ObjectId id, Zone zone, bool carryDeparted = true)
    {
        var obj = State.Objects[id];
        var arrived = new GameObject(new ObjectId(State.NextObjectId), obj.Definition, obj.FaceIndex, obj.Owner, obj.Owner, Key(zone, obj.Owner), State.NextTimestamp);
        var moved = new ObjectMoved(id, arrived, -1, carryDeparted ? obj : null);
        State = moved.Apply(State);
        return moved;
    }

    public Outcome Collect(params GameEvent[] since) => Collector.Collect(State, since);

    private static ZoneKey Key(Zone zone, PlayerId owner) =>
        zone is Zone.Battlefield or Zone.Stack or Zone.Command ? ZoneKey.Shared(zone) : ZoneKey.Of(zone, owner);
}
