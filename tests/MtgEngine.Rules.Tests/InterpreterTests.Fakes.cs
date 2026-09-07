using MtgEngine.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.Interpreter;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// What an object is, set by the test. The interpreter asks for exactly two things — the
/// controller, and whether the source has a keyword — so an object nobody set is a plain,
/// keywordless object rather than an error.
/// </summary>
internal sealed class InterpreterFakeCharacteristics : ICharacteristics
{
    private readonly Dictionary<ObjectId, Characteristics> _of = [];

    public string Name => "fake characteristics";

    public void Set(ObjectId id, Characteristics what) => _of[id] = what;

    public Characteristics Of(GameState state, ObjectId id) =>
        _of.TryGetValue(id, out var what) ? what : Plain(state, id);

    public PlayerId ControllerOf(GameState state, ObjectId id) =>
        _of.TryGetValue(id, out var what) ? what.Controller
        : state.Objects.TryGetValue(id, out var obj) ? obj.InitialController
        : new PlayerId(0);

    private static Characteristics Plain(GameState state, ObjectId id)
    {
        var obj = state.Objects.TryGetValue(id, out var found) ? found : null;
        return new Characteristics(
            obj?.Face.Name ?? "object",
            obj?.Face.Types.Types ?? CardType.None,
            Supertype.None,
            [],
            Color.Colorless,
            null,
            null,
            null,
            null,
            null,
            [],
            obj?.InitialController ?? new PlayerId(0));
    }
}

/// <summary>
/// The replacement layer as CR 616 leaves it when nothing applies: the proposed event happens
/// exactly as proposed. A test that wants a replacement sets <see cref="Instead"/>.
/// </summary>
internal sealed class InterpreterFakeReplacements : IReplacementSystem
{
    public string Name => "fake replacement";

    public Func<GameEvent, GameEvent?> Instead { get; set; } = e => e;

    public List<GameEvent> Proposed { get; } = [];

    public List<Resumption> Resumed { get; } = [];

    /// <summary>CR 616.1: the order question goes back to this system unchanged; the test only records that it arrived.</summary>
    public Outcome Resume(GameState state, Resumption resumption)
    {
        Resumed.Add(resumption);
        return Outcome.Unchanged(state);
    }

    public Outcome Replace(GameState state, GameEvent proposed, PlayerId affected)
    {
        Proposed.Add(proposed);
        var actual = Instead(proposed);
        return actual is null ? Outcome.Unchanged(state) : new Outcome(actual.Apply(state), [actual]);
    }
}

/// <summary>
/// Two players, the real query layer and the real zone system, and made-up card definitions: the
/// interpreter is the only thing under test, but everything it reads through is the real seam so
/// a wrong assumption about a selector or a zone change fails here rather than at merge.
/// </summary>
internal sealed class InterpreterTable
{
    private int _next = 1;

    public InterpreterTable(ICharacteristics? layers = null)
    {
        Characteristics = layers ?? Layers;
        State = GameReducer.Replay([new GameStarted([new PlayerStart("one", [], []), new PlayerStart("two", [], [])], 7, 20, "commander")]);
        Subject = new EffectInterpreter(new Query.Query(Characteristics), new Zones.ZoneSystem(), Replacements, Characteristics);
    }

    public InterpreterFakeCharacteristics Layers { get; } = new();

    /// <summary>What the interpreter reads characteristics through: the fake, or the real layer system when a test asks for it.</summary>
    public ICharacteristics Characteristics { get; }

    public InterpreterFakeReplacements Replacements { get; } = new();

    public EffectInterpreter Subject { get; }

    public GameState State { get; private set; }

    public PlayerId A => new(0);

    public PlayerId B => new(1);

    public static CardDefinition Definition(CardType types, params Ability[] abilities) => new(
        "00000000-0000-0000-0000-000000000009",
        "engine test object",
        Layout.Normal,
        [new Face("engine test object", FaceRole.Main, new TypeLine(types), Abilities: abilities)]);

    /// <summary>A permanent on the battlefield with the characteristics a test says it has.</summary>
    public ObjectId Permanent(PlayerId controller, CardType types = CardType.Creature, int? power = 1, int? toughness = 1, KeywordKind[]? keywords = null)
    {
        var id = Insert(controller, ZoneKey.Shared(Zone.Battlefield), Definition(types));
        Layers.Set(id, new Characteristics(
            "engine test object",
            types,
            Supertype.None,
            [],
            Color.Colorless,
            power,
            toughness,
            null,
            null,
            null,
            [.. (keywords ?? []).Select(k => (Ability)new KeywordAbility(k))],
            controller));
        return id;
    }

    public ObjectId Card(PlayerId owner, Zone zone) => Insert(owner, ZoneKey.Of(zone, owner), Definition(CardType.Creature));

    /// <summary>A spell or ability on the stack, with what it does and what it chose.</summary>
    public ObjectId OnStack(PlayerId controller, Effect does, StackEntry? entry = null)
    {
        var id = new ObjectId(_next++);
        var obj = new GameObject(id, Definition(CardType.Instant, new SpellAbility(does)), 0, controller, controller, ZoneKey.Shared(Zone.Stack), id.Value)
        {
            Stack = entry ?? new StackEntry(null, null, [], [], [], null, false),
        };
        State = new ObjectCreated(obj, -1).Apply(State);
        return id;
    }

    public ImmutableList<GameEvent> Resolve(ObjectId stackObject) => Run(Subject.Resolve(State, stackObject));

    /// <summary>Answer the question the game is waiting on, the way the choice system would.</summary>
    public ImmutableList<GameEvent> Answer(params string[] chosen)
    {
        var pending = State.Pending ?? throw new InvalidOperationException("nothing is waiting on an answer");
        var options = chosen
            .Select(id => pending.Options.FirstOrDefault(o => o.Id == id) ?? new ChoiceOption(id, id))
            .ToImmutableList();
        var answered = new ChoiceAnswered(pending.Id, pending.Player, [.. chosen]);
        State = answered.Apply(State);
        return Run(Subject.Resume(State, new Resumption(pending.Kind, pending.Player, options, pending.Context)));
    }

    public PendingChoice Pending => State.Pending ?? throw new InvalidOperationException("nothing is waiting on an answer");

    public GameObject Object(ObjectId id) => State.Objects[id];

    public ObjectId Put(PlayerId owner, ZoneKey zone, CardDefinition definition) => Insert(owner, zone, definition);

    public ImmutableList<GameEvent> Run(Effect effect, EffectContext? context = null) =>
        Run(Subject.Run(State, effect, context ?? Context()));

    /// <summary>
    /// Run an effect outside the stack, saying which of the source's abilities it is: what the
    /// replacement system does with a <c>ReplacementAbility.With</c>, CR 614.1a.
    /// </summary>
    public ImmutableList<GameEvent> RunFrom(Effect effect, EffectContext context, int? abilityIndex) =>
        Run(Subject.Run(State, effect, context, abilityIndex));

    /// <summary>Turn a permanent over without going through the interpreter, so a test can set the board up.</summary>
    public void SetTapped(ObjectId id, bool tapped) => State = new ObjectChanged(id, Tapped: tapped).Apply(State);

    public EffectContext Context(ObjectId? source = null, int? x = null) => new(
        source,
        A,
        [],
        [],
        x,
        ImmutableDictionary<string, ImmutableList<ObjectId>>.Empty,
        ImmutableDictionary<string, string>.Empty,
        ImmutableDictionary<string, int>.Empty,
        [],
        null);

    public int Life(PlayerId player) => State.Player(player).Life;

    public ImmutableList<ObjectId> In(Zone zone, PlayerId player) => State.In(ZoneKey.Of(zone, player));

    private ObjectId Insert(PlayerId owner, ZoneKey zone, CardDefinition definition)
    {
        var id = new ObjectId(_next++);
        State = new ObjectCreated(new GameObject(id, definition, 0, owner, owner, zone, id.Value), -1).Apply(State);
        return id;
    }

    private ImmutableList<GameEvent> Run(Outcome outcome)
    {
        State = outcome.State;
        return outcome.Events;
    }
}
