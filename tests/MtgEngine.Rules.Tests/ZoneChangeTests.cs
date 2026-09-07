using System.Globalization;
using System.Text;
using MtgEngine.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;
using MtgEngine.Rules.Zones;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// Zones and objects, CR 400: starting a game, moving an object, drawing, shuffling and tokens.
/// Every test here checks the log as well as the state, because the property that makes the
/// engine replayable is that a subsystem's state is the fold of the events it hands back.
/// </summary>
public sealed class ZoneChangeTests
{
    private const string Bear = "oracle-bear";
    private const string Bolt = "oracle-bolt";
    private const string Legend = "oracle-legend";

    private static readonly ZoneSystem Zones = new();
    private static readonly IDefinitionStore Store = new Definitions();

    [Fact]
    public void A_game_starts_with_a_library_per_player_and_the_log_folds_to_the_state()
    {
        var outcome = Zones.Create(Setup(), Store);

        Assert.Equal(2, outcome.State.Players.Count);
        Assert.IsType<GameStarted>(outcome.Events[0]);

        // CR 401.1: each player's ten-card deck became their library; seven are now in hand.
        foreach (var seat in new[] { 0, 1 })
        {
            Assert.Equal(3, outcome.State.In(ZoneKey.Of(Zone.Library, new PlayerId(seat))).Count);
            Assert.Equal(7, outcome.State.In(ZoneKey.Of(Zone.Hand, new PlayerId(seat))).Count);
        }
    }

    [Fact]
    public void Replay_of_the_whole_log_equals_the_state_it_was_folded_from()
    {
        var outcome = Zones.Create(Setup(), Store);

        Assert.Equal(Fingerprint(outcome.State), Fingerprint(GameReducer.Replay(outcome.Events)));
    }

    [Fact]
    public void The_commander_starts_in_the_command_zone()
    {
        // CR 103.2c: in a Commander game each player puts their commander into the command zone.
        var outcome = Zones.Create(Setup(), Store);
        var command = outcome.State.In(ZoneKey.Shared(Zone.Command));

        Assert.Equal(2, command.Count);
        Assert.Equal(new[] { new PlayerId(0), new PlayerId(1) }, command.Select(id => outcome.State.Objects[id].Owner));
        Assert.All(command, id => Assert.Equal(Legend, outcome.State.Objects[id].Definition.OracleId));
    }

    [Fact]
    public void Opening_hands_are_dealt_to_each_player_from_their_own_library()
    {
        var outcome = Zones.Create(Setup(), Store);

        foreach (var seat in new[] { 0, 1 })
        {
            var player = new PlayerId(seat);
            var hand = outcome.State.In(ZoneKey.Of(Zone.Hand, player));
            Assert.Equal(7, hand.Count);

            // CR 400.3: a card goes to its own owner's hand, never another player's.
            Assert.All(hand, id => Assert.Equal(player, outcome.State.Objects[id].Owner));
            Assert.All(hand, id => Assert.Equal(ZoneKey.Of(Zone.Hand, player), outcome.State.Objects[id].Zone));
        }

        // Nothing was dealt twice, and every id in a zone is an object that exists.
        var all = outcome.State.Zones.Values.SelectMany(z => z).ToList();
        Assert.Equal(all.Count, all.Distinct().Count());
        Assert.Equal(all.Count, outcome.State.Objects.Count);
    }

    [Fact]
    public void A_smaller_opening_hand_deals_fewer_cards()
    {
        var outcome = Zones.Create(Setup() with { OpeningHandSize = 2 }, Store);

        Assert.Equal(2, outcome.State.In(ZoneKey.Of(Zone.Hand, new PlayerId(0))).Count);
        Assert.Equal(8, outcome.State.In(ZoneKey.Of(Zone.Library, new PlayerId(0))).Count);
    }

    [Fact]
    public void A_draw_takes_the_top_card_of_the_library_into_the_hand()
    {
        // CR 121.1: to draw a card, put the top card of your library into your hand.
        var state = Zones.Create(Setup(), Store).State;
        var player = new PlayerId(0);
        var top = state.In(ZoneKey.Of(Zone.Library, player))[0];

        var outcome = Zones.Draw(state, player, 1);

        Assert.Equal(2, outcome.State.In(ZoneKey.Of(Zone.Library, player)).Count);
        Assert.Equal(8, outcome.State.In(ZoneKey.Of(Zone.Hand, player)).Count);
        Assert.DoesNotContain(top, outcome.State.Objects.Keys);

        // CR 400.7: the drawn card is a new object with a new id, and it is the one in hand.
        var moved = Assert.IsType<ObjectMoved>(Assert.Single(outcome.Events));
        Assert.Equal(top, moved.From);
        Assert.NotEqual(top, moved.To.Id);
        Assert.Contains(moved.To.Id, outcome.State.In(ZoneKey.Of(Zone.Hand, player)));
        AssertLogFolds(state, outcome);
    }

    [Fact]
    public void A_draw_from_an_empty_library_sets_the_flag_and_moves_nothing()
    {
        // CR 121.4: a player who attempts to draw from an empty library loses the game the next
        // time a player would receive priority; the attempt itself is all that happens here.
        var state = Zones.Create(Setup(), Store).State;
        var player = new PlayerId(0);
        var emptied = Zones.Draw(state, player, 3).State;
        Assert.Empty(emptied.In(ZoneKey.Of(Zone.Library, player)));
        Assert.False(emptied.Player(player).DrewFromEmptyLibrary);

        var outcome = Zones.Draw(emptied, player, 1);

        Assert.True(outcome.State.Player(player).DrewFromEmptyLibrary);
        Assert.IsType<DrawFromEmptyLibraryAttempted>(Assert.Single(outcome.Events));
        Assert.Equal(10, outcome.State.In(ZoneKey.Of(Zone.Hand, player)).Count);
        AssertLogFolds(emptied, outcome);
    }

    [Fact]
    public void A_multi_card_draw_that_outruns_the_library_draws_what_is_there_and_then_attempts()
    {
        var state = Zones.Create(Setup(), Store).State;
        var player = new PlayerId(0);

        var outcome = Zones.Draw(state, player, 5);

        Assert.Equal(3, outcome.Events.OfType<ObjectMoved>().Count());
        Assert.Equal(2, outcome.Events.OfType<DrawFromEmptyLibraryAttempted>().Count());
        Assert.True(outcome.State.Player(player).DrewFromEmptyLibrary);
        AssertLogFolds(state, outcome);
    }

    [Fact]
    public void A_move_to_the_battlefield_makes_a_new_object_and_clears_damage_and_counters()
    {
        // CR 400.7: an object that moves from one zone to another becomes a new object with no
        // memory of, or relation to, its previous existence.
        var state = Zones.Create(Setup(), Store).State;
        var player = new PlayerId(0);
        var card = InHand(state, player, Bear);
        var wounded = state with
        {
            Objects = state.Objects.SetItem(card, state.Objects[card] with
            {
                Damage = 3,
                Counters = ImmutableDictionary<string, int>.Empty.SetItem("+1/+1", 2),
                AttachedTo = new ObjectId(999),
                Tapped = true,
                FaceDown = true,
            }),
        };

        var outcome = Zones.Move(wounded, card, ZoneKey.Shared(Zone.Battlefield));
        var moved = Assert.IsType<ObjectMoved>(Assert.Single(outcome.Events));
        var now = outcome.State.Objects[moved.To.Id];

        Assert.NotEqual(card, now.Id);
        Assert.Equal(0, now.Damage);
        Assert.Empty(now.Counters);
        Assert.Null(now.AttachedTo);
        Assert.False(now.Tapped);
        Assert.False(now.FaceDown);
        Assert.Null(now.Stack);
        Assert.True(now.Timestamp > wounded.Objects[card].Timestamp);
        Assert.Contains(now.Id, outcome.State.In(ZoneKey.Shared(Zone.Battlefield)));
        Assert.DoesNotContain(card, outcome.State.In(ZoneKey.Of(Zone.Hand, player)));
        AssertLogFolds(wounded, outcome);
    }

    [Fact]
    public void A_move_to_the_battlefield_may_arrive_tapped_and_under_another_players_control()
    {
        var state = Zones.Create(Setup(), Store).State;
        var owner = new PlayerId(0);
        var thief = new PlayerId(1);
        var card = InHand(state, owner, Bear);

        var outcome = Zones.Move(state, card, ZoneKey.Shared(Zone.Battlefield), tapped: true, controller: thief);
        var now = outcome.State.Objects[((ObjectMoved)outcome.Events[0]).To.Id];

        Assert.True(now.Tapped);
        Assert.Equal(thief, now.InitialController);

        // CR 108.3: the owner of a card does not change with a change of control.
        Assert.Equal(owner, now.Owner);

        // CR 302.6: a creature that just arrived has not been controlled since the turn began.
        Assert.True(now.SummoningSick);
    }

    [Fact]
    public void A_move_to_another_players_graveyard_goes_to_the_owners_graveyard_instead()
    {
        // CR 400.3: if an object would go to any library, graveyard or hand other than its
        // owner's, it goes to its owner's corresponding zone.
        var state = Zones.Create(Setup(), Store).State;
        var owner = new PlayerId(0);
        var card = state.In(ZoneKey.Of(Zone.Hand, owner))[0];

        var outcome = Zones.Move(state, card, ZoneKey.Of(Zone.Graveyard, new PlayerId(1)), controller: new PlayerId(1));
        var now = outcome.State.Objects[((ObjectMoved)outcome.Events[0]).To.Id];

        Assert.Equal(ZoneKey.Of(Zone.Graveyard, owner), now.Zone);
        Assert.Empty(outcome.State.In(ZoneKey.Of(Zone.Graveyard, new PlayerId(1))));

        // CR 108.4: a card in a graveyard has no controller; its owner stands in.
        Assert.Equal(owner, now.InitialController);
    }

    [Fact]
    public void A_sorcery_that_would_enter_the_battlefield_stays_where_it_is()
    {
        // CR 400.4a: if an instant or sorcery card would enter the battlefield, it remains in its
        // previous zone.
        var state = Zones.Create(Setup(), Store).State;
        var player = new PlayerId(0);
        var spell = InHand(state, player, Bolt);

        var outcome = Zones.Move(state, spell, ZoneKey.Shared(Zone.Battlefield));

        Assert.Empty(outcome.Events);
        Assert.Same(state, outcome.State);
        Assert.Empty(outcome.State.In(ZoneKey.Shared(Zone.Battlefield)));
        Assert.Contains(spell, outcome.State.In(ZoneKey.Of(Zone.Hand, player)));
    }

    [Fact]
    public void A_creature_card_is_not_stopped_by_the_rule_that_stops_sorceries()
    {
        var state = Zones.Create(Setup(), Store).State;
        var creature = InHand(state, new PlayerId(0), Bear);

        var outcome = Zones.Move(state, creature, ZoneKey.Shared(Zone.Battlefield));

        Assert.Single(outcome.State.In(ZoneKey.Shared(Zone.Battlefield)));
    }

    [Fact]
    public void A_move_of_an_object_that_is_no_longer_there_does_nothing()
    {
        var state = Zones.Create(Setup(), Store).State;

        var outcome = Zones.Move(state, new ObjectId(9999), ZoneKey.Shared(Zone.Exile));

        Assert.Empty(outcome.Events);
        Assert.Same(state, outcome.State);
    }

    [Fact]
    public void Placement_puts_a_card_on_the_top_or_the_bottom_of_the_library()
    {
        // CR 400.5: the order of a library can only be changed when a rule or effect allows it, so
        // where in it a card lands is the caller's to say.
        var state = Zones.Create(Setup(), Store).State;
        var player = new PlayerId(0);
        var library = ZoneKey.Of(Zone.Library, player);
        var before = state.In(library);

        var onTop = Zones.Move(state, state.In(ZoneKey.Of(Zone.Hand, player))[0], library, Placement.Top);
        var top = ((ObjectMoved)onTop.Events[0]).To.Id;
        Assert.Equal(top, onTop.State.In(library)[0]);
        Assert.Equal(before, onTop.State.In(library).RemoveAt(0));

        var onBottom = Zones.Move(state, state.In(ZoneKey.Of(Zone.Hand, player))[0], library, Placement.Bottom);
        var bottom = ((ObjectMoved)onBottom.Events[0]).To.Id;
        Assert.Equal(bottom, onBottom.State.In(library)[^1]);
        Assert.Equal(before, onBottom.State.In(library).RemoveAt(before.Count));
    }

    [Fact]
    public void A_card_put_into_a_graveyard_lands_on_top_of_it()
    {
        // CR 404.1: any object that is countered, discarded, destroyed or sacrificed is put on top
        // of its owner's graveyard.
        var state = Zones.Create(Setup(), Store).State;
        var player = new PlayerId(0);
        var graveyard = ZoneKey.Of(Zone.Graveyard, player);

        var first = Zones.Move(state, state.In(ZoneKey.Of(Zone.Hand, player))[0], graveyard);
        var second = Zones.Move(first.State, first.State.In(ZoneKey.Of(Zone.Hand, player))[0], graveyard);

        Assert.Equal(2, second.State.In(graveyard).Count);
        Assert.Equal(((ObjectMoved)second.Events[0]).To.Id, second.State.In(graveyard)[0]);
    }

    [Fact]
    public void A_shuffle_with_the_same_seed_is_the_same_shuffle_and_a_different_seed_is_not()
    {
        // CR 701.24a: shuffling randomizes the library. The generator is seeded from the log, so a
        // reported game replays exactly.
        var player = new PlayerId(0);
        var library = ZoneKey.Of(Zone.Library, player);

        var one = Zones.Create(Setup() with { Seed = 12345, OpeningHandSize = 0 }, Store);
        var two = Zones.Create(Setup() with { Seed = 12345, OpeningHandSize = 0 }, Store);
        var other = Zones.Create(Setup() with { Seed = 999, OpeningHandSize = 0 }, Store);

        Assert.Equal(Order(one), Order(two));
        Assert.NotEqual(Order(one), Order(other));

        // CR 701.24: a shuffle is a reorder and the randomness it consumed, and nothing else.
        var shuffle = Zones.Shuffle(one.State, player);
        Assert.Contains(shuffle.Events, e => e is RngAdvanced);
        var reordered = Assert.Single(shuffle.Events.OfType<ZoneReordered>());
        Assert.Equal(library, reordered.Zone);
        Assert.Equal(one.State.Rng.Counter + 9, shuffle.State.Rng.Counter);
        Assert.Equal(Sorted(one.State, library), Sorted(shuffle.State, library));
        AssertLogFolds(one.State, shuffle);

        List<int> Order(Outcome o) => [.. o.State.In(library).Select(id => id.Value)];
    }

    [Fact]
    public void A_shuffle_keeps_the_same_cards_and_only_reorders_the_shuffling_players_library()
    {
        var state = Zones.Create(Setup(), Store).State;
        var mine = ZoneKey.Of(Zone.Library, new PlayerId(0));
        var theirs = ZoneKey.Of(Zone.Library, new PlayerId(1));

        var outcome = Zones.Shuffle(state, new PlayerId(0));

        Assert.Equal(state.In(theirs), outcome.State.In(theirs));
        Assert.Equal(Sorted(state, mine), Sorted(outcome.State, mine));
        Assert.Equal(state.In(mine).Count, outcome.State.In(mine).Count);
    }

    [Fact]
    public void A_token_exists_on_the_battlefield_with_the_specs_power_and_toughness()
    {
        // CR 111.1: a token is a marker used to represent a permanent that is not a card.
        var state = Zones.Create(Setup(), Store).State;
        var spec = Soldier(1, 1);

        var outcome = Zones.CreateToken(state, spec, new PlayerId(1), tapped: true, attacking: false);

        var created = Assert.IsType<ObjectCreated>(Assert.Single(outcome.Events));
        var token = outcome.State.Objects[created.Object.Id];
        Assert.True(token.IsToken);
        Assert.True(token.Tapped);
        Assert.Contains(token.Id, outcome.State.In(ZoneKey.Shared(Zone.Battlefield)));

        // CR 111.2: the token has the characteristics the effect gave it, and its owner is the
        // player who controls that effect.
        Assert.Equal(new PlayerId(1), token.Owner);
        Assert.Equal(new PlayerId(1), token.InitialController);
        Assert.Equal(spec.Name, token.Face.Name);
        Assert.Equal(CardType.Creature, token.Face.Types.Types);
        Assert.Equal(new[] { "Trooper" }, token.Face.Types.SubtypeList);
        Assert.Equal(1, token.Face.Power?.Number);
        Assert.Equal(1, token.Face.Toughness?.Number);
        Assert.Equal(Color.White, token.Face.ColorIndicator);
        AssertLogFolds(state, outcome);
    }

    [Fact]
    public void A_token_created_attacking_joins_the_combat_that_is_happening()
    {
        // CR 506.3c: an effect can put a creature onto the battlefield attacking. It was never
        // declared as an attacker, so it joins the attack in progress.
        var state = Zones.Create(Setup(), Store).State;
        var attacker = new PlayerId(0);
        var defender = new PlayerId(1);
        var inCombat = state with
        {
            Combat = new CombatState(attacker, ImmutableDictionary<ObjectId, CombatTarget>.Empty, ImmutableDictionary<ObjectId, ImmutableList<ObjectId>>.Empty, AttackersDeclared: true, BlockersDeclared: false),
        };

        var outcome = Zones.CreateToken(inCombat, Soldier(1, 1), attacker, tapped: false, attacking: true);
        var token = ((ObjectCreated)outcome.Events[0]).Object.Id;

        Assert.Equal(2, outcome.Events.Count);
        Assert.NotNull(outcome.State.Combat);
        Assert.Equal(defender, outcome.State.Combat.Attackers[token].Player);
        AssertLogFolds(inCombat, outcome);
    }

    [Fact]
    public void A_token_created_with_no_combat_happening_is_simply_on_the_battlefield()
    {
        var state = Zones.Create(Setup(), Store).State;

        var outcome = Zones.CreateToken(state, Soldier(2, 2), new PlayerId(0), tapped: false, attacking: true);

        Assert.IsType<ObjectCreated>(Assert.Single(outcome.Events));
        Assert.Null(outcome.State.Combat);
    }

    /// <summary>The property every subsystem owes: the state it hands back is the fold of the events it hands back.</summary>
    private static void AssertLogFolds(GameState before, Outcome outcome) =>
        Assert.Equal(Fingerprint(GameReducer.Apply(before, outcome.Events)), Fingerprint(outcome.State));

    /// <summary>
    /// Everything about a state that a zone change can touch, as text. A record's own equality
    /// compares its immutable collections by reference, so two states built from the same events
    /// are never equal by <c>==</c> even when they are the same game.
    /// </summary>
    private static string Fingerprint(GameState state)
    {
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"rng {state.Rng.Seed}/{state.Rng.Counter} next {state.NextObjectId}/{state.NextTimestamp}\n");
        foreach (var player in state.Players)
            text.Append(CultureInfo.InvariantCulture, $"{player.Id} {player.Name} life {player.Life} emptyDraw {player.DrewFromEmptyLibrary}\n");

        foreach (var zone in state.Zones.OrderBy(z => z.Key.Zone).ThenBy(z => z.Key.Player?.Value ?? -1))
        {
            text.Append(CultureInfo.InvariantCulture, $"{zone.Key.Zone}/{zone.Key.Player}:");
            foreach (var id in zone.Value)
            {
                var obj = state.Objects[id];
                text.Append(CultureInfo.InvariantCulture, $" [{id.Value} {obj.Definition.OracleId} own{obj.Owner.Value} ctl{obj.InitialController.Value} ts{obj.Timestamp} tap{obj.Tapped} dmg{obj.Damage} ctr{obj.Counters.Count} tok{obj.IsToken}]");
            }

            text.Append('\n');
        }

        if (state.Combat is { } combat)
        {
            text.Append(CultureInfo.InvariantCulture, $"combat {combat.AttackingPlayer}");
            foreach (var declared in combat.Attackers.OrderBy(a => a.Key.Value))
                text.Append(CultureInfo.InvariantCulture, $" {declared.Key.Value}->{declared.Value.Player}");
            text.Append('\n');
        }

        return text.ToString();
    }

    private static List<int> Sorted(GameState state, ZoneKey zone) =>
        [.. state.In(zone).Select(id => id.Value).OrderBy(v => v)];

    private static ObjectId InHand(GameState state, PlayerId player, string oracleId) =>
        state.In(ZoneKey.Of(Zone.Hand, player)).First(id => string.Equals(state.Objects[id].Definition.OracleId, oracleId, StringComparison.Ordinal));

    /// <summary>A made-up token, so no card's text decides what this test proves.</summary>
    private static TokenSpec Soldier(int power, int toughness) =>
        new("Test Trooper", Color.White, CardType.Creature, ["Trooper"], Power: power, Toughness: toughness);

    private static GameSetup Setup()
    {
        ImmutableList<string> deck = [.. Enumerable.Repeat(Bear, 5), .. Enumerable.Repeat(Bolt, 5)];
        return new GameSetup([new PlayerStart("One", deck, [Legend]), new PlayerStart("Two", deck, [Legend])], Seed: 7);
    }

    /// <summary>
    /// Three made-up definitions. Nothing here is a real card: the engine and its tests know the
    /// vocabulary, and the cards live under <c>cards/</c>.
    /// </summary>
    private sealed class Definitions : IDefinitionStore
    {
        private static readonly CardDefinition Creature = new(Bear, "Test Beast", Layout.Normal,
            [new Face("Test Beast", FaceRole.Main, new TypeLine(CardType.Creature, Subtypes: ["Beast"]), "{1}{G}", new PrintedStat("2"), new PrintedStat("2"))]);

        private static readonly CardDefinition Sorcery = new(Bolt, "Test Blast", Layout.Normal,
            [new Face("Test Blast", FaceRole.Main, new TypeLine(CardType.Sorcery), "{R}")]);

        private static readonly CardDefinition Commander = new(Legend, "Test Legend", Layout.Normal,
            [new Face("Test Legend", FaceRole.Main, new TypeLine(CardType.Creature, Supertype.Legendary, ["Beast"]), "{2}{G}", new PrintedStat("3"), new PrintedStat("3"))]);

        public CardDefinition? ByName(string name) =>
            new[] { Creature, Sorcery, Commander }.FirstOrDefault(d => string.Equals(d.Name, name, StringComparison.Ordinal));

        public CardDefinition? ByOracleId(string oracleId) => oracleId switch
        {
            Bear => Creature,
            Bolt => Sorcery,
            Legend => Commander,
            _ => null,
        };
    }
}
