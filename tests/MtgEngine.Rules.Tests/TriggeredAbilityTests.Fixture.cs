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
    public TriggeredAbilityBoard(int players = 2) =>
        State = GameReducer.Replay(
        [
            new GameStarted([.. Enumerable.Range(0, players).Select(i => new PlayerStart($"seat {i}", [], []))], 11UL, 20, "commander"),
        ]);

    public GameState State { get; private set; }

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

    /// <summary>Puts an object into a zone and hands back the event that put it there, which is what a trigger sees.</summary>
    public ObjectCreated Put(CardDefinition definition, int controller = 0, Zone zone = Zone.Battlefield, ObjectId? attachedTo = null, bool token = false)
    {
        var seat = new PlayerId(controller);
        var created = new ObjectCreated(
            new GameObject(new ObjectId(State.NextObjectId), definition, 0, seat, seat, Key(zone, seat), State.NextTimestamp)
            {
                SummoningSick = false,
                AttachedTo = attachedTo,
                IsToken = token,
            },
            -1);
        State = created.Apply(State);
        return created;
    }

    public ObjectId Id(ObjectCreated created) => created.Object.Id;

    /// <summary>The battlefield to the graveyard, CR 700.4: the object that left is gone from the state by the time triggers are collected.</summary>
    public ObjectMoved Kill(ObjectId id)
    {
        var obj = State.Objects[id];
        var dead = obj with
        {
            Id = new ObjectId(State.NextObjectId),
            Zone = ZoneKey.Of(Zone.Graveyard, obj.Owner),
            Timestamp = State.NextTimestamp,
            AttachedTo = null,
            Damage = 0,
        };
        var moved = new ObjectMoved(id, dead, -1);
        State = moved.Apply(State);
        return moved;
    }

    public TriggeredAbilityBoard Fold(params GameEvent[] events)
    {
        State = GameReducer.Apply(State, events);
        return this;
    }

    /// <summary>Whose turn it is, which is what APNAP and "at the beginning of your upkeep" turn on.</summary>
    public TriggeredAbilityBoard ActivePlayer(int seat) => Fold(new TurnBegan(1, new PlayerId(seat), false));

    public Outcome Collect(params GameEvent[] since) => Collector.Collect(State, since);

    private static ZoneKey Key(Zone zone, PlayerId owner) =>
        zone is Zone.Battlefield or Zone.Stack or Zone.Command ? ZoneKey.Shared(zone) : ZoneKey.Of(zone, owner);
}
