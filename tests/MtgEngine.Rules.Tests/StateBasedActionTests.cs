using MtgEngine.Cards;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.Sba;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// CR 704, plus the losing conditions of 104 and 903.10a that it performs. Every board here is
/// made up: the point of a state-based action is that it reads the state and nothing else.
/// <para>
/// The attachment arms — 704.5m for an Aura and 704.5n for an Equipment or Fortification —
/// live in <c>StateBasedActionTests.Auras.cs</c>, the other half of this partial class, which
/// is where the enchant-ability helpers are too.
/// </para>
/// </summary>
public sealed partial class StateBasedActionTests
{
    private readonly StateBasedActions sba;

    /// <summary>The same instance the rule holds, so a test can read what it was asked (CR 702.5b).</summary>
    private readonly FakeQuery query;

    private readonly FakeCharacteristics characteristics = new();

    public StateBasedActionTests()
    {
        query = new FakeQuery(characteristics);
        sba = new StateBasedActions(new FakeZones(), characteristics, query);
    }

    [Fact]
    [RuleRef("704.3")]
    public void A_quiet_board_performs_nothing()
    {
        var board = new Board();
        board.Put(Board.Creature(2, 2));

        var outcome = sba.Check(board.State);

        Assert.Empty(outcome.Events);
        Assert.Same(board.State, outcome.State);
    }

    [Fact]
    [RuleRef("704.5a")]
    public void A_player_at_zero_life_loses()
    {
        var board = new Board(players: 3).Life(1, 0);

        var outcome = sba.Check(board.State);

        var lost = Assert.Single(outcome.Events.OfType<PlayerLost>());
        Assert.Equal(new PlayerId(1), lost.Player);
        Assert.Contains("life", lost.Reason, StringComparison.Ordinal);
        Assert.True(outcome.State.Player(new PlayerId(1)).HasLost);
    }

    [Fact]
    [RuleRef("704.5a")]
    public void Negative_life_loses_too()
    {
        var board = new Board(players: 3).Life(2, -4);

        Assert.Single(sba.Check(board.State).Events.OfType<PlayerLost>());
    }

    [Fact]
    [RuleRef("704.5b")]
    public void A_player_who_drew_from_an_empty_library_loses()
    {
        var board = new Board(players: 3).DrewFromEmptyLibrary(0);

        var lost = Assert.Single(sba.Check(board.State).Events.OfType<PlayerLost>());

        Assert.Equal(new PlayerId(0), lost.Player);
        Assert.Contains("empty library", lost.Reason, StringComparison.Ordinal);
    }

    [Fact]
    [RuleRef("704.5c")]
    public void Ten_poison_counters_lose_the_game()
    {
        var board = new Board(players: 3).Poison(1, 10);

        var lost = Assert.Single(sba.Check(board.State).Events.OfType<PlayerLost>());

        Assert.Equal(new PlayerId(1), lost.Player);
        Assert.Contains("poison", lost.Reason, StringComparison.Ordinal);
    }

    [Fact]
    [RuleRef("704.5c")]
    public void Nine_poison_counters_do_not()
    {
        var board = new Board(players: 3).Poison(1, 9);

        Assert.Empty(sba.Check(board.State).Events);
    }

    [Fact]
    [RuleRef("704.5d")]
    public void A_token_outside_the_battlefield_ceases_to_exist()
    {
        var board = new Board();
        var id = board.Put(Board.Creature(1, 1, "sba token"), Zone.Graveyard, token: true);

        var outcome = sba.Check(board.State);

        Assert.Equal("ceased", Assert.Single(outcome.Events.OfType<ObjectLeftBy>()).Verb);
        Assert.Equal(id, Assert.Single(outcome.Events.OfType<ObjectCeasedToExist>()).Id);
        Assert.DoesNotContain(id, outcome.State.Objects.Keys);
    }

    [Fact]
    [RuleRef("704.5d")]
    public void A_token_on_the_battlefield_stays()
    {
        var board = new Board();
        board.Put(Board.Creature(1, 1, "sba token"), token: true);

        Assert.Empty(sba.Check(board.State).Events);
    }

    [Fact]
    [RuleRef("704.5e")]
    public void A_copy_of_a_spell_off_the_stack_ceases_to_exist()
    {
        var board = new Board();
        var copy = new StackEntry(null, null, [], [], [], null, IsCopy: true);
        var id = board.Put(Board.Definition("sba copy", CardType.Instant), Zone.Exile, stack: copy);

        var outcome = sba.Check(board.State);

        Assert.Equal(id, Assert.Single(outcome.Events.OfType<ObjectCeasedToExist>()).Id);
    }

    [Fact]
    [RuleRef("704.5e")]
    public void A_copy_of_a_spell_on_the_stack_stays()
    {
        var board = new Board();
        var copy = new StackEntry(null, null, [], [], [], null, IsCopy: true);
        board.Put(Board.Definition("sba copy", CardType.Instant), Zone.Stack, stack: copy);

        Assert.Empty(sba.Check(board.State).Events);
    }

    [Fact]
    [RuleRef("704.5f")]
    public void A_creature_with_toughness_zero_is_put_into_its_owners_graveyard_not_destroyed()
    {
        var board = new Board();
        board.Put(Board.Creature(1, 1), counters: [(Counters.MinusOneMinusOne, 2)]);

        var outcome = sba.Check(board.State);

        // 704.5f is not a destruction: the verb the log records is what a regeneration or an
        // indestructible replacement would key on, and this one must not say "destroyed".
        Assert.Equal("put into graveyard", Assert.Single(outcome.Events.OfType<ObjectLeftBy>()).Verb);
        Assert.Single(outcome.Events.OfType<ObjectMoved>());
        Assert.Equal(Zone.Graveyard, Zoned(outcome.State, "sba beast"));
    }

    [Fact]
    [RuleRef("704.5f")]
    public void Indestructible_does_not_save_a_creature_with_toughness_zero()
    {
        var board = new Board();
        board.Put(
            Board.Creature(1, 1, abilities: [new KeywordAbility(KeywordKind.Indestructible)]),
            counters: [(Counters.MinusOneMinusOne, 1)]);

        Assert.Equal(Zone.Graveyard, Zoned(sba.Check(board.State).State, "sba beast"));
    }

    [Fact]
    [RuleRef("704.5g")]
    public void Lethal_damage_destroys_a_creature()
    {
        var board = new Board();
        board.Put(Board.Creature(2, 2), damage: 2);

        var outcome = sba.Check(board.State);

        Assert.Equal("destroyed", Assert.Single(outcome.Events.OfType<ObjectLeftBy>()).Verb);
        Assert.Equal(Zone.Graveyard, Zoned(outcome.State, "sba beast"));
    }

    [Fact]
    [RuleRef("704.5g")]
    public void Damage_below_toughness_destroys_nothing()
    {
        var board = new Board();
        board.Put(Board.Creature(2, 3), damage: 2);

        Assert.Empty(sba.Check(board.State).Events);
    }

    [Fact]
    [RuleRef("702.12b")]
    public void Indestructible_ignores_lethal_damage()
    {
        var board = new Board();
        board.Put(Board.Creature(2, 2, abilities: [new KeywordAbility(KeywordKind.Indestructible)]), damage: 7);

        var outcome = sba.Check(board.State);

        Assert.Empty(outcome.Events);
        Assert.Equal(Zone.Battlefield, Zoned(outcome.State, "sba beast"));
    }

    [Fact]
    [RuleRef("704.5i")]
    public void A_planeswalker_with_no_loyalty_is_put_into_its_owners_graveyard()
    {
        var board = new Board();
        board.Put(Board.Definition("sba walker", CardType.Planeswalker, supertypes: Supertype.Legendary));

        var outcome = sba.Check(board.State);

        Assert.Equal("put into graveyard", Assert.Single(outcome.Events.OfType<ObjectLeftBy>()).Verb);
        Assert.Equal(Zone.Graveyard, Zoned(outcome.State, "sba walker"));
    }

    [Fact]
    [RuleRef("704.5i")]
    public void A_planeswalker_with_loyalty_stays()
    {
        var board = new Board();
        board.Put(
            Board.Definition("sba walker", CardType.Planeswalker, supertypes: Supertype.Legendary),
            counters: [(Counters.Loyalty, 3)]);

        Assert.Empty(sba.Check(board.State).Events);
    }

    [Fact]
    [RuleRef("704.5j")]
    public void The_legend_rule_asks_the_controller_which_one_to_keep()
    {
        var board = new Board();
        var legend = Board.Creature(2, 2, "sba lord", supertypes: Supertype.Legendary);
        var first = board.Put(legend);
        var second = board.Put(legend);

        var outcome = sba.Check(board.State);

        var asked = Assert.Single(outcome.Events.OfType<ChoiceRequested>());
        Assert.Equal("legend-rule", asked.Choice.Kind);
        Assert.Equal(new PlayerId(0), asked.Choice.Player);
        Assert.Equal(1, asked.Choice.Min);
        Assert.Equal(1, asked.Choice.Max);
        Assert.Equal(new[] { first, second }, asked.Choice.Options.Select(o => o.Object!.Value));

        // Answering is wave 2's: both are still on the battlefield and the loop stopped here.
        Assert.Same(asked.Choice, outcome.State.Pending);
        Assert.Single(outcome.Events);
    }

    [Fact]
    [RuleRef("704.5j")]
    public void The_legend_rule_does_not_reach_across_controllers()
    {
        var board = new Board();
        var legend = Board.Creature(2, 2, "sba lord", supertypes: Supertype.Legendary);
        board.Put(legend, owner: 0);
        board.Put(legend, owner: 1);

        Assert.Empty(sba.Check(board.State).Events);
    }

    [Fact]
    [RuleRef("704.5j")]
    public void Two_legends_with_different_names_coexist()
    {
        var board = new Board();
        board.Put(Board.Creature(2, 2, "sba lord", supertypes: Supertype.Legendary));
        board.Put(Board.Creature(2, 2, "sba baron", supertypes: Supertype.Legendary));

        Assert.Empty(sba.Check(board.State).Events);
    }

    [Fact]
    [RuleRef("704.5q")]
    public void Plus_and_minus_counters_annihilate_in_pairs()
    {
        var board = new Board();
        var id = board.Put(Board.Creature(4, 4), counters: [(Counters.PlusOnePlusOne, 3), (Counters.MinusOneMinusOne, 1)]);

        var outcome = sba.Check(board.State);

        Assert.Equal(2, outcome.Events.OfType<CountersChanged>().Count());
        Assert.All(outcome.Events.OfType<CountersChanged>(), e => Assert.Equal(-1, e.Delta));
        Assert.Equal(2, outcome.State.Objects[id].Counters[Counters.PlusOnePlusOne]);
        Assert.DoesNotContain(Counters.MinusOneMinusOne, outcome.State.Objects[id].Counters.Keys);
        Assert.Equal(Zone.Battlefield, outcome.State.Objects[id].Zone.Zone);
    }

    /// <summary>
    /// The worked example beside CR 704.8: a 1/1 with a +1/+1 counter that is given three
    /// -1/-1 counters. A pair is removed and the creature is in the graveyard when the check
    /// finishes, both as one event.
    /// </summary>
    [Fact]
    [RuleRef("704.8")]
    public void A_pair_is_removed_and_the_creature_dies_in_the_same_check()
    {
        var board = new Board();
        board.Put(Board.Creature(1, 1), counters: [(Counters.PlusOnePlusOne, 1), (Counters.MinusOneMinusOne, 3)]);

        var outcome = sba.Check(board.State);

        Assert.Equal(2, outcome.Events.OfType<CountersChanged>().Count());
        Assert.Equal("put into graveyard", Assert.Single(outcome.Events.OfType<ObjectLeftBy>()).Verb);
        Assert.Equal(Zone.Graveyard, Zoned(outcome.State, "sba beast"));
    }

    /// <summary>
    /// 704.3: if a check performs anything, the check is repeated. The creature dies on the
    /// first pass; only then is the Aura attached to nothing, so it takes a second pass to
    /// notice. One call to <see cref="StateBasedActions.Check"/> has to settle both.
    /// </summary>
    [Fact]
    [RuleRef("704.3")]
    public void The_check_repeats_until_nothing_applies()
    {
        var board = new Board();
        var host = board.Put(Board.Creature(2, 2), damage: 2);
        board.Put(Aura(), attachedTo: host);

        var outcome = sba.Check(board.State);

        var verbs = outcome.Events.OfType<ObjectLeftBy>().Select(e => e.Verb).ToList();
        Assert.Equal(new[] { "destroyed", "put into graveyard" }, verbs);
        Assert.Equal(Zone.Graveyard, Zoned(outcome.State, "sba beast"));
        Assert.Equal(Zone.Graveyard, Zoned(outcome.State, "sba binding"));
    }

    [Fact]
    [RuleRef("903.10a")]
    public void Twenty_one_commander_damage_loses_the_game()
    {
        var board = new Board(life: 40).CommanderDamage(1, new ObjectId(97), 21);

        var outcome = sba.Check(board.State);

        var lost = Assert.Single(outcome.Events.OfType<PlayerLost>());
        Assert.Equal(new PlayerId(1), lost.Player);
        Assert.Contains("21", lost.Reason, StringComparison.Ordinal);
        Assert.Equal(19, outcome.State.Player(new PlayerId(1)).Life);
    }

    [Fact]
    [RuleRef("903.10a")]
    public void Commander_damage_from_two_commanders_does_not_add_up()
    {
        var board = new Board(life: 40)
            .CommanderDamage(1, new ObjectId(97), 11)
            .CommanderDamage(1, new ObjectId(98), 11);

        Assert.Empty(sba.Check(board.State).Events);
    }

    [Fact]
    [RuleRef("104.2a")]
    public void The_game_ends_when_one_player_remains()
    {
        var board = new Board().Life(1, 0);

        var outcome = sba.Check(board.State);

        var ended = Assert.Single(outcome.Events.OfType<GameEnded>());
        Assert.Equal(new PlayerId(0), Assert.Single(ended.Outcome.Winners));
        Assert.False(ended.Outcome.Draw);
        Assert.NotNull(outcome.State.Outcome);
    }

    [Fact]
    [RuleRef("104.4b")]
    public void The_game_is_a_draw_when_every_player_loses_at_once()
    {
        var board = new Board().Life(0, 0).Life(1, 0);

        var outcome = sba.Check(board.State);

        Assert.Equal(2, outcome.Events.OfType<PlayerLost>().Count());
        var ended = Assert.Single(outcome.Events.OfType<GameEnded>());
        Assert.True(ended.Outcome.Draw);
        Assert.Empty(ended.Outcome.Winners);
    }

    [Fact]
    [RuleRef("704.3")]
    public void A_lost_players_permanents_are_left_alone_in_this_slice()
    {
        var board = new Board(players: 3).Lost(2);
        var doomed = board.Put(Board.Creature(1, 1), owner: 2, counters: [(Counters.MinusOneMinusOne, 4)]);

        var outcome = sba.Check(board.State);

        Assert.Empty(outcome.Events);
        Assert.Equal(Zone.Battlefield, outcome.State.Objects[doomed].Zone.Zone);
    }

    [Fact]
    [RuleRef("104.1")]
    public void A_finished_game_is_checked_no_further()
    {
        var board = new Board().Life(1, 0);
        var ended = sba.Check(board.State).State;

        Assert.Empty(sba.Check(ended).Events);
    }

    [Fact]
    [RuleRef("101.4")]
    public void A_pending_choice_stops_the_check()
    {
        var board = new Board();
        board.Put(Board.Creature(1, 1), counters: [(Counters.MinusOneMinusOne, 2)]);
        board.Fold(new ChoiceRequested(new PendingChoice(
            new ChoiceId(1),
            new PlayerId(0),
            "legend-rule",
            "waiting",
            [],
            1,
            1,
            new ChoiceContext(null, null, null, ImmutableDictionary<string, string>.Empty))));

        Assert.Empty(sba.Check(board.State).Events);
    }

    private static Zone? Zoned(GameState state, string name) =>
        state.Objects.Values.FirstOrDefault(o => string.Equals(o.Face.Name, name, StringComparison.Ordinal))?.Zone.Zone;
}
