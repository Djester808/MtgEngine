using System.Text.Json;
using MtgEngine.Cards;
using MtgEngine.Rules.Commands;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.Mana;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// The seams cut after wave 8, from the builders' requests: a card that names the player it is
/// attached to (E5c), a scenario that asserts a shuffle (P11), a casting system that says which
/// objects a cost wants (E3f, E9g), a resumption told what the halted hit dealt (E4c), the tokens a
/// turn created (V6), and mana that remembers where it came from (V6). Each is additive; the
/// builders that read them are wave 9's.
/// </summary>
public sealed class WaveNineSeamTests
{
    private static readonly PlayerId P0 = new(0);

    [Fact]
    [RuleRef("303.4f")]
    public void The_attached_player_reference_resolves_to_the_player_the_source_is_attached_to()
    {
        var board = new Board();
        var aura = board.Put(Board.Creature(0, 0, "seam aura"));
        var state = new ObjectChanged(aura, AttachedToPlayer: P0).Apply(board.State);
        var layers = new Layers.LayerSystem();
        var query = new Query.Query(layers);
        var context = new EffectContext(aura, P0, [], [], null, ImmutableDictionary<string, ImmutableList<ObjectId>>.Empty, ImmutableDictionary<string, string>.Empty, ImmutableDictionary<string, int>.Empty, [], null);

        Assert.Equal(new[] { P0 }, query.Players(state, new AttachedPlayer(), context));
        Assert.Empty(query.Players(board.State, new AttachedPlayer(), context));
    }

    [Fact]
    [RuleRef("702.5b")]
    public void An_enchant_ability_can_say_it_enchants_a_player()
    {
        var enchant = new KeywordAbility(KeywordKind.Enchant, OfPlayer: new PlayerFilter(Opponent: true));
        var json = JsonSerializer.Serialize<Ability>(enchant, CardFileSerializer.Options);

        Assert.Contains("ofPlayer", json, StringComparison.Ordinal);
        var back = Assert.IsType<KeywordAbility>(JsonSerializer.Deserialize<Ability>(json, CardFileSerializer.Options));
        Assert.True(back.OfPlayer?.Opponent);
        Assert.Null(new KeywordAbility(KeywordKind.Enchant).OfPlayer);
    }

    [Fact]
    [RuleRef("701.20")]
    public void A_shuffle_expectation_round_trips_and_a_run_without_one_says_so()
    {
        var expectation = new Shuffled("A");
        var json = JsonSerializer.Serialize<Expectation>(expectation, CardFileSerializer.Options);
        Assert.IsType<Shuffled>(JsonSerializer.Deserialize<Expectation>(json, CardFileSerializer.Options));

        var bear = Board.Creature(2, 2, "seam bear");
        var scenario = new Scenario("seam-shuffle", "nothing is shuffled", new GeneratedSource("seam"), new ScenarioSetup([new PlayerSetup("A"), new PlayerSetup("B")], "A"), [], [expectation]);
        var result = new Scenarios.ScenarioRunner(new Game(Subsystems.Wave2(), new Definitions(bear))).Run(scenario);

        Assert.Null(result.Error);
        var checked_ = Assert.Single(result.Results);
        Assert.False(checked_.Passed);
        Assert.Equal("never shuffled", checked_.Actual);
    }

    [Fact]
    [RuleRef("601.2c")]
    public void A_casting_system_that_cannot_say_what_a_cost_wants_offers_nothing()
    {
        ICastingSystem casting = new Stubs.Casting();
        var board = new Board();

        Assert.Empty(casting.CostChoices(board.State, new PassPriority(P0)));
    }

    [Fact]
    [RuleRef("702.15b")]
    public void A_resumption_told_what_was_dealt_falls_back_to_the_plain_one()
    {
        ICombatSystem combat = new Stubs.Combat();
        var board = new Board();

        var error = Assert.Throws<NotImplementedException>(() => combat.ResumeCombatDamage(board.State, false, 1, []));
        Assert.Contains("E4c", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    [RuleRef("111.1")]
    public void The_turn_remembers_the_tokens_each_player_created()
    {
        var board = new Board();
        var body = board.Put(Board.Creature(1, 1, "seam body"));
        var token = board.State.Objects[body] with { Id = new ObjectId(900), IsToken = true, InitialController = P0 };

        var state = new ObjectCreated(token, -1).Apply(board.State);

        var made = Assert.Single(state.History.TokensCreated[P0]);
        Assert.Equal(new ObjectId(900), made.Id);
        Assert.Empty(new ObjectCreated(board.State.Objects[body] with { Id = new ObjectId(901) }, -1).Apply(board.State).History.TokensCreated);
    }

    [Fact]
    [RuleRef("106.6")]
    public void Mana_remembers_its_source_and_a_spend_forgets_it()
    {
        var board = new Board();
        var land = board.Put(Board.Creature(0, 0, "seam land"));
        var produced = ManaPool.Empty with { Provenance = [new ManaProvenance(Color.Green, 2, land, "that mana")] };

        var state = new ManaChanged(P0, produced, "seam").Apply(board.State);
        var block = Assert.Single(state.Player(P0).Mana.Provenance);
        Assert.Equal(2, block.Amount);

        var spent = new ManaChanged(P0, ManaPool.Empty with { Provenance = [new ManaProvenance(Color.Green, -2, land, "that mana")] }, "seam").Apply(state);
        Assert.Empty(spent.Player(P0).Mana.Provenance);
    }

    private sealed class Definitions(params CardDefinition[] definitions) : IDefinitionStore
    {
        public CardDefinition? ByName(string name) => definitions.FirstOrDefault(d => d.Name == name);

        public CardDefinition? ByOracleId(string oracleId) => definitions.FirstOrDefault(d => d.OracleId == oracleId);
    }
}
