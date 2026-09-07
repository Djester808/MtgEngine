using MtgEngine.Cards;
using MtgEngine.Rules.Commands;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// A real table for CR 614.1c: <see cref="Subsystems.Wave2"/> composed, no subsystem faked, and an
/// invented land whose only replacement ability chooses a creature type as it enters. Nothing here
/// is a real card — the shape is what a card file can hold, not a card.
/// </summary>
internal sealed class ReplacementEffectTestsEnteringTable
{
    internal const string CavernName = "Replacement Tests Deep Hall";
    internal const string CavernId = "replacement-tests-deep-hall";

    /// <summary>The key the land's own choice is stored under, CR 608.2d: a card's key, not the engine's.</summary>
    internal const string TypeKey = "k1";

    /// <summary>
    /// Which ability of the face the replacement is. It is deliberately not the first, so a question
    /// that carries the index proves it was computed and not defaulted to zero (CR 101.4).
    /// </summary>
    internal const int ReplacementIndex = 1;

    private readonly Game _game;

    internal ReplacementEffectTestsEnteringTable()
    {
        _game = new Game(Subsystems.Wave2(), new ReplacementEffectTestsEnteringDefinitions());
        State = _game.Create(new GameSetup(
            [new PlayerStart("One", Deck(), []), new PlayerStart("Two", Deck(), [])],
            Seed: 11,
            StartingLife: 20,
            Format: "test")).State;
    }

    internal static PlayerId P0 { get; } = new(0);

    internal GameState State { get; private set; }

    /// <summary>
    /// "{T}: Add {C}." and "As this land enters, choose a creature type." CR 614.1c: the choice is
    /// part of the entering, so the replacement names only its own arrival and replaces nothing.
    /// </summary>
    [RuleRef("614.1c")]
    internal static CardDefinition Cavern { get; } = new(
        CavernId,
        CavernName,
        Layout.Normal,
        [
            new Face(
                CavernName,
                FaceRole.Main,
                new TypeLine(CardType.Land),
                Abilities:
                [
                    new ActivatedAbility([new TapSelf()], new AddMana(new ManaProduction(Fixed: "{C}"))),
                    new ReplacementAbility(
                        new WouldEnterBattlefield(new ObjectFilter(Also: new IsThisObject())),
                        new ChooseValue(ChooseWhat.CreatureType, TypeKey),
                        SelfOnly: true),
                ]),
        ]);

    internal static ImmutableList<string> Deck() => [.. Enumerable.Repeat(CavernId, 30)];

    /// <summary>Apply one command; anything the rules refuse fails the test with the rule it cited.</summary>
    internal ImmutableList<GameEvent> Do(Command command)
    {
        var result = _game.Apply(State, command);
        if (result is Refused refused)
            Assert.Fail($"{command.GetType().Name} was refused: {refused.Reason} (CR {refused.Rule}) in turn {State.Turn.Number} {State.Turn.Step}.");

        var applied = (Applied)result;
        State = applied.State;
        return applied.Events;
    }

    /// <summary>Pass priority to this step of this turn; a question stops the walk, CR 101.4.</summary>
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

    /// <summary>Play the land and stop wherever the game stops, which is on the question it asks.</summary>
    [RuleRef("614.1c")]
    internal static ReplacementEffectTestsEnteringTable WithTheLandPlayed()
    {
        var table = new ReplacementEffectTestsEnteringTable();
        table.PassTo(1, Step.PrecombatMain);
        table.Do(new PlayLand(P0, table.InHand(P0, CavernName)));
        return table;
    }

    internal ObjectId InHand(PlayerId player, string name) =>
        State.Objects.Values
            .Where(o => o.Zone.Zone == Zone.Hand && o.Owner == player && string.Equals(o.Definition.Name, name, StringComparison.Ordinal))
            .OrderBy(o => o.Id.Value)
            .Select(o => o.Id)
            .FirstOrDefault() is { Value: > 0 } id
                ? id
                : throw new InvalidOperationException($"{player} has no {name} in hand in turn {State.Turn.Number}.");

    internal ImmutableList<ObjectId> Battlefield(PlayerId controller, string name) =>
        [.. State.Objects.Values
            .Where(o => o.Zone.Zone == Zone.Battlefield
                && string.Equals(o.Definition.Name, name, StringComparison.Ordinal)
                && o.InitialController == controller)
            .OrderBy(o => o.Id.Value)
            .Select(o => o.Id)];
}

/// <summary>The definitions this table is played with. A store, not a card file: no card here is real.</summary>
internal sealed class ReplacementEffectTestsEnteringDefinitions : IDefinitionStore
{
    public CardDefinition? ByName(string name) =>
        string.Equals(name, ReplacementEffectTestsEnteringTable.CavernName, StringComparison.Ordinal)
            ? ReplacementEffectTestsEnteringTable.Cavern
            : null;

    public CardDefinition? ByOracleId(string oracleId) =>
        string.Equals(oracleId, ReplacementEffectTestsEnteringTable.CavernId, StringComparison.Ordinal)
            ? ReplacementEffectTestsEnteringTable.Cavern
            : null;
}
