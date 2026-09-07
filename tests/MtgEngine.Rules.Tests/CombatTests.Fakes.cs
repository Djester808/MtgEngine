using MtgEngine.Cards;
using MtgEngine.Rules.Combat;
using MtgEngine.Rules.Commands;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// The seams combat reads from, faked. The characteristics fake returns exactly what a test
/// set for an object, which is what makes a combat test independent of the layer system: a
/// creature "with flying" here is one whose computed characteristics say so, however it got it.
/// </summary>
internal sealed class FakeCharacteristics : ICharacteristics
{
    private readonly Dictionary<ObjectId, Characteristics> _of = [];

    public string Name => "fake characteristics";

    public void Set(ObjectId id, Characteristics what) => _of[id] = what;

    public Characteristics Of(GameState state, ObjectId id) =>
        _of.TryGetValue(id, out var what) ? what : throw new KeyNotFoundException($"no characteristics set for {id}");

    public PlayerId ControllerOf(GameState state, ObjectId id) => Of(state, id).Controller;
}

/// <summary>
/// The query layer, faked to the two questions combat asks it: is this object a commander, and
/// does it match this filter. Everything else throws, so a combat change that starts leaning on
/// the query layer for something new says so instead of silently answering false.
/// </summary>
internal sealed class FakeQuery : IQuery
{
    public string Name => "fake query";

    public HashSet<ObjectId> Commanders { get; } = [];

    public Func<ObjectFilter, ObjectId, bool> Match { get; set; } = (_, _) => false;

    public ImmutableList<PlayerId> RefersTo { get; set; } = [];

    public bool Matches(GameState state, ObjectFilter filter, ObjectId candidate, EffectContext context)
    {
        ArgumentNullException.ThrowIfNull(filter);
        return filter.IsCommander == true ? Commanders.Contains(candidate) : Match(filter, candidate);
    }

    public ImmutableList<PlayerId> Players(GameState state, PlayerRef player, EffectContext context) => RefersTo;

    public bool Holds(GameState state, Condition condition, EffectContext context) => throw new NotSupportedException();

    public int Evaluate(GameState state, Amount amount, EffectContext context) => throw new NotSupportedException();

    public ImmutableList<ObjectId> Resolve(GameState state, Selector selector, EffectContext context) => throw new NotSupportedException();
}

/// <summary>
/// Two players and a battlefield built by hand, with made-up creatures: no card file, no
/// definition store, no turn system. Object ids are their own timestamps, so declaration and
/// damage order is the order the test created them in.
/// </summary>
internal sealed class Table
{
    private static readonly CardDefinition Blank = new(
        "00000000-0000-0000-0000-000000000001",
        "engine test object",
        Layout.Normal,
        [new Face("engine test object", FaceRole.Main, new TypeLine(CardType.Creature))]);

    private int _next = 1;

    public Table()
    {
        State = GameReducer.Replay([new GameStarted([new PlayerStart("one", [], []), new PlayerStart("two", [], [])], 7, 20, "commander")]);
        Subject = new CombatSystem(Layers, Query);
    }

    public FakeCharacteristics Layers { get; } = new();

    public FakeQuery Query { get; } = new();

    public CombatSystem Subject { get; }

    public GameState State { get; private set; }

    public PlayerId A => new(0);

    public PlayerId B => new(1);

    public ObjectId Creature(
        PlayerId controller,
        int power,
        int toughness,
        KeywordKind[]? keywords = null,
        bool tapped = false,
        bool summoningSick = false,
        Color colors = Color.Colorless,
        CardType types = CardType.Creature,
        Ability[]? statics = null,
        bool commander = false)
    {
        var id = new ObjectId(_next++);
        var obj = new GameObject(id, Blank, 0, controller, controller, ZoneKey.Shared(Zone.Battlefield), id.Value)
        {
            Tapped = tapped,
            SummoningSick = summoningSick,
        };
        State = new ObjectCreated(obj, -1).Apply(State);
        Layers.Set(id, new Characteristics(
            "engine test object",
            types,
            Supertype.None,
            [],
            colors,
            power,
            toughness,
            null,
            null,
            null,
            [.. (keywords ?? []).Select(k => (Ability)new KeywordAbility(k)), .. (statics ?? [])],
            controller));
        if (commander)
            Query.Commanders.Add(id);
        return id;
    }

    public ImmutableList<GameEvent> Attack(PlayerId player, ObjectId attacker, PlayerId defender) =>
        Attack(player, new AttackDeclaration(attacker, new CombatTarget(defender, null)));

    public ImmutableList<GameEvent> Attack(PlayerId player, params AttackDeclaration[] attacks) =>
        Run(Subject.DeclareAttackers(State, new DeclareAttackers(player, [.. attacks])));

    public ImmutableList<GameEvent> Block(PlayerId player, params BlockDeclaration[] blocks) =>
        Run(Subject.DeclareBlockers(State, new DeclareBlockers(player, [.. blocks])));

    public ImmutableList<GameEvent> Strike(bool firstStrike) => Run(Subject.DealCombatDamage(State, firstStrike));

    public ImmutableList<GameEvent> End() => Run(Subject.EndCombat(State));

    public void Remove(ObjectId id) => State = new ObjectCeasedToExist(id).Apply(State);

    public int DamageOn(ObjectId id) => State.Objects[id].Damage;

    public bool Tapped(ObjectId id) => State.Objects[id].Tapped;

    public int Life(PlayerId player) => State.Player(player).Life;

    private ImmutableList<GameEvent> Run(Outcome outcome)
    {
        State = outcome.State;
        return outcome.Events;
    }
}
