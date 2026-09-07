using MtgEngine.Cards;
using MtgEngine.Rules.Choices;
using MtgEngine.Rules.Commands;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;
using MtgEngine.Rules.Turn;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// A real table that offers mulligans: the wave-1 subsystems, the real choice system, and the
/// one join between them that CR 103.5 needs and no single subsystem owns — once a player's
/// declaration is finished, the turn system is asked who declares next, or begins turn 1.
/// <para>
/// That join is written here as a decorator rather than faked, because it is one line inside
/// <c>ChoiceSystem.Resume</c> that only the coordinator may add (see <c>seam-requests/E2b.md</c>).
/// Everything it calls is the turn system's own code; nothing about the mulligan rules is
/// implemented in this file.
/// </para>
/// </summary>
[RuleRef("103.5")]
internal sealed class MulliganTable
{
    internal const string LandOracleId = "mulligan-land";
    internal const string BeastOracleId = "mulligan-beast";

    private readonly Game _game;

    internal MulliganTable(int players = 2, bool offerMulligans = true, string format = "test", ulong seed = 7)
    {
        var wave = Subsystems.Wave1();
        var turns = new TurnSystem(wave.Zones, wave.Casting, wave.Combat);
        var subsystems = wave with
        {
            Turn = turns,
            Choices = new MulliganChoices(new ChoiceSystem(wave.Zones, turns), turns),
        };

        _game = new Game(subsystems, new MulliganDefinitions());
        var created = _game.Create(new GameSetup(
            [.. Enumerable.Range(0, players).Select(i => new PlayerStart($"Seat {i}", Deck(), []))],
            Seed: seed,
            StartingLife: 20,
            Format: format,
            OfferMulligans: offerMulligans));
        State = created.State;
        Log.AddRange(created.Events);
    }

    internal static PlayerId P0 { get; } = new(0);

    internal static PlayerId P1 { get; } = new(1);

    internal GameState State { get; private set; }

    internal List<GameEvent> Log { get; } = [];

    internal PendingChoice Pending => State.Pending ?? throw new InvalidOperationException("The game is not waiting on anything.");

    /// <summary>The cards this player holds, by object id, so a redraw is visibly a different hand.</summary>
    internal ImmutableList<ObjectId> Hand(PlayerId player) => State.In(ZoneKey.Of(Zone.Hand, player));

    /// <summary>Answer the open question with these option ids, and keep what the engine did.</summary>
    internal ImmutableList<GameEvent> Answer(params string[] options)
    {
        var pending = Pending;
        var result = _game.Apply(State, new AnswerChoice(pending.Player, pending.Id, [.. options]));
        if (result is Refused refused)
            Assert.Fail($"The answer was refused: {refused.Reason} (CR {refused.Rule}).");

        var applied = (Applied)result;
        State = applied.State;
        Log.AddRange(applied.Events);
        return applied.Events;
    }

    /// <summary>Keep the hand on offer, whoever is being asked.</summary>
    internal ImmutableList<GameEvent> Keep() => Answer(ChoiceKinds.Keep);

    internal ImmutableList<GameEvent> Mulligan() => Answer(ChoiceKinds.Mulligan);

    /// <summary>Ten cards, so a mulligan has something to shuffle back into and redraw from.</summary>
    private static ImmutableList<string> Deck() =>
        [.. Enumerable.Repeat(LandOracleId, 6), .. Enumerable.Repeat(BeastOracleId, 4)];

    /// <summary>
    /// The join CR 103.5 needs between the two subsystems: the choice system answers a mulligan
    /// question, and when that leaves nothing pending the turn system says what happens next.
    /// </summary>
    [RuleRef("103.5")]
    private sealed class MulliganChoices(IChoiceSystem inner, TurnSystem turns) : IChoiceSystem
    {
        public string Name => "choices, with the mulligan loop joined up";

        public Outcome Answer(GameState state, AnswerChoice command)
        {
            var asked = state.Pending;
            var answered = inner.Answer(state, command);
            var mulligan = asked?.Kind is ChoiceKinds.Mulligan or ChoiceKinds.MulliganBottom;
            return mulligan && answered.State.Pending is null
                ? answered.Then(s => turns.AfterMulligans(s, asked!.Player))
                : answered;
        }
    }

    /// <summary>Two made-up definitions: a land and a vanilla creature. No card in this test is a real one.</summary>
    private sealed class MulliganDefinitions : IDefinitionStore
    {
        private static readonly CardDefinition LandCard = Definition(LandOracleId, "Mulligan Grove", new TypeLine(CardType.Land, Supertype.Basic));

        private static readonly CardDefinition BeastCard = Definition(BeastOracleId, "Mulligan Ox", new TypeLine(CardType.Creature, Subtypes: ["Ox"]));

        public CardDefinition? ByName(string name) => name switch
        {
            "Mulligan Grove" => LandCard,
            "Mulligan Ox" => BeastCard,
            _ => null,
        };

        public CardDefinition? ByOracleId(string oracleId) => oracleId switch
        {
            LandOracleId => LandCard,
            BeastOracleId => BeastCard,
            _ => null,
        };

        private static CardDefinition Definition(string oracleId, string name, TypeLine types) =>
            new(oracleId, name, Layout.Normal, [new Face(name, FaceRole.Main, types)]);
    }
}
