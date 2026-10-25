using MtgEngine.Cards;
using MtgEngine.Rules.Commands;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// A real table for the wave-1 exit test: the wave-1 subsystems composed, two made-up
/// definitions, and a way to drive the game one command at a time while keeping the log.
/// Nothing here fakes a subsystem — that is the point of the exercise — so every helper is
/// either a command or a question asked of the state the engine produced.
/// </summary>
internal sealed class IntegrationTable
{
    internal const string LandName = "Test Grove";
    internal const string BeastName = "Test Ox";
    internal const string LandOracleId = "integration-land";
    internal const string BeastOracleId = "integration-beast";

    private readonly Game _game;

    internal IntegrationTable(int startingLife = 2, ulong seed = 7)
    {
        _game = new Game(LegacySubsystems.Create(), new IntegrationDefinitions());
        var created = _game.Create(new GameSetup(
            [new PlayerStart("One", [.. Deck()], []), new PlayerStart("Two", [.. Deck()], [])],
            Seed: seed,
            StartingLife: startingLife,
            Format: "test"));
        State = created.State;
        Log.AddRange(created.Events);
    }

    internal static PlayerId P0 { get; } = new(0);

    internal static PlayerId P1 { get; } = new(1);

    internal GameState State { get; private set; }

    internal List<GameEvent> Log { get; } = [];

    /// <summary>A land whose only ability is "{T}: Add {G}", spelled exactly as the template stage spells it.</summary>
    [RuleRef("605")]
    internal static CardDefinition Land { get; } = Definition(
        LandOracleId,
        LandName,
        new TypeLine(CardType.Land, Supertype.Basic),
        cost: null,
        abilities: [new ActivatedAbility([new TapSelf()], new AddMana(new ManaProduction(Fixed: "{G}")))]);

    /// <summary>A vanilla 2/2 for {1}{G}: no abilities, so nothing but the rules moves it.</summary>
    internal static CardDefinition Beast { get; } = Definition(
        BeastOracleId,
        BeastName,
        new TypeLine(CardType.Creature, Subtypes: ["Ox"]),
        cost: "{1}{G}",
        power: 2,
        toughness: 2);

    /// <summary>Apply one command and keep its events. Anything the rules refuse fails the test here, with the rule.</summary>
    internal ImmutableList<GameEvent> Do(Command command)
    {
        var result = _game.Apply(State, command);
        if (result is Refused refused)
            Assert.Fail($"{command.GetType().Name} was refused: {refused.Reason} (CR {refused.Rule}) in turn {State.Turn.Number} {State.Turn.Step}.");

        var applied = (Applied)result;
        State = applied.State;
        Log.AddRange(applied.Events);
        return applied.Events;
    }

    /// <summary>The refusal a command is given now. Used where a rule is proved by what the engine will not do.</summary>
    internal Refused Refuse(Command command) => Assert.IsType<Refused>(_game.Apply(State, command));

    /// <summary>
    /// Pass priority, with whoever holds it, until the game reaches this step of this turn — or
    /// until it ends. Every pass is a real <see cref="PassPriority"/> command, so the steps in
    /// between happen exactly as they would at a table.
    /// </summary>
    internal void PassTo(int turn, Step step)
    {
        for (var guard = 0; guard < 500; guard++)
        {
            if (State.Outcome is not null)
                return;

            // Nothing in this game asks a question; one would stall the game, CR 101.4.
            Assert.Null(State.Pending);
            if (State.Turn.Number == turn && State.Turn.Step == step)
                return;

            // Declarations are turn-based actions before priority, not passes that skip a step.
            if (State.Priority.Holder is null && State.Turn.Step == Step.DeclareAttackers)
            {
                Do(new DeclareAttackers(State.Turn.ActivePlayer, []));
                continue;
            }
            if (State.Priority.Holder is null && State.Turn.Step == Step.DeclareBlockers && State.Combat is { } fight)
            {
                var defender = fight.DefendingPlayers.First(p => !fight.BlockersDeclaredBy.Contains(p));
                Do(new DeclareBlockers(defender, []));
                continue;
            }

            var holder = State.Priority.Holder ?? throw new InvalidOperationException($"Nobody holds priority in turn {State.Turn.Number} {State.Turn.Step}.");
            Do(new PassPriority(holder));
        }

        throw new InvalidOperationException($"Never reached turn {turn} {step}; stopped in turn {State.Turn.Number} {State.Turn.Step}.");
    }

    /// <summary>Both players pass with an empty stack, which ends the step, CR 117.4.</summary>
    internal ImmutableList<GameEvent> PassRound()
    {
        var events = Do(new PassPriority(State.Priority.Holder!.Value));
        return State.Outcome is null && State.Priority.Holder is { } next
            ? events.AddRange(Do(new PassPriority(next)))
            : events;
    }

    internal ObjectId InHand(PlayerId player, string name) =>
        State.Objects.Values
            .Where(o => o.Zone.Zone == Zone.Hand && o.Owner == player && o.Definition.Name == name)
            .OrderBy(o => o.Id.Value)
            .Select(o => o.Id)
            .FirstOrDefault() is { Value: > 0 } id
            ? id
            : throw new InvalidOperationException($"{player} has no {name} in hand in turn {State.Turn.Number}.");

    internal ImmutableList<ObjectId> Battlefield(PlayerId controller, string name) =>
        [.. State.Objects.Values
            .Where(o => o.Zone.Zone == Zone.Battlefield && o.Definition.Name == name && o.InitialController == controller)
            .OrderBy(o => o.Id.Value)
            .Select(o => o.Id)];

    /// <summary>A zone change makes a new object, CR 400.7; this is the id the move gave it.</summary>
    internal static ObjectId Became(IEnumerable<GameEvent> events, ObjectId from) =>
        events.OfType<ObjectMoved>().Last(m => m.From == from).To.Id;

    /// <summary>Six lands and four creatures. Seven are drawn, so a hand always holds at least three lands and one creature.</summary>
    private static ImmutableList<string> Deck() =>
        [.. Enumerable.Repeat(LandOracleId, 6), .. Enumerable.Repeat(BeastOracleId, 4)];

    private static CardDefinition Definition(string oracleId, string name, TypeLine types, string? cost, int? power = null, int? toughness = null, IReadOnlyList<Ability>? abilities = null) =>
        new(
            oracleId,
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

    /// <summary>The two definitions the game is played with. A store, not a card file: no card in this test is a real one.</summary>
    private sealed class IntegrationDefinitions : IDefinitionStore
    {
        public CardDefinition? ByName(string name) => name switch
        {
            LandName => Land,
            BeastName => Beast,
            _ => null,
        };

        public CardDefinition? ByOracleId(string oracleId) => oracleId switch
        {
            LandOracleId => Land,
            BeastOracleId => Beast,
            _ => null,
        };
    }
}
