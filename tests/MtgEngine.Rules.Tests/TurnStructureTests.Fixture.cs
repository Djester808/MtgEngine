using MtgEngine.Cards;
using MtgEngine.Rules.Commands;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// A table to run turns on, and the two seams the turn system is built from as fakes: the zone
/// system's draw and the casting system's resolution. Both record what they were asked, which
/// is how "the draw was skipped" and "the top of the stack resolved" are asserted without the
/// subsystems that own them.
/// </summary>
internal static class TurnFixture
{
    internal static PlayerId P0 { get; } = new(0);

    internal static PlayerId P1 { get; } = new(1);

    internal static PlayerId P2 { get; } = new(2);

    /// <summary>A creature with no abilities: enough to be a permanent that taps, untaps and takes damage.</summary>
    internal static CardDefinition Vanilla { get; } = new(
        "00000000-0000-0000-0000-000000000001",
        "Test Permanent",
        Layout.Normal,
        [new Face("Test Permanent", FaceRole.Main, new TypeLine(CardType.Creature), "{1}", new PrintedStat("1"), new PrintedStat("1"))]);

    internal static GameState Table(int players) =>
        GameReducer.Replay([new GameStarted(
            [.. Enumerable.Range(0, players).Select(i => new PlayerStart($"Seat {i}", [], []))],
            Seed: 7,
            StartingLife: 20,
            Format: "test")]);

    internal static GameState At(this GameState state, Step step, PlayerId active, int number = 1) =>
        state with { Turn = state.Turn with { Step = step, ActivePlayer = active, Number = number } };

    internal static GameState Holding(this GameState state, PlayerId player) =>
        state with { Priority = new PriorityState(player, []) };

    internal static GameState With(this GameState state, params GameObject[] objects)
    {
        foreach (var o in objects)
            state = new ObjectCreated(o, -1).Apply(state);
        return state;
    }

    internal static GameObject Permanent(int id, PlayerId controller, bool tapped = false, bool sick = false, int damage = 0) =>
        new(new ObjectId(id), Vanilla, 0, controller, controller, ZoneKey.Of(Zone.Battlefield, controller), id)
        {
            Tapped = tapped,
            SummoningSick = sick,
            Damage = damage,
        };

    internal static GameObject InHand(int id, PlayerId owner) =>
        new(new ObjectId(id), Vanilla, 0, owner, owner, ZoneKey.Of(Zone.Hand, owner), id) { SummoningSick = false };

    internal static GameObject OnStack(int id, PlayerId controller) =>
        new(new ObjectId(id), Vanilla, 0, controller, controller, ZoneKey.Shared(Zone.Stack), id)
        {
            SummoningSick = false,
            Stack = new StackEntry(null, null, [], [], [], null, IsCopy: false),
        };

    internal static ActiveEffect Effect(int id, Duration until) =>
        new(id, new CantUntap(), null, P0, [], [], until, id, FromStaticAbility: false);

    internal static List<Step> StepsIn(IEnumerable<GameEvent> events) =>
        [.. events.OfType<StepBegan>().Select(e => e.Step)];

    /// <summary>
    /// The steps of a turn in which nobody attacks, CR 500.1: every step but the first-strike
    /// combat damage step, which exists only when a creature in combat has first or double
    /// strike, CR 510.4.
    /// </summary>
    [RuleRef("510.4")]
    internal static List<Step> TurnWithNoCombat { get; } =
    [
        Step.Untap,
        Step.Upkeep,
        Step.Draw,
        Step.PrecombatMain,
        Step.BeginningOfCombat,
        Step.DeclareAttackers,
        Step.DeclareBlockers,
        Step.CombatDamage,
        Step.EndOfCombat,
        Step.PostcombatMain,
        Step.End,
        Step.Cleanup,
    ];

    /// <summary>
    /// A combat in progress: these creatures are attacking the other player, and the blocks are
    /// whatever the test declares. Enough of CR 506.1 for the turn system, which asks only
    /// whether anyone is in combat and what they are, never how the declaration was made.
    /// </summary>
    [RuleRef("506.1")]
    internal static GameState InCombat(this GameState state, PlayerId attacker, params ObjectId[] attackers) =>
        state with
        {
            Combat = new CombatState(
                attacker,
                attackers.ToImmutableDictionary(a => a, _ => new CombatTarget(new PlayerId((attacker.Value + 1) % state.Players.Count), null)),
                ImmutableDictionary<ObjectId, ImmutableList<ObjectId>>.Empty,
                AttackersDeclared: true,
                BlockersDeclared: false),
        };

    /// <summary>A creature as the layer system computes it: whatever the test says it is, CR 613.</summary>
    [RuleRef("613")]
    internal static Characteristics Creature(PlayerId controller, params KeywordKind[] keywords) =>
        new(
            "Test Permanent",
            CardType.Creature,
            Supertype.None,
            [],
            Color.Colorless,
            1,
            1,
            null,
            null,
            null,
            [.. keywords.Select(k => (Ability)new KeywordAbility(k))],
            controller);

    /// <summary>
    /// Play a game forward by passing priority with whoever holds it, until the turn number
    /// reaches <paramref name="untilTurn"/> or the game stops to ask something.
    /// </summary>
    internal static (GameState State, List<GameEvent> Log) PassUntilTurn(this Rules.Turn.TurnSystem turns, GameState state, int untilTurn)
    {
        var log = new List<GameEvent>();
        var begun = turns.Begin(state);
        log.AddRange(begun.Events);
        state = begun.State;

        for (var guard = 0; guard < 500 && state.Turn.Number < untilTurn && state.Pending is null; guard++)
        {
            var holder = state.Priority.Holder ?? throw new InvalidOperationException($"Nobody holds priority in {state.Turn.Step}.");
            var passed = turns.Pass(state, holder);
            log.AddRange(passed.Events);
            state = passed.State;
        }

        return (state, log);
    }

    internal sealed class FakeZones : IZoneSystem
    {
        public List<(PlayerId Player, int Count)> Draws { get; } = [];

        public string Name => "fake zones";

        public Outcome Create(GameSetup setup, IDefinitionStore store) => throw new NotSupportedException();

        public Outcome Move(GameState state, ObjectId what, ZoneKey to, Placement where = Placement.Default, bool tapped = false, PlayerId? controller = null) =>
            throw new NotSupportedException();

        public Outcome Draw(GameState state, PlayerId player, int count)
        {
            Draws.Add((player, count));
            return Outcome.Unchanged(state);
        }

        public Outcome Shuffle(GameState state, PlayerId player) => throw new NotSupportedException();

        public Outcome CreateToken(GameState state, TokenSpec token, PlayerId controller, bool tapped, bool attacking) =>
            throw new NotSupportedException();
    }

    internal sealed class FakeCasting : ICastingSystem
    {
        public List<ObjectId> Resolved { get; } = [];

        public string Name => "fake casting";

        public Outcome Cast(GameState state, CastSpell command) => throw new NotSupportedException();

        public Outcome Activate(GameState state, ActivateAbility command) => throw new NotSupportedException();

        public Outcome PlayLand(GameState state, PlayLand command) => throw new NotSupportedException();

        /// <summary>Enough of CR 608.2 for the turn system: the top object leaves the stack.</summary>
        public Outcome ResolveTop(GameState state)
        {
            var stack = state.In(ZoneKey.Shared(Zone.Stack));
            if (stack.IsEmpty)
                return Outcome.Unchanged(state);
            var top = stack[^1];
            Resolved.Add(top);
            ImmutableList<GameEvent> events = [new StackObjectResolved(top), new ObjectCeasedToExist(top)];
            return new Outcome(GameReducer.Apply(state, events), events);
        }

        public Refusal? Check(GameState state, Command command) => null;
    }
}
