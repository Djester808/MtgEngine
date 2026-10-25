using MtgEngine.Cards;
using MtgEngine.Rules.Commands;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Tests;

public sealed class MultiplayerBoardFlowTests
{
    private static PlayerId P(int n) => new(n);

    private static readonly CardDefinition Creature = new("board-creature", "Board fixture", Layout.Normal,
        [new Face("Board fixture", FaceRole.Main, new TypeLine(CardType.Creature, Supertype.None, []), "{0}", new PrintedStat("2"), new PrintedStat("2"))]);

    private static Game Engine => new(Subsystems.Wave2(), new Definitions());

    private static GameState Table(Step step = Step.DeclareAttackers, int count = 4)
    {
        var state = GameReducer.Replay([new GameStarted(
            [.. Enumerable.Range(0, count).Select(n => new PlayerStart($"P{n}", [], []))], 42, 40, "test")]);
        return state with { Turn = new TurnState(1, P(0), step, 0), Priority = PriorityState.None };
    }

    private static GameState Put(GameState state, int id, int owner, CardDefinition? definition = null, Zone zone = Zone.Battlefield)
    {
        var key = zone is Zone.Hand or Zone.Library or Zone.Graveyard ? ZoneKey.Of(zone, P(owner)) : ZoneKey.Shared(zone);
        var obj = new GameObject(new ObjectId(id), definition ?? Creature, 0, P(owner), P(owner), key, id) { SummoningSick = false };
        return state with { Objects = state.Objects.Add(obj.Id, obj), Zones = state.Zones.SetItem(key, state.In(key).Add(obj.Id)), NextObjectId = Math.Max(state.NextObjectId, id + 1) };
    }

    private static GameState Apply(GameState state, Command command) => Assert.IsType<Applied>(Engine.Apply(state, command)).State;

    private static GameState PassAll(GameState state)
    {
        for (var n = 0; n < state.Players.Count; n++)
            state = Apply(state, new PassPriority(state.Priority.Holder ?? throw new InvalidOperationException("No priority to pass.")));
        return state;
    }

    [Fact]
    public void All_defenders_declare_before_priority_and_earlier_blocks_survive()
    {
        var state = Put(Put(Put(Put(Table(), 1, 0), 2, 0), 3, 1), 4, 2);
        state = Apply(state, new DeclareAttackers(P(0), [new(new ObjectId(1), new CombatTarget(P(1), null)), new(new ObjectId(2), new CombatTarget(P(2), null))]));
        state = PassAll(state);
        Assert.Equal(Step.DeclareBlockers, state.Turn.Step);
        Assert.Null(state.Priority.Holder);
        state = Apply(state, new DeclareBlockers(P(1), [new(new ObjectId(3), new ObjectId(1))]));
        Assert.False(state.Combat!.BlockersDeclared);
        Assert.Null(state.Priority.Holder);
        state = Apply(state, new DeclareBlockers(P(2), [new(new ObjectId(4), new ObjectId(2))]));
        Assert.True(state.Combat!.BlockersDeclared);
        Assert.Equal(new ObjectId(3), Assert.Single(state.Combat.Blockers[new ObjectId(1)]));
        Assert.Equal(new ObjectId(4), Assert.Single(state.Combat.Blockers[new ObjectId(2)]));
        Assert.Equal(P(0), state.Priority.Holder);
    }

    [Fact]
    public void Unattacked_players_cannot_finish_blocking_for_the_defenders()
    {
        var state = Apply(Put(Table(), 1, 0), new DeclareAttackers(P(0), [new(new ObjectId(1), new CombatTarget(P(2), null))]));
        state = PassAll(state);
        Assert.IsType<Refused>(Engine.Apply(state, new DeclareBlockers(P(1), [])));
    }

    [Fact]
    public void Declare_attackers_has_no_priority_until_the_declaration_is_complete()
    {
        var state = Table(Step.BeginningOfCombat) with { Priority = new PriorityState(P(0), []) };
        state = PassAll(state);
        Assert.Equal(Step.DeclareAttackers, state.Turn.Step);
        Assert.Null(state.Priority.Holder);
        Assert.IsType<Refused>(Engine.Apply(state, new PassPriority(P(0))));
        state = Apply(state, new DeclareAttackers(P(0), []));
        Assert.Equal(P(0), state.Priority.Holder);
    }

    [Fact]
    public void No_attackers_skips_blockers_and_both_damage_steps()
    {
        var state = Apply(Table(), new DeclareAttackers(P(0), []));
        state = PassAll(state);
        Assert.Equal(Step.EndOfCombat, state.Turn.Step);
    }

    [Fact]
    public void A_response_resets_every_earlier_pass()
    {
        var spell = new CardDefinition("board-spell", "Board spell fixture", Layout.Normal,
            [new Face("Board spell fixture", FaceRole.Main, new TypeLine(CardType.Instant, Supertype.None, []), "{0}", Abilities: [new SpellAbility(new Draw(new Fixed(0)))])]);
        var state = Put(Table(Step.Upkeep), 5, 1, spell, Zone.Hand) with { Priority = new PriorityState(P(1), [P(0)]) };
        state = Apply(state, new CastSpell(P(1), new ObjectId(5), 0));
        Assert.Empty(state.Priority.PassedInSuccession);
        foreach (var n in new[] { 1, 2, 3 })
            state = Apply(state, new PassPriority(P(n)));
        Assert.Single(state.In(ZoneKey.Shared(Zone.Stack)));
        state = Apply(state, new PassPriority(P(0)));
        Assert.Empty(state.In(ZoneKey.Shared(Zone.Stack)));
        Assert.Equal(Step.Upkeep, state.Turn.Step);
    }

    [Fact]
    public void A_mana_ability_also_resets_every_earlier_pass()
    {
        var land = new CardDefinition("board-land", "Mountain fixture", Layout.Normal,
            [new Face("Mountain fixture", FaceRole.Main, new TypeLine(CardType.Land, Supertype.Basic, ["Mountain"]))]);
        var state = Put(Table(Step.Upkeep), 6, 1, land) with { Priority = new PriorityState(P(1), [P(0)]) };
        state = Apply(state, new ActivateAbility(P(1), new ObjectId(6), 0));
        Assert.Empty(state.Priority.PassedInSuccession);
        Assert.Equal(1, state.Player(P(1)).Mana.Red);
        Assert.True(state.Objects[new ObjectId(6)].Tapped);
        Assert.Equal(P(1), state.Priority.Holder);
    }

    [Fact]
    public void The_first_duel_turn_skips_the_draw_step_itself()
    {
        var state = Table(Step.Upkeep, 2) with { Priority = new PriorityState(P(0), []) };
        state = PassAll(state);
        Assert.Equal(Step.PrecombatMain, state.Turn.Step);
    }

    [Fact]
    public void The_command_boundary_rejects_attack_declarations_during_main_phase()
    {
        var state = Put(Table(Step.PrecombatMain), 1, 0) with { Priority = new PriorityState(P(0), []) };
        Assert.IsType<Refused>(Engine.Apply(state, new DeclareAttackers(P(0), [new(new ObjectId(1), new CombatTarget(P(1), null))])));
    }

    [Fact]
    public void A_full_table_round_untaps_only_the_active_seat_and_draws_for_each_player()
    {
        var state = Table(Step.PostcombatMain) with { Priority = new PriorityState(P(0), []) };
        for (var seat = 0; seat < 4; seat++)
        {
            state = Put(state, seat + 1, seat);
            state = state with { Objects = state.Objects.SetItem(new ObjectId(seat + 1), state.Objects[new ObjectId(seat + 1)] with { Tapped = true, SummoningSick = true }) };
            for (var card = 0; card < 4; card++)
                state = Put(state, 10 + seat * 4 + card, seat, zone: Zone.Library);
        }

        for (var turn = 1; turn <= 4; turn++)
        {
            var guard = 0;
            while (state.Turn.ActivePlayer != P(turn % 4))
            {
                Assert.True(++guard < 100, "The table must advance without getting stuck.");
                state = state.Turn.Step == Step.DeclareAttackers && state.Combat?.AttackersDeclared != true
                    ? Apply(state, new DeclareAttackers(state.Turn.ActivePlayer, []))
                    : Apply(state, new PassPriority(state.Priority.Holder ?? throw new InvalidOperationException("Unexpected priority gap.")));
            }
            Assert.Equal(Step.Upkeep, state.Turn.Step);
            Assert.Equal(P(turn % 4), state.Priority.Holder);
            Assert.False(state.Objects[new ObjectId(turn % 4 + 1)].Tapped);
            Assert.False(state.Objects[new ObjectId(turn % 4 + 1)].SummoningSick);
            if (turn < 4)
                Assert.True(state.Objects[new ObjectId(1)].Tapped);

            state = PassAll(state);
            Assert.Equal(Step.Draw, state.Turn.Step);
            Assert.Single(state.In(ZoneKey.Of(Zone.Hand, P(turn % 4))));
            Assert.Equal(3, state.In(ZoneKey.Of(Zone.Library, P(turn % 4))).Count);
            state = PassAll(state);
            Assert.Equal(Step.PrecombatMain, state.Turn.Step);
        }
        Assert.Equal(5, state.Turn.Number);
        Assert.All(state.Players, player => Assert.False(player.HasLost));
    }

    [Fact]
    public void Leaving_a_step_empties_every_mana_pool_before_the_next_priority_round()
    {
        var state = Table(Step.PostcombatMain) with { Priority = new PriorityState(P(0), []) };
        state = state with { Players = state.Players.Select(player => player with { Mana = new ManaPool(0, 1, 0, 0, 2, 0) }).ToImmutableList() };
        state = PassAll(state);
        Assert.Equal(Step.End, state.Turn.Step);
        Assert.All(state.Players, player => Assert.Equal(ManaPool.Empty, player.Mana));
        Assert.Empty(state.Priority.PassedInSuccession);
        Assert.Equal(P(0), state.Priority.Holder);
    }

    private sealed class Definitions : IDefinitionStore
    {
        public CardDefinition? ByOracleId(string oracleId) => null;

        public CardDefinition? ByName(string name) => null;
    }
}
