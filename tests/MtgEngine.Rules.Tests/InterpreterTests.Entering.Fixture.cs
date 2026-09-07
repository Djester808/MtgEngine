using MtgEngine.Cards;
using MtgEngine.Rules.Commands;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// A real table for the two roads above: <see cref="Subsystems.Wave2"/> composed, made-up
/// definitions with the two shapes that stalled — a land that chooses a creature type as it enters
/// (CR 614.1c) and an instant that exiles a permanent and then offers its controller a search
/// (CR 608.2 with 701.23a). Nothing here fakes a subsystem. Every card below is invented.
/// </summary>
internal sealed class InterpreterTestsEnteringTable
{
    internal const string HallName = "Interpreter Tests Hall";
    internal const string PlainName = "Interpreter Tests Flat";
    internal const string WoodName = "Interpreter Tests Wood";
    internal const string TrailName = "Interpreter Tests Trail";

    internal const string HallId = "interpreter-tests-hall";
    internal const string PlainId = "interpreter-tests-flat";
    internal const string WoodId = "interpreter-tests-wood";
    internal const string TrailId = "interpreter-tests-trail";

    /// <summary>The key the land's own choice is stored under, CR 608.2d: a card's key, not the engine's.</summary>
    internal const string TypeKey = "k1";

    /// <summary>The key the spell remembers its target under, so a later step can name its controller.</summary>
    internal const string TargetKey = "k2";

    private readonly Game _game;

    internal InterpreterTestsEnteringTable(ImmutableList<string> deckZero, ImmutableList<string> deckOne)
    {
        _game = new Game(Subsystems.Wave2(), new InterpreterTestsEnteringDefinitions());
        var created = _game.Create(new GameSetup(
            [new PlayerStart("One", deckZero, []), new PlayerStart("Two", deckOne, [])],
            Seed: 7,
            StartingLife: 20,
            Format: "test"));
        State = created.State;
    }

    internal static PlayerId P0 { get; } = new(0);

    internal static PlayerId P1 { get; } = new(1);

    internal GameState State { get; private set; }

    /// <summary>
    /// "As this land enters, choose a creature type." CR 614.1c: the choice is part of the entering,
    /// so it is a replacement of the object's own arrival and nothing else is replaced.
    /// </summary>
    [RuleRef("614.1c")]
    internal static CardDefinition Hall { get; } = Definition(
        HallId,
        HallName,
        new TypeLine(CardType.Land),
        abilities:
        [
            new ReplacementAbility(
                new WouldEnterBattlefield(new ObjectFilter(Also: new IsThisObject())),
                new ChooseValue(ChooseWhat.CreatureType, TypeKey),
                SelfOnly: true),
            new ActivatedAbility([new TapSelf()], new AddMana(new ManaProduction(Fixed: "{C}"))),
        ]);

    /// <summary>"{T}: Add {W}", CR 605.1a: what the instant below is paid with.</summary>
    [RuleRef("605.1a")]
    internal static CardDefinition Flat { get; } = Definition(
        PlainId,
        PlainName,
        new TypeLine(CardType.Land, Supertype.Basic),
        abilities: [new ActivatedAbility([new TapSelf()], new AddMana(new ManaProduction(Fixed: "{W}")))]);

    /// <summary>The other player's basic land: the spell's target, and what their search finds.</summary>
    [RuleRef("605.1a")]
    internal static CardDefinition Wood { get; } = Definition(
        WoodId,
        WoodName,
        new TypeLine(CardType.Land, Supertype.Basic),
        abilities: [new ActivatedAbility([new TapSelf()], new AddMana(new ManaProduction(Fixed: "{G}")))]);

    /// <summary>
    /// "Exile target land. Its controller may search their library for a basic land card, put that
    /// card onto the battlefield tapped, then shuffle." CR 608.2 with 701.23a: one resolution that
    /// asks twice, of a player who is not the spell's controller.
    /// </summary>
    [RuleRef("608.2")]
    internal static CardDefinition Trail { get; } = Definition(
        TrailId,
        TrailName,
        new TypeLine(CardType.Instant),
        cost: "{W}",
        abilities:
        [
            new SpellAbility(new Sequence(
            [
                new Exile(new Target(new ObjectFilter(Types: CardType.Land), Key: TargetKey)),
                new May(
                    new Search(
                        new ObjectFilter(Types: CardType.Land, Supertypes: Supertype.Basic),
                        Zone.Battlefield,
                        Who: new ControllerOf(new Remembered(TargetKey)),
                        Tapped: true),
                    Who: new ControllerOf(new Remembered(TargetKey))),
            ])),
        ]);

    /// <summary>
    /// The spell announced and still on the stack, CR 601.2: player one plays a land, player two
    /// plays theirs, and on turn three the instant is cast at player two's land.
    /// </summary>
    [RuleRef("601.2")]
    internal static InterpreterTestsEnteringTable WithASpellOnTheStack()
    {
        var table = new InterpreterTestsEnteringTable(TrailDeck(), WoodDeck());
        table.PassTo(1, Step.PrecombatMain);
        table.Do(new PlayLand(P0, table.InHand(P0, PlainName)));
        table.PassTo(2, Step.PrecombatMain);
        table.Do(new PlayLand(P1, table.InHand(P1, WoodName)));
        table.PassTo(3, Step.PrecombatMain);

        var land = Assert.Single(table.Battlefield(P0, PlainName));
        table.Do(new ActivateAbility(P0, land, 0));
        var target = Assert.Single(table.Battlefield(P1, WoodName));
        table.Do(new CastSpell(P0, table.InHand(P0, TrailName), Targets: [target]));
        return table;
    }

    /// <summary>Fifteen of each, so an opening hand holds both the instant and something to pay with.</summary>
    internal static ImmutableList<string> TrailDeck() =>
        [.. Enumerable.Repeat(TrailId, 15).Concat(Enumerable.Repeat(PlainId, 15))];

    internal static ImmutableList<string> WoodDeck() => [.. Enumerable.Repeat(WoodId, 30)];

    /// <summary>Apply one command and keep its events; anything the rules refuse fails the test with the rule.</summary>
    internal ImmutableList<GameEvent> Do(Command command)
    {
        var result = _game.Apply(State, command);
        if (result is Refused refused)
            Assert.Fail($"{command.GetType().Name} was refused: {refused.Reason} (CR {refused.Rule}) in turn {State.Turn.Number} {State.Turn.Step}.");

        var applied = (Applied)result;
        State = applied.State;
        return applied.Events;
    }

    /// <summary>The refusal a command is given now, where a rule is proved by what the engine will not do.</summary>
    internal Refused Refuse(Command command) => Assert.IsType<Refused>(_game.Apply(State, command));

    /// <summary>Pass priority to this step of this turn. A question stops the walk, CR 101.4.</summary>
    [RuleRef("101.4")]
    internal void PassTo(int turn, Step step)
    {
        for (var guard = 0; guard < 500; guard++)
        {
            if (State.Outcome is not null)
                return;
            if (State.Pending is { Kind: Choices.ChoiceKinds.Discard } discard)
            {
                Do(new AnswerChoice(discard.Player, discard.Id, [.. discard.Options.Take(Math.Max(discard.Min, 1)).Select(o => o.Id)]));
                continue;
            }

            if (State.Pending is { } pending)
                throw new InvalidOperationException($"{pending.Player} was asked \"{pending.Prompt}\" ({pending.Kind}) in turn {State.Turn.Number} {State.Turn.Step}; nothing else happens until it is answered (CR 101.4).");
            if (State.Turn.Number == turn && State.Turn.Step == step)
                return;

            var holder = State.Priority.Holder ?? throw new InvalidOperationException($"Nobody holds priority in turn {State.Turn.Number} {State.Turn.Step}.");
            Do(new PassPriority(holder));
        }

        throw new InvalidOperationException($"Never reached turn {turn} {step}; stopped in turn {State.Turn.Number} {State.Turn.Step}.");
    }

    /// <summary>Pass priority until the game stops to ask something, CR 101.4: the question is the destination.</summary>
    [RuleRef("101.4")]
    internal PendingChoice PassUntilQuestion()
    {
        for (var guard = 0; guard < 200; guard++)
        {
            if (State.Pending is { } pending)
                return pending;
            if (State.Outcome is not null || State.Priority.Holder is not { } holder)
                break;
            Do(new PassPriority(holder));
        }

        throw new InvalidOperationException($"Nothing was asked; the game reached turn {State.Turn.Number} {State.Turn.Step}.");
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

    private static CardDefinition Definition(
        string oracleId,
        string name,
        TypeLine types,
        string? cost = null,
        IReadOnlyList<Ability>? abilities = null) =>
        new(oracleId, name, Layout.Normal, [new Face(name, FaceRole.Main, types, cost, Abilities: abilities)]);
}

/// <summary>The definitions this table is played with. A store, not a card file: no card here is real.</summary>
internal sealed class InterpreterTestsEnteringDefinitions : IDefinitionStore
{
    private static readonly CardDefinition[] All =
    [
        InterpreterTestsEnteringTable.Hall,
        InterpreterTestsEnteringTable.Flat,
        InterpreterTestsEnteringTable.Wood,
        InterpreterTestsEnteringTable.Trail,
    ];

    public CardDefinition? ByName(string name) => Array.Find(All, d => string.Equals(d.Name, name, StringComparison.Ordinal));

    public CardDefinition? ByOracleId(string oracleId) => Array.Find(All, d => string.Equals(d.OracleId, oracleId, StringComparison.Ordinal));
}
