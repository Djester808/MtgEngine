using MtgEngine.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;
using CardZone = MtgEngine.Cards.Zone;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// The seams the casting system takes, standing in for their wave-1 builders. Each fake does
/// the least the rule under test needs: zones make a new object on a move (CR 400.7),
/// characteristics are the printed ones (no layers yet), and the interpreter records what it
/// was asked to resolve.
/// </summary>
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
                InitialController = controller ?? obj.InitialController,
                Damage = 0,
                Stack = null,
            },
            -1);
        return new Outcome(moved.Apply(state), [moved]);
    }

    public Outcome Create(GameSetup setup, IDefinitionStore store) => throw new NotSupportedException();

    public Outcome Draw(GameState state, PlayerId player, int count) => throw new NotSupportedException();

    public Outcome Shuffle(GameState state, PlayerId player) => throw new NotSupportedException();

    public Outcome CreateToken(GameState state, TokenSpec token, PlayerId controller, bool tapped, bool attacking) => throw new NotSupportedException();
}

/// <summary>Printed characteristics only: the layer system is E7's, and none of these tests change one.</summary>
internal sealed class PrintedCharacteristics : ICharacteristics
{
    public string Name => "printed characteristics";

    public Characteristics Of(GameState state, ObjectId id)
    {
        var obj = state.Objects[id];
        var face = obj.Face;
        return new Characteristics(
            face.Name,
            face.Types.Types,
            face.Types.Supertypes,
            [.. face.Types.SubtypeList],
            face.ColorIndicator ?? Color.Colorless,
            face.Power?.Number,
            face.Toughness?.Number,
            null,
            face.Defense,
            face.ManaCost,
            [.. face.AbilityList],
            obj.InitialController);
    }

    public PlayerId ControllerOf(GameState state, ObjectId id) => state.Objects[id].InitialController;
}

internal sealed class RecordingInterpreter : IInterpreter
{
    public string Name => "recording interpreter";

    public List<ObjectId> Resolved { get; } = [];

    public Outcome Resolve(GameState state, ObjectId stackObject)
    {
        Resolved.Add(stackObject);
        return Outcome.Unchanged(state);
    }

    public Outcome Run(GameState state, Effect effect, EffectContext context) => Outcome.Unchanged(state);
}

/// <summary>Amounts only: a fixed amount is a number, which is all a cost in this slice asks for.</summary>
internal sealed class FixedAmounts : IQuery
{
    public string Name => "fixed amounts";

    public int Evaluate(GameState state, Amount amount, EffectContext context) =>
        amount is Fixed number ? number.N : throw new NotSupportedException();

    public bool Matches(GameState state, ObjectFilter filter, ObjectId candidate, EffectContext context) => throw new NotSupportedException();

    public bool Holds(GameState state, Condition condition, EffectContext context) => throw new NotSupportedException();

    public ImmutableList<ObjectId> Resolve(GameState state, Selector selector, EffectContext context) => throw new NotSupportedException();

    public ImmutableList<PlayerId> Players(GameState state, PlayerRef player, EffectContext context) => [context.Controller];
}

/// <summary>A two-player game built event by event, so every test starts from a state a real log could produce.</summary>
internal sealed class Table
{
    public Table()
    {
        State = GameReducer.Replay([new GameStarted([new PlayerStart("One", [], []), new PlayerStart("Two", [], [])], 7, 20, "test")]);
        Turn(A, Step.PrecombatMain);
        Priority(A);
    }

    public GameState State { get; private set; }

    public static PlayerId A => new(0);

    public static PlayerId B => new(1);

    public ObjectId Add(CardDefinition definition, PlayerId owner, CardZone zone, bool tapped = false, bool summoningSick = true)
    {
        var id = new ObjectId(State.NextObjectId);
        var obj = new GameObject(id, definition, 0, owner, owner, new ZoneKey(zone, owner), State.NextTimestamp)
        {
            Tapped = tapped,
            SummoningSick = summoningSick,
        };
        State = new ObjectCreated(obj, -1).Apply(State);
        return id;
    }

    public void Turn(PlayerId active, Step step)
    {
        State = new TurnBegan(1, active, Extra: false).Apply(State);
        State = new StepBegan(step).Apply(State);
    }

    public void MoveToStep(Step step) => State = new StepBegan(step).Apply(State);

    public void Priority(PlayerId player) => State = new PriorityGranted(player).Apply(State);

    public void AddMana(PlayerId player, ManaPool pool) => State = new ManaChanged(player, pool, "test").Apply(State);

    public void Apply(IEnumerable<GameEvent> events) => State = GameReducer.Apply(State, events);
}

/// <summary>
/// Cards as data, built here rather than read from <c>cards/</c> so a casting test needs no card
/// file. Names are deliberately not any card's name.
/// </summary>
internal static class TestFaces
{
    public static CardDefinition Land(string mana = "{G}") => Definition(
        "Test Land",
        new TypeLine(CardType.Land),
        null,
        abilities: [new ActivatedAbility([new TapSelf()], new Cards.AddMana(new ManaProduction(Fixed: mana)))]);

    public static CardDefinition Creature(string cost = "{1}{G}", int power = 2, int toughness = 2, params Ability[] abilities) => Definition(
        "Test Beast",
        new TypeLine(CardType.Creature, Subtypes: ["Beast"]),
        cost,
        power,
        toughness,
        abilities);

    public static CardDefinition Sorcery(string cost = "{2}") => Definition("Test Rite", new TypeLine(CardType.Sorcery), cost);

    public static CardDefinition Instant(string cost = "{1}") => Definition("Test Reply", new TypeLine(CardType.Instant), cost);

    public static CardDefinition Artifact(string cost = "{2}", params Ability[] abilities) => Definition(
        "Test Engine",
        new TypeLine(CardType.Artifact),
        cost,
        abilities: abilities);

    private static CardDefinition Definition(string name, TypeLine types, string? cost, int? power = null, int? toughness = null, IReadOnlyList<Ability>? abilities = null) =>
        new(
            name.ToUpperInvariant(),
            name,
            Layout.Normal,
            [
                new Face(
                    name,
                    FaceRole.Main,
                    types,
                    cost,
                    power is null ? null : new PrintedStat(power.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                    toughness is null ? null : new PrintedStat(toughness.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                    Abilities: abilities),
            ]);
}
