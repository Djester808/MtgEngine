using MtgEngine.Cards;
using MtgEngine.Rules.Choices;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// A replacement system that records what it was offered and hands back whatever the test says
/// happens. The zone system's side of CR 614.1c is that it offers the event and takes the answer;
/// which effects apply and what they do to the event is the replacement system's, tested there.
/// </summary>
internal sealed class ZoneReplacementFake : IReplacementSystem
{
    public string Name => "fake replacements";

    public List<GameEvent> Offered { get; } = [];

    public List<PlayerId> Affected { get; } = [];

    /// <summary>What happens instead. Null means the event stands exactly as it was proposed.</summary>
    public Func<GameState, GameEvent, Outcome>? Instead { get; init; }

    public Outcome Replace(GameState state, GameEvent proposed, PlayerId affected)
    {
        Offered.Add(proposed);
        Affected.Add(affected);
        return Instead is { } instead
            ? instead(state, proposed)
            : new Outcome(proposed.Apply(state), [proposed]);
    }

    /// <summary>An effect that prevents the event outright, CR 614.1: nothing happens at all.</summary>
    public static Outcome Nothing(GameState state, GameEvent proposed) => Outcome.Unchanged(state);

    /// <summary>
    /// CR 616.1: two effects apply and the affected player has to say which goes first, so the
    /// game stops and the proposed event does not happen until they answer (CR 101.4).
    /// </summary>
    public static Outcome Asks(GameState state, GameEvent proposed)
    {
        var choice = new ChoiceRequested(new PendingChoice(
            new ChoiceId(state.NextChoiceId),
            new PlayerId(0),
            ChoiceKinds.ReplacementOrder,
            "Choose which replacement applies first",
            [new ChoiceOption("a", "one"), new ChoiceOption("b", "the other")],
            1,
            1,
            new ChoiceContext(null, null, "replacement", ImmutableDictionary<string, string>.Empty, proposed)));

        return new Outcome(choice.Apply(state), [choice]);
    }
}

/// <summary>The interpreter is E9's; nothing in these tests runs an effect through it.</summary>
internal sealed class ZoneReplacementNoInterpreter : IInterpreter
{
    public string Name => "no interpreter";

    public Outcome Resolve(GameState state, ObjectId stackObject) => throw new NotSupportedException();

    public Outcome Run(GameState state, Effect effect, EffectContext context) => Outcome.Unchanged(state);
}

/// <summary>
/// Made-up cards, so no printed card's text decides what these tests prove: a land that enters
/// tapped, a vanilla creature, and a two-faced card whose second face is a land (CR 712.12).
/// </summary>
internal sealed class ZoneReplacementDefinitions : IDefinitionStore
{
    public const string SlowLand = "oracle-slow-land";
    public const string Creature = "oracle-creature";
    public const string Modal = "oracle-modal";

    /// <summary>CR 614.12: "as this enters, it enters tapped" is part of the entering itself.</summary>
    private static readonly CardDefinition Slow = new(SlowLand, "Test Slow Field", Layout.Normal,
    [
        new Face("Test Slow Field", FaceRole.Main, new TypeLine(CardType.Land),
            Abilities: [new ReplacementAbility(new WouldEnterBattlefield(new ObjectFilter(Types: CardType.Land)), new EntersTapped(), SelfOnly: true)]),
    ]);

    private static readonly CardDefinition Beast = new(Creature, "Test Beast", Layout.Normal,
    [
        new Face("Test Beast", FaceRole.Main, new TypeLine(CardType.Creature, Subtypes: ["Beast"]), "{1}{G}", new PrintedStat("2"), new PrintedStat("2")),
    ]);

    private static readonly CardDefinition Both = new(Modal, "Test Two Ways", Layout.ModalDfc,
    [
        new Face("Test Two Ways", FaceRole.Main, new TypeLine(CardType.Creature, Subtypes: ["Beast"]), "{2}{G}", new PrintedStat("3"), new PrintedStat("3")),
        new Face("Test Two Ways Shore", FaceRole.Back, new TypeLine(CardType.Land)),
    ]);

    public CardDefinition? ByName(string name) =>
        new[] { Slow, Beast, Both }.FirstOrDefault(d => string.Equals(d.Name, name, StringComparison.Ordinal));

    public CardDefinition? ByOracleId(string oracleId) => oracleId switch
    {
        SlowLand => Slow,
        Creature => Beast,
        Modal => Both,
        _ => null,
    };
}
