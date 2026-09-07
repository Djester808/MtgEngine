using MtgEngine.Cards;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.Sba;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// CR 704, plus the losing conditions of 104 and 903.10a that it performs. Every board here is
/// made up: the point of a state-based action is that it reads the state and nothing else.
/// </summary>
public sealed class StateBasedActionTests
{
    private readonly StateBasedActions sba;

    public StateBasedActionTests()
    {
        var characteristics = new FakeCharacteristics();
        sba = new StateBasedActions(new FakeZones(), characteristics, new FakeQuery(characteristics));
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
    [RuleRef("704.5m")]
    public void An_aura_attached_to_nothing_is_put_into_its_owners_graveyard()
    {
        var board = new Board();
        board.Put(Aura());

        var outcome = sba.Check(board.State);

        Assert.Equal("put into graveyard", Assert.Single(outcome.Events.OfType<ObjectLeftBy>()).Verb);
        Assert.Equal(Zone.Graveyard, Zoned(outcome.State, "sba binding"));
    }

    [Fact]
    [RuleRef("704.5m")]
    public void An_aura_on_an_illegal_object_is_put_into_its_owners_graveyard()
    {
        var board = new Board();
        var land = board.Put(Board.Definition("sba waste", CardType.Land));
        board.Put(Aura(), attachedTo: land);

        Assert.Equal(Zone.Graveyard, Zoned(sba.Check(board.State).State, "sba binding"));
    }

    [Fact]
    [RuleRef("704.5m")]
    public void An_aura_on_a_legal_creature_stays()
    {
        var board = new Board();
        var host = board.Put(Board.Creature(2, 2));
        board.Put(Aura(), attachedTo: host);

        Assert.Empty(sba.Check(board.State).Events);
    }

    [Fact]
    [RuleRef("704.5n")]
    public void Equipment_attached_to_a_noncreature_becomes_unattached()
    {
        var board = new Board();
        var land = board.Put(Board.Definition("sba waste", CardType.Land));
        var equipment = board.Put(Board.Definition("sba blade", CardType.Artifact, subtypes: ["Equipment"]), attachedTo: land);

        var outcome = sba.Check(board.State);

        var changed = Assert.Single(outcome.Events.OfType<ObjectChanged>());
        Assert.Equal(equipment, changed.Id);
        Assert.True(changed.ClearAttachment);

        // It remains on the battlefield: 704.5n unattaches, it does not destroy.
        Assert.Equal(Zone.Battlefield, outcome.State.Objects[equipment].Zone.Zone);
        Assert.Null(outcome.State.Objects[equipment].AttachedTo);
    }

    [Fact]
    [RuleRef("704.5n")]
    public void Equipment_attached_to_a_creature_stays_attached()
    {
        var board = new Board();
        var host = board.Put(Board.Creature(2, 2));
        board.Put(Board.Definition("sba blade", CardType.Artifact, subtypes: ["Equipment"]), attachedTo: host);

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

    /// <summary>
    /// CR 704.5m with 205.3d: an Aura that enchants one creature type is legally attached to a
    /// changeling, which is every creature type (CR 702.72a) without carrying any of the names.
    /// The Equipment beside it is the other half of 205.3d: being every creature type is not
    /// being every subtype, so the changeling is a legal host for 704.5n and not an Aura itself.
    /// </summary>
    [Fact]
    [RuleRef("704.5m")]
    public void An_aura_enchanting_one_creature_type_stays_on_a_changeling()
    {
        var board = new Board();
        var host = board.Put(Changeling());
        board.Put(Aura(new ObjectFilter(Subtypes: ["Goblin"])), attachedTo: host);
        board.Put(Board.Definition("sba blade", CardType.Artifact, subtypes: ["Equipment"]), attachedTo: host);

        Assert.Empty(sba.Check(board.State).Events);
    }

    /// <summary>
    /// CR 205.3m: "loses all creature types" takes exactly those away, so the changeling shares
    /// no creature type with anything and its Aura's enchant ability is no longer satisfied —
    /// 704.5m then buries the Aura like any other illegally attached one.
    /// </summary>
    [Fact]
    [RuleRef("704.5m")]
    public void An_aura_enchanting_one_creature_type_is_buried_when_its_changeling_loses_them_all()
    {
        var board = new Board();
        var host = board.Put(ChangelingThatLostEveryCreatureType());
        board.Put(Aura(new ObjectFilter(Subtypes: ["Goblin"])), attachedTo: host);

        var outcome = sba.Check(board.State);

        Assert.Equal("put into graveyard", Assert.Single(outcome.Events.OfType<ObjectLeftBy>()).Verb);
        Assert.Equal(Zone.Graveyard, Zoned(outcome.State, "sba binding"));
        // The host itself is untouched: it is still a creature, just one with no creature type.
        Assert.Equal(Zone.Battlefield, outcome.State.Objects[host].Zone.Zone);
    }

    /// <summary>
    /// CR 303.4f: an Aura enters attached to the object <em>or player</em> it was cast at, and
    /// CR 704.5m asks the same question of both halves. "Enchant player" is spelled as an enchant
    /// ability that names no object filter (see <c>StateBasedActions.EnchantsAPlayer</c>), so this
    /// one stays where an Aura that names a creature would be buried.
    /// </summary>
    [Fact]
    [RuleRef("704.5m")]
    public void An_aura_that_enchants_a_player_stays_on_that_player()
    {
        var board = new Board();
        board.Put(PlayerAura(), attachedToPlayer: 1);

        Assert.Empty(sba.Check(board.State).Events);
    }

    /// <summary>
    /// CR 704.5m: the other direction. An enchant ability that names an object filter is a
    /// restriction to objects, so the player is not something it may enchant and the Aura goes.
    /// This is the arm that keeps the new state field from making every Aura legal on anyone.
    /// </summary>
    [Fact]
    [RuleRef("704.5m")]
    public void An_aura_that_enchants_a_creature_is_buried_when_it_is_attached_to_a_player()
    {
        var board = new Board();
        board.Put(Aura(), attachedToPlayer: 1);

        var outcome = sba.Check(board.State);

        Assert.Equal("put into graveyard", Assert.Single(outcome.Events.OfType<ObjectLeftBy>()).Verb);
        Assert.Equal(Zone.Graveyard, Zoned(outcome.State, "sba binding"));
    }

    /// <summary>
    /// CR 800.4a with 704.5m: a player who has left the game is no longer a player, so the Aura
    /// on them is attached to nothing and is put into its owner's graveyard. Three seats, because
    /// with two the game would already be over and 704.3 performs nothing once it is (104.2a).
    /// </summary>
    [Fact]
    [RuleRef("800.4a")]
    public void An_aura_on_a_player_who_left_the_game_is_put_into_its_owners_graveyard()
    {
        var board = new Board(players: 3);
        board.Put(PlayerAura(), attachedToPlayer: 1);
        board.Lost(1);

        var outcome = sba.Check(board.State);

        Assert.Equal(Zone.Graveyard, Zoned(outcome.State, "sba curse"));
    }

    /// <summary>
    /// CR 704.5m's third case, and the one E3e's request said could not be half landed: attached
    /// to neither an object nor a player, an Aura still goes, whatever it may enchant.
    /// </summary>
    [Fact]
    [RuleRef("704.5m")]
    public void An_aura_that_enchants_a_player_but_is_attached_to_nothing_still_goes()
    {
        var board = new Board();
        board.Put(PlayerAura());

        Assert.Equal(Zone.Graveyard, Zoned(sba.Check(board.State).State, "sba curse"));
    }

    private static CardDefinition Aura(ObjectFilter? enchants = null) =>
        Board.Definition(
            "sba binding",
            CardType.Enchantment,
            subtypes: ["Aura"],
            abilities: [new KeywordAbility(KeywordKind.Enchant, Of: enchants ?? new ObjectFilter(Types: CardType.Creature))]);

    /// <summary>"Enchant player": an enchant ability that restricts nothing about an object.</summary>
    private static CardDefinition PlayerAura() =>
        Board.Definition(
            "sba curse",
            CardType.Enchantment,
            subtypes: ["Aura"],
            abilities: [new KeywordAbility(KeywordKind.Enchant)]);

    /// <summary>CR 702.72a: written as the vocabulary writes it, an <see cref="AddTypes"/> on itself.</summary>
    private static CardDefinition Changeling() =>
        Board.Definition(
            "sba mimic",
            CardType.Creature,
            2,
            2,
            subtypes: ["Shapeshifter"],
            abilities: [new StaticAbility(new AddTypes(AllCreatureTypes: true), To: new Self(), CharacteristicDefining: true)]);

    /// <summary>The same creature under an effect that removes every creature type, CR 205.3m.</summary>
    private static CardDefinition ChangelingThatLostEveryCreatureType() =>
        Board.Definition(
            "sba mimic",
            CardType.Creature,
            2,
            2,
            subtypes: ["Shapeshifter"],
            abilities:
            [
                new StaticAbility(new AddTypes(AllCreatureTypes: true), To: new Self(), CharacteristicDefining: true),
                new StaticAbility(new RemoveTypes(AllCreatureTypes: true), To: new Self()),
            ]);

    private static Zone? Zoned(GameState state, string name) =>
        state.Objects.Values.FirstOrDefault(o => string.Equals(o.Face.Name, name, StringComparison.Ordinal))?.Zone.Zone;
}
